using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// A <see cref="IChatClient"/> (Microsoft.Extensions.AI) backed by a LiteRtLmSharp on-device model. This is
/// the framework-agnostic integration: the same instance works with Microsoft Agent Framework
/// (<c>new ChatClientAgent(client, …)</c>), Semantic Kernel (<c>services.AddChatClient(client)</c>), and any
/// other <see cref="IChatClient"/> consumer or middleware pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless by design.</b> MEAI hands the full message list on every call, so the client re-prefills the
/// prior turns each time rather than holding a long-lived conversation — this keeps the caller's history and
/// the model's KV cache from diverging. The cost is an O(history) prefill per turn; for very long chats
/// prefer the native <see cref="LiteRtConversation"/> API directly.
/// </para>
/// <para>
/// <b>Serialized.</b> Calls are serialized with an internal gate: LiteRtLmSharp allows only one live engine
/// per process and conversations are not thread-safe.
/// </para>
/// <para>
/// <b>Reasoning.</b> Enable the model's reasoning ("thinking") mode with the <c>enable_thinking</c> key in
/// <see cref="ChatOptions.AdditionalProperties"/>. The reasoning trace is surfaced as
/// <see cref="TextReasoningContent"/> (excluded from <see cref="ChatResponse.Text"/>, so the answer stays
/// clean) rather than dropped. Note the reasoning shares the <see cref="ChatOptions.MaxOutputTokens"/>
/// budget with the answer, so give thinking models a generous budget — otherwise the reasoning can consume
/// it and leave the answer empty.
/// </para>
/// <para><b>Scope.</b> Tool calling is not bridged yet (the message list must end with a user message); it is planned.</para>
/// </remarks>
public sealed class LiteRtChatClient : IChatClient
{
    private readonly LiteRtEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ChatClientMetadata _metadata;
    private bool _disposed;

    /// <summary>Creates a chat client over an already-loaded <paramref name="engine"/>. The client does not
    /// own the engine: dispose the engine yourself (after the client). To have a container manage the
    /// engine's lifetime, register from <see cref="LiteRtEngineOptions"/> via <c>AddLiteRtChatClient</c>.</summary>
    /// <param name="engine">The loaded LiteRtLmSharp engine. Must outlive this client.</param>
    /// <param name="modelId">Identifier surfaced as the metadata default model id. Optional.</param>
    public LiteRtChatClient(LiteRtEngine engine, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _metadata = new ChatClientMetadata("litert-lm", null, modelId);
    }

    private string? ModelId => _metadata.DefaultModelId;

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messages);
        (IReadOnlyList<LiteRtMessage> history, string userText) = LiteRtChatMapping.Split(messages);
        LiteRtConversationOptions? convOptions = LiteRtChatMapping.ToConversationOptions(history, options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using LiteRtConversation conv = _engine.CreateConversation(convOptions);
            // Send is a blocking native call; offload it so the async contract holds and the gate wait +
            // pre-call cancellation are honored (it does not interrupt generation mid-flight — use the
            // streaming overload for cooperative mid-generation cancellation).
            LiteRtResponse response = await Task.Run(() => conv.Send(userText), cancellationToken).ConfigureAwait(false);

            // The answer becomes TextContent; any reasoning ("thinking") trace becomes TextReasoningContent,
            // which ChatResponse.Text excludes (so the answer stays clean) but Contents keeps (so a thinking
            // response is never empty when there is reasoning).
            var contents = new List<AIContent>(2);
            if (response.Thinking is { Length: > 0 } reasoning)
                contents.Add(new TextReasoningContent(reasoning));
            if (response.Text is { Length: > 0 } answer)
                contents.Add(new TextContent(answer));

            var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
            {
                ModelId = ModelId,
                RawRepresentation = response,
            };
            // Reasoning shares the output budget with the answer. When the model produced a reasoning trace but
            // no answer text, generation was truncated by MaxOutputTokens before the answer began — surface that
            // as a Length finish reason so callers can detect it (and raise the budget) instead of a silent empty.
            if (string.IsNullOrEmpty(response.Text) && response.Thinking is { Length: > 0 })
                chatResponse.FinishReason = ChatFinishReason.Length;
            return chatResponse;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messages);
        (IReadOnlyList<LiteRtMessage> history, string userText) = LiteRtChatMapping.Split(messages);
        LiteRtConversationOptions? convOptions = LiteRtChatMapping.ToConversationOptions(history, options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LiteRtConversation? conv = null;
        try
        {
            conv = _engine.CreateConversation(convOptions);
            await foreach (LiteRtStreamChunk chunk in conv.SendMessageStreamingAsync(userText, cancellationToken).ConfigureAwait(false))
            {
                // Answer deltas become text; reasoning ("thinking") deltas become TextReasoningContent — kept
                // out of the assistant message's .Text but surfaced for consumers that show reasoning, so a
                // thinking model never streams "nothing". Tool-call chunks are handled in a later phase.
                AIContent? content = chunk.Kind switch
                {
                    LiteRtStreamChunkKind.Answer when chunk.Text.Length > 0 => new TextContent(chunk.Text),
                    LiteRtStreamChunkKind.Thinking when chunk.Text.Length > 0 => new TextReasoningContent(chunk.Text),
                    _ => null,
                };
                if (content is not null)
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [content],
                        ModelId = ModelId,
                        RawRepresentation = chunk,
                    };
            }
        }
        finally
        {
            conv?.Dispose();
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return
            serviceKey is not null ? null :
            serviceType == typeof(ChatClientMetadata) ? _metadata :
            serviceType.IsInstanceOfType(this) ? this :
            null;
    }

    /// <summary>Releases the gate. The engine is not disposed here — it is owned by whoever created it
    /// (you, for the engine constructor; the container, when registered from <see cref="LiteRtEngineOptions"/>).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
