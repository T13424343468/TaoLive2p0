using TaoLive.Model;
using TaoLive.Session;
using Xunit;

namespace TaoLive.Tests;

/// <summary>
/// 收口批② Tv 壳层五件的可测层：五标签过滤（照 vc53 DrawerSupport 口径）、
/// 等网就绪（轻量 TCP 探测+超时放行）、列表健康点三态（只读账）。
/// </summary>
public class TvShellPortsTests
{
    private static Channel Ch(string id, string name, ChannelCategory cat) => new()
    {
        Id = id, SourceId = id, Name = name, Category = cat, EpgKey = id,
        SourceEndpoints =
        {
            new SourceEndpoint($"https://s/{id}.m3u8", SourceKind.Cdn, 10),
        },
    };

    private static readonly List<Channel> Catalog =
    [
        Ch("a", "CCTV-1", ChannelCategory.Cctv),
        Ch("b", "CGTN", ChannelCategory.Cgtn),
        Ch("c", "风云剧场", ChannelCategory.Special),
        Ch("d", "湖南卫视", ChannelCategory.Provincial),
        Ch("e", "上海台", ChannelCategory.City),
        Ch("f", "自建源", ChannelCategory.UserSource),
    ];

    private static bool IsFav(Channel c) => c.Id == "a";

    // ---- 五标签 ----

    [Fact]
    public void Labels_match_vc53_roster()
    {
        Assert.Equal(["全部", "收藏", "央视", "卫视", "地方台"],
            Enum.GetValues<ListTabs.Tab>().Select(ListTabs.Label));
    }

    [Fact]
    public void NextTab_cycles_all_fav_cctv_sat_local()
    {
        Assert.Equal(ListTabs.Tab.Favorite, ListTabs.NextTab(ListTabs.Tab.All, rightward: true));
        Assert.Equal(ListTabs.Tab.All, ListTabs.NextTab(ListTabs.Tab.Local, rightward: true));
        Assert.Equal(ListTabs.Tab.Local, ListTabs.NextTab(ListTabs.Tab.All, rightward: false));
    }

    [Fact]
    public void Filter_follows_vc53_category_mapping()
    {
        Assert.Equal(6, ListTabs.Filter(ListTabs.Tab.All, Catalog, IsFav).Count);
        Assert.Equal(["a"], ListTabs.Filter(ListTabs.Tab.Favorite, Catalog, IsFav).Select(c => c.Id));
        // CGTN/专项并入央视（vc53 v22 第 7 项口径）
        Assert.Equal(["a", "b", "c"], ListTabs.Filter(ListTabs.Tab.Cctv, Catalog, IsFav).Select(c => c.Id));
        Assert.Equal(["d"], ListTabs.Filter(ListTabs.Tab.Satellite, Catalog, IsFav).Select(c => c.Id));
        // 地方台只放城市台/自有源
        Assert.Equal(["e", "f"], ListTabs.Filter(ListTabs.Tab.Local, Catalog, IsFav).Select(c => c.Id));
    }

    // ---- 等网就绪 ----

    [Fact]
    public async Task NetReadiness_returns_on_first_probe_ok()
    {
        var probes = 0;
        var ok = await NetReadiness.WaitAsync(5_000, (_, _, _) =>
        {
            probes++;
            return Task.FromResult(true);
        });
        Assert.True(ok);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task NetReadiness_times_out_and_releases_for_default_channel()
    {
        var ok = await NetReadiness.WaitAsync(60, (_, _, _) => Task.FromResult(false));
        Assert.False(ok); // 超时照常放行（进默认台，起播失败走既有重试）
    }

    [Fact]
    public async Task NetReadiness_probe_exception_keeps_probing_until_timeout()
    {
        var ok = await NetReadiness.WaitAsync(60, (_, _, _) => throw new IOException("断网"));
        Assert.False(ok);
    }

    // ---- 健康点三态 ----

    private static async Task<ChannelPrecheck> PrecheckAfterRoundsAsync(int okRounds, Channel ch)
    {
        var precheck = new ChannelPrecheck(_ => Task.FromResult(true), intervalMs: 0);
        // 核验时间用真实当前时刻（Unix 毫秒）：24 小时新鲜期（老板令 2026-11-15）会把 1970 年的假时间戳读作作废
        long baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < okRounds; i++)
            precheck.ReportSourceResult(ch, ok: true, nowMs: baseMs + i);
        await Task.Yield();
        return precheck;
    }

    [Fact]
    public async Task Health_mark_green_only_after_three_consecutive_oks()
    {
        var ch = Ch("a", "CCTV-1", ChannelCategory.Cctv);
        var two = await PrecheckAfterRoundsAsync(2, ch);
        Assert.Equal(ChannelHealth.Checking, two.MarkOf(ch)); // 通但未满 3 轮=灰
        var three = await PrecheckAfterRoundsAsync(3, ch);
        Assert.Equal(ChannelHealth.Alive, three.MarkOf(ch));  // 近 3 轮全通=绿
    }

    [Fact]
    public async Task Health_mark_dead_when_all_fail_and_grey_when_untouched()
    {
        var ch = Ch("a", "CCTV-1", ChannelCategory.Cctv);
        var untouched = new ChannelPrecheck(_ => Task.FromResult(true));
        Assert.Equal(ChannelHealth.Checking, untouched.MarkOf(ch)); // 未探=灰
        var dead = await PrecheckAfterRoundsAsync(0, ch);
        dead.ReportSourceResult(ch, ok: false, nowMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        dead.MarkState(ch, ChannelHealth.Dead);
        Assert.Equal(ChannelHealth.Dead, dead.MarkOf(ch)); // 全败=红
    }
}
