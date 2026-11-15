using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Views.Animations;
using Android.Widget;
using TaoLive.Foundation;
using TaoLive.Session;

namespace TaoLive.Tv;

/// <summary>
/// 软件设置面板 + 三子页（崩溃日志全屏 / 自诊断 / 进阶调优）+ 显示大小档 + 三级跳。
/// UI 布局与文案照 vc53（eef464b MainActivity 对应段）；2.0 只接真实接线面：
/// 预检握手已接真（常驻循环预检，MainActivity.StartPrecheckLoop）；
/// 开机自启已接真（方案书收口批②：BootReceiver+开关行，BootReceiver.OnReceive 读同一份偏好）；
/// 后台预热开关真实控制开机全量预热与换台邻台预取。
/// </summary>
public partial class MainActivity
{
    // ---- 设置面板 ----
    private LinearLayout? _settingsPanel;
    private bool _settingsPanelOpen;
    private LinearLayout? _settingsList;
    private ScrollView? _settingsScroll;
    private bool _settingsLongFired;

    // ---- 全屏子页（自管面板：非 AlertDialog——对话框浮动容器吃边，真机全屏仍缩半屏） ----
    private FrameLayout? _crashOverlay;
    private ScrollView? _crashScroll;
    private TextView? _crashCleanBtn, _crashCloseBtn;
    private FrameLayout? _diagOverlay;
    private ScrollView? _diagScroll;
    private TextView? _diagExportBtn, _diagCloseBtn;
    private FrameLayout? _advOverlay;
    private FrameLayout? _licenseOverlay;
    private ScrollView? _licenseScroll;
    private TextView? _licenseCloseBtn;

    private ISharedPreferences _prefs = default!;

    private int Dp(float v) => (int)(v * Resources!.DisplayMetrics!.Density + 0.5f);

    // —— 崩溃/失败打点（接真实事件：逐次换源） ——

    private void OnSessionFailover(object? sender, FailoverEventArgs e)
    {
        CrashNet.Record($"[换源] {e.From.Url} → {e.To.Url}（{e.Reason}）· 第 {e.Attempt} 次");
        // 换源黑屏埋点起点（黑屏镜黄④ 11-15）：仅当前台记账，热备攒流不掺
        if (sender is PlaybackSession s && ReferenceEquals(s, _manager.Current))
        {
            _failoverAtMs = System.Environment.TickCount64;
        }
    }

    // —— ⑨「去系统设置」三级跳（v1.0.9 拍板口径）：对口设置页 → 系统设置总入口 → 文字指引 ——

    private void OpenSystemSettings()
    {
        foreach (var action in new[]
        {
            global::Android.Provider.Settings.ActionDisplaySettings, // ①对口设置页
            global::Android.Provider.Settings.ActionSettings,         // ②系统设置总入口
        })
        {
            try
            {
                StartActivity(new Intent(action));
                return;
            }
            catch (Exception ex) { CrashNet.Record($"[MemoryLevel] 内存分级调不动降级下一级：{ex.Message}"); }
        }
        ShowToast("请按遥控器「设置/菜单」键进入电视系统设置"); // ③文字指引
    }

    // —— 软件设置面板（设置键 4077 单击呼出、长按跳系统设置） ——

    private void ToggleSettingsPanel()
    {
        if (_settingsPanelOpen) CloseSettingsPanel(); else OpenSettingsPanel();
    }

    private void OpenSettingsPanel()
    {
        _settingsPanel ??= BuildSettingsPanel();
        _settingsPanelOpen = true;
        RefreshSettingsRows();
        _settingsPanel.Visibility = ViewStates.Visible;
        // 从底部滑入（透明底，尽量少遮挡画面）
        var slide = new TranslateAnimation(0f, 0f, 420f, 0f) { Duration = 220 };
        _settingsPanel.StartAnimation(slide);
        // 焦点必须看得见：弹出后下一帧把焦点直接放到面板第一个可选行
        //（容器 RequestFocus 落空时焦点会留在底下的层里——真机「设置页光标选不中」根因）
        _settingsPanel.Post(() =>
        {
            if (_settingsList is null) return;
            for (int i = 0; i < _settingsList.ChildCount; i++)
                if (_settingsList.GetChildAt(i) is View row && row.Focusable) { row.RequestFocus(); break; }
        });
    }

    private void CloseSettingsPanel()
    {
        _settingsPanelOpen = false;
        if (_settingsPanel != null) _settingsPanel.Visibility = ViewStates.Gone;
    }

    private LinearLayout BuildSettingsPanel()
    {
        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetBackgroundColor(InkTranslucency.PanelColor(this));
        panel.SetPadding(Dp(40), Dp(28), Dp(40), Dp(36));
        panel.SetMinimumWidth(Dp(360));
        panel.Visibility = ViewStates.Gone;

        // 面板内套 ScrollView：行多时整体可滚，面板高度不超出屏幕
        var maxHeight = (int)(Resources!.DisplayMetrics!.HeightPixels * 0.9f);
        _settingsScroll = new MaxHeightScrollView(this, maxHeight);
        _settingsList = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _settingsScroll.AddView(_settingsList, new FrameLayout.LayoutParams(-2, -2));

        panel.AddView(_settingsScroll, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
        });
        _root.AddView(panel, new FrameLayout.LayoutParams(-2, -2)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
        });
        return panel;
    }

    /// <summary>内容高度封顶的 ScrollView——行多则滚动；滚动容器不截焦点（否则上下键变成滚动面板）。</summary>
    private sealed class MaxHeightScrollView : ScrollView
    {
        private readonly int _maxHeight;
        public MaxHeightScrollView(Context c, int maxH) : base(c)
        {
            _maxHeight = maxH;
            HorizontalScrollBarEnabled = false;
            VerticalScrollBarEnabled = false;
            Focusable = false;
        }
        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            base.OnMeasure(widthMeasureSpec,
                MeasureSpec.MakeMeasureSpec(_maxHeight, MeasureSpecMode.AtMost));
        }
    }

    private void RefreshSettingsRows()
    {
        if (_settingsPanel is null || _settingsList is null) return;
        // 重建前记录焦点行索引（行数固定、索引稳定），重建后焦点回原行
        int focusedIdx = -1;
        var focused = CurrentFocus;
        for (int i = 0; i < _settingsList.ChildCount; i++)
            if (_settingsList.GetChildAt(i) == focused) { focusedIdx = i; break; }
        _settingsList.RemoveAllViews();

        void AddLabel(string text)
        {
            var tv = new TextView(this) { Text = text, TextSize = UiScale.T(0.9375f) };
            tv.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(tv);
            tv.SetPadding(Dp(24), Dp(18), Dp(24), Dp(6));
            _settingsList!.AddView(tv);
        }
        void AddRow(string text, Action onClick, string color, Action? onLongClick = null)
        {
            var tv = new TextView(this) { Text = text, TextSize = UiScale.T(1.1875f) };
            tv.SetTextColor(Color.ParseColor(color));
            tv.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
            tv.Focusable = true;
            var focusDrawable = InkTranslucency.FocusDrawable();
            tv.FocusChange += (_, e) =>
            {
                if (e.HasFocus) tv.Background = focusDrawable;
                else if (tv.Background == focusDrawable) tv.Background = null;
            };
            tv.Click += (_, _) => onClick();
            if (onLongClick is not null) tv.LongClick += (_, _) => onLongClick();
            _settingsList!.AddView(tv);
        }

        AddLabel("── 软件设置 ──");
        // ⑤开机自启（方案书收口批②：BootReceiver+开关行）：安装默认开启（照 vc53 v22 第 15 项），与 BootReceiver 同读一份偏好
        AddRow($"开机自启：{(_prefs.GetBoolean("autostart_enabled", true) ? "开" : "关")}（确认切换 · 长按跳系统自启管理）",
            ToggleAutoStart, "#F7F5EE", OpenAutoStartSettings);
        // ⑥开机默认台（方案书收口批②）：设置项指定台，未设进频道列表首位；确认=循环下一个台（含「未指定」档）
        AddRow($"开机默认台：{DefaultChannelLabel()}（确认切换）", CycleDefaultChannel, "#F7F5EE");
        // 预检握手（vc53 v1.0.8 一比一）：真实控制常驻循环预检（关=循环同停）；
        // 收口批④开关合流：预热与预检两个开关管同一条车道必出一个死开关——只留本开关，
        // 预热（开机全量预热+换台邻台预取）随预检同起同停，不再单设开关。
        AddRow($"预检握手：{(_precheckEnabled ? "开" : "关")}（确认键切换 · 预热随此开关同起同停）", TogglePrecheck, "#F7F5EE");
        AddRow($"显示大小：{DisplaySizeLabel()}（确认键切换）", CycleDisplaySize, "#F7F5EE");
        AddRow("崩溃日志（点按查看）", ShowCrashLog, "#C9D6E2");
        AddRow("自诊断（点按查看）", ShowDiagPanel, "#C9D6E2");
        AddRow("进阶调优（点按进入）", ShowAdvancedTuningPanel, "#C9D6E2");
        AddRow("关于：TaoLive · 开源许可 Apache-2.0（点按查看）", ShowLicensePanel, "#C9D6E2");
        AddLabel("返回键收起 · 长按设置键直达系统设置");

        // 重建完成，焦点回原行；滚动容器内光标行滚入可视区
        if (focusedIdx >= 0 && focusedIdx < _settingsList.ChildCount)
        {
            var row = _settingsList.GetChildAt(focusedIdx);
            if (row is not null && row.Focusable)
            {
                row.RequestFocus();
                row.RequestRectangleOnScreen(new Rect(0, row.Top, row.Right, row.Bottom), true);
            }
        }
    }

    /// <summary>⑤开机自启开关（方案书收口批②）：与 BootReceiver 同读一份偏好（默认开）。</summary>
    private void ToggleAutoStart()
    {
        var next = !_prefs.GetBoolean("autostart_enabled", true);
        _prefs.Edit()?.PutBoolean("autostart_enabled", next)?.Apply();
        RefreshSettingsRows();
        ShowToast(next ? "开机自启：开" : "开机自启：关");
    }

    /// <summary>长按自启行跳系统自启管理（照 vc53 三级降级口径，遥控器放行入口）。</summary>
    private void OpenAutoStartSettings() => OpenSystemSettings();

    /// <summary>⑥默认台当前文案（方案书收口批②）：未指定=列表第一个。</summary>
    private string DefaultChannelLabel()
    {
        var saved = _prefs.GetString("default_channel", null);
        if (string.IsNullOrEmpty(saved)) return "未指定（列表第一个）";
        var ch = _channels.FirstOrDefault(c => c.Id == saved);
        return ch?.Name ?? "未指定（列表第一个）";
    }

    /// <summary>⑥确认循环选默认台（方案书收口批②）：未指定→频道 1→频道 2→…→回未指定。</summary>
    private void CycleDefaultChannel()
    {
        var saved = _prefs.GetString("default_channel", null);
        var channels = _channels.ToList();
        var idx = string.IsNullOrEmpty(saved) ? -1 : channels.FindIndex(c => c.Id == saved);
        var next = idx + 1 >= channels.Count ? -1 : idx + 1;
        _prefs.Edit()?.PutString("default_channel", next < 0 ? "" : channels[next].Id)?.Apply();
        RefreshSettingsRows();
        ShowToast(next < 0 ? "开机默认台：未指定（列表第一个）" : $"开机默认台：{channels[next].Name}");
    }

    private void TogglePrecheck()
    {
        _precheckEnabled = !_precheckEnabled;
        _prefs.Edit()?.PutBoolean("precheck_enabled", _precheckEnabled)?.Apply();
        if (_precheckEnabled) StartPrecheckLoop();
        else StopPrecheckLoop();
        RefreshSettingsRows();
        ShowToast(_precheckEnabled ? "预检握手：开" : "预检握手：关");
    }

    // —— 显示大小（vc42 定稿梯子）：大=2/3、中=4/9、小=8/27；面板/字/间距同一系数整体缩放 ——

    private static readonly float[] DisplaySizeSteps = { 2f / 3f, 4f / 9f, 8f / 27f };
    private static readonly string[] DisplaySizeLabels = { "大", "中", "小" };

    private static int DisplaySizeIndex(float f)
    {
        for (int i = 0; i < DisplaySizeSteps.Length; i++)
            if (Math.Abs(DisplaySizeSteps[i] - f) < 0.01f) return i;
        return 1; // 未知值回落「中」
    }

    private string DisplaySizeLabel() => DisplaySizeLabels[DisplaySizeIndex(
        _prefs.GetFloat("ui_scale", 4f / 9f))];

    /// <summary>大→中→小循环，档位记忆；整界面重建即时生效（正在看的台自动接回）。</summary>
    private void CycleDisplaySize()
    {
        int next = (DisplaySizeIndex(_prefs.GetFloat("ui_scale", 4f / 9f)) + 1) % DisplaySizeSteps.Length;
        _prefs.Edit()?.PutFloat("ui_scale", DisplaySizeSteps[next])?.Apply();
        Recreate(); // 重建走 OnCreate 重定标，全套令牌按新档位重算
    }

    // —— 崩溃日志全屏面板（自管：窗底透明+半透明水墨面板，不露 ROM 钢蓝灰） ——

    /// <summary>按尾偏移读日志尾部（fix53 口径），主线程 IO 有界。</summary>
    private static string ReadLogTail(string filePath, int maxBytes) => DiagLog.ReadTail(filePath, maxBytes);

    private void ShowCrashLog()
    {
        try
        {
            var path = new Java.IO.File(FilesDir, "taolive_crash.log");
            if (!path.Exists()) { ShowToast("暂无崩溃日志"); return; } // 空态提示
            var text = ReadLogTail(path.AbsolutePath!, 64 * 1024);
            if (text.Length > 16000) text = "…\n" + text[^16000..];
            var (box, pad) = BuildOverlayPanel("崩溃日志（全屏）", text, out var scroll);
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var clean = new TextView(this) { Text = "一键清理", Gravity = GravityFlags.Center };
            clean.SetPadding(pad, Dp(14), pad, Dp(14));
            clean.Click += (_, _) =>
            {
                try { System.IO.File.Delete(path.AbsolutePath!); ShowToast("崩溃日志已清空"); }
                catch { ShowToast("清理失败，请重试"); }
                CloseCrashPanel();
            };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad, Dp(14), pad, Dp(14));
            close.Click += (_, _) => CloseCrashPanel();
            clean.Id = View.GenerateViewId();
            close.Id = View.GenerateViewId();
            clean.NextFocusRightId = close.Id; // 左右键显式互指，不靠焦点搜索
            close.NextFocusLeftId = clean.Id;
            StyleOverlayButton(clean, InkTranslucency.AccentFocusDrawable()); // 一键清理=朱砂（危险项）
            StyleOverlayButton(close, InkTranslucency.FocusDrawable()); // 关闭=纸白
            int btnH = Dp(52); // 按钮行固定底栏，不论日志多少行按钮始终在屏幕底部
            btns.AddView(clean, new LinearLayout.LayoutParams(0, btnH, 1f));
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });

            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true }; // 吃掉触摸防穿透
            overlay.SetBackgroundColor(InkTranslucency.PanelColor(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _crashOverlay = overlay;
            _crashScroll = scroll;
            _crashCleanBtn = clean;
            _crashCloseBtn = close;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel(); // 打开日志页即收起设置页，两层页面不叠显
            close.RequestFocus(); // 默认焦点落「关闭」——防误清日志
        }
        catch { ShowToast("日志读取失败"); }
    }

    private void CloseCrashPanel()
    {
        if (_crashOverlay is null) return;
        _root.RemoveView(_crashOverlay);
        _crashOverlay.Dispose();
        _crashOverlay = null;
        _crashScroll = null;
        _crashCleanBtn = null;
        _crashCloseBtn = null;
        OpenSettingsPanel();
    }

    /// <summary>子页面板骨架：标题 + 权重铺满的滚动正文 + 统一内边距。按钮行由各页自挂（键位各不同）。</summary>
    private (LinearLayout Box, int Pad) BuildOverlayPanel(string title, string body, out ScrollView scroll)
    {
        int pad = Dp(24);
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.SetPadding(pad, pad, pad, pad);
        box.SetBackgroundColor(InkTranslucency.PanelColor(this));
        var titleView = new TextView(this) { Text = title, TextSize = UiScale.T(1.375f) };
        titleView.SetTextColor(InkTranslucency.TextPrimary(this));
        InkTranslucency.ApplyReadabilityShadow(titleView);
        box.AddView(titleView, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = pad });
        scroll = new ScrollView(this);
        var bodyView = new TextView(this) { Text = body, TextSize = UiScale.T(0.9375f) };
        bodyView.SetTextIsSelectable(false);
        bodyView.Focusable = false; // 正文不抢焦点，焦点只在按钮间转
        bodyView.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
        InkTranslucency.ApplyReadabilityShadow(bodyView);
        scroll.AddView(bodyView);
        box.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f)); // 权重铺满
        return (box, pad);
    }

    /// <summary>自管面板按钮统一描边（崩溃日志/自诊断/进阶调优共用，ui-remote 归一条款）。</summary>
    private void StyleOverlayButton(TextView b, GradientDrawable focusDrawable)
    {
        var idleBg = new ColorDrawable(Color.Argb(96, InkTranslucency.InkR, InkTranslucency.InkG, InkTranslucency.InkB));
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

    // —— 自诊断（S3 口径）：五类现成数据只读一页 + 导出 ——

    private void ShowDiagPanel()
    {
        try
        {
            var (box, pad) = BuildOverlayPanel("自诊断（只读）", BuildDiagSnapshot(), out var scroll);
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var export = new TextView(this) { Text = "导出日志", Gravity = GravityFlags.Center };
            export.SetPadding(pad, Dp(14), pad, Dp(14));
            export.Click += (_, _) =>
            {
                var path = DiagLog.Export(ApplicationContext!, BuildDiagSnapshot());
                CrashNet.Record(path is null ? "[诊断] 导出失败" : $"[诊断] 已导出：{path}");
                ShowToast(path is null ? "导出失败，请重试" : "已导出到文件管理器可见目录");
            };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad, Dp(14), pad, Dp(14));
            close.Click += (_, _) => CloseDiagPanel();
            export.Id = View.GenerateViewId();
            close.Id = View.GenerateViewId();
            export.NextFocusRightId = close.Id;
            close.NextFocusLeftId = export.Id;
            StyleOverlayButton(export, InkTranslucency.FocusDrawable());
            StyleOverlayButton(close, InkTranslucency.FocusDrawable());
            int btnH = Dp(52);
            btns.AddView(export, new LinearLayout.LayoutParams(0, btnH, 1f));
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });
            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true };
            overlay.SetBackgroundColor(InkTranslucency.PanelColor(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _diagOverlay = overlay;
            _diagScroll = scroll;
            _diagExportBtn = export;
            _diagCloseBtn = close;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel();
            close.RequestFocus();
        }
        catch (Exception ex) { CrashNet.Record($"[诊断] 打开失败：{ex.Message}"); ShowToast("诊断打开失败"); }
    }

    private void CloseDiagPanel()
    {
        if (_diagOverlay is null) return;
        _root.RemoveView(_diagOverlay);
        _diagOverlay.Dispose();
        _diagOverlay = null;
        _diagScroll = null;
        _diagExportBtn = null;
        _diagCloseBtn = null;
        OpenSettingsPanel();
    }

    // —— 关于面板（v47 开源合规）：Apache-2.0 许可文本只读一页，来源随包 Assets/NOTICE.txt ——

    private void ShowLicensePanel()
    {
        try
        {
            string body;
            try
            {
                // 许可文本随包走 Assets（NET Android 默认打包 Assets/ 目录），读不到按降级文案不挡面板
                using var stream = Assets!.Open("NOTICE.txt");
                using var reader = new StreamReader(stream);
                body = reader.ReadToEnd();
            }
            catch
            {
                body = "TaoLive · Apache License 2.0\n\n许可文本缺失（NOTICE.txt 未随包），完整条款见项目仓库 LICENSE 文件。";
            }
            var (box, pad) = BuildOverlayPanel("关于：TaoLive", body, out var scroll);
            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(pad, Dp(14), pad, Dp(14));
            close.Click += (_, _) => CloseLicensePanel();
            close.Id = View.GenerateViewId();
            StyleOverlayButton(close, InkTranslucency.FocusDrawable());
            int btnH = Dp(52);
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = pad });
            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true };
            overlay.SetBackgroundColor(InkTranslucency.PanelColor(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _licenseOverlay = overlay;
            _licenseScroll = scroll;
            _licenseCloseBtn = close;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel();
            close.RequestFocus();
        }
        catch (Exception ex) { CrashNet.Record($"[关于] 打开失败：{ex.Message}"); ShowToast("关于打开失败"); }
    }

    private void CloseLicensePanel()
    {
        if (_licenseOverlay is null) return;
        _root.RemoveView(_licenseOverlay);
        _licenseOverlay.Dispose();
        _licenseOverlay = null;
        _licenseScroll = null;
        _licenseCloseBtn = null;
        OpenSettingsPanel();
    }

    /// <summary>五类摘要组装：全部经现有公开接口/公开成员取数，不落设备标识、不新增采集。</summary>
    private string BuildDiagSnapshot()
    {
        // 版本：PackageInfo 现成读取；摘要只显 VersionName（versionCode 字段 API28 起废弃，不碰旧读法）
        string versionLine;
        try
        {
            // GetPackageInfo(string,int) 旧重载：全部支持档（minSdk 24）可用，只读 VersionName 不碰 versionCode
#pragma warning disable CA1422, CS0612, CS0619
            var pkg = PackageManager!.GetPackageInfo(PackageName!, 0)!;
#pragma warning restore CA1422, CS0612, CS0619
            var abi = Build.SupportedAbis is { Count: > 0 } abis ? abis[0] : "?";
            versionLine = $"版本：v{pkg.VersionName} · {abi}";
        }
        catch (Exception ex) { versionLine = "版本：读取失败 " + ex.Message; }

        // 时钟：当前墙钟 + 单调已运行时长 + 时区偏移
        var up = TimeSpan.FromMilliseconds(System.Environment.TickCount64);
        var clockLine = $"时钟：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · 已运行 {(int)up.TotalHours}h{up.Minutes:D2}m · " +
                        $"时区 UTC{TimeZoneInfo.Local.BaseUtcOffset:hh\\:mm}";

        // 网络：活动网络现成判定
        string networkLine;
        try
        {
            var cm = GetSystemService(ConnectivityService) as Android.Net.ConnectivityManager;
            networkLine = $"网络：{(cm?.ActiveNetwork != null ? "就绪" : "不可用（无活动网络）")}";
        }
        catch (Exception ex) { networkLine = "网络：读取失败 " + ex.Message; }

        // 播放：当前会话真实状态（频道/状态/换源账/线路位）
        var sessionLine = _manager.Current is { } s
            ? $"播放：{s.CurrentChannel?.Name ?? "-"} · 状态 {s.State} · 换源 {s.FailoverCount} 次 · 线路 {s.CurrentLineIndex + 1}/{s.LineCount}"
            : "播放：当前无会话";

        // 内核与水位：ExoPlayer（Media3）+ 按内存自适应的深水保满水位线
        string kernelLine;
        try
        {
            var am = (ActivityManager?)GetSystemService(ActivityService);
            var mi = new ActivityManager.MemoryInfo();
            am?.GetMemoryInfo(mi);
            kernelLine = $"内核：ExoPlayer（Media3）· 缓冲水位 {BufferPolicy.MaxWatermarkSeconds(mi.TotalMem)} 秒 · 总内存 {mi.TotalMem / (1024L * 1024L * 1024L)}GB";
        }
        catch (Exception ex) { kernelLine = "内核：读取失败 " + ex.Message; }

        // 运行日志：从日志尾部统计现成打点（无日志文件按空账计，不挡诊断页）
        string tail = "";
        try { tail = DiagLog.ReadTail(System.IO.Path.Combine(FilesDir!.AbsolutePath!, "taolive_crash.log"), 256 * 1024); }
        catch { }
        int failCount = 0, crashCount = 0;
        foreach (var line in tail.Split('\n'))
        {
            if (line.Contains("[ConnectFailure]", StringComparison.Ordinal)) failCount++;
            else if (line.Contains("未捕获异常", StringComparison.Ordinal)) crashCount++;
        }
        var startupLine = $"运行日志 {LogSizeLabel(tail.Length)}（截尾视图）· 取流失败 {failCount} 条 · 未捕获异常 {crashCount} 条";

        return DiagLog.BuildSnapshot(versionLine, clockLine, networkLine, sessionLine, kernelLine, startupLine,
            DiagLog.RecentFailuresFromLog(tail, 10));
    }

    /// <summary>
    /// 运行日志大小显示口径（真机反馈批 2026-11-15 修）：原整除 1024，日志不足 1KB 显示「0KB」，
    /// 与崩溃日志页「有内容」看似矛盾（实为同一文件同一账，纯显示口径）；改 KB 向上取整，空账明示「暂无日志」。
    /// </summary>
    private static string LogSizeLabel(int chars) =>
        chars == 0 ? "0KB（暂无日志）" : $"{Math.Max(1, (int)Math.Ceiling(chars / 1024.0))}KB";

    // —— 进阶调优面板：旋钮全量可调 + 每项讲清作用 + 一键还原 ——

    /// <summary>旋钮档位（确认键循环）。改动即存偏好并即时生效（方案书收口令：ProxyPort 假旋钮已删）。</summary>
    private static readonly int[] AdvGateSteps = { 1, 2, 3, 5, 8, 12 };
    private static readonly int[] AdvReprobeMinSteps = { 1, 2, 5, 10, 15 };
    private static readonly int[] AdvCacheSecSteps = { 15, 30, 60, 120, 300 };
    private static readonly int[] AdvRetrySecSteps = { 30, 60, 120, 300, 600 }; // 起步；封顶 16 分钟（AdvancedTuning.DefaultRetryMaxMs 口径）

    private static int NextStep(int[] steps, int current)
    {
        for (int i = 0; i < steps.Length; i++)
            if (steps[i] > current) return steps[i];
        return steps[^1]; // 到最大档即停（边界限位，不回绕）
    }

    private static int PrevStep(int[] steps, int current)
    {
        for (int i = steps.Length - 1; i >= 0; i--)
            if (steps[i] < current) return steps[i];
        return steps[0]; // 到最小档即停
    }

    private void ShowAdvancedTuningPanel()
    {
        try
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            int padAdv = Dp(24);
            box.SetPadding(padAdv, padAdv, padAdv, padAdv);
            box.SetBackgroundColor(InkTranslucency.PanelColorDeep(this)); // 底色加深一档，不跟画面亮度走
            var title = new TextView(this) { Text = "进阶调优", TextSize = UiScale.T(1.5f) };
            title.SetTextColor(InkTranslucency.TextPrimary(this));
            InkTranslucency.ApplyReadabilityShadow(title);
            box.AddView(title, new LinearLayout.LayoutParams(-2, -2) { BottomMargin = Dp(10) });

            var scroll = new ScrollView(this);
            var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
            scroll.AddView(body);
            box.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

            var refreshers = new List<Action>();

            // 加减号交互：每项「− 当前值 +」——值纯展示不落光标，光标只在 −/+ 两个焦点位间移；
            // 加减真改值且有界：预设最大/最小档，到边界即停不回绕，边界处对应钮灰显。
            void AddKnob(string desc, int[] steps, Func<string> valueText, Action<int> set, Func<int> cur)
            {
                var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetPadding(Dp(24), Dp(12), Dp(24), Dp(2));

                var minus = new TextView(this) { Text = "−", Gravity = GravityFlags.Center };
                var value = new TextView(this) { Gravity = GravityFlags.Start }; // 值靠左与说明对齐
                var plus = new TextView(this) { Text = "+", Gravity = GravityFlags.Center };

                int btn = Dp(40);
                void StyleKnob(TextView b)
                {
                    StyleOverlayButton(b, InkTranslucency.AccentFocusDrawable()); // 选中态加重：朱砂底
                    b.TextSize = UiScale.T(1.25f);
                }
                StyleKnob(minus);
                StyleKnob(plus);
                value.Focusable = false;
                value.TextSize = UiScale.T(1.25f);
                value.SetTextColor(Color.ParseColor("#F7F5EE"));
                value.SetPadding(Dp(12), 0, Dp(12), 0);
                InkTranslucency.ApplyReadabilityShadow(value);

                minus.Click += (_, _) => { if (cur() > steps[0]) { set(PrevStep(steps, cur())); SaveAdvancedTuning(); Refresh(); } };
                plus.Click += (_, _) => { if (cur() < steps[^1]) { set(NextStep(steps, cur())); SaveAdvancedTuning(); Refresh(); } };
                row.AddView(minus, new LinearLayout.LayoutParams(btn, btn));
                row.AddView(value, new LinearLayout.LayoutParams(0, -2, 1f));
                row.AddView(plus, new LinearLayout.LayoutParams(btn, btn));
                body.AddView(row);

                var sub = new TextView(this) { Text = desc, TextSize = UiScale.T(0.9375f) };
                sub.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
                InkTranslucency.ApplyReadabilityShadow(sub);
                sub.SetPadding(Dp(24), 0, Dp(24), Dp(8));
                body.AddView(sub);

                void Refresh()
                {
                    value.Text = valueText();
                    minus.Alpha = cur() <= steps[0] ? 0.35f : 1f; // 边界灰显：到头即停
                    plus.Alpha = cur() >= steps[^1] ? 0.35f : 1f;
                }
                refreshers.Add(Refresh);
                Refresh();
            }

            // 4 个旋钮（每项下挂一行人话作用说明）：写入 AdvancedTuning 即时生效（播放链读门面）
            AddKnob("网络解析出毛病时，撑几次失败才启动自救判别——调低自救快但易误伤，调高稳但自救慢",
                AdvGateSteps,
                () => $"自愈门槛：连败 {AdvancedTuning.SelfHealFailGate} 次（默认 3）",
                v => AdvancedTuning.Apply(v, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs),
                () => AdvancedTuning.SelfHealFailGate);
            AddKnob("自救起来之后，隔多久偷偷试一次能否回直连——调低回得快但探测勤，调高省流量回得慢",
                AdvReprobeMinSteps,
                () => $"自愈回探：{AdvancedTuning.SelfHealReprobeMs / 60_000} 分钟（默认 5）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, v * 60_000L, AdvancedTuning.ListCacheSec, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs),
                () => (int)(AdvancedTuning.SelfHealReprobeMs / 60_000));
            AddKnob("频道清单数据在手上留多久不重取——留久省流量，留短换台信息更新鲜",
                AdvCacheSecSteps,
                () => $"清单缓存：{AdvancedTuning.ListCacheSec} 秒（默认 60）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, v, AdvancedTuning.RetryBaseMs, AdvancedTuning.RetryMaxMs),
                () => AdvancedTuning.ListCacheSec);
            AddKnob("被源服务器拒绝后退多远再试——跟风控博弈的分寸：调急了容易挨打，调钝了台恢复慢（封顶 16 分钟）",
                AdvRetrySecSteps,
                () => $"重试节奏：起步 {AdvancedTuning.RetryBaseMs / 1000} 秒（默认 120）",
                v => AdvancedTuning.Apply(AdvancedTuning.SelfHealFailGate, AdvancedTuning.SelfHealReprobeMs, AdvancedTuning.ListCacheSec, v * 1000L, AdvancedTuning.RetryMaxMs),
                () => (int)(AdvancedTuning.RetryBaseMs / 1000));

            var tip = new TextView(this)
            {
                Text = "左右键切 − / +，确认键调档；到头即停。\n改坏了随时点「一键还原默认」回出厂值，全部即时生效。",
                TextSize = UiScale.T(0.9375f),
            };
            tip.SetTextColor(Color.ParseColor(InkTranslucency.TextSecondary));
            InkTranslucency.ApplyReadabilityShadow(tip);
            tip.SetPadding(Dp(24), Dp(6), Dp(24), Dp(10));
            body.AddView(tip);

            var btns = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var reset = new TextView(this) { Text = "一键还原默认", Gravity = GravityFlags.Center };
            reset.SetPadding(padAdv / 2, Dp(14), padAdv / 2, Dp(14));
            reset.Click += (_, _) =>
            {
                AdvancedTuning.ResetToDefaults();
                SaveAdvancedTuning();
                foreach (var rf in refreshers) rf(); // 原位刷新值与灰显，不整页重建（焦点不丢）
                ShowToast("进阶调优：已全部还原出厂默认");
            };
            var close = new TextView(this) { Text = "关闭", Gravity = GravityFlags.Center };
            close.SetPadding(padAdv / 2, Dp(14), padAdv / 2, Dp(14));
            close.Click += (_, _) => CloseAdvancedPanel();
            reset.Id = View.GenerateViewId();
            close.Id = View.GenerateViewId();
            reset.NextFocusRightId = close.Id;
            close.NextFocusLeftId = reset.Id;
            StyleOverlayButton(reset, InkTranslucency.AccentFocusDrawable()); // 还原=朱砂（改动性按钮）
            StyleOverlayButton(close, InkTranslucency.FocusDrawable());
            int btnH = Dp(52);
            btns.AddView(reset, new LinearLayout.LayoutParams(0, btnH, 1f));
            btns.AddView(close, new LinearLayout.LayoutParams(0, btnH, 1f));
            box.AddView(btns, new LinearLayout.LayoutParams(-1, -2) { TopMargin = padAdv });

            var overlay = new FrameLayout(this) { Clickable = true, LongClickable = true };
            overlay.SetBackgroundColor(InkTranslucency.PanelColorDeep(this));
            overlay.AddView(box, new FrameLayout.LayoutParams(-1, -1));
            _advOverlay = overlay;
            _root.AddView(overlay, new FrameLayout.LayoutParams(-1, -1));
            CloseSettingsPanel();
            close.RequestFocus(); // 默认焦点落「关闭」，防误改
        }
        catch (Exception ex) { CrashNet.Record($"[进阶调优] 打开失败：{ex.Message}"); ShowToast("进阶调优打开失败"); }
    }

    private void CloseAdvancedPanel()
    {
        if (_advOverlay is null) return;
        _root.RemoveView(_advOverlay);
        _advOverlay.Dispose();
        _advOverlay = null;
        OpenSettingsPanel();
    }

    private void SaveAdvancedTuning() // 旋钮写偏好（跨启动记忆；Apply 即生效）
    {
        _prefs.Edit()
            ?.PutInt("adv_fail_gate", AdvancedTuning.SelfHealFailGate)
            ?.PutInt("adv_reprobe_min", (int)(AdvancedTuning.SelfHealReprobeMs / 60_000))
            ?.PutInt("adv_cache_sec", AdvancedTuning.ListCacheSec)
            ?.PutInt("adv_retry_base_sec", (int)(AdvancedTuning.RetryBaseMs / 1000))
            ?.PutInt("adv_retry_max_min", (int)(AdvancedTuning.RetryMaxMs / 60_000))
            ?.Apply();
    }
}
