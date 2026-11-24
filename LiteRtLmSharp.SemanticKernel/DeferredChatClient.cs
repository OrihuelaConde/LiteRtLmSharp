using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.SemanticKernel;

/// <summary>
/// An <see cref="IChatClient"/> that builds the real client on its first call and answers
/// <see cref="ChatClientMetadata"/> without building it. Creating a Semantic Kernel service reads that metadata, so
/// the engine behind the client loads only when the service is used. The container owns what the real client wraps,
/// so disposing this does nothing. A failed build is retried on the next call, and two calls racing the first build
/// can build twice, which is harmless: the container returns the same singleton underneath.
/// </summary>
internal sealed class DeferredChatClient(Func<IChatClient> build, ChatClientMetadata metadata) : IChatClient
{
    private IChatClient? _client;

    private IChatClient Client => _client ??= build();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Client.GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Client.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(ChatClientMetadata)
            ? metadata
            : Client.GetService(serviceType, serviceKey);
    }

    public void Dispose() { }
}
