using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
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
/// 电视端主入口（2.0 W5 接线 + vc53 界面整套搬运 2026-11-15 老板真机令）：
/// 界面视觉与交互照 vc53——全屏视频垫底、半透明水墨抽屉盖画面、亮度自适应、
/// 真实台标列表（无台标=首字占位）、五分类页签、底部节目单、顶部信息条、起播亮牌、水墨退出弹窗。
/// 底层保留 2.0 事件/会话架构（PlaybackSessionManager + ExoPlayer 内核 + SurfaceHook 挂面），
/// 即「vc53 的皮，2.0 的骨」。操作逻辑：菜单键呼/收抽屉、上下换台、左右切页签、确认收藏、频道号直达。
/// </summary>
[Activity(Label = "TaoLive", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.Landscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
[IntentFilter(new[] { Intent.ActionMain },
    Categories = new[] { Intent.CategoryLauncher, Intent.CategoryLeanbackLauncher })]
public partial class MainActivity : Activity
{
    private IServiceProvider _services = default!;
    private PlaybackSessionManager _manager = default!;
    private readonly IReadOnlyList<Channel> _channels = ChannelTable.Build();

    private FrameLayout _root = default!;
    private LinearLayout _listPage = default!;
    private ListView _list = default!;
    private FrameLayout _playerPage = default!;
    private readonly List<TextView> _tabViews = new();
    private ListTabs.Tab _tab = ListTabs.Tab.All;
    private long _healthLastAtMs;
    private readonly SwitchDebouncer _debouncer = new();
    private SurfaceView _surface = default!;
    private TextView _toast = default!;
    private TextView _emptyState = default!;
    private readonly TaoLive.Session.NetGuard _netGuard = new();
    private readonly Handler _main = new(Looper.MainLooper!);
    private bool _onPlayerPage;
    private bool _listOpen; // vc53 抽屉口径：抽屉开着=列表键位，画面在面板下继续
    private int _currentIndex;
    private string _digits = "";
    private Java.Lang.Runnable? _digitCommit;
    private bool _watchdogRunning;
    private bool _watchdogStopped;
    private long? _failoverAtMs; // 换源黑屏埋点账（黑屏镜黄④）：换源起点 → Playing 出画恢复时长落运行台账
    private Java.Lang.Runnable _toastHide = default!;
    private readonly CntvEpgClient _epg = new();
    private HashSet<string> _favorites = new();
    private readonly FavoriteStore _favStore = new(); // 收藏顺序（vc53 排序模式口径）
    private bool _sortMode;      // 收藏排序状态机（v1.0.8 第14项）：列表内长按确认进入，退出恢复默认键位
    private Channel? _sortChannel;
    private readonly List<int> _sortBackup = new();
    private bool _precheckEnabled = true;   // 预检握手开关（vc53 v1.0.8 用户拍板：设置面板可切，持久化）
    private ChannelPrecheck _precheck = default!;
    private TicketResolver _resolver = default!;
    private System.Threading.CancellationTokenSource? _precheckCts;
    private static readonly HttpClient PrecheckHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    // ---- vc53 界面搬运：亮牌 / 底部节目单 / 水墨退出弹窗 ----
    private FrameLayout _loadingPanel = default!;
    private TextView _loadingText = default!;
    private int _loadingSeq;                 // 亮牌代数：换台即换代，旧牌绝不再亮
    private LinearLayout? _epgPanel;
    private TextView? _epgNow;
    private TextView? _epgNext;
    private bool _epgHasData;                // 当前台拉到过节目单（抽屉收起后据此重新亮出）
    private Action? _epgHidePending;         // 7 秒自动收起任务
    private int _epgSeq;                     // 节目单代数：换台即作废旧拉取
    private const int EpgAutoHideMs = 7000;
    private AlertDialog? _exitDialog;        // 退出确认（v1.0.9 用户拍板，vc22 水墨浮层样式）
    private TextView? _exitNo;
    private TextView? _exitYes;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 持久化设置（vc53 口径）：UI 缩放档 + 后台预热开关 + 进阶调优旋钮，启动装载
        //（缩放档须在 BuildRoot 前就位，全套令牌按档位重算）
        _prefs = GetSharedPreferences("taolive_prefs", FileCreationMode.Private)!;
        UiScale.Factor = _prefs.GetFloat("ui_scale", 4f / 9f);
        // 收口批④开关合流：预热与预检共车道只留预检开关，预热（开机全量预热+换台邻台预取+开机默认台焐热）
        // 一律随 _precheckEnabled 同起同停——两个开关管同一条车道必出一个死开关（方案书收口批④原文）。
        _precheckEnabled = _prefs.GetBoolean("precheck_enabled", true);
        AdvancedTuning.Apply(
            _prefs.GetInt("adv_fail_gate", AdvancedTuning.DefaultFailGate),
            _prefs.GetInt("adv_reprobe_min", 5) * 60_000L,
            _prefs.GetInt("adv_cache_sec", AdvancedTuning.DefaultListCacheSec),
            _prefs.GetInt("adv_retry_base_sec", 120) * 1000L,
            _prefs.GetInt("adv_retry_max_min", 120) * 60_000L);
        CrashNet.Install(); // 未捕获异常先落日志再放行（崩溃日志页可查可清）

        var resolver = _resolver = new TicketResolver();

        // 每会话独享内核（Current+Standby 双实例热切的资源前提）；
        // 取流解析器闭包持有本实例，频道上下文由内核 Play 自取（双实例不共用上下文）
        IPlayer KernelFactory()
        {
            var player = new ExoPlayerPlayer(this);
            player.WithStreamResolver(endpoint =>
                resolver.ResolveAsync(endpoint, player.CurrentChannelKey ?? "", player.CurrentChannelName ?? ""));
            WireFramePulse(player); // 画面平稳埋点（方案书 v76/v20：帧回调 10Hz → 滑窗 P95 断言）
            return player;
        }

        // 画面平稳无感量化埋点（v20）：帧回调进滑窗，每 10 秒取数落 [画面平稳] 日志行；
        // P95≤250ms 且连续 3 窗超阈即写「疑似卡顿」——真机验收按此行核，不靠人工观感。
        // 内核随会话换新即重挂，闭包生命周期随内核，无跨会话账。
        void WireFramePulse(IPlayer player)
        {
            var pulse = new FramePulse();
            DateTimeOffset lastReport = DateTimeOffset.UtcNow;
            player.OnFrameTick += (_, e) =>
            {
                pulse.Record(e.AtUtc);
                if (e.AtUtc - lastReport < TimeSpan.FromSeconds(10)) return;
                lastReport = e.AtUtc;
                var p95 = pulse.WindowP95Ms();
                if (p95 is not { } ms) return;
                CrashNet.Record(pulse.StallSuspicion
                    ? $"[画面平稳] 疑似卡顿：连续 3 窗帧间隔 P95 {ms}ms 超阈 {FramePulse.P95ThresholdMs}ms"
                    : $"[画面平稳] 帧间隔 P95 {ms}ms（阈 {FramePulse.P95ThresholdMs}ms）");
            };
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
                // 预热随预检开关同起同停（收口批④开关合流）
                if (_precheckEnabled) _ = Task.Run(() => prefetcher.PrefetchNeighborsAsync(ch, _channels));
            })
            .BuildServiceProvider();
        _manager = _services.GetRequiredService<PlaybackSessionManager>();
        // 内核回调异常出口（闪退镜黄⑤）：矩阵外跳转不掀回调链，落运行台账
        TaoLive.Session.PlaybackSession.KernelCallbackFailed += (_, msg) => CrashNet.Record($"[KernelCallback] {msg}");
        // 收口批①：备源队列吃内置表（换源触发时才遍历注册表，不进预检常驻轮）+ 活性按健康账轻排序
        // （同台活源在前、账新者在前——只动同台内排序不改台间顺序，v28 轻排序条款）；
        // 回主源迟滞判据=主源预检轮次连续命中 OK ≥3 次（复用预检结果，不另起独立探测）+ 稳定 5 分钟（管理器内判）
        _manager.FallbackSourceProvider = ch => FallbackQueueSource.FromRegistry(ch, FallbackHealthHintOf);
        _manager.PrimaryAliveConfirmed = ch =>
            _precheck.SourceHealthOf(ch) is { State: ChannelHealth.Alive, ConsecutiveOks: >= PlaybackSessionManager.ReturnToPrimaryMinConsecutiveOks };
        // 收口批③：会话 LRU 按内存分档（memoryClass≤128MB 低配档 4、高配 8；定标后只调常量）；取不到按低配保守
        var activityManager = (ActivityManager?)GetSystemService(ActivityService);
        _manager.RetainedCapacity = PlaybackSessionManager.CapacityFor(activityManager?.MemoryClass ?? 0);
        _favorites = GetSharedPreferences("favorites", FileCreationMode.Private)!
            .GetStringSet("ids", new List<string>())!.ToHashSet();
        // 收藏顺序（vc53 v1.0.8 第14项：顺序跨启动记忆；逗号编号串，坏段忽略）
        _favStore.Restore(GetSharedPreferences("favorites", FileCreationMode.Private)!.GetString("order", null));
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
                // 失败终态驻留空态（方案书 v77，白屏镜红②）：重连耗尽转空态文案，不再用瞬时 Toast；
                // 键位引导按树内真实键位：OK 重试（退避账管节奏）· 菜单键选台（呼列表）
                HideLoading();
                ShowEmptyState("信号中断，全线路不可达\n按 OK 重试 · 菜单键选台");
            }
            else if (e.State == PlaybackState.Buffering)
            {
                HideEmptyState();
                // 断流自救（FailoverCount>0）零提示（方案书「热切断流自救全程零提示」，四镜·黑屏镜 11-15 修）：
                // 不铺遮罩不弹 OSD，画面保持最后一帧直到新流就绪；换台过渡（FailoverCount=0）才走 SwitchRevealPolicy。
                if (e.Session.FailoverCount == 0 && SwitchRevealPolicy.ShouldVeilOnSwitch(_surface.Holder?.Surface != null))
                {
                    _surface.Visibility = ViewStates.Invisible;
                    ShowToast(TaoLive.Session.SwitchHud.Connecting);
                }
            }
            else if (e.State == PlaybackState.Playing)
            {
                HideEmptyState();
                HideLoading(); // vc53：首帧落地即撤亮牌
                // 换源黑屏埋点（黑屏镜黄④ 11-15）：换源起点到出画恢复的时长落运行台账（含 ≤500ms 快切段）
                if (_failoverAtMs is { } from)
                {
                    _failoverAtMs = null;
                    CrashNet.Record($"[换源黑屏] 恢复出画 {System.Environment.TickCount64 - from}ms（{e.Session.CurrentChannel?.Name ?? ""}）");
                }
                // 断点台落盘（方案书 v24，黑屏镜黄⑤ 11-15）：每次换台成功即同步写档，恢复读最近成功台
                if (e.Session.CurrentChannel is { } playingChannel)
                {
                    _prefs.Edit()!.PutString("last_channel", playingChannel.Id)!.Apply();
                }
                if (SwitchRevealPolicy.ShouldReveal(true)) _surface.Visibility = ViewStates.Visible;
            }
            else
            {
                HideEmptyState();
            }
        };

        // 开机预热（W8：取票口预取进 5 分钟缓存，换台零等待；退避账挂账不烧请求）
        // 预热随预检开关同起同停（收口批④开关合流，不再单设开关）
        if (_precheckEnabled)
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
                    if (_precheckEnabled)
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

    /// <summary>默认台来源（方案书收口批②）：设置项指定台（开机默认台设置行）→ 断点台（v24：最近成功台落盘）→ 列表首位。</summary>
    private int DefaultChannelIndex()
    {
        var saved = _prefs.GetString("default_channel", null);
        if (!string.IsNullOrEmpty(saved))
        {
            var i = _channels.ToList().FindIndex(c => c.Id == saved);
            if (i >= 0) return i;
        }
        var last = _prefs.GetString("last_channel", null); // 断点台（v24）：恢复读最近成功台
        if (!string.IsNullOrEmpty(last))
        {
            var j = _channels.ToList().FindIndex(c => c.Id == last);
            if (j >= 0) return j;
        }
        return 0; // 未设=列表首位
    }

    protected override void OnDestroy()
    {
        // 生命周期收尾（闪退镜黄⑦⑧⑨ 11-15）：停看门狗泵、清数字提交回调、停预检循环、
        // 让位当前台与热备（停内核解码）；内核实例随进程回收，不持 Activity 泄漏
        _watchdogStopped = true;
        _main.RemoveCallbacks(_digitCommit!);
        _digitCommit?.Dispose();
        StopPrecheckLoop();
        _manager.Current?.Supersede();
        _manager.Standby?.Supersede();
        base.OnDestroy();
    }

    // —— 视图（vc53 BuildUi 皮：视频垫底 + 半透明水墨抽屉 + 信息条 + 节目单 + 亮牌） ———

    private void BuildRoot()
    {
        // 水墨主题底（vc53：TaoPalette 水墨暖宣浅；视频首帧前露底即暖宣，不露黑）
        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Color.ParseColor("#F7F5EE"));

        _toastHide = new Java.Lang.Runnable(() =>
            _toast.Visibility = ViewStates.Invisible);

        // 1. 全屏视频层（vc53：垫底；抽屉/信息条/节目单全盖其上）
        _playerPage = new FrameLayout(this);
        _surface = new SurfaceView(this);
        _playerPage.AddView(_surface, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        // 黑屏根因修复（真机「黑屏不出画只有声音」实锤）：SurfaceHook 此前从未注册，
        // Surface 异步创建后无人补挂输出面——注册后首帧创建/回前台重建均自动补挂
        _surface.Holder!.AddCallback(new SurfaceHook(this));

        // 1.5 起播亮牌（雷二·延迟亮）：换台后 0.8 秒内没出首帧才亮，亮了带台名，
        // 明确反馈「没死机」，首帧落地才撤（vc53 LoadingPanel 同款：亮度自适应水墨深底）
        _loadingPanel = new FrameLayout(this);
        _loadingPanel.SetBackgroundColor(InkTranslucency.LoadingColor(this));
        _loadingText = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            TextSize = 24,
        };
        _loadingText.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_loadingText);
        _loadingPanel.AddView(_loadingText, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Center,
        });
        _loadingPanel.Visibility = ViewStates.Gone;
        _playerPage.AddView(_loadingPanel, new FrameLayout.LayoutParams(-1, -1));

        // 失败终态驻留空态（方案书 v77，白屏镜红②）：非瞬时，进失败态显示、离失败态即隐；
        // 样式照 vc53 水墨口径：固定加深水墨底（不跟亮度，失败文案必须醒目）+ 可读性投影
        _emptyState = new TextView(this) { Gravity = GravityFlags.Center };
        _emptyState.SetTextColor(InkTranslucency.TextPrimary(this));
        _emptyState.TextSize = 30;
        _emptyState.SetBackgroundColor(InkTranslucency.PanelColorDeep(this));
        InkTranslucency.ApplyReadabilityShadow(_emptyState);
        _emptyState.SetPadding(40, 24, 40, 24);
        _emptyState.Visibility = ViewStates.Invisible;
        _playerPage.AddView(_emptyState, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Center });

        _root.AddView(_playerPage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // 2. 切台信息条（vc53：顶部居中，自动隐藏；亮度自适应水墨底）
        _toast = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            Visibility = ViewStates.Invisible,
        };
        _toast.SetTextColor(InkTranslucency.TextPrimary(this));
        _toast.SetBackgroundColor(InkTranslucency.InfoColor(this));
        InkTranslucency.ApplyReadabilityShadow(_toast);
        _toast.SetPadding(36, 16, 36, 16);
        _toast.TextSize = 22;
        _root.AddView(_toast, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
            TopMargin = 48,
        });

        // 3. 底部节目单（vc53：官方接口有数据才显示，没数据不硬造；
        // v22 第 13 项·z 序修正：节目单条先入根布局，抽屉盖板永远压在其上，绝不叠字）
        _epgPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _epgPanel.SetBackgroundColor(InkTranslucency.PanelColor(this));
        _epgPanel.SetPadding(36, 18, 36, 22);
        _epgPanel.Visibility = ViewStates.Gone;
        _epgNow = new TextView(this) { TextSize = 18 };
        _epgNow.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(_epgNow);
        _epgNext = new TextView(this) { TextSize = 15 };
        _epgNext.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(_epgNext);
        _epgPanel.AddView(_epgNow);
        _epgPanel.AddView(_epgNext);
        _root.AddView(_epgPanel, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.Left,
        });

        // 4. 频道抽屉（vc53：半透明水墨面板盖画面，宽度随内容、左贴、上下全高）
        _listPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _listPage.SetBackgroundColor(InkTranslucency.PanelColor(this));
        _listPage.Visibility = ViewStates.Gone;
        _root.AddView(_listPage, new FrameLayout.LayoutParams(-1, -1)); // 宽度在 ApplyListWidth 钳制

        // 抽屉页头（vc53：标题 + 台数，同一水平基准）
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetPadding(48, 40, 48, 8);
        var title = new TextView(this) { Text = "TaoLive 频道", TextSize = 24 };
        title.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(title);
        header.AddView(title);
        var count = new TextView(this) { Text = $"  共 {_channels.Count} 路", TextSize = 15 };
        count.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(count);
        header.AddView(count);
        _listPage.AddView(header);

        // 五分类标签行（vc53：页签内容自适应宽度，首页签 LeftMargin=0 与页头文字严格对齐；
        // 当前标签朱砂底高亮，点击/左右键切换）
        _tabViews.Clear();
        var tabRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        tabRow.SetPadding(48, 24, 48, 12);
        int tabIndex = 0;
        foreach (ListTabs.Tab t in Enum.GetValues(typeof(ListTabs.Tab)))
        {
            var tv = new TextView(this) { Text = ListTabs.Label(t), TextSize = 16 };
            tv.SetPadding(12, 10, 12, 10);
            InkTranslucency.ApplyReadabilityShadow(tv);
            tv.Click += (_, _) => SelectTab(t);
            tabRow.AddView(tv, new LinearLayout.LayoutParams(-2, -2) { LeftMargin = tabIndex == 0 ? 0 : 8 });
            _tabViews.Add(tv);
            tabIndex++;
        }
        StyleTabs();
        _listPage.AddView(tabRow);

        // v23 第 1 条：折叠总开关行整行退场——标签行下直接铺频道（vc53 口径）。
        _list = new ListView(this) { Divider = null };
        // 全局透明化（v1.0.8 第15项）：焦点效果=低透明度细边框+极淡底色，不复用实色高亮
        _list.Selector = InkTranslucency.FocusDrawable();
        _list.Adapter = new ChannelListAdapter(this, _channels, _favorites, ch => _precheck.MarkOf(ch), () => _channels[_currentIndex].Id, favoriteOrder: _favStore.Numbers);
        _list.ItemClick += (_, e) =>
        {
            // 行→频道→列表索引（收藏组行也指向同一频道，编号直达语义不变）
            var channel = ((ChannelListAdapter)_list.Adapter!).ChannelAt(e.Position);
            if (channel is null) return;
            // 连不上的台只能看不能切（v1.0.8，用户拍板）：确认键不发起播放
            if (_precheck.MarkOf(channel) == ChannelHealth.Dead)
            {
                ShowToast($"{channel.Name}  暂无信号");
                return;
            }
            PlayAt(_channels.ToList().FindIndex(c => c.Id == channel.Id));
        };
        _listPage.AddView(_list, new LinearLayout.LayoutParams(-1, 0, 1f));
    }

    /// <summary>频道抽屉呼出（vc53 口径）：半透明面板盖画面，播放不中断（不摘输出面，画面在面板下继续）。</summary>
    private void ShowList()
    {
        _listOpen = true;
        _playerPage.Visibility = ViewStates.Visible;
        _listPage.Visibility = ViewStates.Visible;
        _onPlayerPage = false;
        SuppressEpgForDrawer(); // v22 第 13 项：抽屉打开先收节目单条，叠字从根上清掉
        if (_list.Adapter is ChannelListAdapter adapter)
        {
            adapter.Refresh(_favorites, _tab);
            ApplyListWidth(adapter.Rows); // 宽度随内容（vc53 v1.0.8：越小越好，上限半屏、下限 360dp）
        }
        // 焦点默认落当前台行（方案书收口批②）：呼出即定位到正在看的台
        var current = _channels[_currentIndex];
        var pos = (_list.Adapter as ChannelListAdapter)?.PositionOf(current.Id) ?? -1;
        if (pos >= 0) _list.SetSelection(pos);
        _list.RequestFocus();
    }

    /// <summary>抽屉收起回播放（vc53 口径）：画面本就没停，撤面板+亮回节目单条即可。</summary>
    private void ShowPlayer()
    {
        _listOpen = false;
        _listPage.Visibility = ViewStates.Gone;
        _playerPage.Visibility = ViewStates.Visible;
        _onPlayerPage = true;
        EnsureWatchdog();
        // 播放页已就绪而 Surface 早已创建（返回再进场景）：补挂输出面
        if (_surface.Holder?.Surface != null && _surface.Holder.Surface.IsValid)
            _manager.Current?.AttachSurface(_surface.Holder.Surface.Handle);
        // v22 第 13 项：关抽屉后重新亮出节目单条（本台拉到过数据才亮），按 7 秒计时收起
        if (_epgHasData) ShowEpgBar();
    }

    /// <summary>vc53 口径：退出确认弹窗开着=取消继续播放；列表开着=先收抽屉回画面（不强制起播，
    /// 当前台死流才补播，绝不让用户关掉列表面对黑屏）；都收着才弹退出确认。</summary>
    private void ExitOrCollapseList()
    {
        if (_exitDialog != null)
        {
            _exitDialog.Dismiss();
            _exitDialog = null;
            return;
        }
        if (_listOpen)
        {
            var alive = _manager.Current is { State: PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Reconnecting };
            ShowPlayer();
            if (!alive) PlayAt(_currentIndex);
            return;
        }
        ConfirmExit();
    }

    /// <summary>vc53 口径：播放页左右键=列表翻页/呼出——列表没开就呼出，开了整页翻，长按连滚。</summary>
    private void ListNav(int dir)
    {
        if (!_listOpen) { ShowList(); return; }
        if (dir == 0) return;
        int first = _list.FirstVisiblePosition;
        int visible = Math.Max(1, _list.LastVisiblePosition - first + 1);
        int target = Math.Max(0, Math.Min(_list.Count - 1, first + dir * visible));
        _list.SmoothScrollToPosition(target);
    }

    // —— 播放接线 ————————————————————————————————————————————

    private void PlayAt(int index)
    {
        if (index < 0 || index >= _channels.Count) return;
        _currentIndex = index;
        SwitchToCurrent();
        ShowPlayer();
    }

    /// <summary>预检轻探（vc53 口径）：Web 取票口换票成功即服务器可达（结果落 5 分钟缓存）；直连源只取头数据验证，不建视频传输。
    /// 探测结果回灌断网监测（方案书 v82，白屏镜红①）：成功=网络可达；全源失败=一次失败回灌；
    /// 恢复沿触发当前台静默自动重连。</summary>
    private async Task<bool> ProbeChannelAsync(Channel ch)
    {
        var ok = await ProbeChannelCoreAsync(ch).ConfigureAwait(false);
        if (ok)
        {
            if (_netGuard.OnSuccess()) HandleNetRecovered();
        }
        else
        {
            _netGuard.OnFailure();
        }
        return ok;
    }

    private async Task<bool> ProbeChannelCoreAsync(Channel ch)
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

    /// <summary>断网恢复沿（方案书 v82）：网络恢复自动重连失败终态的当前台，静默自愈不打断用户操作；
    /// 退避账窗内不抢跑（既有重试节奏不动），回主线程泵再动会话。</summary>
    private void HandleNetRecovered()
    {
        _main.Post(() =>
        {
            var cur = _manager.Current;
            if (cur is null || cur.State != PlaybackState.Failed) return;
            if (!_manager.TryManualRetry(cur.CurrentChannel?.Id ?? string.Empty, out _)) return;
            PlayAt(_currentIndex); // 静默重连：无 Toast，起播走既有状态机
        });
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

    /// <summary>备援候选健康提示（收口批①轻排序只读账，不写账）：
    /// 候选 URL 对上台目录内来源变体（同址匹配），取健康账结论——活源带核验时间（账新者在前，越旧权重越低）；
    /// 无账/未探返回 null（保持注册表纪律序）。只动同台内排序，不改台间顺序（v28 条款）。</summary>
    private FallbackQueueSource.FallbackHealthHint? FallbackHealthHintOf(SourceEndpoint endpoint)
    {
        long bestAt = 0;
        var best = ChannelHealth.Unknown;
        foreach (var ch in _channels)
        {
            if (ch.Unavailable) continue;
            var hasUrl = false;
            foreach (var s in ch.Sources)
            {
                if (string.Equals(s.Url, endpoint.Url, StringComparison.Ordinal)) { hasUrl = true; break; }
            }
            if (!hasUrl) continue;
            var entry = _precheck.SourceHealthOf(ch);
            if (entry is null) continue;
            if (entry.CheckedAtMs > bestAt) { bestAt = entry.CheckedAtMs; best = entry.State; }
        }
        return best == ChannelHealth.Unknown ? null : new FallbackQueueSource.FallbackHealthHint(best == ChannelHealth.Alive, bestAt);
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
        ShowLoading(ch);    // 亮牌（雷二·延迟亮）：0.8 秒没出首帧才亮，首帧落地即撤
        ShowToast(ch.Name); // 信息条：台名顶条闪现（vc53 口径；节目单走底部节目条）
        RefreshEpg(ch);     // 底部节目单：换台先清场再拉，有数据才亮
    }

    /// <summary>亮牌（vc53 ShowLoading 口径）：换台后 0.8 秒内没出首帧才亮，明确反馈「没死机」；首帧/失败即撤。</summary>
    private void ShowLoading(Channel ch)
    {
        int seq = ++_loadingSeq;
        _loadingText.Text = $"正在连接 {ch.Name}";
        _main.PostDelayed(() =>
        {
            if (seq != _loadingSeq) return; // 已换台/已出画：旧牌作废
            if (_manager.Current is { } s && s.State is PlaybackState.Buffering or PlaybackState.Idle or PlaybackState.Reconnecting)
                _loadingPanel.Visibility = ViewStates.Visible;
        }, 800);
    }

    private void HideLoading()
    {
        _loadingSeq++;
        _loadingPanel.Visibility = ViewStates.Gone;
    }

    // —— 底部节目单（vc53 B 包第 14 项：官方接口，没数据不硬造） ———

    /// <summary>换台即刷新：先清空+隐藏旧节目单条，后台拉官方节目单，有数据才亮底部节目区并按 7 秒计时收起；
    /// 失败/映射外/无数据静默隐藏（vc53 RefreshEpg 同口径）。</summary>
    private void RefreshEpg(Channel ch)
    {
        int seq = ++_epgSeq;
        _epgHasData = false;
        CancelEpgAutoHide();
        if (_epgPanel != null)
        {
            _epgNow!.Text = "";
            _epgNext!.Text = "";
            _epgPanel.Visibility = ViewStates.Gone;
        }
        _ = Task.Run(() =>
        {
            (CntvEpgClient.Entry Now, List<CntvEpgClient.Entry> Next)? result = null;
            try { result = _epg.Fetch(ch.EpgKey); }
            catch { /* 官方接口失败按无数据，不硬造 */ }
            _main.Post(() =>
            {
                if (seq != _epgSeq || _epgPanel is null) return; // 换台后旧节目单作废
                if (result is null)
                {
                    _epgPanel.Visibility = ViewStates.Gone; // 官方无数据不出卡（不硬造）
                    _epgHasData = false;
                    return;
                }
                var (now, next) = result.Value;
                string Clock(DateTime t) => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                _epgNow!.Text = $"{ch.Name} · {Clock(now.Start)} – {Clock(now.End)}  {now.Title}"
                    + $"  ·  还剩 {Math.Max(0, (int)Math.Round((now.End - DateTime.Now).TotalMinutes))} 分钟";
                var upcoming = next.Take(2).ToList();
                _epgNext!.Text = upcoming.Count > 0
                    ? string.Join("   ", upcoming.Select(p => $"{Clock(p.Start)} {p.Title}"))
                    : "";
                _epgNext.Visibility = upcoming.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
                ShowEpgBar();
            });
        });
    }

    /// <summary>v22 第 12 项：节目单条亮出并按 7 秒无操作计时自动收起（换台刷新即重计）。</summary>
    private void ShowEpgBar()
    {
        if (_epgPanel is null) return;
        _epgHasData = true;
        _epgPanel.Visibility = ViewStates.Visible;
        CancelEpgAutoHide();
        _epgHidePending = () =>
        {
            if (_epgPanel != null && !_listOpen) _epgPanel.Visibility = ViewStates.Gone;
        };
        _main.PostDelayed(_epgHidePending, EpgAutoHideMs);
    }

    private void CancelEpgAutoHide()
    {
        if (_epgHidePending != null)
        {
            _main.RemoveCallbacks(_epgHidePending);
            _epgHidePending = null;
        }
    }

    /// <summary>v22 第 13 项：抽屉打开先收节目单条（隐藏+停计时），节目单条压在抽屉下且不叠字。</summary>
    private void SuppressEpgForDrawer()
    {
        CancelEpgAutoHide();
        if (_epgPanel != null) _epgPanel.Visibility = ViewStates.Gone;
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
        _watchdogStopped = false;
        Pump();
        void Pump()
        {
            _main.PostDelayed(() =>
            {
                if (_watchdogStopped) return; // OnDestroy 后停泵（闪退镜黄⑦）：不泄漏 Activity
                // 抽屉开着画面也在播：自救泵跟会话走，不跟面板键位走（vc53 画面常驻口径）
                if (_manager.Current is not null) _manager.Tick();
                Pump();
            }, 3000);
        }
    }

    // —— 遥控器键位（vc53 交互口径 + 2.0 频道号直达：菜单呼/收抽屉、上下换台、左右切页签、确认收藏） ——

    public override bool OnKeyDown([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        // TCL 设置键厂商码（真机实测 4077）：单击呼出软件设置面板、长按跳系统设置（v1.0.8 拍板口径）。
        // 单击/长按在 KeyUp / OnKeyLongPress 分流，这里只消费按键并复位长按标记。
        if ((int)keyCode == 4077)
        {
            _settingsLongFired = false;
            return true;
        }

        // 退出确认弹窗开着时按键全接管：左右键在「取消/退出」间显式切换
        //（焦点切换不依赖系统焦点搜索，真机实测不可靠）；确认键=按当前焦点执行；返回键=取消继续播放（vc53 v1.0.9 口径）
        if (_exitDialog != null)
        {
            switch (keyCode)
            {
                case Keycode.Back:
                    _exitDialog?.Dismiss(); // vc53 v1.0.9 口径：返回键=取消弹窗，继续播放
                    _exitDialog = null;
                    return true;
                case Keycode.DpadLeft:
                    _exitNo?.RequestFocus();
                    return true;
                case Keycode.DpadRight:
                    _exitYes?.RequestFocus();
                    return true;
                case Keycode.DpadCenter or Keycode.Enter:
                    if (_exitDialog.Window?.CurrentFocus is TextView focused) focused.PerformClick();
                    else _exitNo?.PerformClick(); // 焦点异常兜底：落「取消」绝不误退
                    return true;
                default:
                    return true; // 其余按键弹窗期间不穿透，防止误触列表/换台
            }
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

        // 关于面板（v47 开源合规）：键位同自诊断——上下滚许可文本、确认点「关闭」、其余吞掉；单钮无左右移焦
        if (_licenseOverlay != null)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Menu:
                    CloseLicensePanel();
                    return true;
                case Keycode.DpadUp or Keycode.DpadDown when _licenseScroll != null:
                {
                    int dir = keyCode == Keycode.DpadUp ? -1 : 1;
                    if (e is { RepeatCount: 0 })
                        _licenseScroll.SmoothScrollBy(0, dir * (int)(_licenseScroll.Height * 0.8f));
                    else
                        _licenseScroll.ScrollBy(0, dir * Dp(56));
                    return true;
                }
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

        // 收藏排序模式（v1.0.8 第14项）：独立状态机接管键位，退出恢复默认键位
        if (_sortMode)
        {
            HandleSortKey(keyCode);
            return true;
        }

        // 频道抽屉开着（vc53 抽屉键位；画面在面板下继续）：返回=退出确认、菜单=收抽屉、左右切页签、
        // 上下交列表焦点导航、数字键直达（2.0 保留）
        if (_listOpen)
        {
            switch (keyCode)
            {
                case Keycode.Back or Keycode.Escape or Keycode.Menu:
                    ExitOrCollapseList(); // vc53 口径：菜单/返回=先收抽屉回画面，收完再按返回才弹退出确认
                    return true;
                case Keycode.DpadLeft or Keycode.DpadRight:
                    SelectTab(ListTabs.NextTab(_tab, keyCode == Keycode.DpadRight)); // 左右键切标签（vc53 抽屉口径）
                    return true;
                case >= Keycode.Num0 and <= Keycode.Num9:
                    FeedDigit((int)keyCode - (int)Keycode.Num0); // 频道号直达（2.0 保留：抽屉内也可直按）
                    return true;
            }
            return base.OnKeyDown(keyCode, e); // 上下键/确认交 ListView 原生焦点导航与点击
        }

        // 播放页（抽屉收起）
        switch (keyCode)
        {
            case Keycode.DpadUp:
                DebouncedSwitch(Neighbor(-1));
                return true;
            case Keycode.DpadDown:
                DebouncedSwitch(Neighbor(+1));
                return true;
            case Keycode.DpadCenter or Keycode.Enter:
                // 备源耗尽（Failed）时 OK=原地重试，接退避账（收口批③）：指数退避管重试节奏
                if (_manager.Current?.State == PlaybackState.Failed)
                {
                    if (_manager.TryManualRetry(_manager.Current.CurrentChannel?.Id ?? string.Empty, out var waitMs))
                        PlayAt(_currentIndex);
                    else
                        ShowToast(SwitchHud.RetryBackoff((int)Math.Ceiling(waitMs / 1000.0)));
                }
                else
                {
                    ToggleFavoriteOnCurrent(); // vc53：确认键收藏当前台
                }
                return true;
            case Keycode.DpadLeft:
                ListNav(+1); // vc53 口径：列表没开就呼出到对应位置，开了就翻页
                return true;
            case Keycode.DpadRight:
                ListNav(-1);
                return true;
            case Keycode.Menu or Keycode.Settings or Keycode.Info:
                ShowList(); // 菜单键呼抽屉（vc53：呼出同步无延迟）
                return true;
            case Keycode.Back or Keycode.Escape:
                ExitOrCollapseList(); // vc53 口径：播放页返回=退出确认弹窗（返回管退出、菜单管呼列表）
                return true;
            case >= Keycode.Num0 and <= Keycode.Num9:
                FeedDigit((int)keyCode - (int)Keycode.Num0); // 频道号直达（W5 保留）
                return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyUp([GeneratedEnum] Keycode keyCode, KeyEvent? e)
    {
        // 设置键单击在 KeyUp 分流（KeyDown 只复位长按标记）
        if ((int)keyCode == 4077)
        {
            if (!_settingsLongFired)
            {
                // 铁律：任何时刻最多一个层——设置面板开着才允许收起；列表/弹窗/其他面板开着时设置键不生效
                if (_settingsPanelOpen) ToggleSettingsPanel();
                else if (_exitDialog == null && !_listOpen && _advOverlay == null && _diagOverlay == null
                         && _crashOverlay == null && _licenseOverlay == null)
                    ToggleSettingsPanel();
            }
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
            // 铁律同上：任何层开着时长按设置键不跳系统设置，防叠层
            if (_exitDialog == null && !_listOpen && _advOverlay == null && _diagOverlay == null
                && _crashOverlay == null && _licenseOverlay == null)
                OpenSystemSettings();
            return true;
        }
        if (keyCode is Keycode.DpadCenter or Keycode.Enter)
        {
            if (!_onPlayerPage)
            {
                // 列表内长按确认=进入收藏排序（v1.0.8 第14项 vc53 口径；非收藏台亮牌说明不硬进）
                TryEnterSortMode();
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

    /// <summary>退出确认弹窗（v1.0.9 用户拍板；vc22 水墨浮层样式整套搬运）：窗底透明+半透明水墨面板，
    /// 「退出」=朱砂细边框+朱砂极淡底，「取消」=纸白细边框+极淡底；焦点导航、默认焦点落「取消」、
    /// 返回键=第二次返回直接退出（vc53 v1.0.12 口径）。</summary>
    private void ConfirmExit()
    {
        if (_exitDialog != null) return; // 弹窗已开着：不重复弹
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = Dp(20);
        box.SetPadding(pad * 2, pad * 2, pad * 2, pad);
        box.SetBackgroundColor(InkTranslucency.PanelColor(this)); // 半透明水墨底，与抽屉面板同源同号
        box.SetMinimumWidth(Dp(480)); // 居中浮层不因文案缩成窄条

        var msg = new TextView(this) { Text = "确认退出 TaoLive？", TextSize = 20 };
        msg.SetTextColor(InkTranslucency.TextPrimary(this)); // 统一 Token，暗屏自动提白
        InkTranslucency.ApplyReadabilityShadow(msg);
        box.AddView(msg);

        var note = new TextView(this) { Text = "退出后开机自启仍可用，随时回来看", TextSize = 13 };
        note.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(note);
        box.AddView(note, new LinearLayout.LayoutParams(-2, -2) { TopMargin = Dp(8) });

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var lp = new LinearLayout.LayoutParams(0, -2, 1f) { TopMargin = pad, LeftMargin = pad / 2, RightMargin = pad / 2 };
        // vc22 第 11 项：按钮改 TextView 自绘——ROM Material 按钮的焦点高亮/系统着色从根上去掉
        var no = new TextView(this) { Text = "取消（继续看）", Gravity = GravityFlags.Center };
        no.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
        no.Click += (_, _) => { _exitDialog?.Dismiss(); _exitDialog = null; };
        var yes = new TextView(this) { Text = "退出", Gravity = GravityFlags.Center };
        yes.SetPadding(pad / 2, Dp(14), pad / 2, Dp(14));
        yes.Click += (_, _) => { _exitDialog?.Dismiss(); _exitDialog = null; Finish(); };
        // v1.0.10 修复④：左右键在「退出」与「取消（继续看）」间切换——显式指路，不靠焦点搜索碰运气
        no.Id = View.GenerateViewId();
        yes.Id = View.GenerateViewId();
        no.NextFocusRightId = yes.Id;
        yes.NextFocusLeftId = no.Id;
        _exitNo = no;
        _exitYes = yes;

        // vc22 第 11 项统一按钮样式：聚焦=效果层（退出朱砂/取消纸白）+主色字；常驻=水墨深底+次色字
        var idleBg = new ColorDrawable(Color.Argb(96, InkTranslucency.InkR, InkTranslucency.InkG, InkTranslucency.InkB));
        void StyleDialogButton(TextView b, GradientDrawable focusDrawable)
        {
            b.Focusable = true;
            b.Clickable = true;
            b.Background = idleBg;
            b.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(b);
            b.FocusChange += (_, ev) =>
            {
                if (ev.HasFocus) { b.Background = focusDrawable; b.SetTextColor(InkTranslucency.TextPrimary(this)); }
                else if (b.Background == focusDrawable) { b.Background = idleBg; b.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary)); }
            };
        }
        StyleDialogButton(no, InkTranslucency.FocusDrawable());
        StyleDialogButton(yes, InkTranslucency.AccentFocusDrawable());
        buttons.AddView(no, lp);
        buttons.AddView(yes, lp);
        box.AddView(buttons);

        // 绑定契约 SetView/SetCancelable/Create 为可空返回：改语句式构造，窗口可空即放弃弹窗（实际不发生）
        var builder = new AlertDialog.Builder(this);
        builder.SetView(box);
        builder.SetCancelable(true);
        var dialog = builder.Create();
        if (dialog is null) return;
        _exitDialog = dialog;
        dialog.Show();
        // vc22 第 11 项：全屏自管——窗底透明+内容铺满，露出的是播放画面，与抽屉浮在画面上观感一致
        var win = dialog.Window!;
        win.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        win.SetLayout(-1, -1);
        win.SetFlags(WindowManagerFlags.LayoutInScreen, WindowManagerFlags.LayoutInScreen);
        no.RequestFocus(); // 默认焦点落「取消（继续看）」——防误退更稳
        dialog.SetOnCancelListener(new DialogActionPeer(() => { _exitDialog = null; _exitNo = null; _exitYes = null; }));
        dialog.SetOnDismissListener(new DismissActionPeer(() => { _exitDialog = null; _exitNo = null; _exitYes = null; }));
    }

    /// <summary>标准监听器适配（vc53 v1.0.9）：把 Android 弹窗回调接成普通 C# 动作，避免匿名监听类散落。</summary>
    private sealed class DialogActionPeer : Java.Lang.Object, global::Android.Content.IDialogInterfaceOnCancelListener
    {
        private readonly Action _act;
        public DialogActionPeer(Action act) { _act = act; }
        public void OnCancel(global::Android.Content.IDialogInterface? dialog) => _act();
    }

    private sealed class DismissActionPeer : Java.Lang.Object, global::Android.Content.IDialogInterfaceOnDismissListener
    {
        private readonly Action _act;
        public DismissActionPeer(Action act) { _act = act; }
        public void OnDismiss(global::Android.Content.IDialogInterface? dialog) => _act();
    }

    // —— 收藏 ——

    /// <summary>收藏当前台（vc53 确认键收藏口径）：播放页按确认即收藏/取消，信息条给结果。</summary>
    private void ToggleFavoriteOnCurrent()
    {
        var ch = _channels[_currentIndex];
        if (!_favorites.Add(ch.Id)) _favorites.Remove(ch.Id);
        else _favStore.Toggle(ch.Number);
        PersistFavorites();
        ShowToast(_favorites.Contains(ch.Id)
            ? $"{IndexOfChannel(ch, _channels)}  {ch.Name}  ★ 已收藏"
            : $"{IndexOfChannel(ch, _channels)}  {ch.Name}  已取消收藏");
        if (_listOpen && _list.Adapter is ChannelListAdapter adapter) adapter.Refresh(_favorites, _tab);
    }

    private static int IndexOfChannel(Channel target, IReadOnlyList<Channel> list)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == target.Id) return i;
        return -1;
    }

    private void ToggleFavoriteSelected()
    {
        var pos = _list.SelectedItemPosition;
        if (pos < 0) return;
        if (_list.Adapter is not ChannelListAdapter adapter) return;
        var channel = adapter.ChannelAt(pos);
        if (channel is null) return;
        if (!_favorites.Add(channel.Id)) _favorites.Remove(channel.Id);
        else _favStore.Toggle(channel.Number);
        PersistFavorites();
        adapter.Refresh(_favorites, _tab, _favStore.Numbers);
    }

    /// <summary>收藏状态与顺序一并持久化（StringSet 管「是否收藏」；order 串管顺序，vc53 口径）。</summary>
    private void PersistFavorites()
    {
        GetSharedPreferences("favorites", FileCreationMode.Private)!.Edit()!
            .PutStringSet("ids", _favorites)!
            .PutString("order", _favStore.Serialize())!
            .Apply();
    }

    // —— 收藏排序模式（vc53 v1.0.8 第14项 逐成员对照基线原文） ——

    /// <summary>入口：列表内长按确认键且焦点在收藏台上；非收藏台亮牌说明，不硬进。</summary>
    private void TryEnterSortMode()
    {
        if (_list.Adapter is not ChannelListAdapter adapter) return;
        var pos = _list.SelectedItemPosition;
        if (pos < 0 || pos >= adapter.Count) return;
        var row = adapter.ChannelAt(pos);
        if (row is null || !_favStore.IsFavorite(row.Number))
        {
            ShowToast("请在收藏组内长按确认键排序");
            return;
        }
        _sortMode = true;
        _sortChannel = row;
        _sortBackup.Clear();
        _sortBackup.AddRange(_favStore.Numbers);
        ShowToast("排序中：上下移动，确认放下");
        RefreshSortList();
    }

    /// <summary>排序键位（vc53 原文口径）：上下逐位挪、左右按屏快速挪、确认放下保存、返回取消复原、信息键一键置顶。</summary>
    private void HandleSortKey(Keycode keyCode)
    {
        if (_sortChannel is null) { _sortMode = false; return; }
        int num = _sortChannel.Number;
        switch (keyCode)
        {
            case Keycode.DpadUp: _favStore.MoveUp(num); RefreshSortList(); break;
            case Keycode.DpadDown: _favStore.MoveDown(num); RefreshSortList(); break;
            case Keycode.DpadLeft:
            case Keycode.DpadRight:
            {
                int page = Math.Max(1, _list.LastVisiblePosition - _list.FirstVisiblePosition + 1);
                int dir = keyCode == Keycode.DpadLeft ? +page : -page; // 左=向下按屏挪，右=向上（vc53 原文）
                _favStore.MoveBy(num, dir);
                RefreshSortList();
                break;
            }
            case Keycode.DpadCenter or Keycode.Enter: // 放下并立即保存（持久化）
                _sortMode = false;
                PersistFavorites();
                _sortChannel = null;
                ShowToast("排序已保存");
                RefreshSortList();
                break;
            case Keycode.Back or Keycode.Menu: // 取消复原
                _sortMode = false;
                _favStore.Restore(string.Join(",", _sortBackup));
                _sortChannel = null;
                ShowToast("已取消排序");
                RefreshSortList();
                break;
            case Keycode.Info: // 一键置顶
                _favStore.MoveToTop(num);
                RefreshSortList();
                break;
        }
    }

    /// <summary>排序中/排序后重建列表（焦点跟随被排序的台，vc53 RefreshSortList 口径）。</summary>
    private void RefreshSortList()
    {
        if (!_listOpen) return;
        if (_list.Adapter is not ChannelListAdapter adapter) return;
        adapter.Refresh(_favorites, _tab, _favStore.Numbers);
        // 焦点跟随被排序的台，不落到底层
        var pos = PositionOfChannel(_sortChannel);
        if (pos >= 0) _list.SetSelection(pos);
    }

    private int PositionOfChannel(Channel? channel)
    {
        if (channel is null || _list.Adapter is not ChannelListAdapter adapter) return -1;
        for (var i = 0; i < adapter.Count; i++)
            if (adapter.ChannelAt(i)?.Id == channel.Id) return i;
        return -1;
    }

    private int Neighbor(int delta)
    {
        var i = (_currentIndex + delta) % _channels.Count;
        return i < 0 ? i + _channels.Count : i;
    }

    // —— 五标签切换（vc53 抽屉口径：当前标签朱砂底高亮） ——

    private void SelectTab(ListTabs.Tab tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        if (_list.Adapter is ChannelListAdapter adapter) adapter.Refresh(_favorites, tab);
        StyleTabs();
    }

    /// <summary>页签行着色（vc53 StyleTabs 同款）：当前标签朱砂底浅字，其余极淡底。</summary>
    private void StyleTabs()
    {
        for (int i = 0; i < _tabViews.Count; i++)
        {
            var active = (ListTabs.Tab)i == _tab;
            _tabViews[i].SetBackgroundColor(active
                ? Color.ParseColor("#8A3B34") // vc53 StyleTabs：朱砂底
                : Color.Transparent);
            _tabViews[i].SetTextColor(Color.ParseColor(active ? "#F7F5EE" : InkTranslucency.TextSecondary));
        }
    }

    /// <summary>列表宽度随内容（vc53 ApplyListWidth + ListMetrics v1.0.8 口径）：越小越好，下限 360dp、上限半屏。</summary>
    private void ApplyListWidth(IReadOnlyList<Row> rows)
    {
        float density = Resources!.DisplayMetrics!.Density;
        float Estimate(string s, float sizeDp)
        {
            float px = sizeDp * density;
            float w = 0;
            foreach (var c in s) w += c > 127 ? px : px * 0.55f;
            return w;
        }
        float max = 0;
        foreach (var r in rows)
        {
            if (r.Header != null)
                max = Math.Max(max, Estimate(r.Header, 20));
            else if (r.Channel != null)
            {
                // 行宽 = 左右 padding(48×2) + 号(3字符) + 间距(32) + 星(1字符) + 间距(24) + 台名 + 状态标识
                var w = 96 + Estimate(new string('0', 3), 20) + 32
                        + Estimate("★", 20) + 24 + Estimate(r.Channel.Name, 22)
                        + Estimate("●", 20) + 24;
                max = Math.Max(max, w);
            }
        }
        // ListMetrics（vc53 v1.0.8）：行内容 + 48px 留白，下限 360dp，上限半屏
        float total = max + 48 * density;
        float lo = 360f * density;
        float hi = Resources.DisplayMetrics.WidthPixels / 2f;
        int width = (int)Math.Clamp(total, Math.Min(lo, hi), Math.Max(lo, hi));
        _listPage.LayoutParameters = new FrameLayout.LayoutParams(width, FrameLayout.LayoutParams.MatchParent, GravityFlags.Left);
    }

    /// <summary>健康点刷新节流（方案书收口批②：更新节流 5 秒）。只读账刷新展示，不写账。</summary>
    private void OnPrecheckHealthChanged(string epgKey, ChannelHealth state)
    {
        if (_onPlayerPage) return; // 抽屉未开不见列表
        var now = System.Environment.TickCount64;
        if (now - _healthLastAtMs < 5000) return;
        _healthLastAtMs = now;
        _main.Post(() =>
        {
            if (_list.Adapter is not ChannelListAdapter adapter) return;
            adapter.Refresh(_favorites, _tab);
            ApplyListWidth(adapter.Rows); // vc53 RefreshListStatuses：重建行即重算宽度
        });
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
        if (_digits.Length >= 3) return; // 位数封顶（闪退镜红① 11-15）：连按不溢出，编号三位内直达
        _digits = (_digits + d.ToString()).TrimStart('0');
        ShowToast(_digits);
        _main.RemoveCallbacks(_digitCommit!); // 先摘旧回调再换新（闪退镜黄⑨）：已 Dispose 的 runnable 不再执行，窗口内连按不提前换台
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

    /// <summary>信息条（vc53 InfoBar 口径）：顶部居中，自动隐藏；文案即给即显。</summary>
    private void ShowToast(string text)
    {
        _toast.Text = text;
        _toast.Visibility = ViewStates.Visible;
        _main.RemoveCallbacks(_toastHide);
        _main.PostDelayed(_toastHide, 2200);
    }

    /// <summary>失败终态驻留空态（v77）：显示至离开失败态；样式照 vc53 水墨面板口径。</summary>
    private void ShowEmptyState(string text)
    {
        _emptyState.Text = text;
        _emptyState.Visibility = ViewStates.Visible;
    }

    private void HideEmptyState()
    {
        if (_emptyState.Visibility != ViewStates.Visible) return;
        _emptyState.Visibility = ViewStates.Invisible;
    }

    // —— 列表适配器（vc53 ChannelAdapter 皮整套搬运）：
    //      真实台标（无台标=首字占位）+ 编号 + 星标 + 台名 + 健康点；当前台整行强调底；
    //      分组行/空态行 vc53 同款着色。 ——

    private sealed class Row
    {
        public string? Header;      // 分组行显示文本（频道行 null）
        public bool IsNote;         // 空态行（次色平铺，不作分组标题渲染）
        public Channel? Channel;    // 频道行归属台
        public bool DeadOnly;       // 连不上置底组内台（只能看不能切）
    }

    private sealed class ChannelListAdapter : BaseAdapter
    {
        private readonly Context _ctx;
        private readonly IReadOnlyList<Channel> _channels;
        private readonly Func<Channel, ChannelHealth> _markOf; // ④健康点：只读账（预检 MarkOf 三态）
        private readonly Func<string?> _currentId;
        private HashSet<string> _favorites;
        private ListTabs.Tab _tab;
        private List<Row> _rows;
        private IReadOnlyList<int> _favOrder; // 收藏顺序（vc53 排序口径：收藏页签按此顺序铺开）

        public ChannelListAdapter(Context ctx, IReadOnlyList<Channel> channels, HashSet<string> favorites,
            Func<Channel, ChannelHealth> markOf, Func<string?> currentId, ListTabs.Tab tab = ListTabs.Tab.All,
            IReadOnlyList<int>? favoriteOrder = null)
        {
            _ctx = ctx;
            _channels = channels;
            _favorites = favorites;
            _markOf = markOf;
            _currentId = currentId;
            _tab = tab;
            _favOrder = favoriteOrder ?? Array.Empty<int>();
            _rows = BuildRows(channels, favorites, tab, markOf, _favOrder);
        }

        /// <summary>当前装配的行（宽度随内容重算用）。</summary>
        public IReadOnlyList<Row> Rows => _rows;

        /// <summary>行装配（vc53 DrawerSupport.BuildRows 口径一比一）：
        /// 收藏标签=收藏顺次铺开（按 favoriteOrder，未入册的按频道表序缀后）+空态行；其余标签=收藏成组置顶，无信号分组收口（全部标签），
        /// 装配完无任何频道行补空态行（不白屏）。</summary>
        private static List<Row> BuildRows(IReadOnlyList<Channel> channels, HashSet<string> favorites,
            ListTabs.Tab tab, Func<Channel, ChannelHealth> markOf, IReadOnlyList<int> favOrder)
        {
            var rows = new List<Row>();
            // 收藏顺序铺开（vc53 排序口径）：order 在前的在前，未入 order 的收藏台按频道表序缀后
            var favOrdered = new List<Channel>();
            foreach (var n in favOrder)
            {
                var c = channels.FirstOrDefault(x => x.Number == n);
                if (c != null && favorites.Contains(c.Id) && favOrdered.All(x => x.Id != c.Id)) favOrdered.Add(c);
            }
            foreach (var c in channels)
                if (favorites.Contains(c.Id) && favOrdered.All(x => x.Id != c.Id)) favOrdered.Add(c);

            IEnumerable<Channel> baseList = tab switch
            {
                // vc53 v22 第 7 项（老板拍板）：CGTN 并回「央视」；专项随拍板并入央视口径；
                // 「地方台」只放真城市台（City/自有源），不再兜杂烩
                ListTabs.Tab.Cctv => channels.Where(c => c.Category is ChannelCategory.Cctv or ChannelCategory.Cgtn or ChannelCategory.Special),
                ListTabs.Tab.Satellite => channels.Where(c => c.Category == ChannelCategory.Provincial),
                ListTabs.Tab.Local => channels.Where(c => c.Category is ChannelCategory.City or ChannelCategory.UserSource),
                _ => channels,
            };
            var baseL = baseList.Where(c => !c.Unavailable).ToList();

            if (tab == ListTabs.Tab.Favorite)
            {
                foreach (var c in favOrdered) rows.Add(new Row { Channel = c });
                if (favOrdered.Count == 0)
                    rows.Add(new Row { Header = "收藏夹是空的，对着频道按确认可收藏", IsNote = true }); // v22 第 6/15 项
                return rows;
            }

            // 收藏成组置顶（按收藏顺序结果）
            if (baseL.Any(c => favorites.Contains(c.Id)))
            {
                rows.Add(new Row { Header = "★ 收藏" });
                foreach (var c in baseL.Where(c => favorites.Contains(c.Id))) rows.Add(new Row { Channel = c });
            }
            foreach (var c in baseL.Where(c => !favorites.Contains(c.Id)))
                rows.Add(new Row { Channel = c });

            // 全部标签收口：无信号分组置底
            if (tab == ListTabs.Tab.All)
            {
                var off = baseL.Where(c => markOf(c) == ChannelHealth.Dead).ToList();
                if (off.Count > 0)
                {
                    rows.Add(new Row { Header = "── 无信号分组 ──" });
                    foreach (var c in off) rows.Add(new Row { Channel = c, DeadOnly = true });
                }
            }

            // v22 第 6/15 项：装配完没有任何频道行 → 空态提示
            if (!rows.Any(r => r.Channel != null))
                rows.Add(new Row { Header = "该分类暂无频道", IsNote = true });
            return rows;
        }

        /// <summary>行对应的频道；分组头行返回空。</summary>
        public Channel? ChannelAt(int position) =>
            position >= 0 && position < _rows.Count ? _rows[position].Channel : null;

        /// <summary>频道所在行位置（呼出列表焦点落当前台行用）；不在表内返回 -1。</summary>
        public int PositionOf(string channelId)
        {
            for (var i = 0; i < _rows.Count; i++)
                if (_rows[i].Channel?.Id == channelId) return i;
            return -1;
        }

        /// <summary>收藏/标签/健康账变化后重建行（保持选中位置不跳）；favoriteOrder 传排序后的收藏顺序。</summary>
        public void Refresh(HashSet<string> favorites, ListTabs.Tab? tab = null, IReadOnlyList<int>? favoriteOrder = null)
        {
            _favorites = favorites;
            _tab = tab ?? _tab;
            if (favoriteOrder != null) _favOrder = favoriteOrder;
            _rows = BuildRows(_channels, favorites, _tab, _markOf, _favOrder);
            NotifyDataSetChanged();
        }

        public override int Count => _rows.Count;
        public override Java.Lang.Object GetItem(int position) => new Java.Lang.String(position.ToString());
        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var row = _rows[position];
            var ctx = _ctx;
            var line = convertView as LinearLayout ?? new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            line.RemoveAllViews();
            line.SetPadding(48, 20, 48, 20);

            void Style(TextView tv, string color, float size)
            {
                tv.SetTextColor(Color.ParseColor(color));
                tv.TextSize = size;
            }

            if (row.Header != null)
            {
                var h = new TextView(ctx) { Text = row.Header };
                bool isNote = row.IsNote; // vc53 v22 第 15 项：空态行次色平铺，不作分组标题渲染
                Style(h, isNote ? InkTranslucency.TextSecondary : "#A9CFEC", isNote ? 15 : 18);
                InkTranslucency.ApplyReadabilityShadow(h);
                line.AddView(h);
                line.SetBackgroundColor(Color.Transparent);
                return line;
            }

            var ch = row.Channel!;
            var res = ctx.Resources!;
            var density = res.DisplayMetrics!.Density;
            // B 包第 12 项：行首真实台标（vc53 同源内置资源）；无内置台标→首字占位；无信号行不放台标
            var logoRes = ChannelLogoMap.ResourceName(ch.Name);
            if (!row.DeadOnly && logoRes != null)
            {
                try
                {
                    var resId = res.GetIdentifier(logoRes, "drawable", ctx.PackageName);
                    if (resId != 0)
                    {
                        var img = new ImageView(ctx);
                        img.SetAdjustViewBounds(true);
                        img.SetMaxHeight((int)(28 * density));
                        img.SetImageResource(resId);
                        line.AddView(img, new LinearLayout.LayoutParams(-2, (int)(28 * density))
                        { RightMargin = 16, Gravity = GravityFlags.CenterVertical });
                    }
                }
                catch { /* 台标渲染失败不碍选台 */ }
            }
            if (!row.DeadOnly && logoRes == null)
            {
                var ph = new TextView(ctx) { Text = ch.Name.Substring(0, 1) };
                Style(ph, InkTranslucency.TextSecondary, 14);
                ph.Gravity = GravityFlags.Center;
                ph.SetBackgroundColor(Color.ParseColor("#3A4750"));
                line.AddView(ph, new LinearLayout.LayoutParams((int)(40 * density), (int)(28 * density))
                { RightMargin = 16, Gravity = GravityFlags.CenterVertical });
            }
            if (row.DeadOnly)
            {
                line.AddView(new View(ctx), new LinearLayout.LayoutParams(0, 1) { RightMargin = 16 }); // 无信号行行首不放台标（v4 定稿状态2）
            }

            var num = new TextView(ctx) { Text = $"{IndexInTable(ch),3}", Gravity = GravityFlags.Right };
            Style(num, InkTranslucency.TextSecondary, 20);
            InkTranslucency.ApplyReadabilityShadow(num);
            num.SetPadding(0, 0, 32, 0);
            line.AddView(num);

            var star = new TextView(ctx) { Text = _favorites.Contains(ch.Id) ? "★" : " " };
            Style(star, "#F0C563", 20);
            InkTranslucency.ApplyReadabilityShadow(star);
            star.SetPadding(0, 0, 24, 0);
            line.AddView(star);

            var name = new TextView(ctx) { Text = ch.Name };
            var isCur = _currentId() == ch.Id;
            Style(name, isCur ? "#FFFFFF" : InkTranslucency.TextSecondary, 22);
            InkTranslucency.ApplyReadabilityShadow(name);
            if (isCur)
            {
                line.SetBackgroundColor(InkTranslucency.CurrentRowFill(ctx)); // v1.0.9 第③项：当前台整行强调底
                name.Typeface = Typeface.DefaultBold!;
            }
            else
            {
                line.SetBackgroundColor(Color.Transparent);
            }
            line.AddView(name);

            // 可连状态标识（v1.0.8 预检握手）：绿点可用 · 灰点检测中/未探 · 红叉连不上
            var (mark, color) = _markOf(ch) switch
            {
                ChannelHealth.Alive => ("●", "#7ED182"),
                ChannelHealth.Dead => ("✕", "#E97F7F"),
                ChannelHealth.Checking => ("●", "#B5C4D0"),
                _ => ("●", "#8894A0"),
            };
            var dot = new TextView(ctx) { Text = mark };
            Style(dot, color, 18);
            InkTranslucency.ApplyReadabilityShadow(dot);
            dot.SetPadding(24, 0, 0, 0);
            line.AddView(dot);
            return line;
        }

        /// <summary>编号=整表顺序（一期口径：编号永久直达，FeedDigit 同款语义）。</summary>
        private int IndexInTable(Channel c)
        {
            for (var i = 0; i < _channels.Count; i++)
                if (_channels[i].Id == c.Id) return i + 1;
            return 0;
        }
    }
}
