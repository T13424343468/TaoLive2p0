using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Widget;

namespace TaoLive.Tv;

/// <summary>
/// 水墨透明视觉基线（2.0 简化版，口径照 vc53）：
/// 焦点/确认效果=低透明细边框+极淡底，绝不复用实色高亮；面板半透明水墨深底；
/// 文字带可读性深色投影，亮暗画面上都与背景拉开对比。统一一套参数全局复用。
/// </summary>
public static class InkTranslucency
{
    // 水墨底色（与 2.0 主界面深底同系）
    public const byte InkR = 0x2B, InkG = 0x33, InkB = 0x3B;
    public const byte PaperR = 0xF7, PaperG = 0xF5, PaperB = 0xEE; // 效果层浅色（细边框/极淡底）
    // 强调色（水墨 Accent #8A5C3D）：危险项/改动项聚焦共用
    public const byte AccentR = 0x8A, AccentG = 0x5C, AccentB = 0x3D;

    /// <summary>面板/子页底色透明度：半透明水墨，播放画面隐约可见。</summary>
    public const int PanelAlpha = 76;

    /// <summary>进阶调优页底色：加深一档，不跟画面亮度走（vc41 口径）。</summary>
    public const int PanelDeepAlpha = 130;

    /// <summary>焦点效果层底色透明度。</summary>
    public const int FocusFillAlpha = 64;

    /// <summary>焦点细边框透明度：低配电视肉眼清晰可辨。</summary>
    public const int FocusStrokeAlpha = 160;

    /// <summary>次要文字色（vc53 第③项提亮口径）。</summary>
    public const string TextSecondary = "#C9D6E2";

    public static Color PanelColor(Context ctx) => Color.Argb(PanelAlpha, InkR, InkG, InkB);

    public static Color PanelColorDeep(Context ctx) => Color.Argb(PanelDeepAlpha, InkR, InkG, InkB);

    /// <summary>主文字色（纸白）。</summary>
    public static Color TextPrimary(Context ctx) => Color.Argb(255, PaperR, PaperG, PaperB);

    /// <summary>可读性兜底：文字加深色投影，亮暗底上都清晰（标准 TextView API，全机型可用）。</summary>
    public static void ApplyReadabilityShadow(TextView tv) =>
        tv.SetShadowLayer(2f, 0, 1f, Color.Argb(180, 0x1A, 0x1F, 0x24));

    /// <summary>焦点/确认效果层：极淡底 + 细边框圆角（统一一套，面板行/按钮全局复用）。</summary>
    public static GradientDrawable FocusDrawable()
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftRight, new int[] { 0, 0 });
        d.SetColor(Color.Argb(FocusFillAlpha, PaperR, PaperG, PaperB));
        d.SetStroke(1, Color.Argb(FocusStrokeAlpha, PaperR, PaperG, PaperB));
        d.SetCornerRadius(8f);
        return d;
    }

    /// <summary>危险项/改动项聚焦效果层：朱砂细边框+朱砂极淡底（同 FocusDrawable 同构换色）。</summary>
    public static GradientDrawable AccentFocusDrawable()
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftRight, new int[] { 0, 0 });
        d.SetColor(Color.Argb(FocusFillAlpha, AccentR, AccentG, AccentB));
        d.SetStroke(1, Color.Argb(FocusStrokeAlpha, AccentR, AccentG, AccentB));
        d.SetCornerRadius(8f);
        return d;
    }
}
