using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;

// Placed in the Microsoft.SemanticKernel namespace (the convention the official connectors follow) so
// `builder.AddLiteRtChatCompletion` is discoverable wherever IKernelBuilder is in scope.
namespace Microsoft.SemanticKernel;

/// <summary>
/// Adds a LiteRtLmSharp on-device model to Semantic Kernel as an <see cref="IChatCompletionService"/>. Under
/// the hood this registers the LiteRtLmSharp <c>IChatClient</c> (Microsoft.Extensions.AI) and exposes it
/// through Semantic Kernel's <c>AsChatCompletionService</c>
/// adapter, so all of Semantic Kernel's chat machinery — message conversion and function calling — flows
/// through that single chat client (function calling is auto-invoked via MEAI's function-invocation
/// middleware, since Semantic Kernel's adapter does not run the invoke loop for an <c>IChatClient</c>).
/// Register over an engine you own, or from a <see cref="LiteRtEngineOptions"/> (the container
/// loads/owns/disposes a single shared engine).
/// </summary>
public static class LiteRtKernelBuilderExtensions
{
    /// <summary>Adds a chat-completion service over an engine <b>you own</b> (you dispose it).</summary>
    /// <param name="builder">The kernel builder to register into.</param>
    /// <param name="engine">The loaded engine (you dispose it, after the container-resolved service).</param>
    /// <param name="modelId">Optional model id surfaced on the chat client's metadata.</param>
    /// <param name="serviceId">Optional Semantic Kernel service id; registers a keyed service when set.</param>
    /// <param name="optionsTemplate">Optional per-client conversation-options template applied to every call —
    /// the settings MEAI's <c>ChatOptions</c> does not surface (system message, LoRA paths, tool-call streaming,
    /// visual token budget, thinking-KV filtering, extra context, a session-default output cap). Per-call values
    /// still win. Must not carry history (that is per-call) — rejected at registration.</param>
    /// <param name="statefulConversations">Optional opt-in to stateful conversations (see
    /// <see cref="LiteRtStatefulConversationOptions"/>). <c>null</c> (default) = stateless, today's behavior.
    /// After enabling, propagate <c>response.ConversationId</c> into <c>ChatOptions.ConversationId</c> and send
    /// only the new messages each turn (the <see cref="LiteRtChatClient"/> constructor docs show the loop);
    /// function-invocation and agent-thread layers do this automatically.</param>
    public static IKernelBuilder AddLiteRtChatCompletion(this IKernelBuilder builder, LiteRtEngine engine, string? modelId = null, string? serviceId = null, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtChatCompletion(engine, modelId, serviceId, optionsTemplate, statefulConversations);
        return builder;
    }

    /// <summary>Adds a chat-completion service from <paramref name="options"/>; the container loads/owns/disposes
    /// a single shared engine. <paramref name="eager"/> loads it at registration. The underlying
    /// <c>IChatClient</c> is also registered, so the same model is available to Microsoft Agent Framework / MEAI.</summary>
    /// <param name="builder">The kernel builder to register into.</param>
    /// <param name="options">Engine options the container uses to load the single shared engine.</param>
    /// <param name="modelId">Optional model id surfaced on the chat client's metadata.</param>
    /// <param name="serviceId">Optional Semantic Kernel service id; registers a keyed service when set.</param>
    /// <param name="eager">When <c>true</c>, load the engine now (a bad model path/backend throws here);
    /// otherwise it is loaded lazily on first use.</param>
    /// <param name="optionsTemplate">Optional per-client conversation-options template applied to every call —
    /// the settings MEAI's <c>ChatOptions</c> does not surface (system message, LoRA paths, tool-call streaming,
    /// visual token budget, thinking-KV filtering, extra context, a session-default output cap). Per-call values
    /// still win. Must not carry history (that is per-call) — rejected at registration.</param>
    /// <param name="statefulConversations">Optional opt-in to stateful conversations (see
    /// <see cref="LiteRtStatefulConversationOptions"/>). <c>null</c> (default) = stateless, today's behavior.
    /// After enabling, propagate <c>response.ConversationId</c> into <c>ChatOptions.ConversationId</c> and send
    /// only the new messages each turn (the <see cref="LiteRtChatClient"/> constructor docs show the loop);
    /// function-invocation and agent-thread layers do this automatically.</param>
    public static IKernelBuilder AddLiteRtChatCompletion(this IKernelBuilder builder, LiteRtEngineOptions options, string? modelId = null, string? serviceId = null, bool eager = false, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtChatCompletion(options, modelId, serviceId, eager, optionsTemplate, statefulConversations);
        return builder;
    }

    /// <inheritdoc cref="AddLiteRtChatCompletion(IKernelBuilder, LiteRtEngine, string, string, LiteRtConversationOptions, LiteRtStatefulConversationOptions)"/>
    public static IServiceCollection AddLiteRtChatCompletion(this IServiceCollection services, LiteRtEngine engine, string? modelId = null, string? serviceId = null, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        services.AddLiteRtChatClient(engine, modelId, optionsTemplate, statefulConversations);
        return RegisterChatCompletion(services, serviceId);
    }

    /// <inheritdoc cref="AddLiteRtChatCompletion(IKernelBuilder, LiteRtEngineOptions, string, string, bool, LiteRtConversationOptions, LiteRtStatefulConversationOptions)"/>
    public static IServiceCollection AddLiteRtChatCompletion(this IServiceCollection services, LiteRtEngineOptions options, string? modelId = null, string? serviceId = null, bool eager = false, LiteRtConversationOptions? optionsTemplate = null, LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddLiteRtChatClient(options, modelId, eager, optionsTemplate, statefulConversations);
        return RegisterChatCompletion(services, serviceId);
    }

    private static IServiceCollection RegisterChatCompletion(IServiceCollection services, string? serviceId)
    {
        // Expose the registered LiteRtLmSharp IChatClient as a Semantic Kernel IChatCompletionService, wrapped
        // with MEAI's function-invocation middleware. Semantic Kernel's AsChatCompletionService adapter passes
        // the kernel's functions to the client as tools but does NOT run the auto-invoke loop itself, so
        // UseFunctionInvocation is what makes FunctionChoiceBehavior actually invoke the functions. It is a
        // no-op when a request carries no tools, so it is safe to apply unconditionally.
        static IChatCompletionService Factory(IServiceProvider sp, object? _) =>
            sp.GetRequiredService<IChatClient>()
              .AsBuilder()
              .UseFunctionInvocation()
              .Build(sp)
              .AsChatCompletionService(sp);

        if (serviceId is null)
            services.AddSingleton<IChatCompletionService>(sp => Factory(sp, null));
        else
            services.AddKeyedSingleton<IChatCompletionService>(serviceId, Factory);
        return services;
    }
}
