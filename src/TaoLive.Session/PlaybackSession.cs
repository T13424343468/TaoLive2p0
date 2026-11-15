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

    public PlaybackSession(IPlayer player, IClock clock)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _player.OnError += (_, e) => OnPlayerError(e.Code);
        _player.OnFirstFrame += (_, _) => NotifyFirstFrame();
        // 卡流自救接线（方案书 v99 四镜·卡顿镜 11-15 修）：内核缓冲事件接会话，播放中卡流进 Reconnecting 走看门狗。
        // NotifyStalled 内部有 Playing 态守卫，起播期缓冲不误触发。
        _player.OnBufferingStart += (_, _) => NotifyStalled();
    }

    /// <summary>起播超时（看门狗阈值）：缓冲超过此时长即视为断流，走备源切换。</summary>
    public static TimeSpan DefaultOpenTimeout { get; } = TimeSpan.FromSeconds(15);

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
        _state = next;
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
        _endpointIndex = _queue.FindIndex(s => s.Url == p.Endpoint.Url);
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
        AppendFallbacks();
        _endpointIndex = 0;
        FailoverCount = 0;
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
            Transition(PlaybackState.Failed, "open timeout");
            TryFailover("open timeout");
            return true;
        }
        if (_state == PlaybackState.Reconnecting
            && _reconnectingSinceMs is { } stalled
            && _clock.NowMs - stalled >= (long)OpenTimeout.TotalMilliseconds)
        {
            Transition(PlaybackState.Failed, "stall timeout");
            TryFailover("stall timeout");
            return true;
        }
        return false;
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
        TryFailover("player error");
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
