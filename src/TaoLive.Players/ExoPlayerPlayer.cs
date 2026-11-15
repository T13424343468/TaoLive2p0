using Android.Runtime;
using Android.Views;
using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Players;

/// <summary>
/// ExoPlayer（Media3）适配器：2.0 第一内核（老板 2026-11-15 03:37 拍板，不采用 LibVLC 打底）。
/// 实现契约：Foundation/IPlayer——事件语义（次序即文档）与 IPlayer 契约保持一致，
/// 会话层只认 IPlayer 不认内核本尊。
/// 事件映射：
///   内核进入 Buffering → OnBufferingStart；
///   内核进入 Ready → OnBufferingEnd + OnFirstFrame（每次 Play 仅一次）；
///   内核进入 Ended → 视同卡流 OnBufferingStart（R2-72 发现即改：直播流被服务端掐断时内核进
///   ENDED，此前未映射 → 无错误回调、无卡流事件，状态机停在 Playing，画面冻结且看门狗/
///   备源自救永不触发——冻画黑洞。直播语义 ENDED=流断，交会话卡流路径自救）；
///   OnPlayerError → OnError（终态，直到下一次 Play）。
/// </summary>
public sealed class ExoPlayerPlayer : IPlayer
{
    private readonly Forwarder _forwarder;
    private readonly FrameTickForwarder _frameForwarder;
    private readonly AndroidX.Media3.ExoPlayer.IExoPlayer _player;
    private readonly Android.OS.Handler _main;
    private Func<TaoLive.Model.SourceEndpoint, Task<string?>>? _streamResolver;
    private bool _firstFrameRaisedThisPlay;
    private int _playGeneration; // 播放代次（闪退镜红② 11-15）：迟到 resolver 回调对不上代次即弃，不碰已释放内核
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
        _frameForwarder = new FrameTickForwarder(this); // 帧回调（v76：10Hz 节流）随内核装配一次，Play 全程复用
        _player.SetVideoFrameMetadataListener(_frameForwarder);
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
        // 步进式 builder（消 CS8602 可空链，同构造器口径）：绑定 fluent 链挂在可空返回上会报空解引用
        var loadBuilder = new AndroidX.Media3.ExoPlayer.DefaultLoadControl.Builder();
        loadBuilder.SetBufferDurationsMs(watermarkMs, watermarkMs, startupGateMs, startupGateMs);
        return loadBuilder.Build()!;
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
    public event EventHandler<PlayerFrameTickEventArgs>? OnFrameTick;

    public void Play(PlaybackParams params_)
    {
        ArgumentNullException.ThrowIfNull(params_);
        _playGeneration++; // 新一轮播放：作废旧代次迟到回调（闪退镜红②）
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
        var generation = _playGeneration;
        _ = Task.Run(async () =>
        {
            string? url = null;
            try { url = await _streamResolver(pending.Endpoint).ConfigureAwait(false); }
            catch { url = null; }
            _main.Post(() =>
            {
                // 代次校验（闪退镜红② 11-15）：resolver 迟到回调落在 Stop/Dispose/新一轮 Play 之后即弃，
                // 不对已让位或已释放的内核 StartMedia（防 IllegalStateException 与慢源覆盖后播流）
                if (generation != _playGeneration) return;
                if (string.IsNullOrEmpty(url))
                    HandleError("RESOLVE_FAILED", $"取流失败（{channelName ?? channelKey ?? "channel"}）");
                else
                    StartMedia(url);
            });
        });
    }

    private void StartMedia(string url)
    {
        try
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
        catch (Exception ex)
        {
            // 同步路径与异步同口径（闪退镜黄⑩ 11-15）：起播异常接 HandleError 交会话备源自救，不裸奔冒泡主线程
            HandleError("START_FAILED", $"起播失败：{ex.Message}");
        }
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
            try
            {
                var surface = Java.Lang.Object.GetObject<Android.Views.Surface>(surfaceHandle, JniHandleOwnership.DoNotTransfer);
                if (surface is not null)
                {
                    _player.SetVideoSurface(surface);
                }
            }
            catch (Exception ex)
            {
                // 死面挂接（闪退镜黄⑥ 11-15）：SurfaceDestroyed 后热切重挂不砸主线程，摘面待壳层重挂
                _hasSurface = false;
                _surfaceHandle = IntPtr.Zero;
                Console.Error.WriteLine($"[ExoPlayer] attach surface failed: {ex.Message}");
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
        _playGeneration++; // 释放后作废一切在途回调（闪退镜红②）
        _player.RemoveListener(_forwarder);
        _player.ClearVideoFrameMetadataListener(null);
        _frameForwarder.Dispose();
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

    /// <summary>
    /// 帧回调上报（10Hz 节流，方案书 v76）：渲染线程高频回调按 100ms 拍合并，
    /// 订阅方（壳层埋点）拿到的节拍即采样点；TimeSpan 判断免锁（单渲染线程顺序回调）。
    /// </summary>
    internal void HandleFrameAboutToBeRendered()
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastTickRaised != default && now - _lastTickRaised < TimeSpan.FromMilliseconds(100)) return;
        _lastTickRaised = now;
        OnFrameTick?.Invoke(this, new PlayerFrameTickEventArgs { AtUtc = now });
    }
    private DateTimeOffset _lastTickRaised;

    /// <summary>帧元数据监听桥：Media3 渲染线程回调 → 契约 OnFrameTick。</summary>
    private sealed class FrameTickForwarder : Java.Lang.Object, AndroidX.Media3.ExoPlayer.Video.IVideoFrameMetadataListener
    {
        private readonly ExoPlayerPlayer _owner;
        public FrameTickForwarder(ExoPlayerPlayer owner) : base() => _owner = owner;

        public void OnVideoFrameAboutToBeRendered(long presentationTimeUs, long eventTimeMs,
            AndroidX.Media3.Common.Format? format, Android.Media.MediaFormat? mediaFormat)
            => _owner.HandleFrameAboutToBeRendered();
    }

    private static bool IsHls(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 内核事件桥：实现 Media3 Player.Listener 绑定接口（IPlayerListener，全默认方法，
    /// 只重写契约需要的两个回调；OnPlaybackStateChanged 映射 Buffering/Ready/Ended 三态，
    /// Ended 视同卡流上报（R2-72 发现即改），其余内核态按契约未定义语义不映射）。
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
                case AndroidX.Media3.Common.BasePlayer.InterfaceConsts.StateEnded:
                    // R2-72 发现即改：直播流被服务端掐断时内核进 ENDED，此前未映射——
                    // 无错误回调、无卡流事件，状态机停在 Playing，画面冻结且看门狗/备源自救永不触发（冻画黑洞）。
                    // 直播语义 ENDED=流断：视同卡流上报，交会话层卡流路径（NotifyStalled→Reconnecting→超时切备源）。
                    // 用户主动 Stop 不产生 ENDED（ExoPlayer stop 保持状态）； stray 事件由 NotifyStalled 的 Playing 守卫挡住。
                    _owner.HandleBufferingStarted();
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
