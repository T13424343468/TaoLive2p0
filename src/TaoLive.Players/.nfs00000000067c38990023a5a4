using Android.Views;
using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Players;

/// <summary>
/// ExoPlayer（Media3）适配器：2.0 第一内核（老板 2026-11-15 03:37 拍板，不采用 LibVLC 打底）。
/// 实现契约：Foundation/IPlayer——事件语义（次序即文档）与双内核口径保持一致，
/// 会话层只认 IPlayer 不认内核本尊。
/// 事件映射：
///   内核进入 Buffering → OnBufferingStart；
///   内核进入 Ready → OnBufferingEnd + OnFirstFrame（每次 Play 仅一次）；
///   OnPlayerError → OnError（终态，直到下一次 Play）。
/// </summary>
public sealed class ExoPlayerPlayer : IPlayer
{
    private readonly Forwarder _forwarder;
    private readonly AndroidX.Media3.ExoPlayer.IExoPlayer _player;
    private bool _firstFrameRaisedThisPlay;
    private IntPtr _surfaceHandle;
    private bool _hasSurface;

    public ExoPlayerPlayer(Android.Content.Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _forwarder = new Forwarder(this);
        _player = new AndroidX.Media3.ExoPlayer.ExoPlayer.Builder(context).Build()!;
        _player.AddListener(_forwarder);
    }

    // —— 事件出口（契约语义，供会话层订阅）——

    public event EventHandler<PlayerFirstFrameEventArgs>? OnFirstFrame;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingStart;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingEnd;
    public event EventHandler<PlayerErrorEventArgs>? OnError;

    public void Play(PlaybackParams params_)
    {
        ArgumentNullException.ThrowIfNull(params_);
        _firstFrameRaisedThisPlay = false;
        var builder = new AndroidX.Media3.Common.MediaItem.Builder()
            .SetUri(Android.Net.Uri.Parse(params_.Endpoint.Url));
        if (IsHls(params_.Endpoint.Url))
        {
            builder.SetMimeType(AndroidX.Media3.Common.MimeTypes.ApplicationHls);
        }
        _player.SetMediaItem(builder.Build()!);
        _player.Prepare();
        _player.PlayWhenReady = true;
    }

    public void Pause()
    {
        _player.PlayWhenReady = false;
    }

    public void Stop()
    {
        _firstFrameRaisedThisPlay = false;
        _player.Stop();
        _player.ClearMediaItems();
    }

    /// <summary>接输出面：句柄为壳层 Android.Views.Surface 的 Java 对象指针。</summary>
    public void AttachSurface(IntPtr surfaceHandle)
    {
        _surfaceHandle = surfaceHandle;
        _hasSurface = surfaceHandle != IntPtr.Zero;
        if (_hasSurface)
        {
            var surface = new Surface(surfaceHandle);
            _player.SetVideoSurface(surface);
        }
    }

    /// <summary>摘输出面：换源/换台/退出前必调，防黑屏花屏。重复 detach 无害。</summary>
    public void DetachSurface()
    {
        if (_hasSurface)
        {
            _player.ClearVideoSurface();
            _hasSurface = false;
            _surfaceHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        _player.RemoveListener(_forwarder);
        _player.Release();
    }

    private void HandleBufferingStarted()
    {
        OnBufferingStart?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });
    }

    private void HandleReady()
    {
        OnBufferingEnd?.Invoke(this, new PlayerBufferingEventArgs { IsSeeking = false });
        if (!_firstFrameRaisedThisPlay)
        {
            _firstFrameRaisedThisPlay = true;
            OnFirstFrame?.Invoke(this, new PlayerFirstFrameEventArgs { AtUtc = DateTimeOffset.UtcNow });
        }
    }

    private void HandleError(string code, string message)
    {
        OnError?.Invoke(this, new PlayerErrorEventArgs { Code = code, Message = message });
    }

    private static bool IsHls(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>内核事件桥：仅重写契约需要的回调，其余事件按契约未定义语义不映射。</summary>
    private sealed class Forwarder : AndroidX.Media3.Common.PlayerListener
    {
        private readonly ExoPlayerPlayer _owner;

        public Forwarder(ExoPlayerPlayer owner) => _owner = owner;

        public override void OnPlaybackStateChanged(int playbackState)
        {
            switch (playbackState)
            {
                case AndroidX.Media3.Common.Player.StateBuffering:
                    _owner.HandleBufferingStarted();
                    break;
                case AndroidX.Media3.Common.Player.StateReady:
                    _owner.HandleReady();
                    break;
            }
        }

        public override void OnPlayerError(AndroidX.Media3.Common.PlaybackException error)
        {
            var code = error?.ErrorCodeName ?? "UNKNOWN";
            var message = error?.Message ?? "unknown playback error";
            _owner.HandleError(code, message);
        }
    }
}
