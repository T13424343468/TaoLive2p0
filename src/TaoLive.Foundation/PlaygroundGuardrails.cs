namespace TaoLive.Foundation;

/// <summary>看门狗契约：断流判死与出帧超时探测。M2 班全量实现，M1 只立接口。</summary>
public interface IWatchdog
{
    /// <summary>启动监视。帧间隔超过 <paramref name="frameStall"/> 即告警。</summary>
    void Start(TimeSpan frameStall);

    /// <summary>喂狗：每出一帧调一次。停喂超时触发 OnStalled。</summary>
    void Feed();

    event EventHandler Stalled;
}

/// <summary>断路器契约：连续失败计数与熔断窗口。M2 班全量实现。</summary>
public interface ICircuitBreaker
{
    /// <summary>记录一次失败。达到阈值即 Open。</summary>
    void RecordFailure();

    /// <summary>记录一次成功。Open 状态冷却后转 HalfOpen。</summary>
    void RecordSuccess();

    /// <summary>当前是否允许放行（Closed 或 HalfOpen 且冷却到期）。</summary>
    bool AllowAttempt { get; }
}
