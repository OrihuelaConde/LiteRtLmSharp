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
/// <para>
/// <b>Tool calling.</b> Function tools in <see cref="ChatOptions.Tools"/> are passed to the model, and the
/// model's tool calls are surfaced as <see cref="FunctionCallContent"/> (with <see cref="ChatFinishReason.ToolCalls"/>).
/// Compose <c>UseFunctionInvocation()</c> (or, in Semantic Kernel, set a <c>FunctionChoiceBehavior</c>) to
/// auto-invoke them; the executed results come back as a tool message which the client returns to the model.
/// </para>
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
        (IReadOnlyList<LiteRtMessage> history, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split(messages);
        LiteRtConversationOptions? convOptions = LiteRtChatMapping.ToConversationOptions(history, options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using LiteRtConversation conv = _engine.CreateConversation(convOptions);
            // Send / SendToolResults are blocking native calls; offload so the async contract holds and the gate
            // wait + pre-call cancellation are honored (they do not interrupt generation mid-flight — use the
            // streaming overload for cooperative mid-generation cancellation). A tool-results trigger is the
            // function-calling continuation: the assistant tool-call turn was restored as history above.
            LiteRtResponse response = await Task.Run(
                () => trigger.IsToolResults ? conv.SendToolResults(trigger.ToolResults!) : conv.Send(trigger.UserText!),
                cancellationToken).ConfigureAwait(false);

            // Reasoning ("thinking") becomes TextReasoningContent (excluded from ChatResponse.Text but kept on
            // Contents). The reply is then either tool calls (FunctionCallContent) or the answer text.
            var contents = new List<AIContent>(2);
            if (response.Thinking is { Length: > 0 } reasoning)
                contents.Add(new TextReasoningContent(reasoning));

            ChatFinishReason? finishReason = null;
            if (response.IsToolCall)
            {
                for (int i = 0; i < response.ToolCalls.Count; i++)
                    contents.Add(LiteRtChatMapping.ToFunctionCall(response.ToolCalls[i], i));
                finishReason = ChatFinishReason.ToolCalls;
            }
            else if (response.Text is { Length: > 0 } answer)
            {
                contents.Add(new TextContent(answer));
            }

            var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
            {
                ModelId = ModelId,
                RawRepresentation = response,
                FinishReason = finishReason,
            };
            // Reasoning shares the output budget with the answer. When the model produced a reasoning trace but
            // no answer (and no tool call), generation was truncated by MaxOutputTokens before the answer began —
            // surface a Length finish reason so callers can detect it (and raise the budget) instead of a silent empty.
            if (finishReason is null && string.IsNullOrEmpty(response.Text) && response.Thinking is { Length: > 0 })
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
        (IReadOnlyList<LiteRtMessage> history, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split(messages);
        LiteRtConversationOptions? convOptions = LiteRtChatMapping.ToConversationOptions(history, options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LiteRtConversation? conv = null;
        try
        {
            conv = _engine.CreateConversation(convOptions);

            if (trigger.IsToolResults)
            {
                // The native API has no streaming tool-results call, so the function-calling continuation is a
                // single blocking send surfaced as updates (the answer after a tool round is typically short).
                LiteRtResponse continuation = await Task.Run(
                    () => conv.SendToolResults(trigger.ToolResults!), cancellationToken).ConfigureAwait(false);
                foreach (ChatResponseUpdate update in ToUpdates(continuation))
                    yield return update;
                yield break;
            }

            int toolCallIndex = 0;   // monotonic across the whole stream so synthesized call ids stay unique
            await foreach (LiteRtStreamChunk chunk in conv.SendMessageStreamingAsync(trigger.UserText!, cancellationToken).ConfigureAwait(false))
            {
                // Answer deltas become text; reasoning ("thinking") deltas become TextReasoningContent (kept out
                // of .Text but surfaced); a tool-call chunk becomes FunctionCallContent(s) so a function-invoking
                // pipeline can drive it. Same-kind text deltas are concatenated downstream.
                if (chunk.Kind == LiteRtStreamChunkKind.ToolCall)
                {
                    var calls = new List<AIContent>(chunk.ToolCalls.Count);
                    foreach (LiteRtToolCall call in chunk.ToolCalls)
                        calls.Add(LiteRtChatMapping.ToFunctionCall(call, toolCallIndex++));
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = calls,
                        ModelId = ModelId,
                        RawRepresentation = chunk,
                        FinishReason = ChatFinishReason.ToolCalls,
                    };
                    continue;
                }

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

    /// <summary>Surfaces a blocking response (used for the function-calling continuation, which has no native
    /// streaming call) as the streaming updates a caller expects: reasoning, then tool calls or the answer.</summary>
    private IEnumerable<ChatResponseUpdate> ToUpdates(LiteRtResponse response)
    {
        if (response.Thinking is { Length: > 0 } reasoning)
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextReasoningContent(reasoning)],
                ModelId = ModelId,
                RawRepresentation = response,
            };

        if (response.IsToolCall)
        {
            var calls = new List<AIContent>(response.ToolCalls.Count);
            for (int i = 0; i < response.ToolCalls.Count; i++)
                calls.Add(LiteRtChatMapping.ToFunctionCall(response.ToolCalls[i], i));
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = calls,
                ModelId = ModelId,
                RawRepresentation = response,
                FinishReason = ChatFinishReason.ToolCalls,
            };
        }
        else if (response.Text is { Length: > 0 } answer)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent(answer)],
                ModelId = ModelId,
                RawRepresentation = response,
            };
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
