using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 备源自救扩容（W7 测试班）：多源失败→全耗尽→重试语义、Failover 事件参数、
/// 看门狗与内核错误竞态、非法源过滤。固定时钟快拍不真等。
/// </summary>
public class SessionFailoverTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    [Fact]
    public void Failover_exhaustion_then_manual_retry_restarts_from_first_endpoint()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        player.RaiseError("E1");
        player.RaiseError("E2");
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.Equal(1, session.FailoverCount);

        // 耗尽后壳层 OK 重试：Failed → Buffering 从首端点重新起播，计数清零
        var chRetry = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(chRetry);
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal(0, session.FailoverCount);
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Failover_event_carries_from_to_attempt_and_reason()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
        session.ChangeChannel(ch);

        FailoverEventArgs? evt = null;
        session.Failover += (_, e) => evt = e;
        player.RaiseError("NET-1");

        Assert.NotNull(evt);
        Assert.Equal("https://s/a.m3u8", evt!.From.Url);
        Assert.Equal("https://s/b.m3u8", evt.To.Url);
        Assert.Equal(1, evt.Attempt);
        Assert.Contains("player error", evt.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_timeout_on_last_endpoint_fires_all_failed_with_reason()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock) { OpenTimeout = TimeSpan.FromSeconds(5) };
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        AllSourcesFailedEventArgs? args = null;
        session.AllSourcesFailed += (_, e) => args = e;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(session.CheckWatchdog()); // 第一路 open 超时 → 切第二路
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(session.CheckWatchdog()); // 第二路也超时 → 全耗尽
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.NotNull(args);
        Assert.Equal("cctv1", args!.ChannelId);
        Assert.Contains("open timeout", args.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchdog_does_not_fire_after_first_frame_success()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(session.CheckWatchdog()); // Playing 态看门狗不动
        Assert.Equal(PlaybackState.Playing, session.State);
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Late_error_after_exhaustion_is_ignored_until_retry()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        session.ChangeChannel(ch);
        player.RaiseError("E1");
        Assert.Equal(PlaybackState.Failed, session.State);

        player.RaiseError("E-LATE"); // Failed 态不再响应内核错误（终态语义）
        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.Single(player.PlayedUrls);
    }

    [Fact]
    public void Change_channel_during_reconnecting_succeeds()
    {
        // BUG-1 已修（2026-11-15）：Transition 矩阵补 (Reconnecting → Idle)，
        // 卡流中间态换台不再抛——壳层上下键打在 Reconnecting 窗口安全。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/a2.m3u8");
        var ch2 = MakeChannel("cctv5", "https://s/b.m3u8");
        session.ChangeChannel(ch1);
        player.RaiseFirstFrame();
        session.NotifyStalled();
        Assert.Equal(PlaybackState.Reconnecting, session.State);

        session.ChangeChannel(ch2); // 不抛，正常走换台次序
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal(ch2, session.CurrentChannel);
    }

    [Fact]
    public void Failover_skips_invalid_endpoints_and_keeps_order()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var sources = new[]
        {
            new SourceEndpoint("https://s/a.m3u8", SourceKind.Cdn, 3),
            new SourceEndpoint("not-a-url", SourceKind.Cdn, 2),     // 非法源应被过滤
            new SourceEndpoint("https://s/b.m3u8", SourceKind.Cdn, 1),
        };
        var ch = new Channel("cctv1", "CCTV-1", "测试", sources);
        session.ChangeChannel(ch);

        player.RaiseError("E1");
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(new[] { "https://s/a.m3u8", "https://s/b.m3u8" }, player.PlayedUrls);
    }

    [Fact]
    public void Failover_reattaches_surface_after_switch()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.AttachSurface(new IntPtr(7));

        int attachBefore = player.Log.Count(e => e == "Attach");
        player.RaiseError("E1");
        int attachAfter = player.Log.Count(e => e == "Attach");
        Assert.True(attachAfter > attachBefore, "备源切换后必须重挂 Surface");
    }
}

/// <summary>管理器扩容：会话表复用语义、Tick 只泵当前、事件参数完整性。</summary>
public class SessionManagerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    [Fact]
    public void TryGet_returns_null_for_unknown_channel()
    {
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), new FixedClock(T0)));
        Assert.Null(manager.TryGet("cctv-unknown"));
        Assert.Null(manager.Current);
    }

    [Fact]
    public void Same_channel_switch_during_reconnecting_succeeds()
    {
        // BUG-1 已修：Reconnecting 中同频道切回（原地重起播）不再抛，复用会话不新建。
        var clock = new FixedClock(T0);
        var players = new List<LoggingPlayer>();
        var manager = new PlaybackSessionManager(() =>
        {
            var p = new LoggingPlayer();
            players.Add(p);
            return new PlaybackSession(p, clock);
        });
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");

        var first = manager.SwitchTo(ch);
        first.AttachSurface(new IntPtr(1));
        manager.TryGet("cctv1")!.NotifyFirstFrame();
        manager.TryGet("cctv1")!.NotifyStalled();

        var reused = manager.SwitchTo(ch); // 不抛，复用会话原地重起播
        Assert.Same(first, reused);
        Assert.Single(players); // 不新建会话
    }

    [Fact]
    public void Tick_without_current_session_is_noop()
    {
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), new FixedClock(T0)));
        manager.Tick(); // 不抛异常即合格
        Assert.Null(manager.Current);
    }

    [Fact]
    public void State_changed_event_carries_session_and_state()
    {
        var clock = new FixedClock(T0);
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), clock),
            new ImmediateMainThreadDispatcher());
        var events = new List<(PlaybackSession Session, PlaybackState State)>();
        manager.SessionStateChanged += (_, e) => events.Add((e.Session, e.State));

        var session = manager.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        manager.TryGet("cctv1")!.NotifyFirstFrame();

        Assert.Equal(new[] { PlaybackState.Buffering, PlaybackState.Playing },
            events.Select(e => e.State).ToList());
        Assert.All(events, e => Assert.Same(session, e.Session));
    }
}
