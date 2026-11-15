using System.Collections.Concurrent;
using System.Text;

namespace TaoLive.Tv;

/// <summary>
/// 崩溃拦截网（2.0 自建，口径照 vc53）：任何未捕获异常先落日志再放行，崩溃原因可查可报。
/// 日志写在应用私有目录 taolive_crash.log；写入前 1MB 两代轮转封顶，绝不限时占满存储。
/// 主线程只入队（有界，满了丢新条），后台定时器每秒攒批一次写入——磁盘 IO 不占主线程；
/// 进程即死路径（未捕获异常处理器）走 RecordNow 同步落盘——进程来不及等批量。
/// </summary>
public static class CrashNet
{
    // 后台批量队列：有界，满了丢新条最省事（诊断日志可丢，绝不因日志积压拖死主线程/内存）
    private const int QueueCapacity = 512;
    private static readonly ConcurrentQueue<string> _pending = new();
    private static readonly System.Threading.Timer _flushTimer;
    private static int _flushing; // 单飞护栏：防上拍未落盘、下拍并发写

    static CrashNet()
    {
        _flushTimer = new System.Threading.Timer(_ => Flush(), null, 1000, 1000);
    }

    /// <summary>挂接未捕获异常：托管域 + Java 侧线程（含内核渲染线程）双入口，记日志后照常交系统，不吞异常。</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RecordNow($"未捕获异常 {e.ExceptionObject}");
        var next = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
        if (next is not UncaughtCrashPeer)
            Java.Lang.Thread.DefaultUncaughtExceptionHandler = new UncaughtCrashPeer(next);
    }

    /// <summary>记录一条异常（异常对象版）。主线程只入队，磁盘 IO 交给后台批量拍。</summary>
    public static void Record(Exception ex) => Record(ex.ToString());

    /// <summary>记录一条异常（文本版）。主线程只入队，磁盘 IO 交给后台批量拍。</summary>
    public static void Record(string text)
    {
        try { Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n"); }
        catch { }
    }

    /// <summary>同步落盘（不走批量队列）——仅供进程即死的兜底路径（未捕获异常处理器）使用。</summary>
    public static void RecordNow(string text)
    {
        try { WriteAndRotate($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}\n\n"); }
        catch { }
    }

    /// <summary>入队（有界：满了丢新条，保队列稳定不膨胀）。</summary>
    static void Enqueue(string line)
    {
        if (_pending.Count >= QueueCapacity) return; // 队列满载=写盘长期跟不上：诊断日志可丢，不积压拖垮内存
        _pending.Enqueue(line);
    }

    /// <summary>后台批量落盘（每秒一拍，单飞护栏防并发写；≤32KB/拍防单拍过长占住 IO）。</summary>
    static void Flush()
    {
        if (_pending.IsEmpty) return;
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return; // 上拍未完成：留到下拍
        try
        {
            var batch = new StringBuilder();
            while (_pending.TryDequeue(out var line))
            {
                batch.Append(line);
                if (batch.Length >= 32 * 1024) break; // 单拍封顶，剩余下拍继续
            }
            if (batch.Length == 0) return;
            WriteAndRotate(batch.ToString());
        }
        catch { }
        finally { Interlocked.Exchange(ref _flushing, 0); }
    }

    /// <summary>写入前按轮转策略收敛历史日志（1MB 两代封顶），再追加本次内容。</summary>
    static void WriteAndRotate(string line)
    {
        var path = LogPath();
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
            {
                var bak = path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
        }
        catch { /* 轮转失败不影响本次写入 */ }
        File.AppendAllText(path, line);
    }

    static string LogPath() =>
        Path.Combine(global::Android.App.Application.Context!.FilesDir!.AbsolutePath!, "taolive_crash.log");
}

/// <summary>
/// Java 侧未捕获异常挂接：任何线程（含内核渲染线程）抛出的异常，先记日志，
/// 再交给系统默认处理器照常弹错，不吞异常。
/// </summary>
public class UncaughtCrashPeer : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
{
    private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _next;

    public UncaughtCrashPeer(Java.Lang.Thread.IUncaughtExceptionHandler? next)
    {
        _next = next;
    }

    public void UncaughtException(Java.Lang.Thread thread, Java.Lang.Throwable ex) // 契约按绑定非空签名；方法内仍安全处理
    {
        if (thread is not null && ex is not null)
            CrashNet.RecordNow($"未捕获异常 线程={thread.Name} {ex}"); // 进程即死：同步落盘，等不到批量拍
        _next?.UncaughtException(thread, ex);
    }
}
