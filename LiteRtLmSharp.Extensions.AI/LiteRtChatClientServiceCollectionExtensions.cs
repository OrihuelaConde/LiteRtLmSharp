using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the LiteRtLmSharp <see cref="IChatClient"/>. Register over an engine you own, or
/// from a <see cref="LiteRtEngineOptions"/> (the container loads, owns and disposes the engine). The registered
/// <see cref="IChatClient"/> can be wrapped with the usual Microsoft.Extensions.AI middleware
/// (e.g. <c>UseFunctionInvocation()</c>, caching, telemetry) by the consuming application, used to build a
/// Microsoft Agent Framework agent (<c>new ChatClientAgent(client, …)</c>), or exposed to Semantic Kernel.
/// </summary>
/// <remarks>
/// One container holds one unkeyed chat client, plus one per key registered with
/// <c>AddKeyedLiteRtChatClient</c>. Registering again under the same key (or none) with the same engine or
/// the same engine options is a no-op, and the first registration's template and stateful settings stay; a
/// different engine or different options throw <see cref="InvalidOperationException"/> instead of being
/// ignored. Two keyed clients from options load two engines, so set
/// <see cref="LiteRtEngineOptions.AllowMultipleEngines"/> on both.
/// </remarks>
public static class LiteRtChatClientServiceCollectionExtensions
{
    /// <summary>Registers a <see cref="LiteRtChatClient"/> over an engine <b>you own</b> (you dispose it).</summary>
    /// <param name="services">The DI service collection to register into.</param>
    /// <param name="engine">The loaded engine (you dispose it, after the container-resolved client).</param>
    /// <param name="modelId">Optional model id surfaced on the chat client's metadata.</param>
    /// <param name="optionsTemplate">Optional per-client conversation-options template applied to every call
    /// (see <see cref="LiteRtChatClient(LiteRtEngine, string, LiteRtConversationOptions, LiteRtStatefulConversationOptions)"/> for the merge rules).
    /// Must not set <see cref="LiteRtConversationOptions.History"/> / <see cref="LiteRtConversationOptions.HistoryJson"/>
    /// (history is per-call) — rejected here, at registration.</param>
    /// <param name="statefulConversations">Optional opt-in to stateful conversations (see
    /// <see cref="LiteRtStatefulConversationOptions"/>). <c>null</c> (default) = stateless, today's behavior.
    /// After enabling, propagate <c>response.ConversationId</c> into <c>ChatOptions.ConversationId</c> and send
    /// only the new messages each turn (the <see cref="LiteRtChatClient"/> constructor docs show the loop);
    /// function-invocation and agent-thread layers do this automatically.</param>
    /// <exception cref="ArgumentException">The template sets <c>History</c> or <c>HistoryJson</c>.</exception>
    /// <exception cref="InvalidOperationException">An unkeyed chat client is already registered over a different
    /// engine or from engine options.</exception>
    public static IServiceCollection AddLiteRtChatClient(this IServiceCollection services, LiteRtEngine engine, string? modelId = null, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        LiteRtChatMapping.ValidateTemplate(optionsTemplate);   // fail at registration, not first use
        if (!ClaimKey(services, serviceKey: null, engine, options: null))
            return services;
        return Register(services, serviceKey: null, _ => engine, modelId, optionsTemplate, statefulConversations);
    }

    /// <summary>Registers a <see cref="LiteRtChatClient"/> from <paramref name="options"/>; the container loads,
    /// owns and disposes a single shared engine.</summary>
    /// <param name="services">The DI service collection to register into.</param>
    /// <param name="options">Engine options the container uses to load the single shared engine.</param>
    /// <param name="modelId">Optional model id surfaced on the chat client's metadata.</param>
    /// <param name="eager">When <c>true</c>, load the engine now (a bad model path/backend throws here);
    /// otherwise it is loaded lazily on first use.</param>
    /// <param name="optionsTemplate">Optional per-client conversation-options template applied to every call
    /// (see <see cref="LiteRtChatClient(LiteRtEngine, string, LiteRtConversationOptions, LiteRtStatefulConversationOptions)"/> for the merge rules).
    /// Must not set <see cref="LiteRtConversationOptions.History"/> / <see cref="LiteRtConversationOptions.HistoryJson"/>
    /// (history is per-call) — rejected here, at registration.</param>
    /// <param name="statefulConversations">Optional opt-in to stateful conversations (see
    /// <see cref="LiteRtStatefulConversationOptions"/>). <c>null</c> (default) = stateless, today's behavior.
    /// After enabling, propagate <c>response.ConversationId</c> into <c>ChatOptions.ConversationId</c> and send
    /// only the new messages each turn (the <see cref="LiteRtChatClient"/> constructor docs show the loop);
    /// function-invocation and agent-thread layers do this automatically.</param>
    /// <exception cref="ArgumentException">The template sets <c>History</c> or <c>HistoryJson</c>.</exception>
    /// <exception cref="InvalidOperationException">An unkeyed chat client is already registered from different
    /// engine options or over an engine you own.</exception>
    public static IServiceCollection AddLiteRtChatClient(this IServiceCollection services, LiteRtEngineOptions options, string? modelId = null, bool eager = false, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        LiteRtChatMapping.ValidateTemplate(optionsTemplate);   // fail at registration, not first use
        if (!ClaimKey(services, serviceKey: null, engine: null, options))
            return services;
        RegisterEngine(services, serviceKey: null, options, eager);
        return Register(services, serviceKey: null, static sp => sp.GetRequiredService<LiteRtEngine>(), modelId, optionsTemplate, statefulConversations);
    }

    /// <summary>Registers a keyed <see cref="LiteRtChatClient"/> over an engine <b>you own</b> (you dispose it),
    /// resolved with <c>GetRequiredKeyedService&lt;IChatClient&gt;(serviceKey)</c>. Use it to register several
    /// models side by side, for example one per agent.</summary>
    /// <param name="services">The DI service collection to register into.</param>
    /// <param name="serviceKey">The key the client is registered under.</param>
    /// <param name="engine">The loaded engine (you dispose it, after the container-resolved client).</param>
    /// <param name="modelId">Optional model id surfaced on the chat client's metadata.</param>
    /// <param name="optionsTemplate">Optional per-client conversation-options template applied to every call
    /// (see <see cref="LiteRtChatClient(LiteRtEngine, string, LiteRtConversationOptions, LiteRtStatefulConversationOptions)"/> for the merge rules).
    /// Must not set <see cref="LiteRtConversationOptions.History"/> / <see cref="LiteRtConversationOptions.HistoryJson"/>
    /// (history is per-call) — rejected here, at registration.</param>
    /// <param name="statefulConversations">Optional opt-in to stateful conversations (see
    /// <see cref="LiteRtStatefulConversationOptions"/>). <c>null</c> (default) = stateless.</param>
    /// <exception cref="ArgumentException">The template sets <c>History</c> or <c>HistoryJson</c>.</exception>
    /// <exception cref="InvalidOperationException">A chat client is already registered under
    /// <paramref name="serviceKey"/> over a different engine or from engine options.</exception>
    public static IServiceCollection AddKeyedLiteRtChatClient(this IServiceCollection services, object serviceKey, LiteRtEngine engine, string? modelId = null, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(engine);
        LiteRtChatMapping.ValidateTemplate(optionsTemplate);
        if (!ClaimKey(services, serviceKey, engine, options: null))
            return services;
        return Register(services, serviceKey, _ => engine, modelId, optionsTemplate, statefulConversations);
    }

    /// <summary>Registers a keyed <see cref="LiteRtChatClient"/> from <paramref name="options"/>, with its own
    /// container-owned engine registered under the same key. Each keyed engine is a separate engine, so load
    /// them with <see cref="LiteRtEngineOptions.AllowMultipleEngines"/> when more than one can be alive.</summary>
    /// <param name="services">The DI service collection to register into.</param>
    /// <param name="serviceKey">The key the client and its engine are registered under.</param>
    /// <param name="options">Engine options the container uses to load this key's engine.</param>
    /// <param name="modelId">Optional model id surfaced on the chat client's metadata.</param>
    /// <param name="eager">When <c>true</c>, load the engine now (a bad model path/backend throws here);
    /// otherwise it is loaded lazily on first use.</param>
    /// <param name="optionsTemplate">Optional per-client conversation-options template applied to every call
    /// (see <see cref="LiteRtChatClient(LiteRtEngine, string, LiteRtConversationOptions, LiteRtStatefulConversationOptions)"/> for the merge rules).
    /// Must not set <see cref="LiteRtConversationOptions.History"/> / <see cref="LiteRtConversationOptions.HistoryJson"/>
    /// (history is per-call) — rejected here, at registration.</param>
    /// <param name="statefulConversations">Optional opt-in to stateful conversations (see
    /// <see cref="LiteRtStatefulConversationOptions"/>). <c>null</c> (default) = stateless.</param>
    /// <exception cref="ArgumentException">The template sets <c>History</c> or <c>HistoryJson</c>.</exception>
    /// <exception cref="InvalidOperationException">A chat client is already registered under
    /// <paramref name="serviceKey"/> from different engine options or over an engine you own.</exception>
    public static IServiceCollection AddKeyedLiteRtChatClient(this IServiceCollection services, object serviceKey, LiteRtEngineOptions options, string? modelId = null, bool eager = false, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(options);
        LiteRtChatMapping.ValidateTemplate(optionsTemplate);
        if (!ClaimKey(services, serviceKey, engine: null, options))
            return services;
        RegisterEngine(services, serviceKey, options, eager);
        return Register(services, serviceKey, sp => sp.GetRequiredKeyedService<LiteRtEngine>(serviceKey), modelId, optionsTemplate, statefulConversations);
    }

    /// <summary>
    /// Claims <paramref name="serviceKey"/> (or the unkeyed slot) for one chat client. Returns <c>false</c> when
    /// the same engine, or engine options equal field by field, were already registered there (a repeat is a
    /// no-op); throws when something different was; returns <c>true</c> and records the claim otherwise.
    /// </summary>
    private static bool ClaimKey(IServiceCollection services, object? serviceKey, LiteRtEngine? engine, LiteRtEngineOptions? options)
    {
        ChatClientRegistration? existing = services
            .Where(d => d.ServiceType == typeof(ChatClientRegistration) && Equals(d.ServiceKey, serviceKey))
            .Select(d => (serviceKey is null ? d.ImplementationInstance : d.KeyedImplementationInstance) as ChatClientRegistration)
            .FirstOrDefault();
        if (existing is not null)
        {
            bool same = engine is not null
                ? ReferenceEquals(existing.Engine, engine)
                : existing.Options is not null && SameEngineOptions(existing.Options, options!);
            if (!same)
                throw new InvalidOperationException(
                    "A LiteRtLmSharp chat client is already registered " + (serviceKey is null ? "without a key " : $"under '{serviceKey}' ") +
                    "over a different engine or from different LiteRtEngineOptions. Register one chat client per key: " +
                    "use AddKeyedLiteRtChatClient (or a Semantic Kernel serviceId) to run several models side by side.");
            return false;
        }

        var claim = new ChatClientRegistration(engine, options);
        if (serviceKey is null)
            services.AddSingleton(claim);
        else
            services.AddKeyedSingleton(serviceKey, claim);
        return true;
    }

    /// <summary>Whether two engine options describe the same engine. Records compare list members by reference,
    /// so the LoRA rank lists are compared element by element.</summary>
    internal static bool SameEngineOptions(LiteRtEngineOptions a, LiteRtEngineOptions b)
        => a with { SupportedLoraRanks = null, SupportedAudioLoraRanks = null }
               == b with { SupportedLoraRanks = null, SupportedAudioLoraRanks = null }
           && SameList(a.SupportedLoraRanks, b.SupportedLoraRanks)
           && SameList(a.SupportedAudioLoraRanks, b.SupportedAudioLoraRanks);

    private static bool SameList(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
        => a is null ? b is null : b is not null && a.SequenceEqual(b);

    // Registers the container-owned engine for a client from options. Unkeyed, an engine registered another
    // way (for example a LiteRtEngine instance you added yourself) stays in charge.
    private static void RegisterEngine(IServiceCollection services, object? serviceKey, LiteRtEngineOptions options, bool eager)
    {
        if (serviceKey is null && services.Any(d => d.ServiceType == typeof(LiteRtEngine) && d.ServiceKey is null))
            return;

        LiteRtEngine? preloaded = eager ? LiteRtEngine.Load(options) : null;   // eager: a bad path/backend throws here
        // Factory registrations: the container creates (or adopts) and disposes the engine once resolved.
        Func<IServiceProvider, LiteRtEngine> create = _ => preloaded ?? LiteRtEngine.Load(options);
        if (serviceKey is null)
            services.AddSingleton(create);
        else
            services.AddKeyedSingleton(serviceKey, (sp, _) => create(sp));
    }

    private static IServiceCollection Register(IServiceCollection services, object? serviceKey, Func<IServiceProvider, LiteRtEngine> engine, string? modelId, LiteRtConversationOptions? optionsTemplate, LiteRtStatefulConversationOptions? statefulConversations)
    {
        // Unkeyed, TryAdd keeps an IChatClient you registered yourself in charge; the Semantic Kernel package
        // reusing this shares the one client.
        if (serviceKey is null)
            services.TryAddSingleton<IChatClient>(sp => new LiteRtChatClient(engine(sp), modelId, optionsTemplate, statefulConversations));
        else
            services.AddKeyedSingleton<IChatClient>(serviceKey, (sp, _) => new LiteRtChatClient(engine(sp), modelId, optionsTemplate, statefulConversations));
        return services;
    }

    /// <summary>Marks which engine or engine options a chat client was registered from, per key.</summary>
    internal sealed record ChatClientRegistration(LiteRtEngine? Engine, LiteRtEngineOptions? Options);
}
