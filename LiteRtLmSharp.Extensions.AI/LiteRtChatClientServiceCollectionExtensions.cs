using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the LiteRtLmSharp <see cref="IChatClient"/>. Register over an engine you own, or
/// from a <see cref="LiteRtEngineOptions"/> (the container loads, owns and disposes a single shared engine).
/// The registered <see cref="IChatClient"/> can be wrapped with the usual Microsoft.Extensions.AI middleware
/// (e.g. <c>UseFunctionInvocation()</c>, caching, telemetry) by the consuming application, used to build a
/// Microsoft Agent Framework agent (<c>new ChatClientAgent(client, …)</c>), or exposed to Semantic Kernel.
/// </summary>
public static class LiteRtChatClientServiceCollectionExtensions
{
    /// <summary>Registers a <see cref="LiteRtChatClient"/> over an engine <b>you own</b> (you dispose it).</summary>
    public static IServiceCollection AddLiteRtChatClient(this IServiceCollection services, LiteRtEngine engine, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        return Register(services, _ => engine, modelId);
    }

    /// <summary>Registers a <see cref="LiteRtChatClient"/> from <paramref name="options"/>; the container loads,
    /// owns and disposes a single shared engine.</summary>
    /// <param name="eager">When <c>true</c>, load the engine now (a bad model path/backend throws here);
    /// otherwise it is loaded lazily on first use.</param>
    public static IServiceCollection AddLiteRtChatClient(this IServiceCollection services, LiteRtEngineOptions options, string? modelId = null, bool eager = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        RegisterSharedEngine(services, options, eager);
        return Register(services, static sp => sp.GetRequiredService<LiteRtEngine>(), modelId);
    }

    // One engine per process: register the shared engine singleton once (idempotent), owned by the container.
    private static void RegisterSharedEngine(IServiceCollection services, LiteRtEngineOptions options, bool eager)
    {
        if (services.Any(d => d.ServiceType == typeof(LiteRtEngine)))
            return;

        if (eager)
        {
            LiteRtEngine engine = LiteRtEngine.Load(options);   // load now: a bad path/backend throws here
            services.AddSingleton(_ => engine);                  // factory result → the container disposes it
        }
        else
        {
            services.AddSingleton(_ => LiteRtEngine.Load(options)); // lazy; created and disposed by the container
        }
    }

    private static IServiceCollection Register(IServiceCollection services, Func<IServiceProvider, LiteRtEngine> engine, string? modelId)
    {
        // TryAdd so the IChatClient is registered once (one engine/gate per process); the Semantic Kernel
        // package reusing this shares the one client.
        services.TryAddSingleton<IChatClient>(sp => new LiteRtChatClient(engine(sp), modelId));
        return services;
    }
}
