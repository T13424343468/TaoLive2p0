using System.Text;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using TaoLive.Catalog;
using TaoLive.Foundation;
using TaoLive.Model;
using TaoLive.Players;
using TaoLive.Session;

namespace TaoLive.Tv;

/// <summary>
/// 电视端主入口（2.0 W5 接线）：频道列表（编号+分类，电视优先）+ 播放页 SurfaceView，
/// 接会话层（AddTaoLiveSession）与 ExoPlayer 内核（老板 2026-11-15 03:37 换向令）。
/// 操作逻辑沿一期：频道号直达（遥控器数字键）、上下键换台、返回键回列表；
/// 界面样式一期保留（纯文字列表，无花哨横幅）。label=TaoLive 特征关见 AndroidManifest。
/// </summary>
[Activity(Label = "TaoLive", MainLauncher = true)]
public partial class MainActivity : Activity
{
    private IServiceProvider _services = default!;
    private PlaybackSessionManager _manager = default!;
    private readonly IReadOnlyList<Channel> _channels = ChannelTable.Build();

    private FrameLayout _root = default!;
    private LinearLayout _listPage = default!;
    private ListView _list = default!;
    private FrameLayout _playerPage = default!;
    private TextView? _logoBadge;
    private readonly List<TextView> _tabViews = new();
    private ListTabs.Tab _tab = ListTabs.Tab.All;
    private long _healthLastAtMs;
    private readonly SwitchDebouncer _debouncer = new();
    private SurfaceView _surface = default!;
    private TextView _toast = default!;
    private readonly Handler _main = new(Looper.MainLooper!);
    private bool _onPlayerPage;
    private int _currentIndex;
    private string _digits = "";
    private Java.Lang.Runnable? _digitCommit;
    private bool _watchdogRunning;
    private Java.Lang.Runnable _toastHide = default!;
    private readonly CntvEpgClient _epg = new();
    private HashSet<string> _favorites = new();
    private bool _precheckEnabled = true;   // 预检握手开关（vc53 v1.0.8 用户拍板：设置面板可切，持久化）
    private ChannelPrecheck _precheck = default!;
    private TicketResolver _resolver = default!;
    private System.Threading.CancellationTokenSource? _precheckCts;
    private static readonly HttpClient PrecheckHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 持久化设置（vc53 口径）：UI 缩放档 + 后台预热开关 + 进阶调优旋钮，启动装载
        //（缩放档须在 BuildRoot 前就位，全套令牌按档位重算）
        _prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        UiScale.Factor = _prefs.GetFloat("ui_scale", 4f / 9f);
        _warmupEnabled = _prefs.GetBoolean("warmup_enabled", true);
        _precheckEnabled = _prefs.GetBoolean("precheck_enabled", true);
        AdvancedTuning.Apply(
            _prefs.GetInt("adv_fail_gate", AdvancedTuning.DefaultFailGate),
            _prefs.GetInt("adv_reprobe_min", 5) * 60_000L,
            _prefs.GetInt("adv_cache_sec", AdvancedTuning.DefaultListCacheSec),
            _prefs.GetInt("adv_retry_base_sec", 120) * 1000L,
            _prefs.GetInt("adv_retry_max_min", 120) * 60_000L,
            AdvancedTuning.DefaultProxyPort);
        CrashNet.Install(); // 未捕获异常先落日志再放行（崩溃日志页可查可清）

        var resolver = _resolver = new TicketResolver();

        // 每会话独享内核（Current+Standby 双实例热切的资源前提）；
        // 取流解析器闭包持有本实例，频道上下文由内核 Play 自取（双实例不共用上下文）
        IPlayer KernelFactory()
        {
            var player = new ExoPlayerPlayer(this);
            player.WithStreamResolver(endpoint =>
                resolver.ResolveAsync(endpoint, player.CurrentChannelKey ?? "", player.CurrentChannelName ?? ""));
            return player;
        }

        // 换台邻台预取（vc53 任务8）：取票落 ChannelWarmer 既有 5 分钟缓存（归一纪律，不建第二本账），
        // DNS 预解析失败静默（取流链路自带兜底）；全程过 BackgroundLane 单车道 Standard 档让路播放
        var prefetcher = new ChannelPrefetcher(
            async ch =>
            {
                try { return await new ChannelWarmer(resolver).WarmChannelAsync(ch) ? new ResolvedStream { Channel = ch } : null; }
                catch { return null; }
            },
            async host => { try { await System.Net.Dns.GetHostAddressesAsync(host); } catch { /* 预解析失败不挡预取 */ } });

        _services = new ServiceCollection()
            .AddSingleton<IClock, global::TaoLive.Session.SystemClock>()
            .AddSingleton<IMainThreadDispatcher>(new AndroidMainThreadDispatcher(_main))
            .AddTaoLiveSession(KernelFactory, ch =>
            {
                // 换台即触发邻台预取（火车道让路：首帧未落地铁道自动门控，画面平稳优先）；
                // 后台预热开关关闭时同步停——开关真实生效，不留死旋钮
                if (_warmupEnabled) _ = Task.Run(() => prefetcher.PrefetchNeighborsAsync(ch, _channels));
            })
            .BuildServiceProvider();
        _manager = _services.GetRequiredService<PlaybackSessionManager>();
        // 收口批①：备源队列吃内置表（换源触发时才遍历注册表，不进预检常驻轮）；
        // 回主源迟滞判据=主源预检轮次连续命中 OK ≥3 次（复用预检结果，不另起独立探测）+ 稳定 5 分钟（管理器内判）
        _manager.FallbackSourceProvider = TaoLive.Session.FallbackQueueSource.FromRegistry;
        _manager.PrimaryAliveConfirmed = ch =>
            _precheck.SourceHealthOf(ch) is { State: ChannelHealth.Alive, ConsecutiveOks: >= PlaybackSessionManager.ReturnToPrimaryMinConsecutiveOks };
        // 收口批③：会话 LRU 按内存分档（memoryClass≤128MB 低配档 4、高配 8；定标后只调常量）；取不到按低配保守
        var activityManager = (ActivityManager?)GetSystemService(ActivityService);
        _manager.RetainedCapacity = PlaybackSessionManager.CapacityFor(activityManager?.MemoryClass ?? 0);
        _favorites = GetSharedPreferences("favorites", FileCreationMode.Private)!
            .GetStringSet("ids", new List<string>())!.ToHashSet();
        // 备源耗尽提示（一期口径：全源不可达时明确提示，不静默装死；OK 键重试）
        // 只收当前会话事件（热备攒流/换台竞速不掺内部，画面不受热备起播影响——方案书 v5 口径）
        _manager.SessionStateChanged += (_, e) =>
        {
            // 逐次换源打点：所有会话（含热备）均落运行日志（自诊断/崩溃日志统计口径）
            e.Session.Failover -= OnSessionFailover;
            e.Session.Failover += OnSessionFailover;
            if (e.Session != _manager.Current) return;
            if (e.State == PlaybackState.Failed)
            {
                // 失败打点接真实事件：全线路不可达落运行日志
                CrashNet.Record($"[ConnectFailure] {e.Session.CurrentChannel?.Name ?? "未知频道"} 全线路不可达");
                ShowToast(TaoLive.Session.SwitchHud.AllLinesFailed);
            }
            else if (e.State == PlaybackState.Buffering)
            {
                // 换台过渡：铺底色遮罩（一期 SwitchRevealPolicy 口径，新流就绪才亮）
                if (SwitchRevealPolicy.ShouldVeilOnSwitch(_surface.Holder?.Surface != null))
                {
                    _surface.Visibility = ViewStates.Invisible;
                    ShowToast(TaoLive.Session.SwitchHud.Connecting);
                }
            }
            else if (e.State == PlaybackState.Playing)
            {
                if (SwitchRevealPolicy.ShouldReveal(true)) _surface.Visibility = ViewStates.Visible;
            }
        };

        // 开机预热（W8：取票口预取进 5 分钟缓存，换台零等待；退避账挂账不烧请求）
        // 后台预热开关关闭时跳过（设置面板真实开关，不留死旋钮）
        if (_warmupEnabled)
        {
            _ = Task.Run(async () =>
            {
                try { await new ChannelWarmer(resolver).WarmAllAsync(ChannelTable.Build()); }
                catch { /* 预热失败不影响播放 */ }
            });
        }

        // 开机预检握手（vc53 v1.0.8 一比一，常驻后台轮）：全台按序轻探——Web 取票口换票成功=服务器可达
        // （顺手焐热取票缓存，换台零等待）；直连源只取几 KB 头数据验证，不建视频传输。600ms/台限速
        // 防判异常流量；探测结果落健康账（持久化，跨启动可比）。开关关=循环同停，不留死旋钮。
        _precheck = new ChannelPrecheck(ProbeChannelAsync, 600);
        try { _precheck.AttachHealthStore(new SourceHealthStore(FilesDir!.AbsolutePath)); }
        catch { /* 账本初始化失败按账空跑，预检不挡启动 */ }
        if (_precheckEnabled) StartPrecheckLoop();
        _precheck.StateChanged += OnPrecheckHealthChanged; // ④列表健康点：读账刷新展示（5 秒节流）

        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);
        BuildRoot();
        SetContentView(_root);
        ShowList();

        // ⑥开机默认台（方案书收口批②）：开机自启链路（BootReceiver 拉起带 from_boot）才走；
        // 手动打开 App 仍进列表（一期口径不变）
        if (Intent?.GetBooleanExtra("from_boot", false) == true) _ = EnterByBootAsync();
    }

    /// <summary>
    /// 开机链路（方案书收口批②⑤⑥）：等网就绪再进默认台——轻量 TCP 连通探测（非 Wi-Fi 状态位，防假在线），
    /// 初值 15 秒，超时照常进；预检先焐热取票缓存，默认台随后起播（顺序定死）；预热超时/失败照常进，起播走既有重试。
    /// </summary>
    private async Task EnterByBootAsync()
    {
        try
        {
            ShowToast("等待网络…");
            await NetReadiness.WaitAsync(report: m => CrashNet.Record(m)); // ≤15 秒，超时照常放行
            _main.Post(async () =>
            {
                try
                {
                    var idx = DefaultChannelIndex();
                    ShowToast($"正在打开 {_channels[idx].Name}");
                    if (_warmupEnabled)
                    {
                        try
                        {
                            var warm = new ChannelWarmer(_resolver).WarmChannelAsync(_channels[idx]); // 预检先焐热取票缓存
                            await Task.WhenAny(warm, Task.Delay(15_000)); // 预热超时照常进默认台
                        }
                        catch (Exception ex) { CrashNet.Record($"[Boot] 预热未完成照常进默认台：{ex.Message}"); }
                    }
                    if (!_onPlayerPage) PlayAt(idx); // 用户已手动进台不抢
                }
                catch (Exception ex) { CrashNet.Record($"[Boot] 开机链路异常照常进默认台：{ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            CrashNet.Record($"[Boot] 开机链路异常照常进默认台：{ex.Message}");
            _main.Post(() => { if (!_onPlayerPage) PlayAt(DefaultChannelIndex()); });
        }
    }

    /// <summary>默认台来源（方案书收口批②）：设置项指定台（开机默认台设置行），未设进频道列表首位。</summary>
    private int DefaultChannelIndex()
    {
        var saved = _prefs.GetString("default_channel", null);
        if (!string.IsNullOrEmpty(saved))
        {
            var i = _channels.ToList().FindIndex(c => c.Id == saved);
            if (i >= 0) return i;
        }
        return 0; // 未设=列表首位
    }

    protected override void OnDestroy()
    {
        _manager.Current?.DetachSurface();
        base.OnDestroy();
    }

    // —— 视图 ————————————————————————————————————————————

    private void BuildRoot()
    {
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Color.Rgb(8, 10, 14));

        _toastHide = new Java.Lang.Runnable(() =>
            _toast.Visibility = ViewStates.Invisible);

        _listPage = new LinearLayout(this) { Orientation = Orientation.Vertical };

        // ②五标签行（方案书收口批②，照 vc53 DrawerSupport 名册：全部/收藏/央视/卫视/地方台；枚举序即行序）
        _tabViews.Clear();
        var tabBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        tabBar.SetPadding(Dp(24), Dp(16), Dp(24), Dp(8));
        foreach (ListTabs.Tab t in Enum.GetValues(typeof(ListTabs.Tab)))
        {
            var tv = new TextView(this) { Text = ListTabs.Label(t), Gravity = GravityFlags.Center };
            tv.TextSize = 18f;
            tv.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
            tv.Click += (_, _) => SelectTab(t);
            tabBar.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            _tabViews.Add(tv);
        }
        StyleTabs();
        _listPage.AddView(tabBar);

        _list = new ListView(this) { CacheColorHint = Color.Transparent };
        _list.Adapter = new ChannelListAdapter(this, _channels, _favorites, ch => _precheck.MarkOf(ch));
        _list.ItemClick += (_, e) =>
        {
            // 行→频道→列表索引（收藏组行也指向同一频道，编号直达语义不变）
            var channel = ((ChannelListAdapter)_list.Adapter!).ChannelAt(e.Position);
            if (channel is null) return;
            PlayAt(_channels.ToList().FindIndex(c => c.Id == channel.Id));
        };
        _listPage.AddView(_list, new LinearLayout.LayoutParams(-1, 0, 1f));
        _root.AddView(_listPage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _playerPage = new FrameLayout(this) { Visibility = ViewStates.Gone };
        _surface = new SurfaceView(this);
        _playerPage.AddView(_surface, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // ②台标（方案书收口批②）：播放画面台标行，纯展示；2.0 无台标素材→台名文字代标
        //（照 vc53 vc44① 水墨文字台标样式：水墨圆角底+纸色字），素材缺失不留空块
        _logoBadge = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            TextSize = 16f,
        };
        _logoBadge!.SetTextColor(Color.ParseColor("#E7E2D6"));
        var inkBg = new Android.Graphics.Drawables.GradientDrawable();
        inkBg.SetColor(Color.ParseColor("#3A4750"));
        inkBg.SetCornerRadius(Dp(12));
        inkBg.SetStroke(1, Color.ParseColor("#4E5D68"));
        _logoBadge.Background = inkBg;
        _logoBadge.SetPadding(Dp(24), Dp(8), Dp(24), Dp(8));
        _logoBadge.Visibility = ViewStates.Gone;
        _playerPage.AddView(_logoBadge, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Top | GravityFlags.Start, LeftMargin = Dp(28), TopMargin = Dp(20) });

        _toast = new TextView(this) { Gravity = GravityFlags.Center };
        _toast.SetTextColor(Color.White);
        _toast.TextSize = 34;
        _toast.SetBackgroundColor(Color.Argb(170, 0, 0, 0));
        _toast.SetPadding(28, 14, 28, 14);
        _playerPage.AddView(_toast, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center });
        _root.AddView(_playerPage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    private void ShowList()
    {
        _manager.Current?.DetachSurface();
        _playerPage.Visibility = ViewStates.Gone;
        _logoBadge!.Visibility = ViewStates.Gone; // 台标行只在播放画面
        _listPage.Visibility = ViewStates.Visible;
        _onPlayerPage = false;
        // 焦点默认落当前台行（方案书收口批②）：呼出即定位到正在看的台
        var current = _channels[_currentIndex];
        var pos = (_list.Adapter as ChannelListAdapter)?.PositionOf(current.Id) ?? -1;
        if (pos >= 0) _list.SetSelection(pos);
    }

    private void ShowPlayer()
    {
        _listPage.Visibility = ViewStates.Gone;
        _playerPage.Visibility = ViewStates.Visible;
        _onPlayerPage = true;
        _logoBadge!.Text = _channels[_currentIndex].Name; // ②台标：台名文字代标（素材缺失不留空块）
        _logoBadge.Visibility = ViewStates.Visible;
        EnsureWatchdog();
        // 播放页已就绪而 Surface 早已创建（返回再进场景）：补挂输出面
        if (_surface.Holder?.Surface != null && _surface.Holder.Surface.IsValid)
            _manager.Current?.AttachSurface(_surface.Holder.Surface.Handle);
    }

    // —— 播放接线 ————————————————————————————————————————————

    private void PlayAt(int index)
    {
        if (index < 0 || index >= _channels.Count) return;
        _currentIndex = index;
        SwitchToCurrent();
        ShowPlayer();
    }

    /// <summary>预检轻探（vc53 口径）：Web 取票口换票成功即服务器可达（结果落 5 分钟缓存）；直连源只取头数据验证，不建视频传输。</summary>
    private async Task<bool> ProbeChannelAsync(Channel ch)
    {
        foreach (var ep in ch.Sources)
        {
            if (!ep.IsValid) continue;
            try
            {
                if (ep.Kind == SourceKind.Web)
                {
                    var url = await _resolver.ResolveAsync(ep, ch.Id, ch.Name).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(url)) return true;
                }
                else
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, ep.Url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 8191);
                    using var res = await PrecheckHttp.SendAsync(req).ConfigureAwait(false);
                    if ((int)res.StatusCode < 400) return true;
                }
            }
            catch { /* 单源失败落下一源；全断才判死 */ }
        }
        return false;
    }

    /// <summary>常驻循环预检（vc53 口径）：保态复测列表不闪烁；轮间歇 20 秒；单轮失败下一轮再来。</summary>
    private void StartPrecheckLoop()
    {
        StopPrecheckLoop();
        _precheckCts = new System.Threading.CancellationTokenSource();
        var ct = _precheckCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _precheck.RunAsync(_channels, ct, keepKnownState: true).ConfigureAwait(false); }
                catch (System.OperationCanceledException) { return; }
                catch { /* 单轮失败不掀循环 */ }
                try { await Task.Delay(20_000, ct).ConfigureAwait(false); }
                catch (System.OperationCanceledException) { return; }
            }
        });
    }

    private void StopPrecheckLoop()
    {
        _precheckCts?.Cancel();
        _precheckCts = null;
    }

    private void SwitchToCurrent()
    {
        var ch = _channels[_currentIndex];
        // 频道上下文由内核 Play 自取（每会话独享内核，双实例不共用上下文）
        if (_manager.Current is { } cur && cur.CurrentChannel?.Id == ch.Id)
        {
            cur.ChangeChannel(ch); // 同频道原地重起播（切回口径）
        }
        else
        {
            _manager.SwitchTo(ch);
        }
        ShowToast(BuildChannelToast(ch));
    }

    /// <summary>
    /// 频道提示文案：频道名 + 官方节目单（现在播/接下来；官方无数据的台不出节目不硬造，一期口径）。
    /// </summary>
    private string BuildChannelToast(Channel ch)
    {
        var text = new StringBuilder(ch.Name);
        try
        {
            if (_epg.Fetch(ch.Id) is { } epg)
            {
                text.Append('\n').Append("正在播: ").Append(epg.Now.Title);
                if (epg.Next.Count > 0)
                    text.Append('\n').Append("接下来: ").Append(epg.Next[0].Title);
            }
        }
        catch { /* EPG 缺失不影响播放 */ }
        return text.ToString();
    }

    private void AttachSurfaceIfShown()
    {
        if (_onPlayerPage && _surface.Holder?.Surface is { IsValid: true } surface)
            _manager.Current?.AttachSurface(surface.Handle);
    }

    private sealed class SurfaceHook(MainActivity owner) : Java.Lang.Object, ISurfaceHolderCallback2
    {
        public void SurfaceCreated(ISurfaceHolder holder) => owner.AttachSurfaceIfShown();

        public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }

        public void SurfaceDestroyed(ISurfaceHolder holder) =>
            owner._manager.Current?.DetachSurface();

        public void SurfaceRedrawNeeded(ISurfaceHolder holder) { }
    }

    // —— 看门狗泵（会话层 Tick：缓冲超时/卡流自救） ——————————————————

    private void EnsureWatchdog()
    {
        if (_watchdogRunning) return;
        _watchdogRunning = true;
        Pump();
        void Pump()
        {
            _main.PostDelayed(() =>
            {
                if (_onPlayerPage) _manager.Tick();
                Pump();
            }, 3000);
        }
    }

    // —— 遥控器键位（一期操作逻辑：数字直达、上下换台、返回回列表） ——

    public override bool OnKeyDown([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        // TCL 设置键厂商码（真机实测 4077）：单击呼出软件设置面板、长按跳系统设置（v1.0.8 拍板口径）。
        // 单击/长按在 KeyUp / OnKeyLongPress 分流，这里只消费按键并复位长按标记。
        if ((int)keyCode == 4077)
        {
            _settingsLongFired = false;
            return true;
        }

        // 进阶调优面板：返回/菜单=关闭；确认=点当前行；
        // 方向键必须放行（return false）——系统焦点导航在 Activity 消费之后才走，这里吞掉光标就冻结（真机实锤 1.0.9）；
        // 其余键不穿透，防误触底层键位。
        if (_advOverlay != null)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Menu:
                    CloseAdvancedPanel();
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    (CurrentFocus as TextView)?.PerformClick();
                    return true;
                case Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight:
                    return false; // 交还系统走焦点导航（含面板内行间移动与光标行滚入可视区）
                default:
                    return true; // 其余不穿透
            }
        }

        // 自诊断面板：返回/菜单=关闭；上下=滚动（单按近一屏、长按连滚）；左右=两钮移焦点；确认=点当前钮
        if (_diagOverlay != null)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Menu:
                    CloseDiagPanel();
                    return true;
                case Keycode.DpadUp or Keycode.DpadDown when _diagScroll != null:
                {
                    int dir = keyCode == Keycode.DpadUp ? -1 : 1;
                    if (e is { RepeatCount: 0 })
                        _diagScroll.SmoothScrollBy(0, dir * (int)(_diagScroll.Height * 0.8f));
                    else
                        _diagScroll.ScrollBy(0, dir * Dp(56));
                    return true;
                }
                case Keycode.DpadLeft or Keycode.DpadRight when _diagExportBtn != null && _diagCloseBtn != null:
                    (CurrentFocus == _diagExportBtn ? _diagCloseBtn : _diagExportBtn).RequestFocus();
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    (CurrentFocus as TextView)?.PerformClick();
                    return true;
                default:
                    return true; // 不穿透，防误触底层键位
            }
        }

        // 崩溃日志面板（自管全屏）：键位同自诊断——上下滚日志、左右移两钮、确认点当前钮、其余吞掉
        if (_crashOverlay != null)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Menu:
                    CloseCrashPanel();
                    return true;
                case Keycode.DpadUp or Keycode.DpadDown when _crashScroll != null:
                {
                    int dir = keyCode == Keycode.DpadUp ? -1 : 1;
                    if (e is { RepeatCount: 0 })
                        _crashScroll.SmoothScrollBy(0, dir * (int)(_crashScroll.Height * 0.8f));
                    else
                        _crashScroll.ScrollBy(0, dir * Dp(56));
                    return true;
                }
                case Keycode.DpadLeft or Keycode.DpadRight when _crashCleanBtn != null && _crashCloseBtn != null:
                    (CurrentFocus == _crashCleanBtn ? _crashCloseBtn : _crashCleanBtn).RequestFocus();
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    (CurrentFocus as TextView)?.PerformClick();
                    return true;
                default:
                    return true; // 不穿透，防误触底层键位
            }
        }

        // 设置面板开着：返回/菜单收起，其余交给面板焦点导航；左右键不呼列表不换台
        if (_settingsPanelOpen)
        {
            if (keyCode is Keycode.Back or Keycode.Menu) { CloseSettingsPanel(); return true; }
            if (keyCode is Keycode.DpadLeft or Keycode.DpadRight) return true;
            return base.OnKeyDown(keyCode, e);
        }

        switch (keyCode)
        {
            case Keycode.DpadUp when _onPlayerPage:
                DebouncedSwitch(Neighbor(-1));
                return true;
            case Keycode.DpadDown when _onPlayerPage:
                DebouncedSwitch(Neighbor(+1));
                return true;
            case Keycode.DpadCenter or Keycode.Enter when _onPlayerPage:
                // 备源耗尽（Failed）时 OK=原地重试，接退避账（收口批③）：指数退避管重试节奏
                if (_manager.Current?.State == PlaybackState.Failed)
                {
                    if (_manager.TryManualRetry(_manager.Current.CurrentChannel?.Id ?? string.Empty, out var waitMs))
                        PlayAt(_currentIndex);
                    else
                        ShowToast(SwitchHud.RetryBackoff((int)Math.Ceiling(waitMs / 1000.0)));
                }
                return true;
            case Keycode.Back or Keycode.Escape when _onPlayerPage:
                ShowList();
                return true;
            case Keycode.Menu or Keycode.Settings or Keycode.Info when _onPlayerPage:
                ShowList(); // ②键位表原样接（照 vc53）：菜单键呼列表，呼出同步无延迟（≤200ms）
                return true;
            case Keycode.Menu or Keycode.Settings or Keycode.Info:
                PlayAt(_currentIndex); // 列表页菜单键=回播放（vc53 ToggleChannelList 口径）
                return true;
            case Keycode.DpadLeft or Keycode.DpadRight when !_onPlayerPage:
                SelectTab(ListTabs.NextTab(_tab, keyCode == Keycode.DpadRight)); // ②左右键切标签（照 vc53 抽屉口径）
                return true;
            case Keycode.Back or Keycode.Escape:
                ConfirmExit(); // 列表页返回：退出对话框（一期 ExitDialogFocus 口径）
                return true;
            case >= Keycode.Num0 and <= Keycode.Num9 when _onPlayerPage:
                FeedDigit((int)keyCode - (int)Keycode.Num0);
                return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyUp([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        // 设置键单击在 KeyUp 分流（KeyDown 只复位长按标记）
        if ((int)keyCode == 4077)
        {
            if (!_settingsLongFired) ToggleSettingsPanel();
            return true;
        }
        return base.OnKeyUp(keyCode, e);
    }

    public override bool OnKeyLongPress([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        // 设置键长按直达系统设置（三级降级：对口设置页→系统总入口→文字指引）
        if ((int)keyCode == 4077)
        {
            _settingsLongFired = true;
            OpenSystemSettings();
            return true;
        }
        if (keyCode is Keycode.DpadCenter or Keycode.Enter)
        {
            if (!_onPlayerPage)
            {
                // 列表页长按 OK = 收藏/取消收藏（一期口径）；短按交 ListView 原生点击
                ToggleFavoriteSelected();
                return true;
            }
            // 播放页长按 OK = 切下一线路（W8 换线路入口，环形轮转）
            if (_manager.Current is { LineCount: > 1 } session)
            {
                var next = (session.CurrentLineIndex + 1) % session.LineCount;
                session.SwitchLine(next);
                ShowToast(TaoLive.Session.SwitchHud.LineSwitching(next));
            }
            return true;
        }
        return base.OnKeyLongPress(keyCode, e);
    }

    private void ConfirmExit()
    {
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("TaoLive");
        builder.SetMessage("确定退出吗？");
        builder.SetPositiveButton("确定", (_, _) => Finish());
        builder.SetNegativeButton("取消", (_, _) => { });
        var dialog = builder.Create();
        if (dialog is null) return;
        dialog.Show();
        var ok = dialog.GetButton((int)DialogButtonType.Positive);
        ok?.RequestFocus();
    }

    private void ToggleFavoriteSelected()
    {
        var pos = _list.SelectedItemPosition;
        if (pos < 0) return;
        if (_list.Adapter is not ChannelListAdapter adapter) return;
        var channel = adapter.ChannelAt(pos);
        if (channel is null) return;
        if (!_favorites.Add(channel.Id)) _favorites.Remove(channel.Id);
        GetSharedPreferences("favorites", FileCreationMode.Private)!
            .Edit()!.PutStringSet("ids", _favorites)!.Apply();
        adapter.Refresh(_favorites);
    }

    private int Neighbor(int delta)
    {
        var i = (_currentIndex + delta) % _channels.Count;
        return i < 0 ? i + _channels.Count : i;
    }

    // —— ②五标签切换（方案书收口批②，照 vc53 抽屉口径：当前标签强调底高亮） ——

    private void SelectTab(ListTabs.Tab tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        (_list.Adapter as ChannelListAdapter)?.Refresh(_favorites, tab);
        StyleTabs();
    }

    private void StyleTabs()
    {
        for (int i = 0; i < _tabViews.Count; i++)
        {
            var active = (ListTabs.Tab)i == _tab;
            _tabViews[i].SetBackgroundColor(active
                ? Color.Argb(255, InkTranslucency.AccentR, InkTranslucency.AccentG, InkTranslucency.AccentB) // 朱砂强调底（照 vc55 列表页强调色）
                : Color.Transparent);
            _tabViews[i].SetTextColor(Color.ParseColor(active ? "#FFFFFF" : InkTranslucency.TextSecondary));
        }
    }

    /// <summary>健康点刷新节流（方案书收口批②：更新节流 5 秒）。只读账刷新展示，不写账。</summary>
    private void OnPrecheckHealthChanged(string epgKey, ChannelHealth state)
    {
        if (_onPlayerPage) return; // 播放页不见列表
        var now = System.Environment.TickCount64;
        if (now - _healthLastAtMs < 5000) return;
        _healthLastAtMs = now;
        _main.Post(() => (_list.Adapter as ChannelListAdapter)?.Refresh(_favorites, _tab));
    }

    /// <summary>换台去抖入口（W8 SwitchDebouncer 口径）：窗口内连按取末次按键，停稳才真切。</summary>
    private void DebouncedSwitch(int index)
    {
        if (index < 0 || index >= _channels.Count) return;
        var ch = _channels[index];
        // 换台竞速：去抖窗口内即备热备（攒流到起播门槛线，停稳时热切接管不露黑）
        _ = _manager.PrepareStandby(ch);
        _debouncer.Tap(System.Environment.TickCount64, ch.Id);
        ShowToast(ch.Name);
        _main.PostDelayed(() =>
        {
            if (!_debouncer.HasTarget || !_debouncer.IsDue(System.Environment.TickCount64)) return;
            var key = _debouncer.Target;
            _debouncer.Clear();
            var idx = _channels.ToList().FindIndex(c => c.Id == key);
            if (idx >= 0) PlayAt(idx);
        }, SwitchDebouncer.WindowMs + 20);
    }

    private void FeedDigit(int d)
    {
        _digits = (_digits + d.ToString()).TrimStart('0');
        ShowToast(_digits);
        _digitCommit?.Dispose();
        _digitCommit = new Java.Lang.Runnable(() =>
        {
            if (string.IsNullOrEmpty(_digits)) return;
            var number = int.Parse(_digits);
            _digits = "";
            // 编号=列表顺序（一期口径：编号永久直达）
            if (number >= 1 && number <= _channels.Count) PlayAt(number - 1);
            else ShowToast($"无此频道 {number}");
        });
        _main.PostDelayed(_digitCommit, 1200);
    }

    private static void HideToastCore() { }

    private void ShowToast(string text)
    {
        _toast.Text = text;
        _toast.Visibility = ViewStates.Visible;
        _main.RemoveCallbacks(_toastHide);
        _main.PostDelayed(_toastHide, 2200);
    }

    // —— 列表适配器：收藏置顶组 + 分类头 + 编号台名（一期样式：纯文字，编号在左） ——

    private sealed class Row(string title, bool isHeader, Channel? channel)
    {
        public readonly string Title = title;
        public readonly bool IsHeader = isHeader;
        public readonly Channel? Channel = channel;
    }

    private sealed class ChannelListAdapter : BaseAdapter
    {
        private readonly Context _ctx;
        private readonly IReadOnlyList<Channel> _channels;
        private readonly Func<Channel, ChannelHealth> _markOf; // ④健康点：只读账（预检 MarkOf 三态）
        private HashSet<string> _favorites;
        private ListTabs.Tab _tab;
        private List<Row> _rows;

        public ChannelListAdapter(Context ctx, IReadOnlyList<Channel> channels, HashSet<string> favorites,
            Func<Channel, ChannelHealth> markOf, ListTabs.Tab tab = ListTabs.Tab.All)
        {
            _ctx = ctx;
            _channels = channels;
            _favorites = favorites;
            _markOf = markOf;
            _tab = tab;
            _rows = BuildRows(channels, favorites, tab);
        }

        private static List<Row> BuildRows(IReadOnlyList<Channel> channels, HashSet<string> favorites, ListTabs.Tab tab)
        {
            var rows = new List<Row>();
            // ②五标签过滤（照 vc53 DrawerSupport 口径；收藏标签空态照 vc53 v22 第 6/15 项，不白屏）
            var visible = ListTabs.Filter(tab, channels, c => favorites.Contains(c.Id));
            if (visible.Count == 0)
            {
                rows.Add(new Row(tab == ListTabs.Tab.Favorite
                    ? "收藏夹是空的，对着频道按确认可收藏" : "该分类暂无频道", true, null));
                return rows;
            }
            // 收藏置顶组（一期 FavoriteStore 口径：收藏非空才出组，置顶直达；收藏标签下整列即收藏）
            var favs = visible.Where(c => favorites.Contains(c.Id)).ToList();
            if (favs.Count > 0)
            {
                rows.Add(new Row("收藏", true, null));
                rows.AddRange(favs.Select(ch => new Row($"★  {ch.Name}", false, ch)));
            }
            foreach (var cat in ChannelTable.CategoryOrder)
            {
                var group = visible.Where(c => c.CategoryName == cat).ToList();
                if (group.Count == 0) continue;
                rows.Add(new Row(cat, true, null));
                // 编号=整表顺序（一期口径：编号永久直达）
                rows.AddRange(group.Select(ch => new Row($"{IndexOfChannel(channels, ch) + 1}  {ch.Name}", false, ch)));
            }
            return rows;
        }

        private static int IndexOfChannel(IReadOnlyList<Channel> list, Channel c)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Id == c.Id) return i;
            }
            return -1;
        }

        /// <summary>行对应的频道；分类头行返回空。</summary>
        public Channel? ChannelAt(int position) =>
            position >= 0 && position < _rows.Count ? _rows[position].Channel : null;

        /// <summary>频道所在行位置（呼出列表焦点落当前台行用）；不在表内返回 -1。</summary>
        public int PositionOf(string channelId)
        {
            for (var i = 0; i < _rows.Count; i++)
                if (_rows[i].Channel?.Id == channelId) return i;
            return -1;
        }

        /// <summary>收藏/标签变化后重建行（保持选中位置不跳）。</summary>
        public void Refresh(HashSet<string> favorites, ListTabs.Tab? tab = null)
        {
            _favorites = favorites;
            _tab = tab ?? _tab;
            _rows = BuildRows(_channels, favorites, _tab);
            NotifyDataSetChanged();
        }

        public override int Count => _rows.Count;
        public override Java.Lang.Object GetItem(int position) => new Java.Lang.String(_rows[position].Title);
        public override long GetItemId(int position) => position;
        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var row = _rows[position];
            TextView tv;
            if (convertView is TextView existing)
            {
                tv = existing;
            }
            else
            {
                tv = new TextView(_ctx)
                {
                    TextSize = row.IsHeader ? 18f : 26f,
                };
                tv.SetPadding(40, 22, 40, 22);
            }
            tv.Text = row.Title;
            if (row.Channel is { } rowChannel)
            {
                // ④列表健康点（方案书收口批②）：绿●可用/灰●检测中或未探/红✕连不上（色值照 vc53 v1.0.8 口径）。
                // 点染在行首字符上（Spannable），行结构不变，纯读账展示。
                var (markChar, markColor) = _markOf(rowChannel) switch
                {
                    ChannelHealth.Alive => ("●", "#7ED182"),
                    ChannelHealth.Dead => ("✕", "#E97F7F"),
                    ChannelHealth.Checking => ("●", "#B5C4D0"),
                    _ => ("●", "#8894A0"),
                };
                var span = new Android.Text.SpannableString($"{markChar}  {row.Title}");
                span.SetSpan(new Android.Text.Style.ForegroundColorSpan(Color.ParseColor(markColor)),
                    0, 1, Android.Text.SpanTypes.ExclusiveExclusive);
                span.SetSpan(new Android.Text.Style.ForegroundColorSpan(Color.White),
                    1, span.Length(), Android.Text.SpanTypes.ExclusiveExclusive);
                tv.TextFormatted = span;
            }
            else
            {
                tv.Text = row.Title;
            }
            tv.SetTextColor(Color.White);
            tv.SetBackgroundColor(row.IsHeader ? Color.Argb(255, 20, 24, 32) : Color.Transparent);
            return tv;
        }
    }
}
