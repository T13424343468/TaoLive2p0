namespace TaoLive.Tv;

/// <summary>
/// 界面缩放档（vc53 显示大小口径，2.0 自实现）：T(系数)=基准字号×缩放系数，Sp(裸字号)/Px(裸像素)随档缩放。
/// 显示大小档（真机反馈批 #61 尺寸回抬令 2026-11-15 19:3x 老板拍板「中档=肉眼最舒适」）：
/// 大=2/3(24sp)、中=5/9(20sp)、小=1/3(12sp)，默认中；覆盖 #14 的整体×2/3 压缩口径。
/// Sp(裸字号)/Px(裸像素)随档缩放（#16-#19 全元素跟档口径保留），prefs 键 ui_scale / taolive_prefs；
/// 改档由 CycleDisplaySize 走 Recreate() 即时生效——真机反馈批#15 原地换皮：会话跨重建存活，正在看的台不断流。
/// </summary>
public static class UiScale
{
    public const float BaseSize = 36f; // 基准字号（sp），系数 1.0 时的实字号
    public static float Factor { get; set; } = 5f / 9f; // 默认「中」=基准 20sp（#61 回抬令）

    public static float T(float f) => f * BaseSize * Factor;

    /// <summary>
    /// 原始 sp 字号随档缩放（真机反馈批#15 尺寸令 2026-11-15 老板拍板：所有页面所有元素跟三档走）——
    /// 此前散落的裸 TextSize（Toast/节目单/抽屉页头/退出弹窗等）只随密度、不随档位，换「小」档后字缩间距不缩。
    /// </summary>
    public static float Sp(float v) => v * Factor;

    /// <summary>原始 px 尺寸随档缩放（同尺寸令）：内边距/间距/行高等裸 px 值统一过这里。</summary>
    public static int Px(float v) => (int)(v * Factor + 0.5f);
}
