using TaoLive.Foundation;
using TaoLive.Model;

namespace TaoLive.Session;

/// <summary>
/// 备源队列吃内置表（方案书收口批①）：注册表候选转源端点，供 PlaybackSession.FallbackSourceProvider 装配。
/// 顺序即注册表纪律序（观察档垫底、纯 IP 优先、登记序）——内置表 2026-11-12 实测活性为基线，随季度复测刷新；
/// 不做运行期常驻探活，58 路只在换源触发时遍历，不进预检常驻轮。
/// </summary>
public static class FallbackQueueSource
{
    /// <summary>本频道在内置表的全部备援候选（按纪律序）；无登记返回空表。</summary>
    public static IReadOnlyList<SourceEndpoint> FromRegistry(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!FallbackSourceRegistry.Has(channel.Name))
        {
            return Array.Empty<SourceEndpoint>();
        }
        return FallbackSourceRegistry.GetAll(channel.Name)
            .Select(e => new SourceEndpoint(
                e.Url,
                IsIpLiteral(e.Url) ? SourceKind.PureIp : SourceKind.Cdn,
                e.ObservationRank ? 1 : 0))
            .ToList();
    }

    /// <summary>纯 IP 字面量判别（直连免系统解析，解析僵死免疫）。</summary>
    private static bool IsIpLiteral(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.HostNameType == UriHostNameType.IPv4 || u.HostNameType == UriHostNameType.IPv6);
}
