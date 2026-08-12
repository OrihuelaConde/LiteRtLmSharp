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
/// <b>Stateless by default.</b> MEAI hands the full message list on every call, so the client re-prefills the
/// prior turns each time rather than holding a long-lived conversation — this keeps the caller's history and
/// the model's KV cache from diverging. The cost is an O(history) prefill per turn; for very long chats
/// prefer the native <see cref="LiteRtConversation"/> API directly. Opt into <b>stateful conversations</b>
/// (see <see cref="LiteRtStatefulConversationOptions"/>) to instead keep the live native conversation alive
/// between calls and send only the new messages each turn.
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
/// multimodal model. In the stateless (default) mode, media on earlier (history) turns is not replayed — only
/// the triggering turn's media is sent.
/// </para>
/// <para>
/// <b>Token usage.</b> Each response carries <see cref="ChatResponse.Usage"/> with
/// <see cref="UsageDetails.TotalTokenCount"/> always set (the conversation's KV-cache size, read at no cost).
/// In the stateless mode this is the turn's prompt + reply; in the stateful mode it is the conversation's
/// cumulative total, so it grows across the thread. The input/output split —
/// <see cref="UsageDetails.InputTokenCount"/> / <see cref="UsageDetails.OutputTokenCount"/> — is populated
/// <b>only</b> when the engine was loaded with <see cref="LiteRtEngineOptions.EnableBenchmark"/> = <c>true</c>;
/// otherwise those stay <c>null</c> and a note to that effect is left in
/// <see cref="ChatResponse.AdditionalProperties"/>.
/// </para>
/// <para>
/// <b>Stateful conversations (opt-in).</b> Pass a <see cref="LiteRtStatefulConversationOptions"/> to keep the
/// live native conversation alive between calls: the first (no-<see cref="ChatOptions.ConversationId"/>) call
/// returns a <see cref="ChatResponse.ConversationId"/>; set <see cref="ChatOptions.ConversationId"/> on the
/// next request and send <b>only the new messages</b> (typically just the new user turn) to resume it with no
/// history re-prefill. This is the canonical MEAI stateful contract, and it makes
/// <c>FunctionInvokingChatClient</c> tool loops incremental automatically. The conversation's session
/// settings (sampler, thinking, tools, constrained decoding, system message, template values) are fixed when
/// it is created; per-request knobs are <b>ignored</b> on a continuation except those that map to native
/// per-send settings (<see cref="ChatOptions.MaxOutputTokens"/>, the penalties, a JSON-schema
/// <see cref="ChatOptions.ResponseFormat"/> — the last only on a conversation whose first call carried one),
/// and a system message on a continuation throws.
/// See <see cref="LiteRtStatefulConversationOptions"/> for the full contract.
/// </para>
/// </remarks>
public sealed class LiteRtChatClient : IChatClient
{
    private readonly LiteRtEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ChatClientMetadata _metadata;
    private readonly LiteRtConversationOptions? _optionsTemplate;
    // Non-null only in the opt-in stateful mode; when null the client is stateless (the default): a fresh
    // conversation is built and disposed per call. All store access happens inside _gate.
    private readonly LiteRtConversationStore? _store;
    // The forking hatch, handed out via GetService — non-null only in the stateful mode (there are no live
    // conversations to fork otherwise). Allocated once in the constructor so GetService needs no locking.
    private readonly LiteRtConversationBranching? _branching;
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
    /// <param name="statefulConversations">
    /// Opt into <b>stateful conversations</b> (see <see cref="LiteRtStatefulConversationOptions"/>). When
    /// <c>null</c> (the default) the client is stateless — a fresh conversation is created and disposed per
    /// call, exactly as before. When supplied, the client keeps live native conversations alive between calls
    /// and implements the MEAI <see cref="ChatResponse.ConversationId"/> / <see cref="ChatOptions.ConversationId"/>
    /// contract. In the stateful mode a template's conversation-level settings are applied when a conversation
    /// is first created (they cannot change on a continuation).
    /// <para>
    /// <b>After enabling, propagate the id</b>: copy <c>response.ConversationId</c> into
    /// <c>options.ConversationId</c> and send <b>only the new messages</b> each turn — that is what makes
    /// turns incremental. If the id is never propagated (or the full history keeps being resent) the answers
    /// stay correct, but every call pays the full stateless re-prefill <i>plus</i> live conversations pile up
    /// in the LRU with no benefit. Higher-level consumers (<c>FunctionInvokingChatClient</c>, agent-framework
    /// threads) propagate the id automatically.
    /// </para>
    /// <example>
    /// <code>
    /// ChatOptions options = new();
    /// ChatResponse response = await client.GetResponseAsync([new(ChatRole.User, "hi")], options);
    /// options.ConversationId = response.ConversationId;   // adopt the live conversation
    /// response = await client.GetResponseAsync([new(ChatRole.User, "and?")], options);   // only the NEW message
    /// </code>
    /// </example>
    /// </param>
    /// <exception cref="ArgumentException">The template sets <c>History</c> or <c>HistoryJson</c>.</exception>
    public LiteRtChatClient(
        LiteRtEngine engine, string? modelId = null, LiteRtConversationOptions? optionsTemplate = null,
        LiteRtStatefulConversationOptions? statefulConversations = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        LiteRtChatMapping.ValidateTemplate(optionsTemplate);
        _engine = engine;
        _metadata = new ChatClientMetadata("litert-lm", null, modelId);
        _optionsTemplate = optionsTemplate;
        _store = statefulConversations is null
            ? null
            : new LiteRtConversationStore(statefulConversations.MaxLiveConversations);
        // The forking service exists only when there are live conversations to fork (stateful mode).
        _branching = _store is null ? null : new LiteRtConversationBranching(this);
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
        return _store is null
            ? await GetResponseStatelessAsync(messages, options, cancellationToken).ConfigureAwait(false)
            : await GetResponseStatefulAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    // ─────────────────────── Blocking: stateless (default) ───────────────────────

    /// <summary>The stateless path: build a fresh conversation from the full message list, send, and dispose it.</summary>
    private async Task<ChatResponse> GetResponseStatelessAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
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
            LiteRtResponse response = await SendCoreAsync(conv, trigger, sendOptions, cancellationToken).ConfigureAwait(false);
            return BuildResponse(response, conv, conversationId: null, onToolCall: null);
        }
        finally
        {
            ReleaseGate();
        }
    }

    // ─────────────────────── Blocking: stateful (opt-in) ───────────────────────

    /// <summary>The stateful path: create-and-keep a live conversation (no <c>ConversationId</c>) or resume a
    /// stored one (with a <c>ConversationId</c>), sending only the new messages either way.</summary>
    private async Task<ChatResponse> GetResponseStatefulAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        string? conversationId = options?.ConversationId;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (LiteRtConversation conv, LiteRtConversationStore.Entry entry, LiteRtChatMapping.SendTrigger trigger, string id) =
                AcquireConversation(messages, options, conversationId);

            LiteRtSendOptions? sendOptions = LiteRtChatMapping.ToSendOptions(options);
            // Pre-native argument check, OUTSIDE the eviction try: the conversation is untouched and
            // stays resumable (the core would reject this too, but with a lower-level message — and
            // via the catch below, which would wrongly destroy a healthy conversation).
            ThrowIfConstraintWithoutProvider(sendOptions, entry);
            LiteRtResponse response;
            try
            {
                response = await SendCoreAsync(conv, trigger, sendOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (LiteRtContextOverflowException) when (!conv.IsContextFull)
            {
                // The overflow guard rejected the message BEFORE any native work ("message doesn't fit"):
                // the live conversation is untouched and stays resumable — the caller can retry this id
                // with a shorter message, exactly as the exception's own guidance suggests.
                throw;
            }
            catch
            {
                // Any other failure (cancellation, native error, or the guard's context-FULL rejection)
                // leaves the conversation consumed or terminal, so evict and dispose it — a later resume
                // of this id then throws cleanly.
                _store!.Remove(id);
                throw;
            }
            return BuildResponse(response, conv, id, entry.RecordToolCall);
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>
    /// Resolves the live conversation for a stateful request. With no <paramref name="conversationId"/> a new
    /// conversation is created (session settings fixed here) and stored under a fresh id; with one, the stored
    /// conversation is resumed and only the new (not-yet-seen) messages are mapped to the send. Runs inside the
    /// gate. Returns the conversation, its store entry, the send trigger, and the id to surface on the response.
    /// </summary>
    private (LiteRtConversation Conversation, LiteRtConversationStore.Entry Entry, LiteRtChatMapping.SendTrigger Trigger, string Id)
        AcquireConversation(IEnumerable<ChatMessage> messages, ChatOptions? options, string? conversationId)
    {
        if (conversationId is null)
        {
            // New conversation: build it from the message list exactly as the stateless path would, but keep
            // it alive in the store instead of disposing it. Session settings are locked in here.
            (IReadOnlyList<LiteRtMessage> history, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split(messages);
            history = LiteRtChatMapping.WithRequiredToolInstruction(history, options);
            LiteRtConversationOptions? convOptions = LiteRtChatMapping.ToConversationOptions(history, options, _optionsTemplate);
            LiteRtConversation conv = _engine.CreateConversation(convOptions);
            string id = NewConversationId();
            LiteRtConversationStore.Entry entry = _store!.Add(id, conv);   // may evict + dispose the LRU conversation
            entry.HasConstraintProvider = convOptions?.ConstraintProvider is not null;
            return (conv, entry, trigger, id);
        }

        // Continuation: resume the stored conversation; the incoming messages are only the new ones (the native
        // conversation already holds the prior turns), so map them without any history.
        LiteRtConversationStore.Entry found = _store!.Get(conversationId) ?? throw UnknownConversation(conversationId);
        LiteRtChatMapping.SendTrigger contTrigger = LiteRtChatMapping.SplitContinuation(messages, found.CallIdToName);
        return (found.Conversation, found, contTrigger, conversationId);
    }

    /// <summary>
    /// Forks a live stateful conversation: clones the conversation named by <paramref name="conversationId"/>
    /// into an independent copy of its prefilled state, stores the clone in the same LRU as a new entry, copies
    /// the parent's tool-call name map onto it (so tool continuations keep resolving on the branch), and returns
    /// the new id. Runs under the gate — it cannot race an in-flight send or stream. Backs
    /// <see cref="LiteRtConversationBranching.ForkAsync"/>.
    /// </summary>
    internal async Task<string> ForkConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(conversationId);
        // _store is non-null: the branching service is only handed out (via GetService) in the stateful mode.

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Look up (and promote to most-recently-used) the parent, then clone its prefilled state into an
            // independent native conversation. Clone() throws LiteRtException if the backend can't clone.
            LiteRtConversationStore.Entry parent = _store!.Get(conversationId) ?? throw UnknownConversation(conversationId);
            LiteRtConversation clone = parent.Conversation.Clone();

            string forkId = NewConversationId();
            // The fork counts toward the store's capacity and may evict the LRU. After the Get above the parent
            // is at the front, so at a capacity ≥ 2 it is never the immediate victim; at MaxLiveConversations = 1,
            // adding the clone evicts (and disposes) the parent's conversation — the clone is independent
            // native state, so the branch is unaffected, and the parent's *Entry* object (below) survives
            // eviction (only its Conversation is disposed), so copying its name map afterward stays valid.
            LiteRtConversationStore.Entry forkEntry = _store.Add(forkId, clone);
            forkEntry.CopyToolCallsFrom(parent);
            forkEntry.HasConstraintProvider = parent.HasConstraintProvider;   // the native clone keeps the provider
            return forkId;
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>Runs the send that triggers generation — a user message, or the results of executed tools
    /// (the function-calling continuation) — with mid-generation cancellation.</summary>
    private static Task<LiteRtResponse> SendCoreAsync(
        LiteRtConversation conv, LiteRtChatMapping.SendTrigger trigger, LiteRtSendOptions? sendOptions,
        CancellationToken cancellationToken)
        => trigger.IsToolResults
            ? conv.SendToolResultsAsync(trigger.ToolResults!, sendOptions, cancellationToken)
            : conv.SendAsync(trigger.UserText!, trigger.Attachments, sendOptions, cancellationToken);

    /// <summary>
    /// Builds the <see cref="ChatResponse"/> from a completed blocking send: reasoning as
    /// <see cref="TextReasoningContent"/>, then either tool calls (<see cref="FunctionCallContent"/>) or the
    /// answer text, with the finish reason, usage (read off the live conversation), and — in the stateful mode
    /// — the <paramref name="conversationId"/>. When <paramref name="onToolCall"/> is non-null (stateful),
    /// each emitted tool call's synthesized id → name is recorded so a later continuation can resolve it.
    /// </summary>
    private ChatResponse BuildResponse(
        LiteRtResponse response, LiteRtConversation conv, string? conversationId, Action<string, string>? onToolCall)
    {
        // Reasoning ("thinking") becomes TextReasoningContent (excluded from ChatResponse.Text but kept on
        // Contents). The reply is then either tool calls (FunctionCallContent) or the answer text.
        var contents = new List<AIContent>(2);
        if (response.Thinking is { Length: > 0 } reasoning)
            contents.Add(new TextReasoningContent(reasoning));

        ChatFinishReason? finishReason = null;
        if (response.IsToolCall)
        {
            for (int i = 0; i < response.ToolCalls.Count; i++)
            {
                FunctionCallContent call = LiteRtChatMapping.ToFunctionCall(response.ToolCalls[i], i);
                contents.Add(call);
                onToolCall?.Invoke(call.CallId!, call.Name);
            }
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
            ConversationId = conversationId,
        };
        // Reasoning shares the output budget with the answer. When the model produced a reasoning trace but
        // no answer (and no tool call), generation was truncated by MaxOutputTokens before the answer began —
        // surface a Length finish reason so callers can detect it (and raise the budget) instead of a silent empty.
        if (finishReason is null && string.IsNullOrEmpty(response.Text) && response.Thinking is { Length: > 0 })
            chatResponse.FinishReason = ChatFinishReason.Length;
        if (ContextFullFinishReason(conv) is { } contextFull)
            chatResponse.FinishReason = contextFull;
        chatResponse.Usage = ReadUsage(conv);
        if (chatResponse.Usage.OutputTokenCount is null)
            (chatResponse.AdditionalProperties ??= new())[LiteRtChatMapping.UsageBenchmarkNoteKey] = LiteRtChatMapping.UsageBenchmarkNote;
        return chatResponse;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messages);
        // Forward to the mode-specific iterator. Keeping this an async iterator (rather than returning the
        // inner one) preserves the lazy guard checks and lets an outer WithCancellation flow to the token.
        IAsyncEnumerable<ChatResponseUpdate> inner = _store is null
            ? StreamStatelessAsync(messages, options, cancellationToken)
            : StreamStatefulAsync(messages, options, cancellationToken);
        await foreach (ChatResponseUpdate update in inner.ConfigureAwait(false))
            yield return update;
    }

    // ─────────────────────── Streaming: stateless (default) ───────────────────────

    private async IAsyncEnumerable<ChatResponseUpdate> StreamStatelessAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        (IReadOnlyList<LiteRtMessage> history, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split(messages);
        history = LiteRtChatMapping.WithRequiredToolInstruction(history, options);
        LiteRtConversationOptions? convOptions = LiteRtChatMapping.ToConversationOptions(history, options, _optionsTemplate);
        LiteRtSendOptions? sendOptions = LiteRtChatMapping.ToSendOptions(options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LiteRtConversation? conv = null;
        try
        {
            conv = _engine.CreateConversation(convOptions);
            await foreach (ChatResponseUpdate update in
                StreamReplyAsync(conv, trigger, sendOptions, onToolCall: null, cancellationToken).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            conv?.Dispose();
            ReleaseGate();
        }
    }

    // ─────────────────────── Streaming: stateful (opt-in) ───────────────────────

    private async IAsyncEnumerable<ChatResponseUpdate> StreamStatefulAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? conversationId = options?.ConversationId;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool completed = false;
        string? storedId = null;
        try
        {
            (LiteRtConversation conv, LiteRtConversationStore.Entry entry, LiteRtChatMapping.SendTrigger trigger, string id) =
                AcquireConversation(messages, options, conversationId);
            // Set only after the conversation is live in the store: if acquisition threw for a continuation
            // (unknown id, or a system message), the stored conversation was never sent and must be preserved.
            storedId = id;

            LiteRtSendOptions? sendOptions = LiteRtChatMapping.ToSendOptions(options);
            // Pre-native argument check: nothing was sent, so the conversation must stay resumable —
            // mark the stream completed before throwing so the finally below does not evict it.
            if (ConstraintWithoutProvider(sendOptions, entry))
            {
                completed = true;
                ThrowIfConstraintWithoutProvider(sendOptions, entry);
            }
            // Enumerated by hand (not await-foreach) because C# forbids `yield return` inside a try that
            // has a catch, and the MoveNext needs one: the overflow guard's pre-native "message doesn't
            // fit" rejection must NOT evict the untouched, still-resumable conversation.
            IAsyncEnumerator<ChatResponseUpdate> updates = StreamReplyAsync(
                conv, trigger, sendOptions, entry.RecordToolCall, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    ChatResponseUpdate update;
                    try
                    {
                        if (!await updates.MoveNextAsync().ConfigureAwait(false))
                            break;
                        update = updates.Current;
                    }
                    catch (LiteRtContextOverflowException) when (!conv.IsContextFull)
                    {
                        // Pre-native rejection: nothing was streamed and the conversation is untouched —
                        // keep it resumable (the caller can retry this id with a shorter message).
                        completed = true;
                        throw;
                    }
                    update.ConversationId = id;
                    yield return update;
                }
                completed = true;
            }
            finally
            {
                await updates.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // A stream that did not complete cleanly (cancelled, the consumer broke out early, or a
            // mid-stream failure) leaves the native conversation consumed — sending on it again would
            // hang — so evict and dispose it. A clean completion, or the guard's pre-native rejection
            // above, keeps it alive for the next turn.
            if (!completed && storedId is not null)
                _store!.Remove(storedId);
            ReleaseGate();
        }
    }

    // ─────────────────────── Streaming core (shared) ───────────────────────

    /// <summary>
    /// Streams a reply over an already-acquired <paramref name="conv"/>: the function-calling continuation
    /// (tool-results trigger) as updates, or a streamed user send. Does not acquire the gate, create, or
    /// dispose the conversation, nor stamp the conversation id (the callers own those). When
    /// <paramref name="onToolCall"/> is non-null (stateful), each emitted tool call's synthesized id → name is
    /// recorded so a later continuation can resolve it.
    /// </summary>
    private async IAsyncEnumerable<ChatResponseUpdate> StreamReplyAsync(
        LiteRtConversation conv, LiteRtChatMapping.SendTrigger trigger, LiteRtSendOptions? sendOptions,
        Action<string, string>? onToolCall, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (trigger.IsToolResults)
        {
            // The native API has no streaming tool-results call, so the function-calling continuation is a
            // single awaitable send surfaced as updates (the answer after a tool round is typically short);
            // the token cancels it mid-generation like the streaming path.
            LiteRtResponse continuation = await conv.SendToolResultsAsync(
                trigger.ToolResults!, sendOptions, cancellationToken).ConfigureAwait(false);
            foreach (ChatResponseUpdate update in ToUpdates(continuation, onToolCall))
                yield return update;
            yield return UsageUpdate(conv);
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
                foreach (LiteRtToolCall toolCall in chunk.ToolCalls)
                {
                    FunctionCallContent call = LiteRtChatMapping.ToFunctionCall(toolCall, toolCallIndex++);
                    calls.Add(call);
                    onToolCall?.Invoke(call.CallId!, call.Name);
                }
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
        yield return UsageUpdate(conv);
    }

    /// <summary>A final streaming update carrying the turn's token <see cref="UsageContent"/> (and, when the
    /// input/output split is absent, the benchmark note in its <c>AdditionalProperties</c>), plus the
    /// <see cref="ContextFullFinishReason"/> signal when the context filled up during the stream — on
    /// coalescing it wins over an earlier <c>ToolCalls</c> update, per that policy.</summary>
    private ChatResponseUpdate UsageUpdate(LiteRtConversation conv)
    {
        UsageDetails usage = ReadUsage(conv);
        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = ModelId, Contents = [new UsageContent(usage)] };
        if (usage.OutputTokenCount is null)
            (update.AdditionalProperties ??= new())[LiteRtChatMapping.UsageBenchmarkNoteKey] = LiteRtChatMapping.UsageBenchmarkNote;
        if (ContextFullFinishReason(conv) is { } contextFull)
            update.FinishReason = contextFull;
        return update;
    }

    /// <summary>
    /// THE context-full finish-reason policy, written once for both the blocking response and the final
    /// streaming update: when the conversation's context filled up during the turn (the reply was likely
    /// clamped by the KV overflow guard and the next send on it is guaranteed to throw), the turn carries
    /// <see cref="ChatFinishReason.Length"/> — deliberately OVERRIDING <see cref="ChatFinishReason.ToolCalls"/>,
    /// because even a complete tool call cannot be continued on a full conversation (sending its results
    /// would throw); Length is the caller's same-turn cue to wind the thread down instead of discovering
    /// the wall on the next call. Returns <c>null</c> (leave the content-derived reason) otherwise.
    /// </summary>
    private static ChatFinishReason? ContextFullFinishReason(LiteRtConversation conv)
        => conv.IsContextFull ? ChatFinishReason.Length : null;

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
    /// streaming call) as the streaming updates a caller expects: reasoning, then tool calls or the answer.
    /// When <paramref name="onToolCall"/> is non-null (stateful), records each emitted tool call's id → name.</summary>
    private IEnumerable<ChatResponseUpdate> ToUpdates(LiteRtResponse response, Action<string, string>? onToolCall)
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
            {
                FunctionCallContent call = LiteRtChatMapping.ToFunctionCall(response.ToolCalls[i], i);
                calls.Add(call);
                onToolCall?.Invoke(call.CallId!, call.Name);
            }
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

    /// <summary>
    /// Resolves a provider-specific service — the standard <see cref="ChatClientMetadata"/> and the client
    /// itself. Keyed lookups (non-null <paramref name="serviceKey"/>) always return <c>null</c>.
    /// </summary>
    /// <remarks>
    /// In the stateful mode this also resolves the conversation-forking hatch: request
    /// <see cref="LiteRtConversationBranching"/> to branch a live conversation into an independent copy of its
    /// prefilled state (see that type's docs). In the stateless mode the lookup returns <c>null</c> — there are
    /// no live conversations to fork.
    /// </remarks>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return
            serviceKey is not null ? null :
            serviceType == typeof(ChatClientMetadata) ? _metadata :
            // _branching is non-null only in the stateful mode (null when stateless).
            serviceType == typeof(LiteRtConversationBranching) ? _branching :
            serviceType.IsInstanceOfType(this) ? this :
            null;
    }

    /// <summary>Disposes the client. In the stateful mode this also disposes every live native conversation
    /// still in the store. The engine is not disposed here — it is owned by whoever created it (you, for the
    /// engine constructor; the container, when registered from <see cref="LiteRtEngineOptions"/>).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store?.DisposeAll();
        _gate.Dispose();
    }

    /// <summary>Releases the gate, tolerating a disposal that raced an in-flight call (the contract is to
    /// serialize calls and not dispose while one is running, but a finally must never throw over the real result).</summary>
    private void ReleaseGate()
    {
        try { _gate.Release(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>A fresh, opaque conversation id (the fixed id surfaced on every response for one stateful thread).</summary>
    private static string NewConversationId() => Guid.NewGuid().ToString("N");

    /// <summary>The exception thrown when a request names a <c>ConversationId</c> the store does not hold —
    /// it was never issued by this client, or its conversation is gone: evicted by the LRU cap
    /// (<see cref="LiteRtStatefulConversationOptions.MaxLiveConversations"/>), or evicted after a
    /// cancelled/failed/context-full send left it unusable.</summary>
    private static ArgumentException UnknownConversation(string conversationId)
        => new(
            $"No live conversation with id '{conversationId}'. Either it was never started by this client, or " +
            "its conversation is no longer live: it was evicted by the LRU cap " +
            "(LiteRtStatefulConversationOptions.MaxLiveConversations, default 8) when newer conversations or " +
            "forks were started, or it was evicted after a cancelled, failed or context-full send left it " +
            "unusable. Start a new conversation by omitting ChatOptions.ConversationId; the response's " +
            "ConversationId is the id to reuse for the next turn.",
            nameof(conversationId));

    /// <summary>Whether <paramref name="sendOptions"/> carries a per-send constraint (a JSON-schema
    /// <c>ResponseFormat</c>) that the stored conversation cannot enforce (created without the LlGuidance
    /// provider).</summary>
    private static bool ConstraintWithoutProvider(LiteRtSendOptions? sendOptions, LiteRtConversationStore.Entry entry)
        => sendOptions?.Constraint is not null && !entry.HasConstraintProvider;

    /// <summary>Rejects a schema-constrained request on a conversation created without the constraint
    /// provider — in MEAI vocabulary, BEFORE any native work, so the live conversation stays resumable
    /// (the caller can retry the id without the ResponseFormat, or start a new conversation with it).</summary>
    private static void ThrowIfConstraintWithoutProvider(LiteRtSendOptions? sendOptions, LiteRtConversationStore.Entry entry)
    {
        if (ConstraintWithoutProvider(sendOptions, entry))
            throw new ArgumentException(
                "This request's JSON-schema ChatOptions.ResponseFormat cannot be enforced on this conversation: " +
                "the conversation was created without a schema on its FIRST call, and the constraint provider " +
                "is fixed at creation. Retry this ConversationId without the ResponseFormat, start a new " +
                "conversation (omit ConversationId) with the ResponseFormat on its first call, or set " +
                "LiteRtConversationOptions.ConstraintProvider on the client's options template so every " +
                "conversation supports it. The live conversation was not modified and remains resumable.",
                "options");
    }
}
