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
    private Func<TaoLive.Model.SourceEndpoint, Task<string?>>? _streamResolver;
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
        var playerBuilder = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(context);
        playerBuilder.SetLoadControl(BuildLoadControl(context));
        _player = playerBuilder.Build()!;
        _player.AddListener(_forwarder);
        _main = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
        _streamResolver = streamResolver;
    }

    /// <summary>
    /// 缓冲深水接线（vc53 fix56 思路在 ExoPlayer 内核原生落位）：
    /// - 起播门槛线=7 秒（PrefetchCurve.StartupThreshold）：缓冲到线即出画，首帧快字当头，不等满水位；
    /// - 预取水位线=按设备内存自适应安全水位（BufferPolicy.MaxWatermarkSeconds：内存足 60s、低配 30-45s），
    ///   出画后内核后台继续把窗口填满——保满预取即此；
    /// - 水位读写下沉 Players 适配层口径：水位由内核 LoadControl 承担，会话层不掺内部。
    /// </summary>
    private static AndroidX.Media3.ExoPlayer.DefaultLoadControl BuildLoadControl(Android.Content.Context context)
    {
        long totalMem = 0;
        try
        {
            if (context.GetSystemService(Android.Content.Context.ActivityService)
                is Android.App.ActivityManager am)
            {
                var mi = new Android.App.ActivityManager.MemoryInfo();
                am.GetMemoryInfo(mi);
                totalMem = mi.TotalMem;
            }
        }
        catch { /* 内存探测失败按低配保守（BufferPolicy 未知内存=30s） */ }
        int watermarkMs = BufferPolicy.MaxWatermarkSeconds(totalMem) * 1000;
        int startupGateMs = (int)PrefetchCurve.StartupThreshold.TotalMilliseconds;
        return new AndroidX.Media3.ExoPlayer.DefaultLoadControl.Builder()
            // 绑定只挂 4 参重载（min,max,起播门槛,重缓门槛）；回退缓冲等走默认（0/不保留）
            .SetBufferDurationsMs(watermarkMs, watermarkMs, startupGateMs, startupGateMs)
            .Build()!;
    }

    /// <summary>频道上下文：缓存键与整表行匹配名（nmtv/gxtv 按 name 定位），Play 前由壳层设置。</summary>
    public string? CurrentChannelKey { get; set; }
    public string? CurrentChannelName { get; set; }

    /// <summary>构造后补挂取流注入点（内核工厂场景：独享内核创建后挂本实例的解析器）。</summary>
    public ExoPlayerPlayer WithStreamResolver(Func<TaoLive.Model.SourceEndpoint, Task<string?>> resolver)
    {
        _streamResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        return this;
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
        // 频道上下文自取（双实例热切前提：每会话独享内核，壳层不再代设）
        if (!string.IsNullOrEmpty(params_.Channel.Id)) CurrentChannelKey = params_.Channel.Id;
        if (!string.IsNullOrEmpty(params_.Channel.Name)) CurrentChannelName = params_.Channel.Name;
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
