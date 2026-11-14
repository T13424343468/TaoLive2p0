namespace TaoLive.Model;

/// <summary>节目单条目：某频道在某时间段的节目。</summary>
public sealed record ProgramEntry(
    string ChannelId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Title)
{
    /// <summary>给定时刻是否落在本节目时段内（含头不含尾）。</summary>
    public bool Covers(DateTimeOffset at) => at >= Start && at < End;
}
