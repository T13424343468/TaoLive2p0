using System;

namespace TaoLive.Foundation;

/// <summary>
/// 进阶调参档（老板 2026-11-12 令：旋钮全量进设置页、每项讲清作用、支持一键还原）：
/// 单一事实源=BLL 门面；UI 设置页经 Apply 写入，各策略读门面值不读常量——旋钮写活不写死。
/// 出厂默认即原常量值；ResetToDefaults 一键还原。Apply 内钳制合法范围，设置页与还原同一口径。
/// </summary>
public static class AdvancedTuning
{
    // ---- 出厂默认（= 原常量值，改动前口径） ----
    public const int DefaultFailGate = 3;
    public const long DefaultReprobeMs = 5 * 60_000;
    public const int DefaultListCacheSec = 60;
    public const long DefaultRetryBaseMs = 120_000;
    public const long DefaultRetryMaxMs = 16 * 60_000; // vc53 HotSwap 口径：2 分钟起步、2/4/8/16 分钟封顶（方案书 v27「沿 vc53 口径」归一）

    // ---- 合法范围（Apply 钳制） ----
    public const int FailGateMin = 1, FailGateMax = 20;
    public const long ReprobeMsMin = 60_000, ReprobeMsMax = 30 * 60_000;
    public const int ListCacheSecMin = 5, ListCacheSecMax = 600;
    public const long RetryBaseMsMin = 10_000, RetryBaseMsMax = 30 * 60_000;
    public const long RetryMaxMsMin = 60_000, RetryMaxMsMax = 24 * 60 * 60_000;

    /// <summary>自愈门槛：连续起播失败几次才启动自救判别（低=自救快但易误伤，高=稳但自救慢）。</summary>
    public static int SelfHealFailGate { get; private set; } = DefaultFailGate;
    /// <summary>自愈回探：自救启用后多久试一次能否回直连（低=回得快但探测勤，高=省流量回得慢）。</summary>
    public static long SelfHealReprobeMs { get; private set; } = DefaultReprobeMs;
    /// <summary>清单缓存：频道清单/列表数据留手多久不重取（低=信息新但请求勤，高=省流量换台信息陈）。</summary>
    public static int ListCacheSec { get; private set; } = DefaultListCacheSec;
    /// <summary>被拒重试起步：源服务器拒绝后退多远再试（这是跟风控博弈的分寸，急了挨打钝了恢复慢）。</summary>
    public static long RetryBaseMs { get; private set; } = DefaultRetryBaseMs;
    /// <summary>被拒重试封顶：指数退避最深退到几时（低=恢复探测勤，高=长退避省请求）。</summary>
    public static long RetryMaxMs { get; private set; } = DefaultRetryMaxMs;

    /// <summary>写入并钳制（UI 设置页与批量还原同一入口）。retryMax &lt; retryBase 时按 retryBase 收敛。</summary>
    public static void Apply(int failGate, long reprobeMs, int listCacheSec,
        long retryBaseMs, long retryMaxMs)
    {
        SelfHealFailGate = Math.Clamp(failGate, FailGateMin, FailGateMax);
        SelfHealReprobeMs = Math.Clamp(reprobeMs, ReprobeMsMin, ReprobeMsMax);
        ListCacheSec = Math.Clamp(listCacheSec, ListCacheSecMin, ListCacheSecMax);
        RetryBaseMs = Math.Clamp(retryBaseMs, RetryBaseMsMin, RetryBaseMsMax);
        RetryMaxMs = Math.Clamp(retryMaxMs, RetryMaxMsMin, RetryMaxMsMax);
        if (RetryMaxMs < RetryBaseMs) RetryMaxMs = RetryBaseMs;
    }

    /// <summary>一键还原默认：全部回出厂值（设置页「一键还原默认」唯一入口）。</summary>
    public static void ResetToDefaults() =>
        Apply(DefaultFailGate, DefaultReprobeMs, DefaultListCacheSec, DefaultRetryBaseMs, DefaultRetryMaxMs);
}
