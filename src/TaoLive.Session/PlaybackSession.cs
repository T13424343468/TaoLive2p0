using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 单个播放会话：一个频道一个会话，会话内一个状态机，
/// 状态只能经 Transition 改变，外部只读不写（封装口径，tao-code-standard 〇·八.1）。
/// 时钟注入可测（Tests 快拍不真等）。
/// 换台顺序（契约）：DetachSurface → Stop 旧源 → 状态归 Idle → 选新源 → 起播 → 重挂 Surface。
/// 断流自救：内核 OnError 或起播超时 → 依频道备源列表顺序切下一路重起播，不崩；
/// 全部备源耗尽则停在 Failed 并发 AllSourcesFailed 事件，由上层决定重试或放弃。
/// 被换下的会话（Supersede）不再响应内核事件，避免共享内核时旧会话抢自救。
/// </summary>
public sealed class PlaybackSession
{
    private readonly IPlayer _player;
    private readonly IClock _clock;
    private PlaybackState _state = PlaybackState.Idle;
    private List<SourceEndpoint> _queue = new();
    private int _endpointIndex = -1;
    private Channel? _channel;
    private IntPtr _surfaceHandle;
    private bool _hasSurface;
    private bool _active = true;
    private bool _preferLowLatency;
    // 单调计时账（方案书 v12 铁律，四镜·卡顿镜 11-15 修）：open/stall 看门狗判定一律按单调钟，
    // 墙钟（StartedAtUtc 等）仅留展示与台账，开机校时跳变不再误触/漏触看门狗。
    private long? _bufferingSinceMs;
    private long? _reconnectingSinceMs;
    // 预防性换源账（老板令 #24）：库存跌破线连续两拍（间隔≥1 秒）即主动切备线，不等卡流 15 秒。
    // 单调钟记账；0 读数按「无读数」不判趋势（IPlayer 契约），冷却窗防横跳。
    private int _lowInventoryRounds;
    private long _lowInventoryLastAtMs;
    private long? _lastFailoverAtMs; // 可空：无账=从未切过，不占冷却窗（初值 0 会把开机头 60 秒全锁死）

    public PlaybackSession(IPlayer player, IClock clock)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        // 内核回调守卫（闪退镜黄⑤ 11-15）：矩阵外跳转等异常不掀回调链崩进程，落 KernelCallbackFailed 交壳层记台账
        _player.OnError += (_, e) => GuardKernel(() => OnPlayerError(e.Code));
        _player.OnFirstFrame += (_, _) => GuardKernel(NotifyFirstFrame);
        // 卡流自救接线（方案书 v99 四镜·卡顿镜 11-15 修）：内核缓冲事件接会话，播放中卡流进 Reconnecting 走看门狗。
        // NotifyStalled 内部有 Playing 态守卫，起播期缓冲不误触发。
        _player.OnBufferingStart += (_, _) => GuardKernel(NotifyStalled);
        // 恢复沿接线（真机反馈批#5 修 2026-11-15）：内核 Ready 发 OnBufferingEnd=画面恢复出帧（契约文档明载
        // 「断流自救期间会成对反复出现」），Reconnecting 态收即回 Playing——此前矩阵无恢复沿，短卡流后即使
        // 自行恢复也会被 15 秒看门狗误杀切备源（真机「断流后一直黑屏」归因实锤）。
        _player.OnBufferingEnd += (_, _) => GuardKernel(NotifyRecovered);
    }

    /// <summary>内核回调链异常出口（闪退镜黄⑤）：壳层接 CrashNet 落运行台账。</summary>
    public static event EventHandler<string>? KernelCallbackFailed;

    private void GuardKernel(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            KernelCallbackFailed?.Invoke(this, $"{CurrentChannel?.Name ?? CurrentChannel?.Id ?? "未知频道"}: {ex.Message}");
        }
    }

    /// <summary>起播超时（看门狗阈值）：缓冲超过此时长即视为断流，走备源切换。
    /// #74 切台提速（2026-11-15 老板定值）：15 秒→3 秒——死源撞满钟是切台黑屏 8-10 秒主账，快切快走。</summary>
    public static TimeSpan DefaultOpenTimeout { get; } = TimeSpan.FromSeconds(3);

    /// <summary>本会话的起播超时阈值，测试可调。</summary>
    public TimeSpan OpenTimeout { get; set; } = DefaultOpenTimeout;

    public PlaybackState State => _state;

    public DateTimeOffset? StartedAtUtc { get; private set; }

    /// <summary>当前会话所属频道；从未起播过则为空。</summary>
    public Channel? CurrentChannel => _channel;

    /// <summary>当前正在拉取的源端点；未起播为空。</summary>
    public SourceEndpoint? CurrentEndpoint =>
        _endpointIndex >= 0 && _endpointIndex < _queue.Count ? _queue[_endpointIndex] : null;

    /// <summary>本频道线路数（手动换线路用，2.0 W8）；未起播为 0。</summary>
    public int LineCount => _queue.Count;

    /// <summary>当前线路序号（0 基）；未起播为 -1。</summary>
    public int CurrentLineIndex => _endpointIndex;

    /// <summary>本会话累计断流自救切换次数；换台归零。</summary>
    public int FailoverCount { get; private set; }

    /// <summary>
    /// 备源队列吃内置表（方案书收口批①）：建队列时在本台源之后追加该供给给出的备援候选
    /// （URL 去重，本台已有同址不重复入队）；空=不吃内置表（既有契约不变，测试可注空）。
    /// </summary>
    public Func<Channel, IReadOnlyList<SourceEndpoint>>? FallbackSourceProvider { get; set; }

    /// <summary>#74 源队列健康排序（2026-11-15 老板令）：建队（含备源追加后）整体重排——本台源死账时
    /// 死源沉底、健康候选补位。壳层接健康账（账空=原表序不猜）；未设=按原表序（既有契约与测试不变）。</summary>
    public Func<Channel, IEnumerable<SourceEndpoint>, IEnumerable<SourceEndpoint>>? QueueOrderer { get; set; }

    /// <summary>当前输出面句柄（管理器热切换移交用）；未接面为 IntPtr.Zero。</summary>
    public IntPtr SurfaceHandle => _surfaceHandle;

    /// <summary>状态迁移完成事件（迁移成功后触发，含换台与自救引发的迁移）。</summary>
    public event EventHandler<PlaybackState>? StateChanged;

    /// <summary>断流自救切换事件：From 为切出源，To 为切入源，Attempt 从 1 计。</summary>
    public event EventHandler<FailoverEventArgs>? Failover;

    /// <summary>备源耗尽：本频道全部源都试败，会话停在 Failed。</summary>
    public event EventHandler<AllSourcesFailedEventArgs>? AllSourcesFailed;

    /// <summary>受控迁移：仅允许状态机矩阵内的跳转，非法跳转抛异常不静默。</summary>
    public void Transition(PlaybackState next, string? reason = null)
    {
        bool legal = (_state, next) switch
        {
            (PlaybackState.Idle, PlaybackState.Buffering) => true,
            (PlaybackState.Idle, PlaybackState.Failed) => true,
            (PlaybackState.Buffering, PlaybackState.Playing) => true,
            (PlaybackState.Buffering, PlaybackState.Reconnecting) => true,
            (PlaybackState.Buffering, PlaybackState.Failed) => true,
            (PlaybackState.Buffering, PlaybackState.Idle) => true,
            (PlaybackState.Playing, PlaybackState.Reconnecting) => true,
            (PlaybackState.Playing, PlaybackState.Failed) => true,
            (PlaybackState.Playing, PlaybackState.Idle) => true,
            (PlaybackState.Reconnecting, PlaybackState.Buffering) => true,
            (PlaybackState.Reconnecting, PlaybackState.Playing) => true,
            (PlaybackState.Reconnecting, PlaybackState.Failed) => true,
            // BUG-1（测试分身 2026-11-15 报告）：卡流窗口换台必经 Stop→Idle，矩阵补齐合法迁移
            (PlaybackState.Reconnecting, PlaybackState.Idle) => true,
            (PlaybackState.Failed, PlaybackState.Buffering) => true,
            (PlaybackState.Failed, PlaybackState.Idle) => true,
            _ => false,
        };
        if (!legal)
        {
            throw new InvalidOperationException(
                $"Illegal playback transition {_state} -> {next} (reason: {reason ?? "n/a"})");
        }
        if (next == PlaybackState.Buffering && (_state == PlaybackState.Idle || _state == PlaybackState.Failed))
        {
            // 起播计时刻：首播与失败重试都算新一轮，StartedAtUtc 以本次缓冲起点为准（墙钟仅展示）。
            StartedAtUtc = _clock.UtcNow;
            _bufferingSinceMs = _clock.NowMs;
        }
        if (next != PlaybackState.Reconnecting)
        {
            _reconnectingSinceMs = null;
        }
        // 后台车道门控接线（白屏镜红①自救链 11-15 修）：门控此前生产零置位——预检/健康点/断网恢复
        // 整链在真机死锁（测试专门放行防无限等，生产漏接线）。接线口径按车道令原语义：
        // 起播/换台/自救过渡期（Buffering/Reconnecting）静默零后台网络；首帧落地（Playing）恢复放行；
        // 失败/空闲（Failed/Idle，无画面在播）回空闲档放行——断网期预检照跑，恢复沿才回得来。
        if (next == PlaybackState.Buffering || next == PlaybackState.Reconnecting)
            BackgroundLane.SetFirstFrameLanded(false);
        else
            BackgroundLane.SetFirstFrameLanded(true);
        BackgroundLane.SetPlaying(next == PlaybackState.Playing);
        var fromState = _state;
        _state = next;
        // 起播打点（真机反馈批·埋点补充 2026-11-15）：起播/换台/换源指令到首帧的耗时账——
        // 覆盖正常换台与备源自救两条路径（换源后路径带换源次数），是白屏/换台慢排查的第一账；
        // 热备攒流会话同样落账（攒流到门槛线的真实时长可查）。
        if (next == PlaybackState.Playing && fromState == PlaybackState.Buffering
            && _bufferingSinceMs is { } startedMs)
        {
            var elapsedMs = _clock.NowMs - startedMs;
            SessionTrace.Emit($"[起播] {DescribeChannel()} 首帧 {elapsedMs}ms"
                + $"（线路 {CurrentLineIndex + 1}/{LineCount}，第 {FailoverCount} 次换源后）");
        }
        StateChanged?.Invoke(this, next);
    }

    /// <summary>起播入口：Idle/Failed 均可发起新一轮播放。</summary>
    public void Start(PlaybackParams p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (_state != PlaybackState.Idle && _state != PlaybackState.Failed)
        {
            throw new InvalidOperationException($"Start only legal from Idle/Failed, got {_state}");
        }
        _active = true;
        _channel = p.Channel;
        _queue = p.Channel.Sources.Where(s => s.IsValid).ToList();
        AppendFallbacks();
        if (QueueOrderer is { } orderer2) _queue = orderer2(p.Channel, _queue).ToList(); // #74 健康排序：备源入队后整体重排
        _endpointIndex = _queue.FindIndex(s => s.Url == p.Endpoint.Url);
        if (_endpointIndex < 0) _endpointIndex = 0; // 未入队端点（闪退镜黄④ 11-15）：记账归 0，防 -1 越界 TryFailover
        _preferLowLatency = p.PreferLowLatency;
        Transition(PlaybackState.Buffering, "start requested");
        _player.Play(p);
        ReattachSurface();
    }

    /// <summary>
    /// 换台：一个频道一个会话的入口。顺序按类注释契约；
    /// 频道无任何合法源则抛参数异常，会话状态不变。
    /// </summary>
    public void ChangeChannel(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.IsValid)
        {
            throw new ArgumentException($"Channel {channel.Id} has no valid sources", nameof(channel));
        }
        DetachSurface();
        if (_state is PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Reconnecting)
        {
            _player.Stop();
            Transition(PlaybackState.Idle, "channel switch");
        }
        _active = true;
        _channel = channel;
        _queue = channel.Sources.Where(s => s.IsValid).ToList();
        if (_queue.Count == 0)
        {
            // 全端点无效（IsValid 只查端点数量不查有效性，闪退镜红③ 11-15）：判失败终态不越界，
            // 交壳层失败终态空态文案与重试路径；不抛索引越界崩主线程。
            Transition(PlaybackState.Failed, "no valid sources");
            return;
        }
        AppendFallbacks();
        if (QueueOrderer is { } orderer) _queue = orderer(channel, _queue).ToList(); // #74 健康排序：备源入队后整体重排，死源沉底
        _endpointIndex = 0;
        FailoverCount = 0;
        _stallTimes.Clear(); // 欠载趋势账随换台清零（老板令 #26）：账只记本台画质，不跟人走
        Transition(PlaybackState.Buffering, "change channel");
        _player.Play(new PlaybackParams(channel, _queue[0], _preferLowLatency));
        ReattachSurface();
    }

    /// <summary>
    /// 手动换线路（2.0 W8）：切到本频道指定序号的源端点（0 基），顺序契约与换台一致：
    /// 停旧流 → 状态归 Idle → 起播新线路 → 重挂 Surface。
    /// 播放中选同一路为无害操作；未起播先抛异常（先有台才有线路）。
    /// </summary>
    public void SwitchLine(int endpointIndex)
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("SwitchLine requires a started channel");
        }
        if (endpointIndex < 0 || endpointIndex >= _queue.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointIndex));
        }
        if (endpointIndex == _endpointIndex && _state == PlaybackState.Playing)
        {
            return; // 同线无操作：不打断在播画面
        }
        if (_state is PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Reconnecting)
        {
            _player.Stop();
            Transition(PlaybackState.Idle, "line switch");
        }
        _endpointIndex = endpointIndex;
        FailoverCount = 0;
        Transition(PlaybackState.Buffering, "manual line switch");
        _player.Play(new PlaybackParams(_channel, _queue[endpointIndex], _preferLowLatency));
        ReattachSurface();
    }

    /// <summary>接输出面：会话记住句柄，换台/自救重起播后自动重挂。契约见 IPlayer。</summary>
    public void AttachSurface(IntPtr surfaceHandle)
    {
        _surfaceHandle = surfaceHandle;
        _hasSurface = true;
        _player.AttachSurface(surfaceHandle);
    }

    /// <summary>摘输出面：换源/换台/退出前必调，重复调用无害。</summary>
    public void DetachSurface()
    {
        if (_hasSurface)
        {
            _player.DetachSurface();
            _hasSurface = false;
        }
    }

    /// <summary>首帧落地：Buffering 到 Playing 的唯一正门。</summary>
    public void NotifyFirstFrame()
    {
        if (_state == PlaybackState.Buffering)
        {
            Transition(PlaybackState.Playing, "first frame");
        }
    }

    /// <summary>播放中卡流：进入换台中间态，等待看门狗决策（计时按单调钟）。</summary>
    public void NotifyStalled()
    {
        if (_state == PlaybackState.Playing)
        {
            Transition(PlaybackState.Reconnecting, "watchdog stall");
            _reconnectingSinceMs = _clock.NowMs;
            RecordStall(_clock.NowMs); // 欠载趋势账（老板令 #26）：反复短卡流也是主动介入依据
        }
    }

    /// <summary>持续欠载判据（老板令 #26）：滚动窗内短卡流满此次数即预防性换线，
    /// 不等下一次卡死 15 秒——库存读数看「这一刻还剩多少货」，欠载账看「这阵子饿了几次」，两判据互补。</summary>
    public const int StallChurnThreshold = 3;

    /// <summary>持续欠载滚动窗（毫秒）：窗内卡流满 3 次即触发，超窗自动滑出。</summary>
    public static readonly TimeSpan StallChurnWindow = TimeSpan.FromMinutes(5);

    private readonly List<long> _stallTimes = new();

    private void RecordStall(long nowMs)
    {
        _stallTimes.RemoveAll(t => nowMs - t >= StallChurnWindow.TotalMilliseconds);
        _stallTimes.Add(nowMs);
    }

    /// <summary>
    /// 断流自愈（真机反馈批#5 修 2026-11-15）：Reconnecting 中内核恢复出帧（OnBufferingEnd）即回 Playing，
    /// 看门狗计时账随之清零——好画面不因历史卡流被误杀。Buffering 态（起播期）收 End 走 NotifyFirstFrame 正门，不进此路。
    /// </summary>
    private void NotifyRecovered()
    {
        if (_state == PlaybackState.Reconnecting)
        {
            Transition(PlaybackState.Playing, "stream recovered");
        }
    }

    /// <summary>
    /// 看门狗泵：缓冲超时或卡流超时即断流自救。返回是否触发了迁移。
    /// 生产由壳层定时器或管理器 Tick 驱动，测试注入 FixedClock 后手动泵。判定一律按单调钟。
    /// </summary>
    public bool CheckWatchdog()
    {
        if (_state == PlaybackState.Buffering
            && _bufferingSinceMs is { } started
            && _clock.NowMs - started >= (long)OpenTimeout.TotalMilliseconds)
        {
            // 看门狗打点（埋点补充 2026-11-15）：此前看门狗触发完全静默，真机只能看到「换源」结果看不到因——
            // 补触发原因+已等时长，断流自救链路（含误杀嫌疑对账）可查。
            SessionTrace.Emit($"[看门狗] {DescribeChannel()} 起播超时 {_clock.NowMs - started}ms≥{(int)OpenTimeout.TotalMilliseconds}ms，转备源自救（原线路 {CurrentLineIndex + 1}/{LineCount}）");
            Transition(PlaybackState.Failed, "open timeout");
            TryFailover("open timeout");
            return true;
        }
        if (_state == PlaybackState.Reconnecting
            && _reconnectingSinceMs is { } stalled
            && _clock.NowMs - stalled >= (long)OpenTimeout.TotalMilliseconds)
        {
            SessionTrace.Emit($"[看门狗] {DescribeChannel()} 卡流超时 {_clock.NowMs - stalled}ms≥{(int)OpenTimeout.TotalMilliseconds}ms，转备源自救（原线路 {CurrentLineIndex + 1}/{LineCount}）");
            Transition(PlaybackState.Failed, "stall timeout");
            TryFailover("stall timeout");
            return true;
        }
        return false;
    }

    /// <summary>预防性换源库存线（老板令 #24）：Playing 期库存跌破起播下限（BufferInventory.TargetMs=7 秒）
    /// 连续两拍即主动切备线——卡顿前动手，不等卡流 15 秒看门狗。终值随真机定标。</summary>
    public static readonly TimeSpan ProactiveInventoryFloor = TimeSpan.FromMilliseconds(BufferInventory.TargetMs);

    /// <summary>预防性换源冷却窗：切换后冷却期内不再预防性介入（防坏线连切横跳）；卡流看门狗自救不受限。</summary>
    public static readonly TimeSpan ProactiveCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 预防性介入泵（老板令 #24/#26：主动预防，不做「出了问题再处理问题」）：
    /// Playing 期两条判据，命中其一且有备线、不在冷却窗 → 预防性切下一路线：
    /// ① 库存线（#24）：内核库存（BufferedMs）跌破线（&gt;0 且 &lt;7 秒）连续两拍（间隔≥1 秒，单次网络抖动不切）；
    /// ② 欠载线（#26）：滚动窗内短卡流满 3 次——反复饿也算坏线，不等下一次卡死 15 秒。
    /// 0 读数按「无读数」不判趋势（IPlayer 契约：直播源无数≠见底）；真见底（内核进卡流态）走既有看门狗。
    /// 生产由管理器 Tick 驱动，与 CheckWatchdog 同泵；测试注入 FixedClock 后手动泵。
    /// </summary>
    public bool CheckProactiveFailover()
    {
        if (!_active || _state != PlaybackState.Playing)
        {
            _lowInventoryRounds = 0;
            return false;
        }
        if (_endpointIndex + 1 >= _queue.Count)
        {
            return false; // 没有备线，无从预防（最后一根线由看门狗兜底）
        }
        long nowMs = _clock.NowMs;
        if (_lastFailoverAtMs is { } lastFailover
            && nowMs - lastFailover < ProactiveCooldown.TotalMilliseconds)
        {
            _lowInventoryRounds = 0; // 冷却窗内只攒账不切，防坏线连切横跳
            return false;
        }

        // 判据②（老板令 #26）：滚动窗内卡流满 3 次，账内即时坐实，不需第二拍等趋势
        _stallTimes.RemoveAll(t => nowMs - t >= StallChurnWindow.TotalMilliseconds);
        bool churnTripped = _stallTimes.Count >= StallChurnThreshold;

        long bufferedMs = _player.BufferedMs;
        bool lowInventory = bufferedMs > 0 && bufferedMs < ProactiveInventoryFloor.TotalMilliseconds;
        if (!lowInventory)
        {
            _lowInventoryRounds = 0; // 库存回线：趋势账清零
        }
        else if (_lowInventoryRounds == 0 || nowMs - _lowInventoryLastAtMs >= 1000)
        {
            _lowInventoryRounds++;
            _lowInventoryLastAtMs = nowMs;
        }
        bool inventoryTripped = lowInventory && _lowInventoryRounds >= 2;
        if (!churnTripped && !inventoryTripped)
        {
            return false; // 两条判据都没坐实：单次抖动不切，等趋势
        }
        _lowInventoryRounds = 0;
        // 预防打点（老板令 #24/#26）：主动介入的账面依据——命中哪条判据、账面多少、切前是第几路线
        string basis = churnTripped
            ? $"滚动窗 {StallChurnWindow.TotalMinutes:0}min 内卡流 {_stallTimes.Count} 次（满 {StallChurnThreshold} 次）"
            : $"库存 {bufferedMs}ms 连续 2 拍低于 {(int)ProactiveInventoryFloor.TotalMilliseconds}ms";
        SessionTrace.Emit($"[预防] {DescribeChannel()} {basis}，预防性换源（原线路 {CurrentLineIndex + 1}/{LineCount}，不等卡流超时）");
        _stallTimes.Clear(); // 介入一次清一次账：新线重新起账，老账不跟去新线
        // 走既有自救口径：先归 Failed 再切线（状态机矩阵 Playing 不直跳 Buffering，与看门狗/报错同路）
        Transition(PlaybackState.Failed, "proactive switch");
        TryFailover(churnTripped ? "stall churn proactive" : "low inventory proactive");
        return true;
    }

    /// <summary>被管理器换下：停内核、摘面、状态归 Idle，此后不再响应内核事件。</summary>
    public void Supersede()
    {
        _active = false;
        DetachSurface();
        if (_state is PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Reconnecting)
        {
            _player.Stop();
            Transition(PlaybackState.Idle, "superseded");
        }
    }

    /// <summary>
    /// 释放内核（独享内核场景下管理器 LRU 淘汰会话用）：仅限已让位（Superseded）会话，
    /// 释放后本会话不可再起播。在播/在备会话调用抛异常（资源闸不误杀热备）。
    /// </summary>
    public void ReleaseKernel()
    {
        if (_active)
        {
            throw new InvalidOperationException("ReleaseKernel only legal on superseded sessions");
        }
        _player.Dispose();
    }

    private void OnPlayerError(string code)
    {
        if (!_active)
        {
            return;
        }
        if (_state is not (PlaybackState.Buffering or PlaybackState.Playing or PlaybackState.Reconnecting))
        {
            return;
        }
        Transition(PlaybackState.Failed, $"player error {code}");
        // 换源账随真实码（真机诊断 11-15）：诊断页换源行能看到具体错误码，不再只剩裸词
        TryFailover($"player error {code}");
    }

    private void TryFailover(string reason)
    {
        var next = _endpointIndex + 1;
        if (_channel is null || next >= _queue.Count)
        {
            AllSourcesFailed?.Invoke(this, new AllSourcesFailedEventArgs(_channel?.Id ?? string.Empty, reason));
            return;
        }
        var from = _queue[_endpointIndex];
        _endpointIndex = next;
        FailoverCount++;
        _lastFailoverAtMs = _clock.NowMs; // 预防性介入冷却账起算（老板令 #24）
        Transition(PlaybackState.Buffering, $"failover after {reason}");
        _player.Play(new PlaybackParams(_channel, _queue[next], _preferLowLatency));
        ReattachSurface();
        Failover?.Invoke(this, new FailoverEventArgs(from, _queue[next], FailoverCount, reason));
    }

    private void ReattachSurface()
    {
        if (_hasSurface)
        {
            _player.AttachSurface(_surfaceHandle);
        }
    }

    /// <summary>台账频道名（埋点补充 2026-11-15）：名称优先，无名退 Id，从未起播记「未知频道」。</summary>
    private string DescribeChannel() =>
        _channel?.Name ?? _channel?.Id ?? "未知频道";

    /// <summary>
    /// 备源队列吃内置表（方案书收口批①）：换源触发时才遍历注册表（不进预检常驻轮），
    /// 本台源在前、注册表纪律序（观察档垫底/纯 IP 优先/登记序=2026-11-12 实测活性基线）在后；
    /// 同址去重，本台已有的源不重复入队。注册表 Referer/UA 在 2.0 端点模型无承接面（内核不带每源头），仅收录 URL。
    /// </summary>
    private void AppendFallbacks()
    {
        if (_channel is null || FallbackSourceProvider is not { } provider)
        {
            return;
        }
        var seen = new HashSet<string>(_queue.Select(s => s.Url), StringComparer.Ordinal);
        foreach (var candidate in provider(_channel))
        {
            if (candidate.IsValid && seen.Add(candidate.Url))
            {
                _queue.Add(candidate);
            }
        }
    }
}

/// <summary>时钟抽象：测试注入固定时钟，生产用系统时钟。NowMs 为单调毫秒（计时账铁律）。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>单调时钟毫秒读数：只前进不回跳，供运行期计时账使用（开机校时跳变不影响）。</summary>
    long NowMs { get; }
}

/// <summary>断流自救切换事件参数。</summary>
public sealed class FailoverEventArgs(
    SourceEndpoint from,
    SourceEndpoint to,
    int attempt,
    string reason)
    : EventArgs
{
    public SourceEndpoint From { get; } = from;
    public SourceEndpoint To { get; } = to;
    public int Attempt { get; } = attempt;
    public string Reason { get; } = reason;
}

/// <summary>备源耗尽事件参数。</summary>
public sealed class AllSourcesFailedEventArgs(string channelId, string reason) : EventArgs
{
    public string ChannelId { get; } = channelId;
    public string Reason { get; } = reason;
}
