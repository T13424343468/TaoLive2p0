using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 单个播放会话：一台会话一个状态机，状态只能经 Transition 改变，
/// 外部只读不写（封装口径，tao-code-standard 〇·八.1）。
/// 时钟注入可测（Tests 快拍不真等）。
/// </summary>
public sealed class PlaybackSession
{
    private readonly IPlayer _player;
    private readonly IClock _clock;
    private PlaybackState _state = PlaybackState.Idle;

    public PlaybackSession(IPlayer player, IClock clock)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _player.OnError += (_, e) => Transition(PlaybackState.Failed, $"player error: {e.Code}");
    }

    public PlaybackState State => _state;

    public DateTimeOffset? StartedAtUtc { get; private set; }

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
            (PlaybackState.Playing, PlaybackState.Reconnecting) => true,
            (PlaybackState.Playing, PlaybackState.Failed) => true,
            (PlaybackState.Playing, PlaybackState.Idle) => true,
            (PlaybackState.Reconnecting, PlaybackState.Buffering) => true,
            (PlaybackState.Reconnecting, PlaybackState.Failed) => true,
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
            // 起播计时刻：首播与失败重试都算新一轮，StartedAtUtc 以本次缓冲起点为准。
            StartedAtUtc = _clock.UtcNow;
        }
        _state = next;
    }

    /// <summary>起播入口：Idle/Failed 均可发起新一轮播放。</summary>
    public void Start(PlaybackParams p)
    {
        if (_state != PlaybackState.Idle && _state != PlaybackState.Failed)
        {
            throw new InvalidOperationException($"Start only legal from Idle/Failed, got {_state}");
        }
        Transition(PlaybackState.Buffering, "start requested");
        _player.Play(p);
    }

    public void NotifyFirstFrame()
    {
        if (_state == PlaybackState.Buffering)
        {
            Transition(PlaybackState.Playing, "first frame");
        }
    }

    public void NotifyStalled()
    {
        if (_state == PlaybackState.Playing)
        {
            Transition(PlaybackState.Reconnecting, "watchdog stall");
        }
    }
}

/// <summary>时钟抽象：测试注入固定时钟，生产用系统时钟。</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
