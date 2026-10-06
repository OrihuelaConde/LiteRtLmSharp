using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the LiteRtLmSharp <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>. Register
/// over an embedding engine you own, or from a <see cref="LiteRtEmbeddingEngineOptions"/> (the container loads,
/// owns and disposes a single shared embedding engine). An embedding engine does not count toward the
/// one-live-engine rule, so this registration coexists with <c>AddLiteRtChatClient</c>.
/// </summary>
public static class LiteRtEmbeddingGeneratorServiceCollectionExtensions
{
    /// <summary>Registers a <see cref="LiteRtEmbeddingGenerator"/> over an embedding engine <b>you own</b> (you
    /// dispose it).</summary>
    /// <param name="services">The DI service collection to register into.</param>
    /// <param name="engine">The loaded embedding engine (you dispose it, after the container-resolved generator).</param>
    /// <param name="modelId">Optional model id surfaced on the generator's metadata and on each embedding.</param>
    /// <param name="defaultOptions">Optional options applied to every call unless the call overrides them.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddLiteRtEmbeddingGenerator(
        this IServiceCollection services, LiteRtEmbeddingEngine engine, string? modelId = null, LiteRtEmbeddingOptions? defaultOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            _ => new LiteRtEmbeddingGenerator(engine, modelId, defaultOptions));
        return services;
    }

    /// <summary>Registers a <see cref="LiteRtEmbeddingGenerator"/> from <paramref name="options"/>; the container
    /// loads, owns and disposes a single shared <see cref="LiteRtEmbeddingEngine"/>.</summary>
    /// <param name="services">The DI service collection to register into.</param>
    /// <param name="options">Options the container uses to load the shared embedding engine.</param>
    /// <param name="modelId">Optional model id surfaced on the generator's metadata and on each embedding.</param>
    /// <param name="eager">When <c>true</c>, load the engine now (a bad model path or backend throws here);
    /// otherwise it is loaded on first use.</param>
    /// <param name="defaultOptions">Optional options applied to every call unless the call overrides them.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddLiteRtEmbeddingGenerator(
        this IServiceCollection services, LiteRtEmbeddingEngineOptions options, string? modelId = null, bool eager = false,
        LiteRtEmbeddingOptions? defaultOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        RegisterSharedEngine(services, options, eager);
        services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            sp => new LiteRtEmbeddingGenerator(sp.GetRequiredService<LiteRtEmbeddingEngine>(), modelId, defaultOptions));
        return services;
    }

    internal static void RegisterSharedEngine(IServiceCollection services, LiteRtEmbeddingEngineOptions options, bool eager)
    {
        if (services.Any(d => d.ServiceType == typeof(LiteRtEmbeddingEngine)))
            return;
        if (eager)
        {
            LiteRtEmbeddingEngine engine = LiteRtEmbeddingEngine.Load(options);   // load now: a bad path or backend throws here
            services.AddSingleton(_ => engine);                                    // factory result: the container disposes it
        }
        else
        {
            services.AddSingleton(_ => LiteRtEmbeddingEngine.Load(options));       // lazy; created and disposed by the container
        }
    }
}
