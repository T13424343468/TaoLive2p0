using Microsoft.Extensions.DependencyInjection;
using TaoLive.Foundation;

namespace TaoLive.Session;

/// <summary>
/// DI 装配点（方案书 v5 口径）：壳层只调用 AddTaoLiveSession() 一次，
/// 避免各壳各拼导致行为漂移。播放内核由壳层以 IPlayer 注册进来。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTaoLiveSession(this IServiceCollection services)
    {
        services.AddSingleton<PlaybackSessionManager>(sp =>
        {
            var player = sp.GetRequiredService<IPlayer>();
            var clock = sp.GetService<IClock>() ?? new SystemClock();
            var dispatcher = sp.GetService<IMainThreadDispatcher>() ?? new ImmediateMainThreadDispatcher();
            return new PlaybackSessionManager(() => new PlaybackSession(player, clock), dispatcher);
        });
        return services;
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
