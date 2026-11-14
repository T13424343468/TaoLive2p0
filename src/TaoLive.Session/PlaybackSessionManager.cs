using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 会话管理器：会话表（一个频道一个会话）、当前会话切换、事件回主线程。
/// 换台时旧会话被 Supersede（停内核、状态归 Idle、不再响应内核事件），
/// 新会话按频道 Id 从会话表复用，同频道切回即原地重起播。
/// M3 升级 Current+Standby 双实例热切（方案书 v5 已定，到时主界面只收事件不掺内部）。
/// </summary>
public sealed class PlaybackSessionManager
{
    private readonly Func<PlaybackSession> _sessionFactory;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Dictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);

    public PlaybackSessionManager(Func<PlaybackSession> sessionFactory, IMainThreadDispatcher? dispatcher = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _dispatcher = dispatcher ?? new ImmediateMainThreadDispatcher();
    }

    /// <summary>当前前台会话；尚未切换过为空。</summary>
    public PlaybackSession? Current { get; private set; }

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

    /// <summary>切到指定频道：旧会话让位，目标会话从会话表复用或新建，然后起播。</summary>
    public PlaybackSession SwitchTo(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (Current is { } prev
            && prev.CurrentChannel is { } prevChannel
            && !string.Equals(prevChannel.Id, channel.Id, StringComparison.Ordinal))
        {
            prev.Supersede();
        }
        if (!_sessions.TryGetValue(channel.Id, out var session))
        {
            session = _sessionFactory();
            _sessions[channel.Id] = session;
            WireSession(session);
        }
        session.ChangeChannel(channel);
        Current = session;
        RaiseCurrentChanged(session);
        return session;
    }

    /// <summary>按频道 Id 查会话；查无返回空。</summary>
    public PlaybackSession? TryGet(string channelId) =>
        _sessions.TryGetValue(channelId, out var session) ? session : null;

    /// <summary>看门狗泵：由壳层定时器周期调用，仅作用于当前会话。</summary>
    public void Tick() => Current?.CheckWatchdog();

    private void WireSession(PlaybackSession session)
    {
        session.StateChanged += (_, state) =>
            _dispatcher.Post(() => SessionStateChanged?.Invoke(this, new SessionStateEventArgs(session, state)));
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
