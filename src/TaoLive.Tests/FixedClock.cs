using TaoLive.Session;

namespace TaoLive.Tests;

/// <summary>测试固定时钟：不真等，快拍断言。</summary>
public sealed class FixedClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow => start;
}
