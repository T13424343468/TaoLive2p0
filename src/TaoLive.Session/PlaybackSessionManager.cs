using TaoLive.Foundation;

namespace TaoLive.Session;

/// <summary>
/// 会话管理器。M1：单实例；M3 升级 Current+Standby 双实例热切（方案书 v5 已定，
/// 到时主界面只收事件不掺内部）。本班只立骨架与事件面。
/// </summary>
public sealed class PlaybackSessionManager
{
    private readonly Func<PlaybackSession> _sessionFactory;

    public PlaybackSessionManager(Func<PlaybackSession> sessionFactory)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public PlaybackSession? Current { get; private set; }

    public PlaybackSession Begin()
    {
        Current = _sessionFactory();
        return Current;
    }
}
