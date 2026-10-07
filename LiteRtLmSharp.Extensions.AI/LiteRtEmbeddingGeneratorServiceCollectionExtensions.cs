using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the LiteRtLmSharp <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>. Register
/// over an embedding engine you own, or from a <see cref="LiteRtEmbeddingEngineOptions"/> (the container loads,
/// owns and disposes a single shared embedding engine). An embedding engine does not count toward the
/// one-live-engine rule, so this registration coexists with <c>AddLiteRtChatClient</c>. One container holds one
/// generator: registering again with the same options is a no-op, and with different options throws.
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
    /// otherwise it is loaded on first use. Either way the container disposes the engine once it has resolved
    /// it, so an eager engine that is never resolved lives until the process ends.</param>
    /// <param name="defaultOptions">Optional options applied to every call unless the call overrides them.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The collection already registers a shared embedding engine
    /// from different options.</exception>
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

    /// <summary>
    /// Registers the container-owned engine for <paramref name="options"/>, once: the same options again are a
    /// no-op, different options throw (a second engine would otherwise be silently ignored). With
    /// <paramref name="serviceKey"/>, registers a keyed engine for that key instead, so keyed generators can
    /// run different models or backends side by side.
    /// </summary>
    internal static void RegisterSharedEngine(
        IServiceCollection services, LiteRtEmbeddingEngineOptions options, bool eager, object? serviceKey = null)
    {
        SharedEmbeddingEngine? existing = services
            .Where(d => d.ServiceType == typeof(SharedEmbeddingEngine) && Equals(d.ServiceKey, serviceKey))
            .Select(d => (serviceKey is null ? d.ImplementationInstance : d.KeyedImplementationInstance) as SharedEmbeddingEngine)
            .FirstOrDefault();
        if (existing is not null)
        {
            if (existing.Options != options)
                throw new InvalidOperationException(
                    "An embedding engine is already registered " + (serviceKey is null ? "" : $"for '{serviceKey}' ") +
                    "from different LiteRtEmbeddingEngineOptions. Register one generator per container, or give each " +
                    "Semantic Kernel registration its own serviceId.");
            return;
        }
        if (serviceKey is null && services.Any(d => d.ServiceType == typeof(LiteRtEmbeddingEngine) && d.ServiceKey is null))
            return;   // an engine registered another way (for example an instance you own) stays in charge

        LiteRtEmbeddingEngine? preloaded = eager ? LiteRtEmbeddingEngine.Load(options) : null;   // eager: a bad path throws here
        // Factory registrations: the container creates (or adopts) and disposes the engine once resolved.
        Func<IServiceProvider, LiteRtEmbeddingEngine> create = _ => preloaded ?? LiteRtEmbeddingEngine.Load(options);
        if (serviceKey is null)
        {
            services.AddSingleton(new SharedEmbeddingEngine(options));
            services.AddSingleton(create);
        }
        else
        {
            services.AddKeyedSingleton(serviceKey, new SharedEmbeddingEngine(options));
            services.AddKeyedSingleton(serviceKey, (sp, _) => create(sp));
        }
    }

    /// <summary>Marks the options a container-owned embedding engine was registered from.</summary>
    internal sealed record SharedEmbeddingEngine(LiteRtEmbeddingEngineOptions Options);
}
