using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// A <see cref="IChatClient"/> (Microsoft.Extensions.AI) backed by a LiteRtLmSharp on-device model. This is
/// the framework-agnostic integration: the same instance works with Microsoft Agent Framework
/// (<c>new ChatClientAgent(client, …)</c>), Semantic Kernel (via the <c>LiteRtLmSharp.SemanticKernel</c>
/// connector's <c>AddLiteRtChatCompletion</c> / <c>AsChatCompletionService</c>), and any other
/// <see cref="IChatClient"/> consumer or middleware pipeline.
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
/// <para>
/// <b>Multimodal.</b> Image/audio content on the final user message — a <see cref="DataContent"/> (inline
/// bytes) or a file-path <see cref="UriContent"/> with an <c>image/*</c> or <c>audio/*</c> media type — is sent
/// to the model as an attachment. The engine must have been loaded with the matching modality enabled
/// (<see cref="LiteRtEngineOptions.VisionBackend"/> / <see cref="LiteRtEngineOptions.AudioBackend"/>) on a
/// multimodal model. Media on earlier (history) turns is not replayed — only the triggering turn's media is sent.
/// </para>
/// <para>
/// <b>Token usage.</b> Each response carries <see cref="ChatResponse.Usage"/> with
/// <see cref="UsageDetails.TotalTokenCount"/> always set (the turn's prompt + reply, read from the conversation
/// at no cost). The input/output split — <see cref="UsageDetails.InputTokenCount"/> /
/// <see cref="UsageDetails.OutputTokenCount"/> — is populated <b>only</b> when the engine was loaded with
/// <see cref="LiteRtEngineOptions.EnableBenchmark"/> = <c>true</c>; otherwise those stay <c>null</c> and a note
/// to that effect is left in <see cref="ChatResponse.AdditionalProperties"/>.
/// </para>
/// </remarks>
public sealed class LiteRtChatClient : IChatClient
{
    private readonly LiteRtEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ChatClientMetadata _metadata;
    private readonly LiteRtConversationOptions? _optionsTemplate;
    private bool _disposed;

    /// <summary>Creates a chat client over an already-loaded <paramref name="engine"/>. The client does not
    /// own the engine: dispose the engine yourself (after the client). To have a container manage the
    /// engine's lifetime, register from <see cref="LiteRtEngineOptions"/> via <c>AddLiteRtChatClient</c>.</summary>
    /// <param name="engine">The loaded LiteRtLmSharp engine. Must outlive this client.</param>
    /// <param name="modelId">Identifier surfaced as the metadata default model id. Optional.</param>
    /// <param name="optionsTemplate">
    /// Optional per-client conversation-options template. Its conversation-level settings — those MEAI's
    /// <see cref="ChatOptions"/> does not surface (e.g. <see cref="LiteRtConversationOptions.SystemMessage"/>,
    /// <see cref="LiteRtConversationOptions.LoraPath"/> / <see cref="LiteRtConversationOptions.AudioLoraPath"/>,
    /// <see cref="LiteRtConversationOptions.StreamToolCalls"/>, <see cref="LiteRtConversationOptions.VisualTokenBudget"/>,
    /// <see cref="LiteRtConversationOptions.FilterThinkingFromKvCache"/>, <see cref="LiteRtConversationOptions.ExtraContext"/>,
    /// and a session-default <see cref="LiteRtConversationOptions.MaxOutputTokens"/>) — apply to every call, while
    /// any value the per-call <see cref="ChatOptions"/> supplies (sampler, thinking, constrained decoding, tools,
    /// system message) wins. See <see cref="LiteRtChatMapping.ToConversationOptions"/> for the full merge rules.
    /// The template must not set <see cref="LiteRtConversationOptions.History"/> or
    /// <see cref="LiteRtConversationOptions.HistoryJson"/> (history is per-call) — doing so throws.
    /// </param>
    /// <exception cref="ArgumentException">The template sets <c>History</c> or <c>HistoryJson</c>.</exception>
    public LiteRtChatClient(LiteRtEngine engine, string? modelId = null, LiteRtConversationOptions? optionsTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        LiteRtChatMapping.ValidateTemplate(optionsTemplate);
        _engine = engine;
        _metadata = new ChatClientMetadata("litert-lm", null, modelId);
        _optionsTemplate = optionsTemplate;
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
        // For a Required tool mode, fold a best-effort "you must call a tool" instruction into the system prompt
        // (the native API has no forced tool choice). No-op for Auto/None.
        history = LiteRtChatMapping.WithRequiredToolInstruction(history, options);
        LiteRtConversationOptions? convOptions = LiteRtChatMapping.ToConversationOptions(history, options, _optionsTemplate);
        // ChatOptions.MaxOutputTokens is a MEAI per-request option, so it maps to the native per-send cap
        // rather than the conversation-level session config (see LiteRtChatMapping.ToConversationOptions).
        LiteRtSendOptions? sendOptions = LiteRtChatMapping.ToSendOptions(options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using LiteRtConversation conv = _engine.CreateConversation(convOptions);
            // The awaitable sends cancel the native inference mid-generation when the token fires
            // (OperationCanceledException). A tool-results trigger is the function-calling continuation:
            // the assistant tool-call turn was restored as history above.
            LiteRtResponse response = trigger.IsToolResults
                ? await conv.SendToolResultsAsync(trigger.ToolResults!, sendOptions, cancellationToken).ConfigureAwait(false)
                : await conv.SendAsync(trigger.UserText!, trigger.Attachments, sendOptions, cancellationToken).ConfigureAwait(false);

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
            chatResponse.Usage = ReadUsage(conv);
            if (chatResponse.Usage.OutputTokenCount is null)
                (chatResponse.AdditionalProperties ??= new())[LiteRtChatMapping.UsageBenchmarkNoteKey] = LiteRtChatMapping.UsageBenchmarkNote;
            return chatResponse;
        }
        finally
        {
            ReleaseGate();
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
        // For a Required tool mode, fold a best-effort "you must call a tool" instruction into the system prompt
        // (the native API has no forced tool choice). No-op for Auto/None.
        history = LiteRtChatMapping.WithRequiredToolInstruction(history, options);
        LiteRtConversationOptions? convOptions = LiteRtChatMapping.ToConversationOptions(history, options, _optionsTemplate);
        // ChatOptions.MaxOutputTokens is a MEAI per-request option, so it maps to the native per-send cap
        // rather than the conversation-level session config (see LiteRtChatMapping.ToConversationOptions).
        LiteRtSendOptions? sendOptions = LiteRtChatMapping.ToSendOptions(options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LiteRtConversation? conv = null;
        try
        {
            conv = _engine.CreateConversation(convOptions);

            if (trigger.IsToolResults)
            {
                // The native API has no streaming tool-results call, so the function-calling continuation is a
                // single awaitable send surfaced as updates (the answer after a tool round is typically short);
                // the token cancels it mid-generation like the streaming path.
                LiteRtResponse continuation = await conv.SendToolResultsAsync(
                    trigger.ToolResults!, sendOptions, cancellationToken).ConfigureAwait(false);
                foreach (ChatResponseUpdate update in ToUpdates(continuation))
                    yield return update;
                yield return UsageUpdate(conv!);
                yield break;
            }

            IAsyncEnumerable<LiteRtStreamChunk> stream = trigger.HasAttachments
                ? conv.SendStreamingAsync(trigger.UserText!, trigger.Attachments!, sendOptions, cancellationToken)
                : conv.SendStreamingAsync(trigger.UserText!, attachments: null, sendOptions, cancellationToken);

            int toolCallIndex = 0;   // monotonic across the whole stream so synthesized call ids stay unique
            bool sawAnswer = false, sawReasoning = false, sawToolCall = false;
            await foreach (LiteRtStreamChunk chunk in stream.ConfigureAwait(false))
            {
                // Answer deltas become text; reasoning ("thinking") deltas become TextReasoningContent (kept out
                // of .Text but surfaced); a tool-call chunk becomes FunctionCallContent(s) so a function-invoking
                // pipeline can drive it. Same-kind text deltas are concatenated downstream.
                if (chunk.Kind == LiteRtStreamChunkKind.ToolCall)
                {
                    sawToolCall = true;
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

                // A ToolCallDelta is a raw tool-call progress fragment. These only arrive when the template
                // enabled StreamToolCalls, so surfacing them is opt-in by construction (invisible otherwise):
                // emit a content-less update carrying the raw fragment under litertlm.tool_call_delta.
                if (chunk.Kind == LiteRtStreamChunkKind.ToolCallDelta)
                {
                    if (LiteRtChatMapping.ToToolCallDeltaUpdate(chunk, ModelId) is { } deltaUpdate)
                    {
                        deltaUpdate.RawRepresentation = chunk;
                        yield return deltaUpdate;
                    }
                    continue;
                }

                // Answer/Thinking deltas map to text/reasoning content; every other (future) kind is ignored so
                // it can't break the stream.
                AIContent? content = LiteRtChatMapping.ToStreamingTextContent(chunk);
                if (content is not null)
                {
                    if (content is TextReasoningContent) sawReasoning = true; else sawAnswer = true;
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [content],
                        ModelId = ModelId,
                        RawRepresentation = chunk,
                    };
                }
            }

            // Mirror GetResponseAsync: reasoning but no answer (and no tool call) means the reasoning consumed
            // the MaxOutputTokens budget before the answer began — emit a Length finish reason so an empty
            // streamed answer is diagnosable rather than silent.
            if (sawReasoning && !sawAnswer && !sawToolCall)
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    ModelId = ModelId,
                    FinishReason = ChatFinishReason.Length,
                };

            // Token usage (TotalTokenCount always; the input/output split only when EnableBenchmark is on).
            yield return UsageUpdate(conv!);
        }
        finally
        {
            conv?.Dispose();
            ReleaseGate();
        }
    }

    /// <summary>A final streaming update carrying the turn's token <see cref="UsageContent"/> (and, when the
    /// input/output split is absent, the benchmark note in its <c>AdditionalProperties</c>).</summary>
    private ChatResponseUpdate UsageUpdate(LiteRtConversation conv)
    {
        UsageDetails usage = ReadUsage(conv);
        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = ModelId, Contents = [new UsageContent(usage)] };
        if (usage.OutputTokenCount is null)
            (update.AdditionalProperties ??= new())[LiteRtChatMapping.UsageBenchmarkNoteKey] = LiteRtChatMapping.UsageBenchmarkNote;
        return update;
    }

    /// <summary>
    /// Reads the just-completed turn's token usage off the (still-live) conversation. <c>TotalTokenCount</c>
    /// is always set; <c>InputTokenCount</c>/<c>OutputTokenCount</c> are populated only when the engine was
    /// loaded with <see cref="LiteRtEngineOptions.EnableBenchmark"/> = <c>true</c> (see
    /// <see cref="LiteRtChatMapping.BuildUsage"/>).
    /// </summary>
    private static UsageDetails ReadUsage(LiteRtConversation conv)
    {
        LiteRtBenchmarkInfo? benchmark = null;
        try { benchmark = conv.GetBenchmarkInfo(); }
        catch (EntryPointNotFoundException) { /* native build predates the benchmark API → total-only */ }
        return LiteRtChatMapping.BuildUsage(conv.TokenCount, benchmark);
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
        else if (response.Thinking is { Length: > 0 })
        {
            // Reasoning consumed the budget before any answer — signal truncation (mirrors GetResponseAsync).
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                ModelId = ModelId,
                RawRepresentation = response,
                FinishReason = ChatFinishReason.Length,
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

    /// <summary>Releases the gate, tolerating a disposal that raced an in-flight call (the contract is to
    /// serialize calls and not dispose while one is running, but a finally must never throw over the real result).</summary>
    private void ReleaseGate()
    {
        try { _gate.Release(); }
        catch (ObjectDisposedException) { }
    }
}
