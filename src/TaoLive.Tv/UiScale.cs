namespace TaoLive.Tv;

/// <summary>
/// 界面缩放档（vc53 显示大小口径，2.0 自实现）：T(系数)=基准字号×缩放系数。
/// 显示大小档 大=4/9、中=8/27、小=16/81（默认中；真机反馈批#14 整体×2/3），prefs 键 ui_scale / taolive_prefs；
/// 改档由 CycleDisplaySize 走 Recreate() 重建即时生效（正在看的台自动接回）。
/// </summary>
public static class UiScale
{
    public const float BaseSize = 36f; // 基准字号（sp），系数 1.0 时的实字号
    public static float Factor { get; set; } = 8f / 27f; // 默认「中」

    public static float T(float f) => f * BaseSize * Factor;
}
