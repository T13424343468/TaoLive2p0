using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 会话管理器：会话表（一个频道一个会话）、当前会话切换、事件回主线程。
/// 换台时旧会话被 Supersede（停内核、状态归 Idle、不再响应内核事件），
/// 新会话按频道 Id 从会话表复用，同频道切回即原地重起播。
/// M3 升级 Current+Standby 双实例热切（方案书 v5：主界面只收事件不掺内部）：
/// - PrepareStandby 预先把目标台会话攒流（无面起播，预取水位线内后头填）；
/// - SwitchTo 时若热备已达 Playing（起播门槛线已过），同步换面接管——
///   新画面直接覆盖旧画面，旧会话随后让位，主界面无感、不露黑；
/// - 无热备或热备未就绪时回落既有会话表复用路径（冷切），W1 契约不变。
/// 每会话独享内核实例（kernelFactory 注入；不注入则共用单内核=只走冷切）。
/// 会话表按 LRU 封顶（MaxRetainedSessions），淘汰已让位会话并释放内核，
/// 热备资源闸（方案书：内存 ≤30MB 初值）不被历史频道内核累积击穿。
/// </summary>
public sealed class PlaybackSessionManager
{
    /// <summary>会话表容量上限（LRU 淘汰已让位会话；Current/Standby 永不淘汰）。收口批③后=高配档默认值，实际容量经 RetainedCapacity 分档。</summary>
    public const int MaxRetainedSessions = 8;

    /// <summary>回主源稳定窗口（方案书收口批①·迟滞判据初值）：断流自救后播放无断流满此时长才回；终值随真机定标。</summary>
    public static readonly TimeSpan ReturnToPrimaryStableFor = TimeSpan.FromMinutes(5);

    /// <summary>回主源健康账门槛：主源预检轮次连续命中 OK 至少此次数（复用预检结果，不另起独立探测）。</summary>
    public const int ReturnToPrimaryMinConsecutiveOks = 3;

    /// <summary>会话表容量（收口批③·LRU 按内存分档）：高配 8（兼作既有常量默认）；低配 4。</summary>
    public const int HighMemorySessions = 8;

    /// <summary>低配档容量：低配 4-5 取下限（低配档内单会话预算更紧），终值随真机定标。</summary>
    public const int LowMemorySessions = 4;

    /// <summary>分档界线初值：memoryClass ≤128MB 低配档、&gt;128MB 高配档；定标后只调常量不改逻辑。</summary>
    public const int LowMemoryClassThresholdMb = 128;

    /// <summary>按设备内存分档（方案书收口批③）：界线改动只动常量。</summary>
    public static int CapacityFor(int memoryClassMb) =>
        memoryClassMb <= LowMemoryClassThresholdMb ? LowMemorySessions : HighMemorySessions;

    private readonly Func<PlaybackSession> _sessionFactory;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Action<Channel>? _onSwitchingChannel;
    private readonly bool _hotStandbyEnabled;
    private readonly Dictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastUsed = new(StringComparer.Ordinal);
    private readonly object _returnGate = new();
    private readonly Func<long> _monotonicMs;
    private readonly RetryBackoffAccount _retryBackoff;
    private Func<Channel, IReadOnlyList<SourceEndpoint>>? _fallbackProvider;
    private Func<Channel, bool>? _primaryAliveConfirmed;
    private PlaybackSession? _returnStableSession;
    private long _returnStableSinceMs;
    private long _stamp;

    public PlaybackSessionManager(Func<PlaybackSession> sessionFactory, IMainThreadDispatcher? dispatcher = null,
        Action<Channel>? onSwitchingChannel = null, bool hotStandbyEnabled = false,
        Func<Channel, bool>? primaryAliveConfirmed = null, Func<long>? monotonicMs = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _dispatcher = dispatcher ?? new ImmediateMainThreadDispatcher();
        _onSwitchingChannel = onSwitchingChannel;
        _hotStandbyEnabled = hotStandbyEnabled;
        _primaryAliveConfirmed = primaryAliveConfirmed;
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64); // 单调时钟（方案书 v12：计时账一律按单调钟）
        _retryBackoff = new RetryBackoffAccount(_monotonicMs);
    }

    /// <summary>会话表容量上限（收口批③·LRU 按内存分档）：壳层按设备 memoryClass 经 CapacityFor 设定。</summary>
    public int RetainedCapacity { get; set; } = HighMemorySessions;

    /// <summary>
    /// OK 重试接退避账（收口批③）：Failed 态手动重试放行判定；退避窗未满返回 false 并带出
    /// 还须等待的毫秒数（壳层提示节奏）。播放成功起播即清账（StateChanged Playing）。
    /// </summary>
    public bool TryManualRetry(string channelId, out long retryAfterMs) =>
        _retryBackoff.TryAcquire(channelId ?? string.Empty, out retryAfterMs);

    /// <summary>
    /// 备源队列吃内置表（方案书收口批①）：新会话与既有会话统一挂供给；未设=不吃内置表。
    /// </summary>
    public Func<Channel, IReadOnlyList<SourceEndpoint>>? FallbackSourceProvider
    {
        get => _fallbackProvider;
        set
        {
            _fallbackProvider = value;
            foreach (var session in _sessions.Values)
            {
                session.FallbackSourceProvider = value;
            }
        }
    }

    /// <summary>
    /// 回主源健康账判据（方案书收口批①）：主源预检轮次连续命中 OK ≥3 次才准回；未设=不回主源（从严）。
    /// </summary>
    public Func<Channel, bool>? PrimaryAliveConfirmed
    {
        get => _primaryAliveConfirmed;
        set => _primaryAliveConfirmed = value;
    }

    /// <summary>当前前台会话；尚未切换过为空。</summary>
    public PlaybackSession? Current { get; private set; }

    /// <summary>热备会话（PrepareStandby 攒流中的目标台）；未备为空。</summary>
    public PlaybackSession? Standby { get; private set; }

    /// <summary>会话表快照：频道 Id 到会话的映射，只读。</summary>
    public IReadOnlyDictionary<string, PlaybackSession> Sessions => _sessions;

    /// <summary>当前会话切换完成事件：保证经主线程调度器触发。</summary>
    public event EventHandler<PlaybackSession>? CurrentChanged;

    /// <summary>任一会话状态变化事件：保证经主线程调度器触发。</summary>
    public event EventHandler<SessionStateEventArgs>? SessionStateChanged;

    /// <summary>裸建一个会话并置为当前（保留 W1 语义，供壳层自定义起播）。</summary>
    public PlaybackSession Begin()
    {
        var session = _sessionFactory();
        WireSession(session);
        Current = session;
        RaiseCurrentChanged(session);
        return session;
    }

    /// <summary>
    /// 热备请求（换台竞速思路·vc53）：为指定频道提前备一个可接管会话。
    /// 同台重复请求、目标即当前台均无害（返回既有会话不重播）；
    /// 换备目标时旧热备先行让位（停内核，不白烧网络）。
    /// 新会话无面起播攒流（ExoPlayer 无面照样缓冲），到 Playing 即具备接管条件。
    /// 未开热备（共用单内核）时返回空。
    /// </summary>
    public PlaybackSession? PrepareStandby(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!_hotStandbyEnabled || !channel.IsValid) return null;
        if (Current is { } cur && string.Equals(cur.CurrentChannel?.Id, channel.Id, StringComparison.Ordinal))
        {
            return cur; // 就是当前台，无需备
        }
        if (Standby is { } sb && string.Equals(sb.CurrentChannel?.Id, channel.Id, StringComparison.Ordinal))
        {
            return sb; // 已在备
        }
        // 旧热备让位：换备目标即停旧攒流（内核独享实例，不停则白烧网络）
        if (Standby is { } oldSb
            && !string.Equals(oldSb.CurrentChannel?.Id, channel.Id, StringComparison.Ordinal)
            && !ReferenceEquals(oldSb, Current))
        {
            oldSb.Supersede();
        }
        if (_sessions.TryGetValue(channel.Id, out var existing))
        {
            Touch(channel.Id);
            existing.ChangeChannel(channel);
            Standby = existing;
            return existing;
        }
        var session = _sessionFactory();
        _sessions[channel.Id] = session;
        _lastUsed[channel.Id] = _stamp++;
        WireSession(session);
        EvictIfOverCapacity();
        session.ChangeChannel(channel); // 无面起播：攒流到门槛线，不接管画面
        Standby = session;
        return session;
    }

    /// <summary>切到指定频道：热备就绪走热切（换面接管），否则旧会话让位冷切。</summary>
    public PlaybackSession SwitchTo(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _onSwitchingChannel?.Invoke(channel); // 换台即触发邻台预取（ChannelPrefetcher，单车道让路口径）

        // 热切：热备就是目标台且已达 Playing（起播门槛线已过）→ 换面接管
        if (Current is { } cur
            && Standby is { } sb
            && string.Equals(sb.CurrentChannel?.Id, channel.Id, StringComparison.Ordinal)
            && sb.State == PlaybackState.Playing)
        {
            // 新画面先挂面覆盖旧画面（同面后画覆盖先画，旧画面撑到让位不露黑）
            sb.AttachSurface(cur.SurfaceHandle);
            cur.Supersede();
            Current = sb;
            Standby = null;
            Touch(channel.Id);
            RaiseCurrentChanged(sb);
            return sb;
        }

        // 冷切（W1 契约路径）：旧会话让位，目标会话从会话表复用或新建，然后起播。
        var prev = Current;
        if (prev is { } p
            && p.CurrentChannel is { } prevChannel
            && !string.Equals(prevChannel.Id, channel.Id, StringComparison.Ordinal))
        {
            p.Supersede();
        }
        if (ReferenceEquals(Standby, prev)) Standby = null;
        if (!_sessions.TryGetValue(channel.Id, out var session))
        {
            session = _sessionFactory();
            _sessions[channel.Id] = session;
            _lastUsed[channel.Id] = _stamp++;
            WireSession(session);
        }
        if (ReferenceEquals(Standby, session)) Standby = null;
        Touch(channel.Id);
        session.ChangeChannel(channel);
        Current = session;
        EvictIfOverCapacity();
        RaiseCurrentChanged(session);
        return session;
    }

    /// <summary>按频道 Id 查会话；查无返回空。</summary>
    public PlaybackSession? TryGet(string channelId) =>
        _sessions.TryGetValue(channelId, out var session) ? session : null;

    /// <summary>看门狗泵：由壳层定时器周期调用，仅作用于当前会话；顺带跑回主源迟滞判据。</summary>
    public void Tick()
    {
        var current = Current;
        if (current is null)
        {
            lock (_returnGate) _returnStableSession = null;
            return;
        }
        current.CheckWatchdog();
        TryReturnToPrimary(current);
    }

    /// <summary>
    /// 稳定后回主源（方案书收口批①·迟滞判据）：断流自救切到备源后——
    /// 播放无断流满 5 分钟（回后若再断重新计时）且主源预检轮次连续命中 OK ≥3 次，
    /// 仅当前台前台稳定时触发，热备攒流中不触发（Tick 只泵 Current，防主备横跳）。
    /// </summary>
    private void TryReturnToPrimary(PlaybackSession current)
    {
        if (current.CurrentLineIndex <= 0 || current.State != PlaybackState.Playing)
        {
            return;
        }
        lock (_returnGate)
        {
            if (!ReferenceEquals(_returnStableSession, current)
                || _monotonicMs() - _returnStableSinceMs < ReturnToPrimaryStableFor.TotalMilliseconds)
            {
                return;
            }
            var channel = current.CurrentChannel;
            if (channel is null || _primaryAliveConfirmed?.Invoke(channel) != true)
            {
                return;
            }
            _returnStableSession = null; // 回后若再断由 Failover 事件重新起窗计时
        }
        current.SwitchLine(0);
    }

    private void Touch(string channelId)
    {
        if (_lastUsed.ContainsKey(channelId)) _lastUsed[channelId] = _stamp++;
    }

    /// <summary>资源闸：超容即淘汰最久未用的已让位会话并释放内核；Current/Standby 永不淘汰。</summary>
    private void EvictIfOverCapacity()
    {
        while (_sessions.Count > RetainedCapacity)
        {
            string? victimId = null;
            long oldest = long.MaxValue;
            foreach (var (id, s) in _sessions)
            {
                if (ReferenceEquals(s, Current) || ReferenceEquals(s, Standby)) continue;
                long used = _lastUsed.GetValueOrDefault(id, 0);
                if (used < oldest)
                {
                    oldest = used;
                    victimId = id;
                }
            }
            if (victimId is null) break; // 全被保护：宁可超容，不误杀热备
            var victim = _sessions[victimId];
            _sessions.Remove(victimId);
            _lastUsed.Remove(victimId);
            try { victim.ReleaseKernel(); }
            catch { /* 淘汰释放失败不拖垮会话表：内核随进程回收 */ }
        }
    }

    private void WireSession(PlaybackSession session)
    {
        session.FallbackSourceProvider = _fallbackProvider;
        session.Failover += (s, _) =>
        {
            // 回主源计时账（方案书 v12 单调钟）：每次断流自救切换重新起窗（回后若再断重新计时）
            if (ReferenceEquals(s, Current))
            {
                lock (_returnGate)
                {
                    _returnStableSession = null; // 先归 Idle/Buffering，等 Playing 再起窗
                    _returnStableSinceMs = _monotonicMs();
                }
            }
        };
        session.StateChanged += (_, state) =>
        {
            TrackReturnStability(session, state);
            if (state == PlaybackState.Playing && session.CurrentChannel is { } playingChannel)
            {
                _retryBackoff.Clear(playingChannel.Id); // 播放成功起播即清退避账（共账不共判据：换源断路器另有判据）
            }
            _dispatcher.Post(() => SessionStateChanged?.Invoke(this, new SessionStateEventArgs(session, state)));
        };
    }

    /// <summary>回主源稳定窗：当前台切回 Playing 起窗，离开 Playing（断流/换台）即重新计时。</summary>
    private void TrackReturnStability(PlaybackSession session, PlaybackState state)
    {
        lock (_returnGate)
        {
            if (state == PlaybackState.Playing
                && ReferenceEquals(session, Current)
                && session.CurrentLineIndex > 0)
            {
                _returnStableSession = session;
                _returnStableSinceMs = _monotonicMs();
            }
            else if (_returnStableSession is { } armed
                     && (state != PlaybackState.Playing || !ReferenceEquals(session, Current)))
            {
                _returnStableSession = null;
            }
        }
    }

    private void RaiseCurrentChanged(PlaybackSession session) =>
        _dispatcher.Post(() => CurrentChanged?.Invoke(this, session));
}

/// <summary>会话状态变化事件参数：携带会话与迁移后的状态。</summary>
public sealed class SessionStateEventArgs(PlaybackSession session, PlaybackState state) : EventArgs
{
    public PlaybackSession Session { get; } = session;
    public PlaybackState State { get; } = state;
}
