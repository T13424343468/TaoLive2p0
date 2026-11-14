namespace TaoLive.Model;

/// <summary>
/// 频道：一个可播的电视台。Id 稳定不变，名称为展示文案。
/// </summary>
public sealed record Channel(
    string Id,
    string Name,
    string Category,
    IReadOnlyList<SourceEndpoint> Sources)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Name)
        && Sources.Count > 0;
}
