namespace TaoLive.Session;

/// <summary>
/// 播放会话五态（老板 2026-11-15 拍板：禁止布尔标志位乱跳）。
/// 合法迁移矩阵：
///   Idle → Buffering | Failed
///   Buffering → Playing | Reconnecting | Failed
///   Playing → Reconnecting | Failed | Idle（正常 Stop）
///   Reconnecting → Buffering | Failed
///   Failed → Buffering（重试） | Idle（放弃）
/// 其余迁移一律抛 InvalidOperationException（用例把关）。
/// </summary>
public enum PlaybackState
{
    Idle = 0,
    Buffering,
    Playing,
    Reconnecting,
    Failed,
}
