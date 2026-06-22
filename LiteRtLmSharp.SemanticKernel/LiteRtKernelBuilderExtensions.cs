using LiteRtLmSharp;
using LiteRtLmSharp.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.TextGeneration;

// Placed in the Microsoft.SemanticKernel namespace (the convention the official connectors follow) so
// `builder.AddLiteRt…` is discoverable wherever IKernelBuilder is in scope.
namespace Microsoft.SemanticKernel;

/// <summary>
/// Registration helpers for the LiteRtLmSharp Semantic Kernel connectors. Each service can be registered
/// over an engine you already loaded (you own its lifetime) or from a <see cref="LiteRtEngineOptions"/>
/// (the container loads, owns and disposes a shared engine for you) — and as the default service or a
/// keyed one (<c>serviceId</c>).
/// </summary>
public static class LiteRtKernelBuilderExtensions
{
    // ─────────────────────────── Shared engine ───────────────────────────

    /// <summary>
    /// Registers a shared <see cref="LiteRtEngine"/> singleton loaded from <paramref name="options"/>,
    /// owned and disposed by the container. Because only one engine may live per process, the registration
    /// is idempotent: if an engine is already registered it is reused (so a chat and a text-generation
    /// service added from options share the one engine). The <c>AddLiteRt…</c> service helpers call this for
    /// you when given options; call it directly only to register the engine without a service.
    /// </summary>
    /// <param name="eager">When <c>true</c>, the engine is loaded now, so a bad model path or unsupported
    /// backend throws here at registration; when <c>false</c> (default), it is loaded lazily on first use.
    /// An eagerly-loaded engine is disposed with the service provider once a resolving service has been
    /// resolved (the normal case — you register it because a service will use it).</param>
    public static IServiceCollection AddLiteRtEngine(this IServiceCollection services, LiteRtEngineOptions options, bool eager = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // One engine per process: register once. If an engine is already registered, reuse it — and for the
        // eager path do NOT load a second engine (LiteRtEngine.Load throws while the first one is alive).
        if (services.Any(d => d.ServiceType == typeof(LiteRtEngine)))
            return services;

        if (eager)
        {
            LiteRtEngine engine = LiteRtEngine.Load(options);   // load now: a bad path/backend throws here
            services.AddSingleton(_ => engine);                  // factory result → the container disposes it
        }
        else
        {
            services.AddSingleton(_ => LiteRtEngine.Load(options)); // lazy; created and disposed by the container
        }
        return services;
    }

    /// <inheritdoc cref="AddLiteRtEngine(IServiceCollection, LiteRtEngineOptions, bool)"/>
    public static IKernelBuilder AddLiteRtEngine(this IKernelBuilder builder, LiteRtEngineOptions options, bool eager = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtEngine(options, eager);
        return builder;
    }

    // ─────────────────────────── Chat completion ───────────────────────────

    /// <summary>Adds a <see cref="LiteRtChatCompletionService"/> over an engine <b>you own</b> (you load
    /// and dispose it; it must outlive the service).</summary>
    public static IKernelBuilder AddLiteRtChatCompletion(
        this IKernelBuilder builder, LiteRtEngine engine,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtChatCompletion(engine, defaultSettings, modelId, serviceId);
        return builder;
    }

    /// <summary>Adds a <see cref="LiteRtChatCompletionService"/> from <paramref name="options"/>; the
    /// container loads, owns and disposes a shared engine (see <see cref="AddLiteRtEngine(IServiceCollection, LiteRtEngineOptions, bool)"/>).</summary>
    public static IKernelBuilder AddLiteRtChatCompletion(
        this IKernelBuilder builder, LiteRtEngineOptions options,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null, bool eager = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtChatCompletion(options, defaultSettings, modelId, serviceId, eager);
        return builder;
    }

    /// <inheritdoc cref="AddLiteRtChatCompletion(IKernelBuilder, LiteRtEngine, LiteRtPromptExecutionSettings, string, string)"/>
    public static IServiceCollection AddLiteRtChatCompletion(
        this IServiceCollection services, LiteRtEngine engine,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        return RegisterChat(services, _ => engine, defaultSettings, modelId, serviceId);
    }

    /// <inheritdoc cref="AddLiteRtChatCompletion(IKernelBuilder, LiteRtEngineOptions, LiteRtPromptExecutionSettings, string, string, bool)"/>
    public static IServiceCollection AddLiteRtChatCompletion(
        this IServiceCollection services, LiteRtEngineOptions options,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null, bool eager = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddLiteRtEngine(options, eager);
        return RegisterChat(services, static sp => sp.GetRequiredService<LiteRtEngine>(), defaultSettings, modelId, serviceId);
    }

    /// <summary>Adds a <see cref="LiteRtChatCompletionService"/> over a <see cref="LiteRtEngine"/> already
    /// registered in the container — e.g. one registered with
    /// <see cref="AddLiteRtEngine(IKernelBuilder, LiteRtEngineOptions, bool)"/>. Resolution throws if no
    /// engine is registered.</summary>
    public static IKernelBuilder AddLiteRtChatCompletion(
        this IKernelBuilder builder,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtChatCompletion(defaultSettings, modelId, serviceId);
        return builder;
    }

    /// <inheritdoc cref="AddLiteRtChatCompletion(IKernelBuilder, LiteRtPromptExecutionSettings, string, string)"/>
    public static IServiceCollection AddLiteRtChatCompletion(
        this IServiceCollection services,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return RegisterChat(services, static sp => sp.GetRequiredService<LiteRtEngine>(), defaultSettings, modelId, serviceId);
    }

    private static IServiceCollection RegisterChat(
        IServiceCollection services, Func<IServiceProvider, LiteRtEngine> engine,
        LiteRtPromptExecutionSettings? defaultSettings, string? modelId, string? serviceId)
    {
        LiteRtChatCompletionService Factory(IServiceProvider sp, object? _) => new(engine(sp), defaultSettings, modelId);
        if (serviceId is null)
            services.AddSingleton<IChatCompletionService>(sp => Factory(sp, null));
        else
            services.AddKeyedSingleton<IChatCompletionService>(serviceId, Factory);
        return services;
    }

    // ─────────────────────────── Text generation ───────────────────────────

    /// <summary>Adds a <see cref="LiteRtTextGenerationService"/> over an engine <b>you own</b> (you load
    /// and dispose it; it must outlive the service).</summary>
    public static IKernelBuilder AddLiteRtTextGeneration(
        this IKernelBuilder builder, LiteRtEngine engine,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtTextGeneration(engine, defaultSettings, modelId, serviceId);
        return builder;
    }

    /// <summary>Adds a <see cref="LiteRtTextGenerationService"/> from <paramref name="options"/>; the
    /// container loads, owns and disposes a shared engine.</summary>
    public static IKernelBuilder AddLiteRtTextGeneration(
        this IKernelBuilder builder, LiteRtEngineOptions options,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null, bool eager = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtTextGeneration(options, defaultSettings, modelId, serviceId, eager);
        return builder;
    }

    /// <inheritdoc cref="AddLiteRtTextGeneration(IKernelBuilder, LiteRtEngine, LiteRtPromptExecutionSettings, string, string)"/>
    public static IServiceCollection AddLiteRtTextGeneration(
        this IServiceCollection services, LiteRtEngine engine,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        return RegisterText(services, _ => engine, defaultSettings, modelId, serviceId);
    }

    /// <inheritdoc cref="AddLiteRtTextGeneration(IKernelBuilder, LiteRtEngineOptions, LiteRtPromptExecutionSettings, string, string, bool)"/>
    public static IServiceCollection AddLiteRtTextGeneration(
        this IServiceCollection services, LiteRtEngineOptions options,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null, bool eager = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddLiteRtEngine(options, eager);
        return RegisterText(services, static sp => sp.GetRequiredService<LiteRtEngine>(), defaultSettings, modelId, serviceId);
    }

    /// <summary>Adds a <see cref="LiteRtTextGenerationService"/> over a <see cref="LiteRtEngine"/> already
    /// registered in the container — e.g. one registered with
    /// <see cref="AddLiteRtEngine(IKernelBuilder, LiteRtEngineOptions, bool)"/>. Resolution throws if no
    /// engine is registered.</summary>
    public static IKernelBuilder AddLiteRtTextGeneration(
        this IKernelBuilder builder,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtTextGeneration(defaultSettings, modelId, serviceId);
        return builder;
    }

    /// <inheritdoc cref="AddLiteRtTextGeneration(IKernelBuilder, LiteRtPromptExecutionSettings, string, string)"/>
    public static IServiceCollection AddLiteRtTextGeneration(
        this IServiceCollection services,
        LiteRtPromptExecutionSettings? defaultSettings = null, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return RegisterText(services, static sp => sp.GetRequiredService<LiteRtEngine>(), defaultSettings, modelId, serviceId);
    }

    private static IServiceCollection RegisterText(
        IServiceCollection services, Func<IServiceProvider, LiteRtEngine> engine,
        LiteRtPromptExecutionSettings? defaultSettings, string? modelId, string? serviceId)
    {
        LiteRtTextGenerationService Factory(IServiceProvider sp, object? _) => new(engine(sp), defaultSettings, modelId);
        if (serviceId is null)
            services.AddSingleton<ITextGenerationService>(sp => Factory(sp, null));
        else
            services.AddKeyedSingleton<ITextGenerationService>(serviceId, Factory);
        return services;
    }
}
