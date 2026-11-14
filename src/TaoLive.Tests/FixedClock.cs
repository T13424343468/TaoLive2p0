using TaoLive.Session;

namespace TaoLive.Tests;

/// <summary>测试固定时钟：不真等，快拍断言；Advance 快进看门狗窗口。</summary>
public sealed class FixedClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow => _now;

    /// <summary>快进指定时长（仅推进测试时钟，不真实等待）。</summary>
    public void Advance(TimeSpan delta) => _now += delta;
}
