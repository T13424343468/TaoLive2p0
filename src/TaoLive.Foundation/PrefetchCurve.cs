namespace TaoLive.Foundation;

/// <summary>
/// 预取双曲线常量（老板 2026-11-15 02:39 确认原样继承）。
/// 数据来源：PlaybackParams 已镜像同值，此处为唯一事实源，Model 端引用本值。
/// </summary>
public static class PrefetchCurve
{
    /// <summary>起播门槛线：缓冲到 7 秒即出画，首帧不等满水位。</summary>
    public static readonly TimeSpan StartupThreshold = TimeSpan.FromSeconds(7);

    /// <summary>预取水位线：出画后继续填到窗口取满（对应 10~30 秒延迟取舍）。</summary>
    public static readonly TimeSpan PrefillWaterline = TimeSpan.FromSeconds(30);
}
