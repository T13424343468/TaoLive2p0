using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// W3 会话层全量用例：状态迁移矩阵、换台顺序、断流备源自救、
/// 起播看门狗、多会话切换与主线程事件面。固定时钟快拍不真等。
/// </summary>
public class SessionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T00:00:00Z");

    private static Channel MakeChannel(string id, params string[] urls)
    {
        var sources = urls.Select((u, i) =>
            new SourceEndpoint(u, i == 0 ? SourceKind.PureIp : SourceKind.Cdn, 10 - i)).ToList();
        return new Channel(id, $"频道-{id}", "测试", sources);
    }

    [Fact]
    public void Fresh_session_change_channel_plays_first_endpoint()
    {
        var session = new PlaybackSession(new LoggingPlayer(), new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/a.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(T0, session.StartedAtUtc);
        Assert.Equal(0, session.FailoverCount);
    }

    [Fact]
    public void Channel_switch_follows_detach_stop_play_order()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.AttachSurface(new IntPtr(1));
        session.NotifyFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);

        session.ChangeChannel(ch);
        Assert.Equal(PlaybackState.Buffering, session.State);
        var log = player.Log;
        var detach = log.IndexOf("Detach");
        var stop = log.IndexOf("Stop", detach);
        var play = stop >= 0
            ? log.FindIndex(stop + 1, e => e.StartsWith("Play:", StringComparison.Ordinal))
            : -1;
        Assert.True(detach >= 0 && stop >= 0 && play >= 0, $"detach/stop/play order broken: {string.Join(",", log)}");
    }

    [Fact]
    public void Player_error_fails_over_to_next_source_in_order()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8");
        session.ChangeChannel(ch);

        player.RaiseError("NET-1");
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(1, session.FailoverCount);

        player.RaiseError("NET-2");
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/c.m3u8", session.CurrentEndpoint?.Url);
        Assert.Equal(2, session.FailoverCount);
        Assert.Equal(new[] { "https://s/a.m3u8", "https://s/b.m3u8", "https://s/c.m3u8" }, player.PlayedUrls);
    }

    [Fact]
    public void Exhausted_sources_fires_all_failed_and_stays_failed()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8");
        session.ChangeChannel(ch);

        AllSourcesFailedEventArgs? args = null;
        session.AllSourcesFailed += (_, e) => args = e;
        player.RaiseError("NET-1");

        Assert.Equal(PlaybackState.Failed, session.State);
        Assert.NotNull(args);
        Assert.Equal("cctv1", args!.ChannelId);
        Assert.Equal(0, session.FailoverCount);
    }

    [Fact]
    public void Watchdog_open_timeout_switches_source_without_crash()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock) { OpenTimeout = TimeSpan.FromSeconds(15) };
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);

        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.False(session.CheckWatchdog());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(session.CheckWatchdog());
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Watchdog_stall_timeout_fails_over_after_reconnecting()
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseFirstFrame();
        session.NotifyStalled();
        Assert.Equal(PlaybackState.Reconnecting, session.State);

        Assert.False(session.CheckWatchdog());
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.True(session.CheckWatchdog());
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Kernel_buffering_event_wires_stall_selfheal()
    {
        // 卡流自救接线（四镜·卡顿镜 11-15 修）：内核 OnBufferingStart 在 Playing 态自动进 Reconnecting，
        // 看门狗超时后走备源自救；起播期（Buffering 态）缓冲事件不误触发。
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        player.RaiseBufferingStart();
        Assert.Equal(PlaybackState.Buffering, session.State);

        player.RaiseFirstFrame();
        Assert.Equal(PlaybackState.Playing, session.State);
        player.RaiseBufferingStart();
        Assert.Equal(PlaybackState.Reconnecting, session.State);

        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.True(session.CheckWatchdog());
        Assert.Equal(PlaybackState.Buffering, session.State);
        Assert.Equal("https://s/b.m3u8", session.CurrentEndpoint?.Url);
    }

    [Fact]
    public void Superseded_session_stops_responding_to_player_errors()
    {
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, new FixedClock(T0));
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");
        session.ChangeChannel(ch);
        session.Supersede();
        Assert.Equal(PlaybackState.Idle, session.State);

        player.RaiseError("NET-LATE");
        Assert.Equal(PlaybackState.Idle, session.State);
        Assert.Single(player.PlayedUrls);
    }

    [Theory]
    [InlineData(PlaybackState.Idle, PlaybackState.Playing)]
    [InlineData(PlaybackState.Failed, PlaybackState.Playing)]
    // (Reconnecting → Idle) 自 BUG-1 修复起为合法迁移，移出非法表（SessionFailoverTests 正向覆盖）
    [InlineData(PlaybackState.Playing, PlaybackState.Buffering)]
    public void Illegal_transitions_throw(PlaybackState jumpFrom, PlaybackState jumpTo)
    {
        var clock = new FixedClock(T0);
        var player = new LoggingPlayer();
        var session = new PlaybackSession(player, clock);
        var ch = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/b.m3u8");

        switch (jumpFrom)
        {
            case PlaybackState.Idle:
                break;
            case PlaybackState.Failed:
                session.Transition(PlaybackState.Failed, "test");
                break;
            case PlaybackState.Playing:
                session.ChangeChannel(ch);
                player.RaiseFirstFrame();
                break;
            case PlaybackState.Reconnecting:
                session.ChangeChannel(ch);
                player.RaiseFirstFrame();
                session.NotifyStalled();
                break;
        }

        Assert.Throws<InvalidOperationException>(() => session.Transition(jumpTo, "test"));
    }

    [Fact]
    public void Manager_switch_supersedes_previous_and_reuses_table_session()
    {
        var clock = new FixedClock(T0);
        var players = new List<LoggingPlayer>();
        var manager = new PlaybackSessionManager(() =>
        {
            var p = new LoggingPlayer();
            players.Add(p);
            return new PlaybackSession(p, clock);
        });
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/a2.m3u8");
        var ch2 = MakeChannel("cctv5", "https://s/b.m3u8", "https://s/b2.m3u8");

        var first = manager.SwitchTo(ch1);
        Assert.Equal(PlaybackState.Buffering, first.State);
        Assert.Same(first, manager.Current);

        var second = manager.SwitchTo(ch2);
        Assert.Equal(PlaybackState.Idle, first.State);
        Assert.Equal(PlaybackState.Buffering, second.State);
        Assert.Same(second, manager.Current);

        var back = manager.SwitchTo(ch1);
        Assert.Same(first, back);
        Assert.Equal(PlaybackState.Buffering, back.State);
        Assert.Equal(2, manager.Sessions.Count);
        Assert.Collection(players, _ => { }, _ => { });
    }

    [Fact]
    public void Manager_events_arrive_via_dispatcher_in_order()
    {
        var clock = new FixedClock(T0);
        var manager = new PlaybackSessionManager(
            () => new PlaybackSession(new LoggingPlayer(), clock),
            new ImmediateMainThreadDispatcher());
        var states = new List<PlaybackState>();
        var switched = new List<PlaybackSession>();
        manager.SessionStateChanged += (_, e) => states.Add(e.State);
        manager.CurrentChanged += (_, s) => switched.Add(s);

        manager.SwitchTo(MakeChannel("cctv1", "https://s/a.m3u8"));
        Assert.Equal(new[] { PlaybackState.Buffering }, states);
        Assert.Single(switched);
    }

    [Fact]
    public void Manager_tick_pumps_current_session_watchdog_only()
    {
        var clock = new FixedClock(T0);
        var players = new List<LoggingPlayer>();
        var manager = new PlaybackSessionManager(() =>
        {
            var p = new LoggingPlayer();
            players.Add(p);
            return new PlaybackSession(p, clock);
        });
        var ch1 = MakeChannel("cctv1", "https://s/a.m3u8", "https://s/a2.m3u8");
        var ch2 = MakeChannel("cctv5", "https://s/b.m3u8", "https://s/b2.m3u8");
        manager.SwitchTo(ch1);
        manager.SwitchTo(ch2);

        clock.Advance(TimeSpan.FromSeconds(30));
        manager.Tick();
        Assert.Equal(PlaybackState.Buffering, manager.Current!.State);
        Assert.Equal("https://s/b2.m3u8", manager.Current.CurrentEndpoint?.Url);
        Assert.Equal(PlaybackState.Idle, manager.TryGet("cctv1")!.State);
    }
}

/// <summary>记录型内核桩：把指令顺序落日志，供顺序断言；事件可手动触发。</summary>
internal sealed class LoggingPlayer : IPlayer
{
    public List<string> Log { get; } = new();
    public List<string> PlayedUrls { get; } = new();

    public event EventHandler<PlayerFirstFrameEventArgs>? OnFirstFrame;
    public event EventHandler<PlayerFrameTickEventArgs>? OnFrameTick;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingStart;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingEnd;
    public event EventHandler<PlayerErrorEventArgs>? OnError;

    internal void RaiseFirstFrame() =>
        OnFirstFrame?.Invoke(this, new PlayerFirstFrameEventArgs { AtUtc = DateTimeOffset.UtcNow });
    internal void RaiseFrameTick() =>
        OnFrameTick?.Invoke(this, new PlayerFrameTickEventArgs { AtUtc = DateTimeOffset.UtcNow });

    internal void RaiseBufferingStart() =>
        OnBufferingStart?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });

    internal void RaiseBufferingEnd() =>
        OnBufferingEnd?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });

    internal void RaiseError(string code) =>
        OnError?.Invoke(this, new PlayerErrorEventArgs { Code = code, Message = code });

    public void Play(PlaybackParams params_)
    {
        PlayedUrls.Add(params_.Endpoint.Url);
        Log.Add($"Play:{params_.Endpoint.Url}");
    }

    public void Pause() => Log.Add("Pause");

    public void Stop() => Log.Add("Stop");

    public void AttachSurface(IntPtr surfaceHandle) => Log.Add("Attach");

    public void DetachSurface() => Log.Add("Detach");

    public void Dispose() => Log.Add("Dispose");
}
