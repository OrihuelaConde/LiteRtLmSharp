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
/// different engine or different options under that key throw <see cref="InvalidOperationException"/> instead
/// of being ignored. An <see cref="IChatClient"/> or <see cref="LiteRtEngine"/> you registered yourself under the
/// same key stays in charge. Registrations under different keys from equal engine options share one engine, so a
/// model loads once, and a LoRA adapter one of them sets in its template applies to that whole engine (see
/// <see cref="LiteRtConversationOptions.LoraPath"/>); registrations from different options load one engine each,
/// so set <see cref="LiteRtEngineOptions.AllowMultipleEngines"/> on each of them.
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
    /// function-invocation and agent-session layers do this automatically.</param>
    /// <exception cref="ArgumentException">The template sets <c>History</c> or <c>HistoryJson</c>.</exception>
    /// <exception cref="InvalidOperationException">An unkeyed chat client is already registered over a different
    /// engine or from engine options.</exception>
    public static IServiceCollection AddLiteRtChatClient(this IServiceCollection services, LiteRtEngine engine, string? modelId = null, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        LiteRtChatMapping.ValidateTemplate(optionsTemplate);   // fail at registration, not first use
        if (IsRegistered(services, serviceKey: null, engine, options: null))
            return services;
        Register(services, serviceKey: null, _ => engine, modelId, optionsTemplate, statefulConversations);
        Claim(services, serviceKey: null, new ChatClientRegistration(engine, Options: null));
        return services;
    }

    /// <summary>Registers a <see cref="LiteRtChatClient"/> from <paramref name="options"/>; the container loads,
    /// owns and disposes the engine.</summary>
    /// <param name="services">The DI service collection to register into.</param>
    /// <param name="options">Engine options the container uses to load the engine.</param>
    /// <param name="modelId">Optional model id surfaced on the chat client's metadata.</param>
    /// <param name="eager">When <c>true</c>, load the engine now (a bad model path/backend throws here);
    /// otherwise it is loaded lazily on first use. It has no effect when an earlier registration from equal options
    /// already provides the engine. An eager engine that is never resolved lives until the process ends, because
    /// only a resolved engine is disposed by the container.</param>
    /// <param name="optionsTemplate">Optional per-client conversation-options template applied to every call
    /// (see <see cref="LiteRtChatClient(LiteRtEngine, string, LiteRtConversationOptions, LiteRtStatefulConversationOptions)"/> for the merge rules).
    /// Must not set <see cref="LiteRtConversationOptions.History"/> / <see cref="LiteRtConversationOptions.HistoryJson"/>
    /// (history is per-call) — rejected here, at registration.</param>
    /// <param name="statefulConversations">Optional opt-in to stateful conversations (see
    /// <see cref="LiteRtStatefulConversationOptions"/>). <c>null</c> (default) = stateless, today's behavior.
    /// After enabling, propagate <c>response.ConversationId</c> into <c>ChatOptions.ConversationId</c> and send
    /// only the new messages each turn (the <see cref="LiteRtChatClient"/> constructor docs show the loop);
    /// function-invocation and agent-session layers do this automatically.</param>
    /// <exception cref="ArgumentException">The template sets <c>History</c> or <c>HistoryJson</c>.</exception>
    /// <exception cref="InvalidOperationException">An unkeyed chat client is already registered from different
    /// engine options or over an engine you own.</exception>
    public static IServiceCollection AddLiteRtChatClient(this IServiceCollection services, LiteRtEngineOptions options, string? modelId = null, bool eager = false, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        LiteRtChatMapping.ValidateTemplate(optionsTemplate);   // fail at registration, not first use
        if (IsRegistered(services, serviceKey: null, engine: null, options))
            return services;
        Func<IServiceProvider, LiteRtEngine> resolve = RegisterEngine(services, serviceKey: null, options, eager);
        Register(services, serviceKey: null, resolve, modelId, optionsTemplate, statefulConversations);
        Claim(services, serviceKey: null, new ChatClientRegistration(Engine: null, options));
        return services;
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
        if (IsRegistered(services, serviceKey, engine, options: null))
            return services;
        Register(services, serviceKey, _ => engine, modelId, optionsTemplate, statefulConversations);
        Claim(services, serviceKey, new ChatClientRegistration(engine, Options: null));
        return services;
    }

    /// <summary>Registers a keyed <see cref="LiteRtChatClient"/> from <paramref name="options"/>, with a
    /// container-owned engine registered under the same key. A registration under another key from equal
    /// options shares its engine; different options load a separate engine, so load them with
    /// <see cref="LiteRtEngineOptions.AllowMultipleEngines"/> when more than one can be alive.</summary>
    /// <param name="services">The DI service collection to register into.</param>
    /// <param name="serviceKey">The key the client and its engine are registered under.</param>
    /// <param name="options">Engine options the container uses to load this key's engine.</param>
    /// <param name="modelId">Optional model id surfaced on the chat client's metadata.</param>
    /// <param name="eager">When <c>true</c>, load the engine now (a bad model path/backend throws here);
    /// otherwise it is loaded lazily on first use. It has no effect when an earlier registration from equal options
    /// already provides the engine. An eager engine that is never resolved lives until the process ends, because
    /// only a resolved engine is disposed by the container.</param>
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
        return AddKeyedFromOptions(services, serviceKey, options, modelId, eager, optionsTemplate, statefulConversations, reuseUnkeyedEngine: false);
    }

    /// <summary>The keyed registration from options. <paramref name="reuseUnkeyedEngine"/> makes a LiteRtEngine the
    /// app registered without a key serve this key, which the Semantic Kernel registrations do, as they did before
    /// keyed clients existed.</summary>
    internal static IServiceCollection AddKeyedFromOptions(IServiceCollection services, object serviceKey, LiteRtEngineOptions options, string? modelId, bool eager, LiteRtConversationOptions? optionsTemplate, LiteRtStatefulConversationOptions? statefulConversations, bool reuseUnkeyedEngine)
    {
        LiteRtChatMapping.ValidateTemplate(optionsTemplate);
        ThrowIfAnyKey(serviceKey);
        if (IsRegistered(services, serviceKey, engine: null, options))
            return services;
        Func<IServiceProvider, LiteRtEngine> resolve = RegisterEngine(services, serviceKey, options, eager, reuseUnkeyedEngine);
        Register(services, serviceKey, resolve, modelId, optionsTemplate, statefulConversations);
        Claim(services, serviceKey, new ChatClientRegistration(Engine: null, options));
        return services;
    }

    // The client resolves its engine by the registered key, and the container refuses AnyKey for a single service.
    private static void ThrowIfAnyKey(object serviceKey)
    {
        if (ReferenceEquals(serviceKey, KeyedService.AnyKey))
            throw new ArgumentException(
                "KeyedService.AnyKey cannot name a chat client registered from options: its engine is resolved by that " +
                "key. Register each key you use.", nameof(serviceKey));
    }

    /// <summary>
    /// Whether <paramref name="serviceKey"/> (or the unkeyed slot) already holds a chat client from the same
    /// engine, or from engine options equal field by field (a repeat is a no-op). Throws when it holds one from
    /// something different. Checked before anything is registered; the claim is recorded only once the
    /// registration succeeded (<see cref="Claim"/>), so a failed eager load leaves the key free for a retry. A claim
    /// whose client was removed from the collection no longer counts, so a host can replace a registration.
    /// </summary>
    private static bool IsRegistered(IServiceCollection services, object? serviceKey, LiteRtEngine? engine, LiteRtEngineOptions? options)
    {
        ChatClientRegistration? existing = Claims(services)
            .Where(c => Equals(c.Key, serviceKey))
            .Select(c => c.Registration)
            .FirstOrDefault();
        if (existing is null)
            return false;

        if (!services.Any(d => d.ServiceType == typeof(IChatClient) && Equals(d.ServiceKey, serviceKey)))
        {
            ServiceDescriptor stale = services.First(d => d.ServiceType == typeof(ChatClientRegistration) && Equals(d.ServiceKey, serviceKey));
            services.Remove(stale);
            return false;
        }

        bool same = engine is not null
            ? ReferenceEquals(existing.Engine, engine)
            : existing.Options is not null && SameEngineOptions(existing.Options, options!);
        if (same)
            return true;

        if (existing.AliasOf is { } serviceId)
            throw new InvalidOperationException(
                $"The Semantic Kernel serviceId '{serviceId}' also made its LiteRtLmSharp chat client the one without a " +
                "key, so another one without a key would be ignored. Register the chat client without a key first, " +
                "or give it a key with AddKeyedLiteRtChatClient.");
        throw new InvalidOperationException(
            "A LiteRtLmSharp chat client is already registered " + (serviceKey is null ? "without a key " : $"under '{serviceKey}' ") +
            "over a different engine or from different LiteRtEngineOptions. Register one chat client per key: " +
            "use AddKeyedLiteRtChatClient (or a Semantic Kernel serviceId) to run several models side by side.");
    }

    private static void Claim(IServiceCollection services, object? serviceKey, ChatClientRegistration registration)
    {
        if (serviceKey is null)
            services.AddSingleton(registration);
        else
            services.AddKeyedSingleton(serviceKey, registration);
    }

    private static IEnumerable<(object? Key, ChatClientRegistration Registration)> Claims(IServiceCollection services)
        => services
            .Where(d => d.ServiceType == typeof(ChatClientRegistration))
            .Select(d => (d.ServiceKey, (ChatClientRegistration)(d.IsKeyedService ? d.KeyedImplementationInstance! : d.ImplementationInstance!)));

    /// <summary>Whether two engine options describe the same engine. Records compare list members by reference,
    /// so the LoRA rank lists are compared element by element.</summary>
    internal static bool SameEngineOptions(LiteRtEngineOptions a, LiteRtEngineOptions b)
        => a with { SupportedLoraRanks = null, SupportedAudioLoraRanks = null }
               == b with { SupportedLoraRanks = null, SupportedAudioLoraRanks = null }
           && SameList(a.SupportedLoraRanks, b.SupportedLoraRanks)
           && SameList(a.SupportedAudioLoraRanks, b.SupportedAudioLoraRanks);

    private static bool SameList(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
        => a is null ? b is null : b is not null && a.SequenceEqual(b);

    // Registers the engine for a client from options under the client's key and returns how the client resolves
    // it. An engine registered another way under that key (for example a LiteRtEngine you added yourself) stays
    // in charge. Equal options already registered under another key share that key's engine, so one model is
    // loaded once. With reuseUnkeyedEngine (a Semantic Kernel serviceId), a LiteRtEngine the app registered
    // without a key is used, as before keyed clients existed. Otherwise the container loads, owns and disposes a
    // new engine.
    private static Func<IServiceProvider, LiteRtEngine> RegisterEngine(IServiceCollection services, object? serviceKey, LiteRtEngineOptions options, bool eager, bool reuseUnkeyedEngine = false)
    {
        Func<IServiceProvider, LiteRtEngine> resolve = sp => ResolveEngine(sp, serviceKey);
        if (services.Any(d => d.ServiceType == typeof(LiteRtEngine) && Equals(d.ServiceKey, serviceKey)))
            return resolve;

        (object? Key, ChatClientRegistration Registration) owner = Claims(services)
            .FirstOrDefault(c => c.Registration.Options is { } other && SameEngineOptions(other, options));
        if (owner.Registration is not null)
        {
            AddAlias<LiteRtEngine>(services, serviceKey, owner.Key);
            return resolve;
        }

        if (reuseUnkeyedEngine
            && services.Any(d => d.ServiceType == typeof(LiteRtEngine) && d.ServiceKey is null)
            && !Claims(services).Any(c => c.Key is null && c.Registration.Options is not null))
        {
            AddAlias<LiteRtEngine>(services, serviceKey, targetKey: null);
            return resolve;
        }

        LiteRtEngine? preloaded = eager ? LiteRtEngine.Load(options) : null;   // eager: a bad path/backend throws here
        // Factory registrations: the container creates (or adopts) and disposes the engine once resolved.
        Func<IServiceProvider, LiteRtEngine> create = _ => preloaded ?? LiteRtEngine.Load(options);
        if (serviceKey is null)
            services.AddSingleton(create);
        else
            services.AddKeyedSingleton(serviceKey, (sp, _) => create(sp));
        return resolve;
    }

    private static LiteRtEngine ResolveEngine(IServiceProvider sp, object? serviceKey)
        => serviceKey is null ? sp.GetRequiredService<LiteRtEngine>() : sp.GetRequiredKeyedService<LiteRtEngine>(serviceKey);

    // Registers the service under targetKey under aliasKey too. An instance the app registered stays an instance:
    // the container would dispose what a factory returned. A container-owned engine is tracked twice, which is
    // safe: a disposed engine stays alive natively until its last conversation is disposed.
    private static void AddAlias<T>(IServiceCollection services, object? aliasKey, object? targetKey) where T : class
    {
        ServiceDescriptor target = services.Last(d => d.ServiceType == typeof(T) && Equals(d.ServiceKey, targetKey));
        object? instance = target.IsKeyedService ? target.KeyedImplementationInstance : target.ImplementationInstance;
        if (instance is T owned)
        {
            if (aliasKey is null)
                services.TryAddSingleton(owned);
            else
                services.AddKeyedSingleton(aliasKey, owned);
            return;
        }

        Func<IServiceProvider, T> resolve = targetKey is null
            ? sp => sp.GetRequiredService<T>()
            : sp => sp.GetRequiredKeyedService<T>(targetKey);
        if (aliasKey is null)
            services.TryAddSingleton(resolve);
        else
            services.AddKeyedSingleton<T>(aliasKey, (sp, _) => resolve(sp));
    }

    private static void Register(IServiceCollection services, object? serviceKey, Func<IServiceProvider, LiteRtEngine> engine, string? modelId, LiteRtConversationOptions? optionsTemplate, LiteRtStatefulConversationOptions? statefulConversations)
    {
        // TryAdd keeps an IChatClient you registered yourself (under the same key, or none) in charge.
        if (serviceKey is null)
            services.TryAddSingleton<IChatClient>(sp => new LiteRtChatClient(engine(sp), modelId, optionsTemplate, statefulConversations));
        else
            services.TryAddKeyedSingleton<IChatClient>(serviceKey, (sp, _) => new LiteRtChatClient(engine(sp), modelId, optionsTemplate, statefulConversations));
    }

    /// <summary>
    /// Makes the chat client registered under <paramref name="serviceKey"/> the unkeyed <see cref="IChatClient"/>
    /// too (and, for a client from options, its engine the unkeyed <see cref="LiteRtEngine"/>) when the unkeyed
    /// slot is free, and claims that slot with the same engine or options. A later unkeyed registration from the
    /// same settings is then a no-op, and one from different settings throws instead of being ignored. The
    /// Semantic Kernel registrations use it, so a model registered with a serviceId stays available without a key.
    /// </summary>
    internal static void AliasUnkeyed(IServiceCollection services, object serviceKey)
    {
        if (Claims(services).Any(c => c.Key is null))
            return;

        ChatClientRegistration keyed = Claims(services).First(c => Equals(c.Key, serviceKey)).Registration;
        if (keyed.Options is not null)
            AddAlias<LiteRtEngine>(services, aliasKey: null, serviceKey);

        // An unkeyed IChatClient you registered yourself stays in charge, and nothing claims the slot.
        if (services.Any(d => d.ServiceType == typeof(IChatClient) && d.ServiceKey is null))
            return;
        AddAlias<IChatClient>(services, aliasKey: null, serviceKey);
        Claim(services, serviceKey: null, keyed with { AliasOf = serviceKey });
    }

    /// <summary>Marks which engine or engine options a chat client was registered from, per key. On the unkeyed
    /// slot, <paramref name="AliasOf"/> is the key whose client took it.</summary>
    internal sealed record ChatClientRegistration(LiteRtEngine? Engine, LiteRtEngineOptions? Options, object? AliasOf = null);
}
