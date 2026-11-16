using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TaoLive.Session;

/// <summary>竞速握手探测（六项优化①）：对候选并发握手（只等响应头，不拉数据），谁先成用谁。</summary>
public interface IHandshakeProber
{
    /// <summary>
    /// 对前 maxRacers 路候选并发握手，返回最先握手成功者的候选下标；全败/全超时 -1。
    /// 实现承诺：不取消自身参数令牌（调用方生命周期归调用方）。
    /// </summary>
    Task<int> RaceAsync(IReadOnlyList<string> candidates, int maxRacers = 3, CancellationToken ct = default);
}

/// <summary>
/// HTTP 竞速握手（产线默认）：前 min(maxRacers, count) 路并发 GET（ResponseHeadersRead——
/// 响应头到即判成功，不读体不拉数据），最先成功者赢，其余取消。把 12 秒串行盲等变毫秒级赛马。
/// </summary>
public sealed class HttpHandshakeProber : IHandshakeProber
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<int> RaceAsync(IReadOnlyList<string> candidates, int maxRacers = 3, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return -1;
        int racerCount = Math.Clamp(maxRacers, 1, candidates.Count);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = candidates.Take(racerCount).Select(async (url, i) =>
        {
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                    .ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                return code >= 200 && code < 400 ? i : -1;
            }
            catch
            {
                return -1; // 超时/取消/网络错：本路败，不掀整场
            }
        }).ToList();
        int pending = tasks.Count;
        while (pending > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            pending--;
            int result = await done.ConfigureAwait(false);
            if (result >= 0)
            {
                linked.Cancel(); // 赢者已定：撤其余在途
                return result;
            }
            tasks.Remove(done);
        }
        return -1;
    }
}
