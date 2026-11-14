using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Players;

/// <summary>
/// LibVLC 3.10.x 适配器占位（W2 班填实现）。
/// M1 阶段所有方法抛 NotImplementedException：契约用例保证两个内核长一样，
/// 未实现不得静默假绿。
/// </summary>
public sealed class LibVlcPlayer : IPlayer
{
    // 占位期：事件走显式接口实现存订阅者但永不触发——占位内核不发假事件（不自欺）；
    // W2 接真内核时改为字段触发并补契约用例。
    private EventHandler<PlayerFirstFrameEventArgs>? _firstFrame;
    private EventHandler<PlayerBufferingEventArgs>? _bufferingStart;
    private EventHandler<PlayerBufferingEventArgs>? _bufferingEnd;
    private EventHandler<PlayerErrorEventArgs>? _error;

    event EventHandler<PlayerFirstFrameEventArgs>? IPlayer.OnFirstFrame
    {
        add => _firstFrame += value;
        remove => _firstFrame -= value;
    }

    event EventHandler<PlayerBufferingEventArgs>? IPlayer.OnBufferingStart
    {
        add => _bufferingStart += value;
        remove => _bufferingStart -= value;
    }

    event EventHandler<PlayerBufferingEventArgs>? IPlayer.OnBufferingEnd
    {
        add => _bufferingEnd += value;
        remove => _bufferingEnd -= value;
    }

    event EventHandler<PlayerErrorEventArgs>? IPlayer.OnError
    {
        add => _error += value;
        remove => _error -= value;
    }

    public void Play(PlaybackParams params_) => throw new NotImplementedException("W2 班填 LibVLC 起播实现");
    public void Pause() => throw new NotImplementedException("W2 班填暂停实现");
    public void Stop() => throw new NotImplementedException("W2 班填停止实现");
    public void AttachSurface(IntPtr surfaceHandle) => throw new NotImplementedException("W2 班填输出面接线");
    public void DetachSurface() { /* 占位：无内部状态可摘 */ }
    public void Dispose() { /* 占位：无内核资源可放 */ }
}
