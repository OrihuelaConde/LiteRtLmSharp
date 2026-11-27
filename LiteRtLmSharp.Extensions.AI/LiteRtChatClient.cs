using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TurnCheckpoint = LiteRtLmSharp.Extensions.AI.LiteRtConversationStore.TurnCheckpoint;

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
/// <b>Serialized per engine.</b> Calls are serialized with a gate shared by every
/// <see cref="LiteRtChatClient"/> over the same engine, so one generation runs on an engine at a time and no
/// conversation is used from two threads at once (conversations are not thread-safe). Clients over different
/// engines (see <see cref="LiteRtEngineOptions.AllowMultipleEngines"/>) do not wait for each other. A stream holds
/// the gate until it is read to the end or disposed, so a call on any client over the same engine (a fork
/// included) made from inside the loop that reads the stream waits for that loop forever. Finish or dispose the
/// stream first, or use a client over another engine.
/// </para>
/// <para>
/// The gate is this client's own rule. With LiteRT-LM v0.20.0 on win-x64 (gemma-4 E2B), the runtime itself
/// queued concurrent sends on different conversations of one engine, and every reply was correct: on CPU, and on
/// GPU (an NVIDIA RTX 3080) with blocking sends. Concurrent streaming through this client was not measured.
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
/// multimodal model. In the stateless (default) mode, image and audio content on earlier user turns is carried
/// into the restored history and re-encoded through prefill on every call, so the model can refer back to it:
/// with gemma-4 E2B on LiteRT-LM v0.20.0 (win-x64, CPU and GPU), the model recalled an image from an earlier
/// turn. Audio on an earlier turn was not tested.
/// </para>
/// <para>
/// <b>Token usage.</b> Each response carries <see cref="ChatResponse.Usage"/> with
/// <see cref="UsageDetails.TotalTokenCount"/> always set (the conversation's KV-cache size, read at no cost).
/// In the stateless mode this is the turn's prompt + reply; in the stateful mode it is the conversation's
/// cumulative total, so it grows across the thread. The runtime removes a reply's thinking from the KV cache
/// after the reply, so after a reply with thinking the total leaves the thinking out and can be lower than
/// <see cref="UsageDetails.InputTokenCount"/> + <see cref="UsageDetails.OutputTokenCount"/>, because the output
/// count includes the thinking (LiteRT-LM v0.20.0, win-x64, CPU, gemma-4 E2B). The input/output split —
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
    // One gate per engine, shared by every client over it (see the class remarks). Weak keys, so a collected
    // engine takes its gate with it; a SemaphoreSlim whose wait handle is never used needs no disposal.
    private static readonly ConditionalWeakTable<LiteRtEngine, SemaphoreSlim> s_engineGates = new();
    private readonly SemaphoreSlim _gate;
    private readonly ChatClientMetadata _metadata;
    private readonly LiteRtConversationOptions? _optionsTemplate;
    // Non-null only in the opt-in stateful mode; when null the client is stateless (the default): a fresh
    // conversation is built and disposed per call. All store access happens inside _gate.
    private readonly LiteRtConversationStore? _store;
    // The forking hatch, handed out via GetService — non-null only in the stateful mode (there are no live
    // conversations to fork otherwise). Allocated once in the constructor so GetService needs no locking.
    private readonly LiteRtConversationBranching? _branching;
    private bool _disposed;
    // Set after the first Clone() the backend reports as UNIMPLEMENTED, so stateful sends stop attempting a
    // checkpoint (and pay no failing native call each time). Read and written under _gate.
    private bool _checkpointUnsupported;
    private readonly bool _checkpointConstrained;

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
    /// <see cref="LiteRtConversationOptions.EnableSpeculativeDecoding"/>,
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
    /// in the LRU with no benefit. Higher-level consumers (<c>FunctionInvokingChatClient</c>, Agent Framework
    /// sessions) propagate the id automatically.
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
        _gate = s_engineGates.GetValue(engine, static _ => new SemaphoreSlim(1, 1));
        _metadata = new ChatClientMetadata("litert-lm", null, modelId);
        _optionsTemplate = optionsTemplate;
        _checkpointConstrained = statefulConversations?.CheckpointConstrainedConversations == true;
        _store = statefulConversations is null
            ? null
            : new LiteRtConversationStore(statefulConversations.MaxLiveConversations);
        // The forking service exists only when there are live conversations to fork (stateful mode).
        _branching = _store is null ? null : new LiteRtConversationBranching(this);
    }

    private string? ModelId => _metadata.DefaultModelId;

    /// <summary>The gate this client serializes its calls with, shared by every client over the same engine.</summary>
    internal SemaphoreSlim Gate => _gate;

    /// <summary>The live conversations (stateful mode only), for tests to inspect a rollback.</summary>
    internal LiteRtConversationStore? Store => _store;

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
        // (the native tool_choice setting does not force a call: on LiteRT-LM v0.20.0, win-x64, the runtime
        // ignored it without constrained decoding and rejected every value but "auto" with it). No-op for
        // Auto/None.
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
            // The turn's rollback point, so a cancelled or failed send on a continuation leaves its id valid.
            TurnCheckpoint turn = BeginTurn(entry, conv, trigger, newConversation: conversationId is null, out bool startsTurn);
            LiteRtResponse response;
            try
            {
                response = await SendCoreAsync(conv, trigger, sendOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (LiteRtContextOverflowException) when (!conv.IsContextFull)
            {
                // The overflow guard rejected the message BEFORE any native work ("message doesn't fit"):
                // the live conversation is untouched and stays resumable — the caller can retry this id
                // with a shorter message, exactly as the exception's own guidance suggests. A turn in
                // progress stays as it was. A conversation this call created is removed instead: the caller
                // never received its id, so nothing could resume it.
                if (conversationId is null)
                    _store!.Remove(id);
                else if (startsTurn)
                    turn.Checkpoint?.Dispose();
                throw;
            }
            catch
            {
                // Any other failure (cancellation, native error, or the guard's context-FULL rejection)
                // leaves the conversation consumed or terminal: roll the turn back, or evict it.
                RecoverFailedTurn(id, entry, turn);
                throw;
            }
            EndSend(entry, turn, endsWithToolCalls: response.ToolCalls.Count > 0);
            return BuildResponse(response, conv, id, ToolCallRecorder(entry, turn));
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
            entry.CloneIsSlow = entry.HasConstraintProvider || convOptions?.EnableConstrainedDecoding == true;
            return (conv, entry, trigger, id);
        }

        // Continuation: resume the stored conversation; the incoming messages are only the new ones (the native
        // conversation already holds the prior turns), so map them without any history.
        LiteRtConversationStore.Entry found = _store!.Get(conversationId) ?? throw UnknownConversation(conversationId);
        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        ThrowIfResultsOfADiscardedTurn(list, found);
        LiteRtChatMapping.SendTrigger contTrigger = LiteRtChatMapping.SplitContinuation(list, found.CallIdToName);
        return (found.Conversation, found, contTrigger, conversationId);
    }

    /// <summary>Rejects tool results for a call whose turn was rolled back: the conversation holds neither the
    /// question nor the call, so the model would answer without that context and nothing would report it.</summary>
    internal static void ThrowIfResultsOfADiscardedTurn(IReadOnlyList<ChatMessage> messages, LiteRtConversationStore.Entry entry)
    {
        // Only the last message is sent; an earlier one is a resent history, which the mapping ignores.
        if (messages.Count == 0 || messages[^1].Role != ChatRole.Tool)
            return;
        foreach (FunctionResultContent result in messages[^1].Contents.OfType<FunctionResultContent>())
            if (entry.IsDiscardedToolCall(result.CallId))
                throw new ArgumentException(
                    $"The tool call '{result.CallId}' belongs to a turn that was cancelled or failed, and the client " +
                    "discarded that turn, its question included. Send the user message again.", nameof(messages));
    }

    /// <summary>What a stateful send calls for each tool call in its reply.</summary>
    private static Action<string, string> ToolCallRecorder(LiteRtConversationStore.Entry entry, TurnCheckpoint turn) =>
        (callId, name) =>
        {
            turn.CallIds.Add(callId);
            entry.RecordToolCall(callId, name);
        };

    /// <summary>
    /// Returns the rollback point of a stateful send's turn (<paramref name="startsTurn"/> tells whether the send
    /// starts it). A send that carries the tool results a pending turn waits for continues that turn and its
    /// checkpoint; any other send starts a turn, with a checkpoint of the conversation as it is now (none for a
    /// conversation this call created, which has nothing to roll back to). A pending turn whose tool calls the
    /// caller did not answer is over: its checkpoint is released once the new turn's send gets past the overflow
    /// guard (see <see cref="ReleaseSupersededTurn"/>).
    /// </summary>
    private TurnCheckpoint BeginTurn(
        LiteRtConversationStore.Entry entry, LiteRtConversation conversation, LiteRtChatMapping.SendTrigger trigger,
        bool newConversation, out bool startsTurn)
    {
        if (trigger.IsToolResults && entry.PendingTurn is { } pending)
        {
            startsTurn = false;
            return pending;
        }
        startsTurn = true;
        // A turn that starts with tool results is never rolled back (see RecoverFailedTurn), so it takes no checkpoint.
        LiteRtConversation? checkpoint = newConversation || trigger.IsToolResults || (entry.CloneIsSlow && !_checkpointConstrained)
            ? null
            : TakeCheckpoint(conversation);
        return new TurnCheckpoint(checkpoint, entry.SnapshotToolCalls(), trigger.UserText);
    }

    /// <summary>Records the end of a stateful send that completed: a reply with tool calls keeps its turn
    /// pending until their results come back; any other reply completes the turn and releases its
    /// checkpoint.</summary>
    private static void EndSend(LiteRtConversationStore.Entry entry, TurnCheckpoint turn, bool endsWithToolCalls)
    {
        ReleaseSupersededTurn(entry, turn);
        if (endsWithToolCalls)
        {
            entry.PendingTurn = turn;
            return;
        }
        turn.Checkpoint?.Dispose();
    }

    /// <summary>Clears the pending turn, and releases its checkpoint when a newer turn took over from it.</summary>
    private static void ReleaseSupersededTurn(LiteRtConversationStore.Entry entry, TurnCheckpoint turn)
    {
        if (entry.PendingTurn is { } pending && !ReferenceEquals(pending, turn))
            pending.Checkpoint?.Dispose();
        entry.PendingTurn = null;
    }

    /// <summary>
    /// Handles a stateful send that did not complete. With a checkpoint, and a context that is not full, the
    /// conversation is rolled back to it under the same id: the whole turn (its user message and any tool
    /// rounds) is discarded, results sent for its tool calls are rejected, and the next request continues from
    /// the turn before it. Otherwise the conversation is evicted, and resuming its id throws: a turn that created
    /// the conversation, no checkpoint, a checkpoint that is itself full (the guard's context-full rejection of
    /// a turn's first send), or a turn started by tool results. This client's rollback of a turn started by tool
    /// results is not measured. Through the core conversation API, re-sending the tool results on a checkpoint
    /// taken before a cut send gave the same reply as an uncut turn (LiteRT-LM v0.20.0, win-x64, gemma-4 E2B,
    /// CPU and GPU, one tool and one prompt).
    /// </summary>
    private void RecoverFailedTurn(string id, LiteRtConversationStore.Entry entry, TurnCheckpoint turn)
    {
        ReleaseSupersededTurn(entry, turn);
        if (turn.Checkpoint is { IsContextFull: false } && turn.UserText is not null)
        {
            entry.RestoreToolCalls(turn.ToolCalls);
            entry.DiscardToolCalls(turn.CallIds);
            _store!.Replace(id, turn.Checkpoint);
            return;
        }
        turn.Checkpoint?.Dispose();
        _store!.Remove(id);
    }

    /// <summary>
    /// Takes the checkpoint a stateful turn rolls back to: a clone of the live conversation, taken under the
    /// gate right before the turn's first send. It is a transient second conversation, outside
    /// <see cref="LiteRtStatefulConversationOptions.MaxLiveConversations"/>, disposed when the turn completes.
    /// Returns <c>null</c> when no checkpoint can be taken; after the backend reports that it does not implement
    /// cloning, no further clone is attempted, so a failure then evicts as before.
    /// </summary>
    /// <remarks>
    /// Creating it costs no measurable time or memory (measured on LiteRT-LM v0.18.0) except on a conversation
    /// with constrained decoding or a constraint provider, which gets none unless the client opted in (see
    /// <see cref="LiteRtConversationStore.Entry.CloneIsSlow"/>). Using it is not always cheaper than a replay: on
    /// the GPU with LiteRT-LM v0.20.0 (win-x64, gemma-4 E2B), a clone saved the replay only when nothing else
    /// generated after it was taken. After the original generated, as it does in a turn that is rolled back, the
    /// clone's first send took as long as a <c>History</c> restore or longer.
    /// </remarks>
    private LiteRtConversation? TakeCheckpoint(LiteRtConversation conversation)
    {
        if (_checkpointUnsupported)
            return null;
        try
        {
            return conversation.Clone();
        }
        catch (LiteRtException ex)
        {
            if (ex.StatusCode == LiteRtStatusCode.Unimplemented)
                _checkpointUnsupported = true;
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;   // nothing prefilled to copy, or the conversation is already consumed
        }
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
            forkEntry.CloneIsSlow = parent.CloneIsSlow;
            forkEntry.CopyRollbackStateFrom(parent);
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
        // completed: the stream ran to its end. untouched: the guard rejected the message before any native
        // work. failed: the stream threw. With none of them, the consumer stopped reading.
        bool completed = false, untouched = false, failed = false, sawToolCall = false, startsTurn = false;
        string? storedId = null;
        LiteRtConversation? streamed = null;
        LiteRtConversationStore.Entry? streamedEntry = null;
        TurnCheckpoint? turn = null;
        try
        {
            (LiteRtConversation conv, LiteRtConversationStore.Entry entry, LiteRtChatMapping.SendTrigger trigger, string id) =
                AcquireConversation(messages, options, conversationId);
            storedId = id;
            streamed = conv;
            streamedEntry = entry;

            LiteRtSendOptions? sendOptions = LiteRtChatMapping.ToSendOptions(options);
            // Pre-native argument check: nothing was sent and no turn began, so the finally below leaves the
            // conversation as it is (resumable).
            ThrowIfConstraintWithoutProvider(sendOptions, entry);
            // The turn's rollback point, so a cancelled or failed stream on a continuation leaves its id valid.
            turn = BeginTurn(entry, conv, trigger, newConversation: conversationId is null, out startsTurn);
            Action<string, string> recordToolCall = ToolCallRecorder(entry, turn);
            // Enumerated by hand (not await-foreach) because C# forbids `yield return` inside a try that
            // has a catch, and the MoveNext needs one: the overflow guard's pre-native "message doesn't
            // fit" rejection must NOT evict the untouched, still-resumable conversation.
            IAsyncEnumerator<ChatResponseUpdate> updates = StreamReplyAsync(
                conv, trigger, sendOptions,
                (callId, name) => { sawToolCall = true; recordToolCall(callId, name); },
                cancellationToken).GetAsyncEnumerator(cancellationToken);
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
                        untouched = true;
                        throw;
                    }
                    catch
                    {
                        failed = true;
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
            if (turn is not null)
            {
                if (untouched)
                {
                    // Nothing was streamed, so the caller never received the id of a conversation this call
                    // created: remove it, as nothing could resume it.
                    if (conversationId is null)
                        _store!.Remove(storedId!);
                    else if (startsTurn)
                        turn.Checkpoint?.Dispose();   // a turn in progress stays as it was
                }
                else if (!(streamed?.IsCancelled ?? false) && (completed || !failed))
                {
                    // A clean end, or a consumer that stopped reading after the model had finished the reply
                    // (for example at its tool calls): the turn stands, as it would for a blocking send. A stream
                    // that the core marked cancelled does not stand even when it ran to its end: its
                    // conversation is consumed.
                    EndSend(streamedEntry!, turn, sawToolCall);
                }
                else
                {
                    // Cancelled, cut while the consumer stopped reading, or failed: the native conversation is
                    // consumed (every later send on it fails with CANCELLED), so roll the turn back, or evict it.
                    RecoverFailedTurn(storedId!, streamedEntry!, turn);
                }
            }
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
        // In the stateful mode, tool calls are yielded together after the native stream ended. The runtime emits a
        // call before the stop token, and a consumer that stops at the call (a manual tool loop, an approval flow)
        // would otherwise cancel the generation and lose a turn it is about to answer, or miss a later call.
        List<AIContent>? heldToolCalls = null;
        LiteRtStreamChunk? lastToolCallChunk = null;
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
                if (onToolCall is not null)
                {
                    (heldToolCalls ??= []).AddRange(calls);
                    lastToolCallChunk = chunk;
                    continue;
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

        if (heldToolCalls is not null)
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = heldToolCalls,
                ModelId = ModelId,
                RawRepresentation = lastToolCallChunk,
                FinishReason = ChatFinishReason.ToolCalls,
            };

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
    /// <see cref="ContextFullFinishReason"/> signal when the context filled up or the reply was cut at the context
    /// limit during the stream — on
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
    /// THE context-limit finish-reason policy, written once for both the blocking response and the final
    /// streaming update: when the conversation's context filled up during the turn (the next send on it is
    /// guaranteed to throw), or the KV overflow guard capped the reply at the room left in the context and the
    /// reply used all of it (<see cref="LiteRtConversation.LastReplyReachedContextLimit"/>), the turn carries
    /// <see cref="ChatFinishReason.Length"/> — deliberately OVERRIDING <see cref="ChatFinishReason.ToolCalls"/>,
    /// because a reply cut at the context limit, tool call included, is not one to continue as is; Length is
    /// the caller's same-turn cue. After a reply with thinking the runtime removes the thinking from the KV
    /// cache, so the context can have room again and the next send works. Returns <c>null</c> (leave the
    /// content-derived reason) otherwise.
    /// </summary>
    private static ChatFinishReason? ContextFullFinishReason(LiteRtConversation conv)
        => conv.IsContextFull || conv.LastReplyReachedContextLimit ? ChatFinishReason.Length : null;

    /// <summary>
    /// Reads the just-completed turn's token usage off the (still-live) conversation. <c>TotalTokenCount</c>
    /// is always set; <c>InputTokenCount</c>/<c>OutputTokenCount</c> are populated only when the engine was
    /// loaded with <see cref="LiteRtEngineOptions.EnableBenchmark"/> = <c>true</c> (see
    /// <see cref="LiteRtChatMapping.BuildUsage"/>).
    /// </summary>
    private static UsageDetails ReadUsage(LiteRtConversation conv)
    {
        LiteRtBenchmarkInfo? benchmark = conv.GetBenchmarkInfo();
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
        // The gate belongs to the engine and other clients may share it, so it is not disposed here.
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
    /// (<see cref="LiteRtStatefulConversationOptions.MaxLiveConversations"/>), or evicted by a send rejected because its context is full
    /// its context (or was cancelled or failed when no checkpoint could be taken).</summary>
    private static ArgumentException UnknownConversation(string conversationId)
        => new(
            $"No live conversation with id '{conversationId}'. Either it was never started by this client, or " +
            "its conversation is no longer live: the LRU cap (LiteRtStatefulConversationOptions." +
            "MaxLiveConversations, default 8) evicted it when newer conversations or forks were started; a send " +
            "on it was rejected because its context is full; or a send on it was cancelled or failed with no " +
            "checkpoint to roll back to (in the turn that started the conversation, tool rounds included, in a " +
            "turn started by tool results, on a conversation with constrained decoding or a constraint provider " +
            "(LiteRtStatefulConversationOptions.CheckpointConstrainedConversations keeps those), or on a backend " +
            "that cannot clone). Start a new " +
            "conversation by omitting ChatOptions.ConversationId; the response's ConversationId is the id to " +
            "reuse for the next turn.",
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
