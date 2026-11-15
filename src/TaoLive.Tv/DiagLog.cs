using Android.Content;

namespace TaoLive.Tv;

/// <summary>
/// 自诊断摘要与导出（2.0 自建，口径照 vc53）：五类现成数据只读展示 +
/// 导出完整诊断文件到文件管理器可见目录。不新增采集、不外传、不碰播放链路。
/// </summary>
public static class DiagLog
{
    /// <summary>导出到外部存储应用目录（文件管理器可达）；不可达或失败返回空。</summary>
    public static string? Export(Context context, string snapshot)
    {
        try
        {
            var dir = context.GetExternalFilesDir(null);
            if (dir is null) return null;
            var path = System.IO.Path.Combine(dir.AbsolutePath!, "taolive_diag.txt");
            System.IO.File.WriteAllText(path, snapshot);
            return path;
        }
        catch { return null; }
    }

    /// <summary>按尾偏移读日志尾部（fix53 口径），主线程 IO 有界，不整读全文件。</summary>
    public static string ReadTail(string filePath, int maxBytes)
    {
        using var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open,
            System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
        var start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, System.IO.SeekOrigin.Begin);
        using var reader = new System.IO.StreamReader(fs);
        return reader.ReadToEnd();
    }

    /// <summary>从日志尾部提取最近的失败/崩溃打点（最多 max 条）。</summary>
    public static string RecentFailuresFromLog(string tail, int max)
    {
        try
        {
            var hits = tail.Split('\n')
                .Where(l => l.Contains("[ConnectFailure]", StringComparison.Ordinal)
                         || l.Contains("未捕获异常", StringComparison.Ordinal))
                .ToList();
            if (hits.Count == 0) return "（近段无失败打点）";
            return string.Join("\n", hits.Skip(Math.Max(0, hits.Count - max)));
        }
        catch { return "（打点提取失败）"; }
    }

    public static string BuildSnapshot(string versionLine, string clockLine, string networkLine,
        string sessionLine, string kernelLine, string startupLine, string recentFailures) =>
        $"""
         ══ TaoLive 自诊断（只读）══
         {versionLine}
         {clockLine}
         {networkLine}
         {sessionLine}
         {kernelLine}
         {startupLine}

         —— 最近失败打点 ——
         {recentFailures}
         """;
}
