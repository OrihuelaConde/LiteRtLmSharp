using LiteRtLmSharp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;

// Placed in the Microsoft.SemanticKernel namespace (the convention the official connectors follow) so
// `builder.AddLiteRtChatCompletion` is discoverable wherever IKernelBuilder is in scope.
namespace Microsoft.SemanticKernel;

/// <summary>
/// Adds a LiteRtLmSharp on-device model to Semantic Kernel as an <see cref="IChatCompletionService"/>. Under
/// the hood this registers the LiteRtLmSharp <c>IChatClient</c> (Microsoft.Extensions.AI) and exposes it
/// through Semantic Kernel's
/// <see cref="ChatCompletionServiceExtensions.AsChatCompletionService(IChatClient, System.IServiceProvider)"/>
/// adapter, so all of Semantic Kernel's chat machinery — message conversion and function calling — flows
/// through that single chat client (function calling is auto-invoked via MEAI's function-invocation
/// middleware, since Semantic Kernel's adapter does not run the invoke loop for an <c>IChatClient</c>).
/// Register over an engine you own, or from a <see cref="LiteRtEngineOptions"/> (the container
/// loads/owns/disposes a single shared engine).
/// </summary>
public static class LiteRtKernelBuilderExtensions
{
    /// <summary>Adds a chat-completion service over an engine <b>you own</b> (you dispose it).</summary>
    public static IKernelBuilder AddLiteRtChatCompletion(this IKernelBuilder builder, LiteRtEngine engine, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtChatCompletion(engine, modelId, serviceId);
        return builder;
    }

    /// <summary>Adds a chat-completion service from <paramref name="options"/>; the container loads/owns/disposes
    /// a single shared engine. <paramref name="eager"/> loads it at registration. The underlying
    /// <c>IChatClient</c> is also registered, so the same model is available to Microsoft Agent Framework / MEAI.</summary>
    public static IKernelBuilder AddLiteRtChatCompletion(this IKernelBuilder builder, LiteRtEngineOptions options, string? modelId = null, string? serviceId = null, bool eager = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddLiteRtChatCompletion(options, modelId, serviceId, eager);
        return builder;
    }

    /// <inheritdoc cref="AddLiteRtChatCompletion(IKernelBuilder, LiteRtEngine, string, string)"/>
    public static IServiceCollection AddLiteRtChatCompletion(this IServiceCollection services, LiteRtEngine engine, string? modelId = null, string? serviceId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(engine);
        services.AddLiteRtChatClient(engine, modelId);
        return RegisterChatCompletion(services, serviceId);
    }

    /// <inheritdoc cref="AddLiteRtChatCompletion(IKernelBuilder, LiteRtEngineOptions, string, string, bool)"/>
    public static IServiceCollection AddLiteRtChatCompletion(this IServiceCollection services, LiteRtEngineOptions options, string? modelId = null, string? serviceId = null, bool eager = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddLiteRtChatClient(options, modelId, eager);
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
