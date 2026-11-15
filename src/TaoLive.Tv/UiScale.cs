namespace TaoLive.Tv;

/// <summary>
/// 界面缩放档（vc53 显示大小口径，2.0 自实现）：T(系数)=基准字号×缩放系数。
/// 显示大小档 大=2/3、中=4/9、小=8/27（默认中），prefs 键 ui_scale / taolive_prefs；
/// 改档由 CycleDisplaySize 走 Recreate() 重建即时生效（正在看的台自动接回）。
/// </summary>
public static class UiScale
{
    public const float BaseSize = 36f; // 基准字号（sp），系数 1.0 时的实字号
    public static float Factor { get; set; } = 4f / 9f; // 默认「中」

    public static float T(float f) => f * BaseSize * Factor;
}
