using Android.Runtime;
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
    private readonly Android.OS.Handler _main;
    private readonly Func<TaoLive.Model.SourceEndpoint, Task<string?>>? _streamResolver;
    private bool _firstFrameRaisedThisPlay;
    private IntPtr _surfaceHandle;
    private bool _hasSurface;

    /// <summary>
    /// 取流注入点（2.0 W4 口径）：API 取票口端点播前经 resolver 换真实流（频道号+运行时取票），
    /// 直连端点原样放行。null=全部直连。解析失败按 OnError 交会话备源自救，契约不变。
    /// 频道上下文（缓存键/整表行匹配名）由壳层在 Play 前设置。
    /// </summary>
    public ExoPlayerPlayer(Android.Content.Context context,
        Func<TaoLive.Model.SourceEndpoint, Task<string?>>? streamResolver = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _forwarder = new Forwarder(this);
        _player = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(context).Build()!;
        _player.AddListener(_forwarder);
        _main = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
        _streamResolver = streamResolver;
    }

    /// <summary>频道上下文：缓存键与整表行匹配名（nmtv/gxtv 按 name 定位），Play 前由壳层设置。</summary>
    public string? CurrentChannelKey { get; set; }
    public string? CurrentChannelName { get; set; }

    // —— 事件出口（契约语义，供会话层订阅）——

    public event EventHandler<PlayerFirstFrameEventArgs>? OnFirstFrame;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingStart;
    public event EventHandler<PlayerBufferingEventArgs>? OnBufferingEnd;
    public event EventHandler<PlayerErrorEventArgs>? OnError;

    public void Play(PlaybackParams params_)
    {
        ArgumentNullException.ThrowIfNull(params_);
        _firstFrameRaisedThisPlay = false;
        if (_streamResolver is null)
        {
            StartMedia(params_.Endpoint.Url);
            return;
        }
        // 取票口：异步换真实流，回到主线程再挂 MediaItem（ExoPlayer 要求在其创建线程调用）
        var channelKey = CurrentChannelKey;
        var channelName = CurrentChannelName;
        var pending = params_;
        _ = Task.Run(async () =>
        {
            string? url = null;
            try { url = await _streamResolver(pending.Endpoint).ConfigureAwait(false); }
            catch { url = null; }
            _main.Post(() =>
            {
                if (string.IsNullOrEmpty(url))
                    HandleError("RESOLVE_FAILED", $"取流失败（{channelName ?? channelKey ?? "channel"}）");
                else
                    StartMedia(url);
            });
        });
    }

    private void StartMedia(string url)
    {
        var builder = new AndroidX.Media3.Common.MediaItem.Builder();
        builder.SetUri(Android.Net.Uri.Parse(url));
        if (IsHls(url))
        {
            builder.SetMimeType(AndroidX.Media3.Common.MimeTypes.ApplicationM3u8);
        }
        var mediaItem = builder.Build() ?? throw new InvalidOperationException("MediaItem build returned null");
        _player.SetMediaItem(mediaItem);
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
            // 壳层传入的是 Android.Views.Surface 的 Java 对象指针，需还原包装（壳层仍持有所有权，不转移）
            var surface = Java.Lang.Object.GetObject<Android.Views.Surface>(surfaceHandle, JniHandleOwnership.DoNotTransfer);
            if (surface is not null)
            {
                _player.SetVideoSurface(surface);
            }
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

    /// <summary>
    /// 内核事件桥：实现 Media3 Player.Listener 绑定接口（IPlayerListener，全默认方法，
    /// 只重写契约需要的两个回调，其余按契约未定义语义不映射）。
    /// </summary>
    private sealed class Forwarder : Java.Lang.Object, AndroidX.Media3.Common.IPlayerListener
    {
        private readonly ExoPlayerPlayer _owner;

        public Forwarder(ExoPlayerPlayer owner) : base() => _owner = owner;

        public void OnPlaybackStateChanged(int playbackState)
        {
            switch (playbackState)
            {
                case AndroidX.Media3.Common.BasePlayer.InterfaceConsts.StateBuffering:
                    _owner.HandleBufferingStarted();
                    break;
                case AndroidX.Media3.Common.BasePlayer.InterfaceConsts.StateReady:
                    _owner.HandleReady();
                    break;
            }
        }

        public void OnPlayerError(AndroidX.Media3.Common.PlaybackException? error)
        {
            var code = error?.ErrorCodeName ?? "UNKNOWN";
            var message = error?.Message ?? "unknown playback error";
            _owner.HandleError(code, message);
        }
    }
}
