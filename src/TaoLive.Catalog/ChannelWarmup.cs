using System.Collections.Concurrent;
using TaoLive.Model;

namespace TaoLive.Catalog;

/// <summary>单源限流节拍配置（预热细案 §三逐字移植，不可变）。时间毫秒；※标注为真机调优项。</summary>
public readonly struct SourcePace
{
    public SourcePace(long refreshIntervalMs, long minGapMs, bool batchAggregation = false,
        long backoffBaseMs = 120_000, long backoffMaxMs = 7_200_000)
    {
        RefreshIntervalMs = refreshIntervalMs;
        MinGapMs = minGapMs;
        BatchAggregation = batchAggregation;
        BackoffBaseMs = backoffBaseMs;
        BackoffMaxMs = backoffMaxMs;
    }

    /// <summary>保鲜期：距上次确认成功超过该值才需要再探。</summary>
    public long RefreshIntervalMs { get; }
    /// <summary>两次请求最小间隔（让路前台连接）。</summary>
    public long MinGapMs { get; }
    /// <summary>整表 API 一发全台的源（nmtv/gxtv）：一次请求覆盖全源，节拍按源算不按台算。</summary>
    public bool BatchAggregation { get; }
    /// <summary>403 退避指数基值（细案：2 分钟起步）。</summary>
    public long BackoffBaseMs { get; }
    /// <summary>403 退避封顶（细案：2 小时）。</summary>
    public long BackoffMaxMs { get; }
}

/// <summary>
/// 按源限流节拍政策（一期 A 类第6项·预热改造的调度依据逐字移植）：
/// yangshipin 最敏感最慢；nmtv/gxtv 整表聚合节拍按源；xatv/gxtv 低敏感长保鲜。
/// </summary>
public static class WarmupPolicy
{
    /// <summary>逐源节拍（键=SourceId；未注册源按 conservative 口径）。</summary>
    public static readonly IReadOnlyDictionary<string, SourcePace> Paces = new Dictionary<string, SourcePace>
    {
        // 央视频：IP 绑定画像最重，节拍最慢、退避最深（细案 §一.1）
        ["yangshipin"] = new SourcePace(refreshIntervalMs: 10 * 60_000, minGapMs: 2_000),
        // 整表 API：一次请求全台（细案 §一.5/§一.6）
        ["nmtv"] = new SourcePace(refreshIntervalMs: 30 * 60_000, minGapMs: 1_000, batchAggregation: true),
        ["gxtv"] = new SourcePace(refreshIntervalMs: 6 * 60 * 60_000, minGapMs: 1_000, batchAggregation: true),
        // 中敏感：票据短效但可整轮缓存
        ["gzstv"] = new SourcePace(refreshIntervalMs: 30 * 60_000, minGapMs: 1_500),
        ["cztv"] = new SourcePace(refreshIntervalMs: 2 * 60 * 60_000, minGapMs: 1_000),
        // 低敏感：URL 恒定，落盘价值最高
        ["xatv"] = new SourcePace(refreshIntervalMs: 6 * 60 * 60_000, minGapMs: 1_000),
    };

    /// <summary>取源节拍；未注册源给保守节拍（10 分钟保鲜 + 2 秒让路）。</summary>
    public static SourcePace PaceOf(string sourceId) =>
        Paces.TryGetValue(sourceId ?? "", out var pace) ? pace
        : new SourcePace(refreshIntervalMs: 10 * 60_000, minGapMs: 2_000);

    /// <summary>是否整表聚合源（A⑬竞速与预热按源不按台调度）。</summary>
    public static bool IsBatchSource(string sourceId) => PaceOf(sourceId).BatchAggregation;
}

/// <summary>
/// 403 源级指数退避账（一期总装口径三逐字移植：与竞速/预热共用同一本账）：
/// 任一台 403 → 该源全部排队任务冻结一个退避周期；连续 403 指数加深（×4 步进，2 分钟起步封顶 2 小时）；
/// 出现 200 即全额恢复。纯逻辑可测，时间由调用方注入。
/// </summary>
public class SourceBackoffLedger
{
    private readonly ConcurrentDictionary<string, (long UntilMs, int Streak)> _frozen = new(StringComparer.Ordinal);

    /// <summary>记录一次 403：该源冻结一个退避周期。nowMs=TickCount64 基准。</summary>
    public void RecordLimited(string sourceId, long nowMs, SourcePace? pace = null)
    {
        var p = pace ?? WarmupPolicy.PaceOf(sourceId);
        _frozen.AddOrUpdate(sourceId,
            _ => (nowMs + BackoffMs(1, p), 1),
            (_, cur) => (nowMs + BackoffMs(cur.Streak + 1, p), cur.Streak + 1));
    }

    private static long BackoffMs(int streak, SourcePace p)
    {
        // 指数：基值 × 4^(streak-1)，封顶 BackoffMaxMs
        long ms = p.BackoffBaseMs;
        for (int i = 1; i < streak; i++)
        {
            ms *= 4;
            if (ms >= p.BackoffMaxMs) { ms = p.BackoffMaxMs; break; }
        }
        return Math.Min(ms, p.BackoffMaxMs);
    }

    /// <summary>记录一次成功（200）：退避全额恢复。</summary>
    public bool Recover(string sourceId) => _frozen.TryRemove(sourceId, out _);

    /// <summary>源是否处于退避期（竞速跳过/预热挂起的唯一判据）。</summary>
    public bool IsFrozen(string sourceId, long nowMs) =>
        _frozen.TryGetValue(sourceId, out var cur) && nowMs < cur.UntilMs;

    /// <summary>剩余冻结毫秒（未冻结为 0）。</summary>
    public long RemainingMs(string sourceId, long nowMs) =>
        _frozen.TryGetValue(sourceId, out var cur) && nowMs < cur.UntilMs ? cur.UntilMs - nowMs : 0;

    /// <summary>清账（换台/测试重置）。</summary>
    public void Clear() => _frozen.Clear();
}

/// <summary>频道可连状态（v1.0.8 开机预检口径逐字移植）：未探测/探测中/可用/失效。</summary>
public enum ChannelHealth
{
    Unknown,
    Checking,
    Alive,
    Dead,
}

/// <summary>
/// 频道预检（v1.0.8/v1.0.9 口径移植，2.0 W8）：
/// - 按台单顺序逐台轻量探测（只取票，不建视频传输），限速执行防被判异常流量；
/// - 同名频道各来源变体逐一探测，任一来源可达即整台可用，全源不可达才进失效组；
/// - 失效台可复测（Requeue），票据/网络恢复即转回可用。
/// probe 由装配层注入（生产=取票器轻探，测试=可脚本假探针），纯状态机可测。
/// </summary>
public class ChannelPrecheck
{
    private readonly Func<Channel, Task<bool>> _probe;
    private readonly int _intervalMs;
    private readonly ConcurrentDictionary<string, ChannelHealth> _states = new();

    /// <summary>状态变化事件：(频道 Id, 新状态)。壳层订阅后刷新列表。</summary>
    public event Action<string, ChannelHealth>? StateChanged;

    public ChannelPrecheck(Func<Channel, Task<bool>> probe, int intervalMs = 600)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _intervalMs = Math.Max(0, intervalMs);
    }

    private static string GroupKey(Channel ch) => NormalizeName(ch.Name);

    /// <summary>同频道各来源归并键：台名去空格后一致视为同一频道（一期 ChannelPrecheck.NormalizeName 口径）。</summary>
    internal static string NormalizeName(string name)
    {
        var trimmed = (name ?? "").Replace(" ", "").Replace("　", "");
        return string.IsNullOrEmpty(trimmed) ? (name ?? "") : trimmed;
    }

    /// <summary>查询某台当前状态（未探测=Unknown）。同频道任一来源可达=可用。</summary>
    public ChannelHealth Of(Channel ch) =>
        _states.TryGetValue(ch.Id, out var s) ? s : ChannelHealth.Unknown;

    /// <summary>直接标记状态（播放实测/预检覆盖写）。仅在状态真正变化时发事件，防重复刷列表。</summary>
    public void MarkState(Channel ch, ChannelHealth state)
    {
        var prev = Of(ch);
        if (prev == state) return;
        _states[ch.Id] = state;
        StateChanged?.Invoke(ch.Id, state);
    }

    /// <summary>失效台复测入口：置回探测中，探测轮次走完自动更新。</summary>
    public void Requeue(Channel ch) => MarkState(ch, ChannelHealth.Checking);

    /// <summary>
    /// 按台单顺序逐台探测，每台之间限速间隔。不阻塞调用方播放流程。
    /// 探测抛异常按失效处理——预检不吞播放在途状态，只记录结论。
    /// 同频道（台名归并）的多个来源变体逐一探测，任一来源通=整台可用。
    /// keepKnownState（循环预检轮次用）：true 时已知状态的台不先置「检测中」，列表不闪烁。
    /// </summary>
    public async Task RunAsync(IEnumerable<Channel> channels, CancellationToken ct = default, bool keepKnownState = false)
    {
        foreach (var group in channels.GroupBy(GroupKey))
        {
            if (ct.IsCancellationRequested) return;
            var variants = group.ToList();
            var lead = variants[0];
            if (!keepKnownState || Of(lead) == ChannelHealth.Unknown)
                MarkState(lead, ChannelHealth.Checking);
            var anyOk = false;
            foreach (var ch in variants)
            {
                if (ct.IsCancellationRequested) return;
                bool ok;
                try { ok = await _probe(ch).ConfigureAwait(false); }
                catch { ok = false; }
                anyOk |= ok;
                if (_intervalMs > 0)
                {
                    try { await Task.Delay(_intervalMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            foreach (var ch in variants)
                MarkState(ch, anyOk ? ChannelHealth.Alive : ChannelHealth.Dead);
        }
    }
}

/// <summary>
/// 频道预热器（2.0 W8，对接一期预热细案与 TicketResolver 5 分钟换票缓存）：
/// 后台按源节拍（WarmupPolicy）对取票口端点预取真实流，换台即命中缓存零等待；
/// 403 退避账（SourceBackoffLedger）与预热共用：退避期内该源全部挂起，恢复后从断点续跑。
/// 节拍与让路间隔真实等待由 Task.Delay 承担，测试注入假 HTTP 客户端（TicketResolver.Http）快拍。
/// </summary>
public sealed class ChannelWarmer
{
    private readonly TicketResolver _resolver;
    private readonly SourceBackoffLedger _backoff;
    private readonly IReadOnlyDictionary<string, long> _lastRunMs;
    private readonly Func<long> _nowMs;

    /// <summary>让路间隔：预热请求之间最小间隔（生产 1500ms，测试可调小快拍）。</summary>
    public int MinGapOverrideMs { get; set; } = 1_500;

    public ChannelWarmer(TicketResolver resolver,
        SourceBackoffLedger? backoff = null,
        IReadOnlyDictionary<string, long>? lastRunMs = null,
        Func<long>? nowMs = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _backoff = backoff ?? new SourceBackoffLedger();
        _lastRunMs = lastRunMs ?? new Dictionary<string, long>(StringComparer.Ordinal);
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>预热退避账（预检/竞速共用同一本账的口径不变）。</summary>
    public SourceBackoffLedger Backoff => _backoff;

    /// <summary>
    /// 单台预热：对频道全部取票口端点预取真实流。返回频道是否就绪（有取票口端点时=至少一路预取成功；
    /// 纯直连频道原样可播，视为就绪）。
    /// 退避期内该源直接跳过（挂起不烧请求）；解析失败回写退避账，成功全额恢复。
    /// </summary>
    public async Task<bool> WarmChannelAsync(Channel ch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ch);
        var nowMs = _nowMs();
        bool hasWeb = false, anyOk = false;
        foreach (var endpoint in ch.Sources)
        {
            if (ct.IsCancellationRequested) return anyOk;
            if (endpoint.Kind != SourceKind.Web) continue; // 直连无需换票
            hasWeb = true;
            var sourceKey = SourceKeyOf(endpoint);
            if (_backoff.IsFrozen(sourceKey, nowMs)) continue; // 退避期挂起
            string? resolved;
            try { resolved = await _resolver.ResolveAsync(endpoint, ch.Id, ch.Name).ConfigureAwait(false); }
            catch { resolved = null; }
            if (resolved is null)
            {
                _backoff.RecordLimited(sourceKey, nowMs);
            }
            else
            {
                _backoff.Recover(sourceKey);
                anyOk = true;
            }
            if (MinGapOverrideMs > 0)
            {
                try { await Task.Delay(MinGapOverrideMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return anyOk; }
            }
        }
        return !hasWeb || anyOk;
    }

    /// <summary>整表预热（开机第一轮口径）：按台单顺序逐台预热，交给壳层后台 Task 驱动。</summary>
    public async Task WarmAllAsync(IEnumerable<Channel> channels, CancellationToken ct = default)
    {
        foreach (var ch in channels)
        {
            if (ct.IsCancellationRequested) return;
            await WarmChannelAsync(ch, ct).ConfigureAwait(false);
        }
    }

    /// <summary>源标识：端点主机即源键（与 TicketResolver 分发口径一致，不硬编码机型）。</summary>
    internal static string SourceKeyOf(SourceEndpoint endpoint)
    {
        try { return new Uri(endpoint.Url, UriKind.Absolute).Host; }
        catch { return endpoint.Url; }
    }
}
