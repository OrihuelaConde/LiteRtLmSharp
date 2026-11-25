using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>
/// A stateful conversation over a <see cref="LiteRtEngine"/>. Handles chat templating
/// internally (mirrors the Gemini Chat APIs). Not thread-safe: serialize calls per instance — and
/// serialize <b>sends per engine</b>, not just per conversation: multiple live conversations may be
/// interleaved freely, but keep one generation in flight per engine. Upstream reports non-deterministic
/// output and GPU-backend errors for concurrent generation on different conversations of one engine
/// (LiteRT-LM#2807). The C API's threading model (<c>c/engine.h</c>) forbids overlapping calls on one
/// conversation.
/// </summary>
/// <remarks>
/// <b>Interleaving vs. concurrency.</b> Switching between several live conversations of one engine turn by
/// turn is supported (no state loss, replies stay correct, measured on CPU and GPU). What is <i>not</i>
/// supported is two generations in flight on one engine at the same time: upstream reports
/// non-deterministic output and GPU-backend errors (LiteRT-LM#2807). Native calls that go through the
/// engine, such as <see cref="TokenCount"/> and creating a conversation, also wait for a generation in
/// flight on any conversation of the engine. Do the scheduling at the application level: keep one send in
/// flight per engine, and cut the running one with <see cref="CancelProcess"/> when something more urgent
/// arrives (see <see cref="Clone"/> for keeping a checkpoint to continue from). With a
/// <see cref="LiteRtSession"/> and a conversation on one engine, LiteRT-LM v0.20.0 ran an overlapping session
/// decode and send one after the other, and both replies equaled their replies alone (measured on win-x64,
/// gemma-4 E2B, CPU and GPU); that does not show that generation at the same time is safe.
/// </remarks>
public sealed class LiteRtConversation : IDisposable
{
    private readonly ConversationHandle _conversation;
    private readonly ConversationConfigHandle? _config;
    private readonly SessionConfigHandle? _sessionConfig;
    // The engine this conversation was spawned from: carries the configured context limit
    // (MaxNumTokens, 0 = unknown) and the tokenizer the overflow guard measures prefill with.
    // Inherited by Clone(). The conversation holds a reference on the native engine, so the guard can
    // measure even after the engine object was disposed.
    private readonly LiteRtEngine _engineOwner;
    // Image prefill budget (from LiteRtConversationOptions.VisualTokenBudget); 0 = engine default.
    // Applied per send by building a native optional-args object. Inherited by Clone().
    private readonly int _visualTokenBudget;
    // Conversation-level output cap (LiteRtConversationOptions.MaxOutputTokens); 0 = engine default.
    // The native session config already enforces it — this copy only lets the overflow guard pick the
    // smaller of the caller's cap and the remaining context budget. Inherited by Clone().
    private readonly int _maxOutputTokens;
    // Channel name the native side streams raw tool-call text on (LiteRtConversationOptions.StreamToolCalls);
    // null = the feature is off and channel content routes to Thinking as before. Inherited by Clone().
    private readonly string? _toolCallStreamChannel;
    // Whether the conversation was created with a custom constraint provider (LlGuidance): the gate
    // for per-send LiteRtSendOptions.Constraint. Inherited by Clone().
    private readonly bool _hasConstraintProvider;
    // The conversation-level thinking settings, cached so per-send overrides can COMPOSE with them:
    // the native per-send thinking config replaces the conversation-level one wholesale
    // (conversation.cc ResolveThinkingConfig returns the optional_args config with BOTH fields), so
    // "null = inherit" on LiteRtSendOptions.EnableThinking/ThinkingTokenBudget is implemented here by
    // filling the unset field from these before building the per-send config. Inherited by Clone().
    private readonly bool? _thinkingEnable;
    private readonly int? _thinkingBudget;
    private bool _disposed;
    // Backs IsCancelled (see its remarks for what sets it).
    private volatile bool _cancelled;
    // How many native cancels this conversation has requested (CancelProcess and the stream teardown), so a
    // stream can tell whether one was requested while it ran.
    private int _cancelRequests;

    /// <summary>Channel name passed to <c>set_stream_tool_calls</c>. Pinned explicitly (rather than
    /// relying on the native default, currently also "tool_call") so the split logic and the native
    /// side can never disagree.</summary>
    internal const string ToolCallStreamChannelName = "tool_call";

    private LiteRtConversation(
        ConversationHandle conversation, ConversationConfigHandle? config, SessionConfigHandle? sessionConfig,
        LiteRtEngine engineOwner, int visualTokenBudget = 0, int maxOutputTokens = 0,
        string? toolCallStreamChannel = null, bool hasConstraintProvider = false,
        bool? thinkingEnable = null, int? thinkingBudget = null)
    {
        _conversation = conversation;
        _config = config;
        _sessionConfig = sessionConfig;
        _engineOwner = engineOwner;
        _visualTokenBudget = visualTokenBudget;
        _maxOutputTokens = maxOutputTokens;
        _toolCallStreamChannel = toolCallStreamChannel;
        _hasConstraintProvider = hasConstraintProvider;
        _thinkingEnable = thinkingEnable;
        _thinkingBudget = thinkingBudget;
    }

    internal static LiteRtConversation Create(
        LiteRtEngine engine, LiteRtConversationOptions? options, bool engineIsMultimodal = false)
    {
        // The native runtime supports one constrained-decoding mode per conversation: the
        // tool-calling path (EnableConstrainedDecoding) or a custom provider (ConstraintProvider) —
        // upstream docs/api/cpp/constrained-decoding.md says choose one. Checked before the platform
        // guard below so an invalid combination reports the same error on every OS.
        if (options is { EnableConstrainedDecoding: true, ConstraintProvider: not null })
        {
            throw new ArgumentException(
                "EnableConstrainedDecoding (tool-calling constrained decoding) and ConstraintProvider " +
                "(custom constraints) are mutually exclusive — the native runtime supports only one " +
                "per conversation. Set one of the two.", nameof(options));
        }

        ConversationConfigHandle? config = null;
        SessionConfigHandle? sessionConfig = null;

        try
        {
            nint configPtr = nint.Zero;
            bool hasTools = options?.Tools is { Count: > 0 };
            // Merge the typed EnableThinking flag with any raw ExtraContext into one JSON object
            // (null when neither is set); validates the JSON shape before touching native.
            string? extraContext = options is null
                ? null
                : LiteRtJson.ExtraContext(options.ExtraContext, options.EnableThinking);
            // Resolves the typed History (wins) or the raw HistoryJson into a messages-array string;
            // validates HistoryJson is an array before touching native.
            string? historyJson = options is null
                ? null
                : LiteRtJson.ResolveHistory(options.History, options.HistoryJson);

            // A multimodal engine needs the conversation to carry a session config, otherwise the
            // vision/audio executor never loads and the first attachment send fails with "Vision/Audio
            // executor should not be null". A BARE session config is enough and is neutral for text, so
            // attach one whenever the engine has an encoder enabled — even if the caller passed no
            // sampler/output cap — so a plain CreateConversation() can send attachments without setup.
            bool? speculative = options?.EnableSpeculativeDecoding;

            bool needsSessionConfig =
                options?.Sampler is not null || options?.MaxOutputTokens > 0
                || options?.LoraPath is not null || options?.AudioLoraPath is not null
                || speculative is not null
                || engineIsMultimodal;
            bool needsConfig = needsSessionConfig ||
                (options is not null &&
                 (options.SystemMessage is not null || hasTools || options.EnableConstrainedDecoding ||
                  extraContext is not null || options.FilterThinkingFromKvCache ||
                  options.StreamToolCalls || historyJson is not null ||
                  options.PromptTemplate is not null || options.ConstraintProvider is not null ||
                  options.ThinkingTokenBudget is not null));

            if (needsConfig)
            {
                nint createdConfig;   // configPtr is captured by a lambda below, so it cannot take the out value
                unsafe
                {
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_create(&createdConfig),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_create));
                }
                configPtr = createdConfig;
                config = new ConversationConfigHandle(configPtr);

                if (needsSessionConfig)
                {
                    nint sessionPtr;
                    unsafe
                    {
                        NativeError.ThrowIfFailed(
                            LiteRtLmNative.litert_lm_session_config_create(&sessionPtr),
                            nameof(LiteRtLmNative.litert_lm_session_config_create));
                    }
                    sessionConfig = new SessionConfigHandle(sessionPtr);

                    if (options?.MaxOutputTokens > 0)
                        NativeError.ThrowIfFailed(
                            LiteRtLmNative.litert_lm_session_config_set_max_output_tokens(sessionPtr, options.MaxOutputTokens),
                            nameof(LiteRtLmNative.litert_lm_session_config_set_max_output_tokens));

                    NativeSampler.Apply(sessionPtr, options?.Sampler);

                    // LoRA adapters (text / audio). The native side opens the file at set time, so a bad
                    // path surfaces here as a clear LiteRtException rather than a later create failure.
                    if (options?.LoraPath is { } loraPath)
                    {
                        LiteRtStatusCode status = LiteRtLmNative.litert_lm_session_config_set_lora_path(sessionPtr, loraPath);
                        if (status != LiteRtStatusCode.Ok)
                            throw NativeError.Exception(
                                status,
                                $"litert_lm_session_config_set_lora_path failed for '{loraPath}'.",
                                hint: "The path must point to a readable LoRA weights file.");
                    }

                    if (options?.AudioLoraPath is { } audioLoraPath)
                    {
                        LiteRtStatusCode status = LiteRtLmNative.litert_lm_session_config_set_audio_lora_path(sessionPtr, audioLoraPath);
                        if (status != LiteRtStatusCode.Ok)
                            throw NativeError.Exception(
                                status,
                                $"litert_lm_session_config_set_audio_lora_path failed for '{audioLoraPath}'.",
                                hint: "The path must point to a readable audio LoRA weights file.");
                    }

                    // Per-conversation speculative decoding: unset inherits the engine setting, also after
                    // another conversation loaded the drafter; true on an engine loaded without it makes the
                    // executor load the MTP drafter lazily on this conversation's first send; false turns it
                    // off here even when the engine enables it.
                    if (speculative is { } enableSpeculative)
                        NativeError.ThrowIfFailed(
                            LiteRtLmNative.litert_lm_session_config_set_enable_speculative_decoding(sessionPtr, enableSpeculative),
                            nameof(LiteRtLmNative.litert_lm_session_config_set_enable_speculative_decoding));
                    // Multimodal-only (no sampler/output cap/LoRA): the session config stays bare — its
                    // mere presence is what lets the encoder executor load.

                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_session_config(configPtr, sessionPtr),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_session_config));
                }

                // LiteRtJson.SystemMessage wraps the text in a content-parts ARRAY — a bare part object
                // gets dropped by the chat template and the system turn renders empty (the old
                // "SystemMessage silently ignored" bug; see the builder's doc for the full story).
                if (options?.SystemMessage is not null)
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_system_message(configPtr, LiteRtJson.SystemMessage(options.SystemMessage)),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_system_message));

                if (hasTools)
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_tools(configPtr, LiteRtJson.Tools(options!.Tools!)),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_tools));

                if (options?.EnableConstrainedDecoding == true)
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_enable_constrained_decoding(configPtr, true),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_enable_constrained_decoding));

                if (extraContext is not null)
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_extra_context(configPtr, extraContext),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_extra_context));

                if (options?.FilterThinkingFromKvCache == true)
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_filter_channel_content_from_kv_cache(configPtr, true),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_filter_channel_content_from_kv_cache));

                if (options?.StreamToolCalls == true)
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_stream_tool_calls(configPtr, true, ToolCallStreamChannelName),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_stream_tool_calls));

                if (historyJson is not null)
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_messages(configPtr, historyJson),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_messages));

                if (options?.PromptTemplate is { } promptTemplate)
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_prompt_template(configPtr, promptTemplate),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_prompt_template));

                // Passed by value; the public enum's values match the native ones (LlGuidance = 1).
                if (options?.ConstraintProvider is { } provider)
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_config_set_constraint_provider(
                            configPtr, (LiteRtLmConstraintProviderType)provider),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_constraint_provider));

                // Conversation-level thinking token budget → typed thinking config. Only built when a
                // budget is set: the plain EnableThinking flag keeps its established extra_context
                // route (byte-identical rendering; the native config feeds the same enable_thinking
                // template variable and explicit extra context wins anyway — conversation.cc).
                if (options?.ThinkingTokenBudget is { } budget)
                {
                    // The budget only bounds the thinking block, so pair it with the enable flag:
                    // an explicit EnableThinking wins; a budget alone implies thinking on (the native
                    // ThinkingConfig default is also enabled).
                    ApplyThinkingConfig(
                        options.EnableThinking ?? true, budget,
                        cfg => LiteRtLmNative.litert_lm_conversation_config_set_thinking_config(configPtr, cfg),
                        nameof(LiteRtLmNative.litert_lm_conversation_config_set_thinking_config));
                }
            }

            // The conversation keeps the native engine alive until it is deleted (ConversationHandle releases
            // this reference), so disposing the engine first can never free it under a live session.
            bool engineReferenced = false;
            engine.Handle.DangerousAddRef(ref engineReferenced);
            nint convPtr;
            LiteRtStatusCode createStatus;
            unsafe
            {
                createStatus = LiteRtLmNative.litert_lm_conversation_create(engine.Handle.Ptr, configPtr, &convPtr);
            }
            if (createStatus != LiteRtStatusCode.Ok)
            {
                // Read the report before any other native call: the release can delete the engine.
                LiteRtException failure = NativeError.Exception(createStatus, "litert_lm_conversation_create failed.");
                engine.Handle.DangerousRelease();
                throw failure;
            }

            return new LiteRtConversation(
                new ConversationHandle(convPtr, engine.Handle), config, sessionConfig, engine,
                options?.VisualTokenBudget ?? 0, options?.MaxOutputTokens ?? 0,
                options?.StreamToolCalls == true ? ToolCallStreamChannelName : null,
                options?.ConstraintProvider is not null,
                options?.EnableThinking, options?.ThinkingTokenBudget);
        }
        catch
        {
            sessionConfig?.Dispose();
            config?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Tokens currently held in this conversation's KV cache (prefill + decode, accumulated across
    /// turns). When this approaches the engine's <see cref="LiteRtEngineOptions.MaxNumTokens"/> the
    /// context is full and further generation degrades — manage history before that point. When the
    /// engine was loaded with an explicit <c>MaxNumTokens</c>, the send methods guard the limit and
    /// throw <see cref="LiteRtContextOverflowException"/> rather than let the runtime reject a send at the
    /// end of the cache (a rejected prefill leaves the conversation failing every later send; LiteRT-LM v0.20.0,
    /// CPU, gemma-4 E2B; GPU not measured).
    /// </summary>
    /// <remarks>
    /// <b>This read is a native call and can block.</b> The C API reads the count through the engine, so it
    /// queues behind a generation that is in flight on <i>any</i> conversation of the engine — measured at
    /// up to ~12 s on gemma-4 E2B (CPU) when another conversation was decoding thousands of tokens. Use
    /// <see cref="TokenCountCached"/> for display/diagnostics while generation is running, and this property
    /// when the exact current value matters and nothing is generating. Every completed send refreshes the
    /// cache, so after a send the two agree; a send that a cancel consumed (<see cref="IsCancelled"/>) does not.
    /// </remarks>
    public int TokenCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadTokenCount();
        }
    }

    /// <summary>
    /// The token count as of the last completed send (or the last <see cref="TokenCount"/> read). It
    /// <b>never waits</b>, which is the point: the native <see cref="TokenCount"/> reader queues behind a
    /// generation in flight on any conversation of the engine.
    /// </summary>
    /// <remarks>
    /// <c>0</c> until the conversation has completed a send or the exact count was read once (a
    /// <see cref="Clone"/> starts with its parent's count). Every completed
    /// send refreshes it (blocking, streaming and tool-result sends, each with one native read right after the
    /// send), so after a send the two agree; a send that a cancel consumed (<see cref="IsCancelled"/>) leaves it
    /// unchanged. While a send is in flight it does not include the turn being generated. Unlike the rest of the conversation, this property and <see cref="IsContextFull"/> can be read
    /// from another thread while a send is in flight, for example by a status line.
    /// </remarks>
    public int TokenCountCached => _cachedTokenCount;

    /// <summary>Reads the exact native count and refreshes <see cref="TokenCountCached"/>. Every internal
    /// read goes through here, so a value the guard already fetched is never re-fetched for the caller.
    /// A failed read gives -1 (the runtime reports the failure as a status and writes no count) and leaves
    /// no message on the thread.</summary>
    private unsafe int ReadTokenCount()
    {
        nuint native;
        LiteRtStatusCode status = LiteRtLmNative.litert_lm_conversation_get_token_count(_conversation.Ptr, &native);
        int count;
        if (status == LiteRtStatusCode.Ok)
        {
            count = checked((int)native);
        }
        else
        {
            NativeError.Clear();
            count = -1;
        }
        _cachedTokenCount = count;
        return count;
    }

    /// <summary>
    /// Whether a send on this conversation was cut short: a cancelled <c>SendAsync</c>, a stream abandoned
    /// before its reply ended, or <see cref="CancelProcess"/> during a send. The runtime then answers
    /// <c>CANCELLED</c> to every later send, and <see cref="Clone"/> refuses the conversation, so continue on a
    /// checkpoint taken before the send, or on a new conversation. A stream that failed for any other reason
    /// counts too, because a failure during generation consumes the conversation the same way (its later sends
    /// fail with another status); this is the safe reading, and some failures may leave it usable.
    /// </summary>
    /// <remarks>A cancel requested while a send runs sets it, even when that send then completes: a cancel that
    /// lands as the reply ends still makes the runtime fail every later send. <see cref="CancelProcess"/> on an
    /// idle conversation, a cancelled token that fires only after the send returned, and a stream left after its
    /// final chunk had already arrived keep it <c>false</c>; a stream left before that is cut. Like
    /// <see cref="TokenCountCached"/>, it can be read from another thread.</remarks>
    public bool IsCancelled => _cancelled;

    /// <summary>
    /// Whether this conversation's context is full: a completed send was detected to have run its reply
    /// into the KV overflow guard's ceiling (exact detection from the guard's own counts), or the
    /// <see cref="TokenCountCached">cached token count</see> has reached that ceiling (the engine's
    /// <see cref="LiteRtEngineOptions.MaxNumTokens"/> minus a reserve of ~128 tokens — the native
    /// executor prefills in fixed work groups of the model's prefill-signature lengths and rejects any
    /// send with fewer free entries than the smallest signature, so those trailing entries are
    /// unusable for a new send by construction). When <c>true</c>,
    /// the reply that got here was likely truncated by the guard's decode clamp (mid-sentence text, or a
    /// stream that just stopped), and the <b>next</b> send is guaranteed to throw
    /// <see cref="LiteRtContextOverflowException"/> — check this right after a send (or after a stream
    /// completes) to learn in the same turn that the conversation is over, instead of discovering it on the
    /// next call. Always <c>false</c> when the engine was loaded without an explicit <c>MaxNumTokens</c>
    /// (the limit is then internal to the engine and the guard is off).
    /// </summary>
    /// <remarks>Reads only the cached token count, so unlike <see cref="TokenCount"/> it never waits behind a
    /// generation running on another conversation of the same engine, and it can be read from another thread
    /// while a send is in flight (see <see cref="TokenCountCached"/>). It lags a send that is still in
    /// flight. A reply with thinking that the decode clamp cut can leave it <c>false</c>: the runtime removes the
    /// thinking from the KV cache after the reply, so the context has room again and the next send passes the
    /// guard. <see cref="LastReplyReachedContextLimit"/> tells that such a reply was cut.</remarks>
    public bool IsContextFull
        => _sawCeiling || LiteRtContextGuard.IsContextFull(_cachedTokenCount, _engineOwner.MaxNumTokens);

    /// <summary>
    /// Gets a value indicating whether the reply of the last send that reached the runtime ran to the limit of the
    /// context: the KV overflow
    /// guard capped the reply at the room left in the context, and the reply used that whole budget, so it was
    /// likely cut mid-sentence.
    /// </summary>
    /// <remarks>
    /// <para>Unlike <see cref="IsContextFull"/>, it does not mean that the next send fails. After a reply with
    /// thinking, the runtime removes the thinking from the KV cache, so the context has room again: this property
    /// is <c>true</c> and <see cref="IsContextFull"/> is <c>false</c>.</para>
    /// <para>Each send that reaches the runtime resets it. A send that the binding rejects before calling the
    /// runtime, such as one that throws
    /// <see cref="LiteRtContextOverflowException"/>, leaves it as it was, as it leaves
    /// <see cref="LiteRtBenchmarkInfo.LastSendPrefillTokenCount"/>. A <see cref="Clone"/> starts at <c>false</c>.
    /// It stays <c>false</c> when the reply ended on its own, when a
    /// <c>MaxOutputTokens</c> cap set by the caller was the tighter limit, when the guard could not measure the
    /// message (attachments or an extra context), and when the engine was loaded without an explicit
    /// <see cref="LiteRtEngineOptions.MaxNumTokens"/>. With <see cref="LiteRtEngineOptions.EnableBenchmark"/> on,
    /// it compares the decoded token count with the budget. Otherwise it tokenizes the answer and thinking text the
    /// send returned, which can count a few tokens fewer than were decoded, so a non-empty reply that ends on its
    /// own within 16 tokens of the budget also reads as cut; tool calls in the reply are not counted then, so a
    /// reply cut inside a tool call can read as not cut.</para>
    /// </remarks>
    public bool LastReplyReachedContextLimit => _lastReplyReachedLimit;

    /// <summary>
    /// Returns benchmark timings (prefill/decode tokens-per-second, time-to-first-token, init
    /// time) for this conversation, or <c>null</c> when benchmarking was not enabled
    /// (<see cref="LiteRtEngineOptions.EnableBenchmark"/>).
    /// </summary>
    /// <remarks>
    /// Before the first send, the info has zero prefill and decode turns: the per-turn values and
    /// <see cref="LiteRtBenchmarkInfo.TimeToFirstTokenSeconds"/> are 0, and
    /// <see cref="LiteRtBenchmarkInfo.TotalInitTimeSeconds"/> is already set.
    /// The native per-turn accessors reject an index past the last turn, so this only reads a turn
    /// after confirming the corresponding turn count is &gt; 0.
    /// Wraps a native surface Google still marks experimental in its own bindings; the reported
    /// values may change with the native runtime version.
    /// </remarks>
    /// <exception cref="LiteRtException">Reading a benchmark value failed in the runtime.</exception>
    public LiteRtBenchmarkInfo? GetBenchmarkInfo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint infoPtr;
        unsafe
        {
            if (LiteRtLmNative.litert_lm_conversation_get_benchmark_info(_conversation.Ptr, &infoPtr) != LiteRtStatusCode.Ok)
            {
                // Benchmarking off is a failure natively and null here.
                NativeError.Clear();
                return null;
            }
        }

        using var info = new BenchmarkInfoHandle(infoPtr);
        return ReadBenchmarkInfo(info.Ptr, _sendFirstPrefillTurn);
    }

    /// <summary>Reads every value of a native benchmark info. A failed read throws: the runtime writes no
    /// value on failure, and every getter fails only on a null handle or an index past the last turn, which
    /// this never passes. <paramref name="sendFirstPrefillTurn"/> is the index of the first prefill turn of the
    /// last send (-1 when not tracked).</summary>
    private static unsafe LiteRtBenchmarkInfo ReadBenchmarkInfo(nint info, int sendFirstPrefillTurn)
    {
        nuint prefillTurns, decodeTurns;
        double timeToFirstToken, totalInitTime;
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_benchmark_info_get_num_prefill_turns(info, &prefillTurns),
            nameof(LiteRtLmNative.litert_lm_benchmark_info_get_num_prefill_turns));
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_benchmark_info_get_num_decode_turns(info, &decodeTurns),
            nameof(LiteRtLmNative.litert_lm_benchmark_info_get_num_decode_turns));
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_benchmark_info_get_time_to_first_token(info, &timeToFirstToken),
            nameof(LiteRtLmNative.litert_lm_benchmark_info_get_time_to_first_token));
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_benchmark_info_get_total_init_time_in_second(info, &totalInitTime),
            nameof(LiteRtLmNative.litert_lm_benchmark_info_get_total_init_time_in_second));

        nuint lastPrefillTokens = 0, lastDecodeTokens = 0;
        double lastPrefillRate = 0, lastDecodeRate = 0;
        if (prefillTurns > 0)
        {
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_benchmark_info_get_prefill_token_count_at(info, prefillTurns - 1, &lastPrefillTokens),
                nameof(LiteRtLmNative.litert_lm_benchmark_info_get_prefill_token_count_at));
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_benchmark_info_get_prefill_tokens_per_sec_at(info, prefillTurns - 1, &lastPrefillRate),
                nameof(LiteRtLmNative.litert_lm_benchmark_info_get_prefill_tokens_per_sec_at));
        }
        if (decodeTurns > 0)
        {
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_benchmark_info_get_decode_token_count_at(info, decodeTurns - 1, &lastDecodeTokens),
                nameof(LiteRtLmNative.litert_lm_benchmark_info_get_decode_token_count_at));
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_benchmark_info_get_decode_tokens_per_sec_at(info, decodeTurns - 1, &lastDecodeRate),
                nameof(LiteRtLmNative.litert_lm_benchmark_info_get_decode_tokens_per_sec_at));
        }

        // After a reply with thinking, the runtime records the KV-cache refill as a second prefill turn of the
        // same send, so the send's message is its first prefill turn, not the last one. A send that the runtime
        // rejected before its prefill recorded none, so it reports 0 rather than the previous send's last turn.
        nuint sendPrefillTokens = sendFirstPrefillTurn < 0 ? lastPrefillTokens : 0;
        if (sendFirstPrefillTurn >= 0 && (nuint)sendFirstPrefillTurn < prefillTurns)
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_benchmark_info_get_prefill_token_count_at(
                    info, (nuint)sendFirstPrefillTurn, &sendPrefillTokens),
                nameof(LiteRtLmNative.litert_lm_benchmark_info_get_prefill_token_count_at));

        return new LiteRtBenchmarkInfo
        {
            TimeToFirstTokenSeconds = timeToFirstToken,
            TotalInitTimeSeconds = totalInitTime,
            NumPrefillTurns = checked((int)prefillTurns),
            NumDecodeTurns = checked((int)decodeTurns),
            LastPrefillTokenCount = checked((int)lastPrefillTokens),
            LastSendPrefillTokenCount = checked((int)sendPrefillTokens),
            LastDecodeTokenCount = checked((int)lastDecodeTokens),
            LastPrefillTokensPerSecond = lastPrefillRate,
            LastDecodeTokensPerSecond = lastDecodeRate,
        };
    }

    /// <summary>
    /// Forks this conversation into a new, independent one that starts from a copy of the current
    /// prefilled (KV-cache) state — branch a conversation to explore several continuations without
    /// re-prefilling the shared prefix. The clone advances on its own; this conversation is untouched.
    /// </summary>
    /// <remarks>
    /// Call this only when the conversation is idle (no in-flight <see cref="SendStreamingAsync(string, System.Threading.CancellationToken)"/>) —
    /// conversations are not thread-safe. Dispose the clone like any conversation, before the engine.
    /// Cloning duplicates state in memory; to persist a conversation across process restarts use
    /// <see cref="LiteRtConversationOptions.History"/> instead.
    /// <para>
    /// <b>The conversation must have advanced at least once</b> (<see cref="TokenCount"/> &gt; 0): the
    /// native clone duplicates the <i>prefilled</i> state, and creating a conversation prefills nothing —
    /// the system message, tools and <see cref="LiteRtConversationOptions.History"/> are only pushed
    /// into the KV cache by the first send. Cloning a never-sent conversation is not rejected natively but
    /// the result is unreliable: the first clone happens to work (it pays the parent's prefill itself),
    /// and after any other conversation runs on the engine the parent and its later clones continue that
    /// other conversation's context instead of their own (observed on LiteRT-LM v0.15.0 and v0.16.0, CPU
    /// and GPU). To keep a reusable "base" (system prompt + tools) to branch from, send one real turn on
    /// it first, then clone.
    /// </para>
    /// <para>
    /// <b>A clone is also a checkpoint.</b> A send that is cut short (a cancelled <c>SendAsync</c>, an
    /// abandoned stream, or <see cref="CancelProcess"/> during a send) leaves the conversation answering
    /// <c>CANCELLED</c> to every later send, and a clone taken after the cut does too, so this method refuses
    /// it. Take the clone <i>before</i> a send that might be cut, and continue on the clone if it is. The same
    /// applies to a send that failed during generation: the runtime keeps the failure for that conversation,
    /// so clone before the send, not after it. This method cannot tell that a blocking send failed, and the clone
    /// it returns then fails too.
    /// </para>
    /// <para>
    /// <b>Re-sending a message on a clone, GPU backend.</b> On LiteRT-LM v0.18.0, when a clone's first send repeated
    /// word for word a message the conversation it came from had already processed, and another conversation
    /// generated in between, the reply came out wrong, with no error. This was not reproduced with v0.20.0 on the
    /// GPU measured (win-x64 WebGPU, gemma-4 E2B, Float32 activations): 60 of 60 such re-sends gave the same reply
    /// as a fresh conversation. Other GPUs were not measured.
    /// </para>
    /// <para>
    /// Creating the clone is lazy: on text conversations it costs no measurable time or memory until the clone
    /// generates. The runtime also copies the message history, so inline attachments are copied once per clone. A
    /// conversation with <see cref="LiteRtConversationOptions.EnableConstrainedDecoding"/> or a
    /// <see cref="LiteRtConversationOptions.ConstraintProvider"/> is the exception, because the runtime
    /// rebuilds the constraint machinery for the clone: about 0.9 s and 1 s per clone on gemma-4 E2B
    /// (LiteRT-LM v0.18.0, win-x64, CPU and GPU alike). On LiteRT-LM v0.18.0, a clone did not carry the native
    /// state that removed earlier reasoning from the KV cache
    /// (<see cref="LiteRtConversationOptions.FilterThinkingFromKvCache"/>), so that reasoning stayed in the clone's
    /// context. On v0.20.0, the token count read after a reply with thinking already leaves the thinking out, and a
    /// clone taken then holds the same count (measured on CPU, gemma-4 E2B).
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The conversation has not advanced yet (<see cref="TokenCount"/> is 0), so there is no prefilled
    /// state to duplicate: send a message on it first. Or a send on this conversation was cut short, so
    /// there is nothing left to duplicate: take the clone before a send that might be cut.
    /// </exception>
    /// <exception cref="LiteRtException">
    /// The native clone failed. The usual cause is an engine/backend whose executor does not implement
    /// cloning (the native layer returns <c>Unimplemented</c>). The standard executors do — cloning is
    /// verified on both CPU and GPU (win-x64 WebGPU).
    /// </exception>
    public LiteRtConversation Clone()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // A cancelled conversation clones into an empty conversation that also answers CANCELLED (measured
        // on LiteRT-LM v0.18.0, CPU and GPU), so the checkpoint has to be taken before the send.
        if (_cancelled)
            throw new InvalidOperationException(
                "Cannot clone a conversation whose send was cut short: the runtime fails every later send on it, " +
                "and a clone of it fails too. To continue after a cancellation, take the " +
                "Clone() before the send that might be cut and continue on the clone, or restore the turns " +
                "on a new conversation through LiteRtConversationOptions.History.");
        // Nothing prefilled yet: the native clone would copy an empty state and the branch silently drifts
        // onto whatever conversation ran last on the engine (see the doc above). Fail loudly instead.
        if (TokenCount <= 0)
            throw new InvalidOperationException(
                "Cannot clone a conversation that has not advanced yet: Clone() duplicates the prefilled " +
                "KV-cache state, and a newly created conversation has none (its system message, tools and " +
                "History are prefilled by the first send). Cloning it anyway produces a branch that continues " +
                "another conversation's context once any other conversation runs on the engine. Send one " +
                "message on this conversation first, then clone it.");

        bool engineReferenced = false;
        _engineOwner.Handle.DangerousAddRef(ref engineReferenced);   // released by the clone's handle
        nint clonedPtr;
        LiteRtStatusCode cloneStatus;
        unsafe
        {
            cloneStatus = LiteRtLmNative.litert_lm_conversation_clone(_conversation.Ptr, &clonedPtr);
        }
        if (cloneStatus != LiteRtStatusCode.Ok)
        {
            LiteRtException failure = NativeError.Exception(
                cloneStatus, "litert_lm_conversation_clone failed.",
                hint: "Cloning duplicates the conversation's prefilled KV-cache state into a new conversation, " +
                "and some engines or backends do not implement it (the native layer then reports " +
                "UNIMPLEMENTED). To restore a conversation from persisted messages instead, create one with " +
                "LiteRtConversationOptions.History.");
            _engineOwner.Handle.DangerousRelease();
            throw failure;
        }

        // The clone is a fully independent native conversation; it does not share or need the parent's
        // config handles (those are only read at create time). Each conversation frees its own native
        // object on Dispose, so the clone outlives a disposed parent (and keeps the native engine alive).
        // It inherits the parent's engine (context limit + tokenizer for the overflow guard), visual
        // token budget, output cap, tool-call stream channel, constraint-provider flag and thinking
        // settings, so attachments, the guard, streaming, per-send constraints and per-send thinking
        // overrides behave the same on the branch (the native clone copies the whole config —
        // including the constraint provider, conversation.cc CloneInternal recreates it).
        var clone = new LiteRtConversation(
            new ConversationHandle(clonedPtr, _engineOwner.Handle), config: null, sessionConfig: null, _engineOwner,
            _visualTokenBudget, _maxOutputTokens, _toolCallStreamChannel, _hasConstraintProvider,
            _thinkingEnable, _thinkingBudget);
        // The branch holds the same tokens as the parent (the TokenCount check above just refreshed the
        // parent's cache), and a full parent means a full branch.
        clone._cachedTokenCount = _cachedTokenCount;
        clone._sawCeiling = _sawCeiling;
        // The native clone carries its parent's benchmark turns, so the parent's last send is the clone's too.
        clone._sendFirstPrefillTurn = _sendFirstPrefillTurn;
        return clone;
    }

    /// <summary>Sends a user message and returns the reply (blocking). The answer text is
    /// <see cref="LiteRtResponse.Text"/>; when the conversation has tools the model may instead return
    /// <see cref="LiteRtResponse.ToolCalls"/> (see <see cref="SendToolResults"/>), and reasoning models
    /// expose their thinking trace via <see cref="LiteRtResponse.Thinking"/>. For an awaitable variant
    /// with mid-generation cancellation use <see cref="SendAsync(string, CancellationToken)"/>; to cut a
    /// blocking send short from another thread, call <see cref="CancelProcess"/>.</summary>
    public LiteRtResponse Send(string text) => Send(text, attachments: null);

    /// <summary>
    /// Sends a user message with optional image/audio <paramref name="attachments"/> (null/empty =
    /// text-only) and optional per-send <paramref name="options"/>, returning the structured response.
    /// Attachments are appended after the text in content-part order and require the engine to have the
    /// matching modality enabled (see <see cref="LiteRtEngineOptions.VisionBackend"/> /
    /// <see cref="LiteRtEngineOptions.AudioBackend"/>) on a multimodal model.
    /// </summary>
    /// <exception cref="LiteRtContextOverflowException">Sending would overflow the KV cache sized by
    /// <see cref="LiteRtEngineOptions.MaxNumTokens"/>; the conversation is full and must be replaced.
    /// Only thrown when the engine was loaded with an explicit <c>MaxNumTokens</c>.</exception>
    public LiteRtResponse Send(
        string text, IReadOnlyList<LiteRtAttachment>? attachments, LiteRtSendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        // The typed overloads know whether the send carries media — no JSON probing needed.
        return LiteRtResponse.Parse(SendRawCore(
            LiteRtJson.UserMessage(text, attachments), extraContext: null, options,
            unmeasured: attachments is { Count: > 0 }));
    }

    /// <summary>
    /// Sends the results of executed tools back to the model and returns its next response.
    /// Call after a <see cref="LiteRtResponse"/> with <see cref="LiteRtResponse.IsToolCall"/> = true.
    /// </summary>
    /// <exception cref="LiteRtContextOverflowException">Sending would overflow the KV cache sized by
    /// <see cref="LiteRtEngineOptions.MaxNumTokens"/> — the guard that keeps a long tool loop from
    /// running into the end of the context. Only thrown when the engine was loaded with an explicit
    /// <c>MaxNumTokens</c>.</exception>
    public LiteRtResponse SendToolResults(IEnumerable<LiteRtToolResult> results, LiteRtSendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        return LiteRtResponse.Parse(SendRawCore(
            LiteRtJson.ToolResults(results), extraContext: null, options, unmeasured: false));
    }

    /// <summary>
    /// Awaitable <see cref="Send(string)"/> with true mid-generation cancellation: cancelling
    /// <paramref name="cancellationToken"/> cancels the native inference (via <see cref="CancelProcess"/>)
    /// and the task faults with <see cref="OperationCanceledException"/>. Treat a cancelled conversation
    /// as consumed: dispose it and continue on a fresh one. To keep the earlier turns without replaying them,
    /// take a <see cref="Clone"/> before the send and continue on the clone; otherwise restore prior turns via
    /// <see cref="LiteRtConversationOptions.History"/>. Sending again on a
    /// cancelled conversation returns <c>CANCELLED</c> for every later call (LiteRT-LM v0.16.0 and v0.18.0)
    /// or hangs (v0.14.0); the engine itself is unaffected. While the send is in flight, do not touch the
    /// conversation from other threads — cancellation, and reading <see cref="TokenCountCached"/> or
    /// <see cref="IsContextFull"/>, are the only supported concurrent operations.
    /// </summary>
    public Task<LiteRtResponse> SendAsync(string text, CancellationToken cancellationToken = default)
        => SendAsync(text, attachments: null, options: null, cancellationToken);

    /// <summary>
    /// Awaitable <see cref="Send(string, IReadOnlyList{LiteRtAttachment}, LiteRtSendOptions)"/> with
    /// true mid-generation cancellation — see <see cref="SendAsync(string, CancellationToken)"/> for the
    /// cancellation contract. <paramref name="attachments"/> may be null/empty for a text-only send.
    /// </summary>
    public Task<LiteRtResponse> SendAsync(
        string text, IReadOnlyList<LiteRtAttachment>? attachments, LiteRtSendOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        return RunCancellable(() => Send(text, attachments, options), cancellationToken);
    }

    /// <summary>
    /// Awaitable <see cref="SendToolResults"/> with true mid-generation cancellation — see
    /// <see cref="SendAsync(string, CancellationToken)"/> for the cancellation contract.
    /// </summary>
    public Task<LiteRtResponse> SendToolResultsAsync(
        IEnumerable<LiteRtToolResult> results, LiteRtSendOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(results);
        return RunCancellable(() => SendToolResults(results, options), cancellationToken);
    }

    /// <summary>
    /// Cancels any in-flight generation on this conversation: a blocking <see cref="Send(string)"/>
    /// running on another thread, a <see cref="SendAsync(string, CancellationToken)"/>, or an active
    /// <see cref="SendStreamingAsync(string, System.Threading.CancellationToken)"/> stream. Besides reading
    /// <see cref="TokenCountCached"/> and <see cref="IsContextFull"/>, it is the only operation that is safe to
    /// call from any thread while a send is in flight; when nothing is in flight it is a no-op. The send it
    /// cuts fails with a <see cref="LiteRtException"/> whose <see cref="LiteRtException.StatusCode"/> is
    /// <see cref="LiteRtStatusCode.Cancelled"/>, a stream included; the async variants throw
    /// <see cref="OperationCanceledException"/> instead only when their own token was cancelled. Afterwards,
    /// treat the conversation as consumed: dispose it and continue on a fresh one. To keep the earlier turns without
    /// replaying them, take a <see cref="Clone"/> before the send and continue on the clone; otherwise restore
    /// prior turns via <see cref="LiteRtConversationOptions.History"/>. Sending again on a
    /// cancelled conversation returns <c>CANCELLED</c> for every later call (LiteRT-LM v0.16.0 and v0.18.0)
    /// or hangs (v0.14.0); the engine itself is unaffected. Mirrors the native <c>cancel_process</c> (the
    /// Kotlin binding's <c>cancelProcess</c> / the JS binding's <c>cancel</c>).
    /// </summary>
    public void CancelProcess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // engine.h allows cancel_process to overlap another call on the conversation only from the stream
        // callback. A cancel from another thread during a blocking send relies on the implementation, where
        // the cancel only reads the handle and flags the session's active tasks under the execution
        // manager's lock.
        Interlocked.Increment(ref _cancelRequests);
        NativeError.ThrowIfFailed(
            LiteRtLmNative.litert_lm_conversation_cancel_process(_conversation.Ptr),
            nameof(LiteRtLmNative.litert_lm_conversation_cancel_process));
    }

    /// <summary>
    /// Runs a blocking send on the thread pool with the cancellation contract of the Async sends: the
    /// token triggers the native cancel; a send that then fails while the token is set surfaces as
    /// <see cref="OperationCanceledException"/> (the native send returns the CANCELLED status, i.e. a
    /// <see cref="LiteRtException"/> here).
    /// </summary>
    private async Task<LiteRtResponse> RunCancellable(Func<LiteRtResponse> send, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
        {
            // A late cancel racing Dispose is benign — the generation it would cut short is already gone.
            try { ((LiteRtConversation)state!).CancelProcess(); }
            catch (ObjectDisposedException) { }
        }, this);

        try
        {
            LiteRtResponse response = await Task.Run(send, CancellationToken.None).ConfigureAwait(false);
            // The token can win the race but the native task may still have completed first (e.g. it was
            // cancelled between decode steps or after the reply finished) — honor the contract regardless.
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        // LiteRtContextOverflowException is excluded: the overflow guard throws it BEFORE the native send,
        // so a concurrent cancel did not cause it — translating it would discard the context-full
        // diagnosis (TokenCount/MaxNumTokens) and leave the caller retrying against an invisible wall.
        catch (LiteRtException e) when (cancellationToken.IsCancellationRequested
                                        && e is not LiteRtContextOverflowException)
        {
            throw new OperationCanceledException(
                "The generation was cancelled (LiteRtConversation.CancelProcess).", cancellationToken);
        }
    }

    // The token count the native getter last reported (every internal read refreshes it) so callers can
    // poll the fullness of a conversation that shares its engine with an in-flight generation without
    // queueing behind it. TokenCountCached and IsContextFull are documented as readable from another thread
    // while a send is in flight, hence volatile.
    private volatile int _cachedTokenCount;

    // Set by GuardContextOverflow on each measured send whose output cap is the guard's budget, which is every
    // measured send without a tighter caller cap (pre-send token count, measured prefill, imposed decode budget) and consumed after the send completes to detect — exactly, not by
    // threshold — that the reply saturated its budget and the context is full. Conversations are not
    // thread-safe (documented), so plain fields are safe here.
    private (int Used, int Input, int Budget)? _ceilingProbe;
    // Latched true once a send is detected to have filled the context (probe above, ± the guard's safety
    // margin of measurement drift). A context never shrinks, so the latch never resets. Volatile for the same
    // reason as _cachedTokenCount (IsContextFull reads it).
    private volatile bool _sawCeiling;
    // LastReplyReachedContextLimit: set by CompleteCeilingProbe, cleared right before the native send. Volatile for the same
    // reason as _sawCeiling.
    private volatile bool _lastReplyReachedLimit;
    // The number of prefill turns the native benchmark had recorded when the last send started, which is the
    // index of that send's own prefill turn; -1 when not tracked (benchmarking off, or no send yet).
    private int _sendFirstPrefillTurn = -1;

    /// <summary>Resets the per-send state right before the native send, after the guard and the optional args, so
    /// that a send the binding rejects leaves both <see cref="LastReplyReachedContextLimit"/> and the send's prefill
    /// index as they were: clears the truncation flag and records the index of the send's first prefill turn.</summary>
    private void BeginSend()
    {
        _lastReplyReachedLimit = false;
        MarkSendPrefillTurn();
    }

    /// <summary>Records the index the send's first prefill turn will take. Reads the native benchmark only when the
    /// engine has benchmarking on.</summary>
    private unsafe void MarkSendPrefillTurn()
    {
        if (!_engineOwner.BenchmarkEnabled)
            return;
        int turns = -1;
        nint info;
        if (LiteRtLmNative.litert_lm_conversation_get_benchmark_info(_conversation.Ptr, &info) == LiteRtStatusCode.Ok)
        {
            using var handle = new BenchmarkInfoHandle(info);
            nuint prefillTurns;
            if (LiteRtLmNative.litert_lm_benchmark_info_get_num_prefill_turns(handle.Ptr, &prefillTurns) == LiteRtStatusCode.Ok)
                turns = checked((int)prefillTurns);
            else
                NativeError.Clear();
        }
        else
        {
            NativeError.Clear();
        }
        _sendFirstPrefillTurn = turns;
    }

    /// <summary>
    /// The KV overflow guard (see <see cref="LiteRtContextOverflowException"/>), run before every send.
    /// Active only when the engine was loaded with an explicit <see cref="LiteRtEngineOptions.MaxNumTokens"/>
    /// (the C API exposes no getter for the engine's internal default, so an unset limit is unknowable).
    /// Two layers:
    /// (1) a hard stop — a conversation at the guard ceiling (the shared predicate behind
    ///     <see cref="IsContextFull"/>, so signal and stop can never disagree) throws instead of reaching
    ///     native code, which is the state in which the runtime rejects the next send;
    /// (2) a decode clamp — the message's real prefill cost is measured (render + tokenize, no inference)
    ///     and the remaining context becomes this send's <c>max_output_tokens</c>, so decode physically
    ///     cannot grow the KV cache past the limit. A message whose prefill alone leaves no room throws.
    /// The measurement is best-effort: sends flagged <paramref name="unmeasured"/> (media attachments or a
    /// per-send extra context, whose token costs are not measurable managed-side) and sends whose render or
    /// tokenize fails fall back to layer (1) alone — leave headroom under the limit for those.
    /// </summary>
    private LiteRtSendOptions? GuardContextOverflow(string messageJson, LiteRtSendOptions? options, bool unmeasured)
    {
        int limit = _engineOwner.MaxNumTokens;
        if (limit <= 0)
            return options;

        _ceilingProbe = null;
        int used = ReadTokenCount();
        if (_sawCeiling)
            LiteRtContextGuard.ThrowContextFull(used, limit);
        LiteRtContextGuard.ThrowIfContextFull(used, limit);

        if (unmeasured)
            return options;

        int inputTokens;
        try
        {
            // The native render returns exactly what the next send would prefill: on a conversation whose
            // preface (system + tools + history) has not been consumed by a first send yet, the rendered
            // string INCLUDES it (verified empirically on v0.14.0); after the first send it is the turn
            // alone. So one render measures the whole prefill in either state.
            string rendered = RenderMessageRaw(messageJson);
            // An empty render for a non-empty message means the template did not actually process it —
            // measuring 0 would inflate the decode budget, so treat it as unmeasurable instead.
            if (rendered.Length == 0)
                return options;
            inputTokens = _engineOwner.CountTokens(rendered);
        }
        catch (LiteRtException)
        {
            // A failed render or tokenize, such as a message shape the template cannot render: the guard cannot
            // measure, and the hard stop above still rejects a send on a full context, which the runtime would reject.
            return options;
        }

        int budget = LiteRtContextGuard.DecodeBudget(used, limit, inputTokens);
        int callerCap = options is { MaxOutputTokens: > 0 } ? options.MaxOutputTokens : _maxOutputTokens;
        int effective = LiteRtContextGuard.EffectiveOutputCap(budget, callerCap);
        if (effective == callerCap)
            return options;   // the caller's own tighter cap ends the reply, not the context ceiling

        // The ceiling-derived budget is the cap: arm the post-send probe that detects — from exact counts,
        // tolerating measurement drift up to the safety margin in either direction — whether the reply
        // saturated it, i.e. the context is now full and the reply was likely truncated.
        _ceilingProbe = (used, inputTokens, effective);
        return (options ?? new LiteRtSendOptions()) with { MaxOutputTokens = effective };
    }

    /// <summary>Consumes the pending ceiling probe after a completed send: latches the context-full state
    /// when the KV cache grew by at least the measured prefill plus the imposed decode budget (less the
    /// safety margin, absorbing measurement drift) — the reply ran to the ceiling rather than ending on
    /// its own — and sets <see cref="LastReplyReachedContextLimit"/> when the reply used the whole budget. The
    /// reply text for the tokenizer count comes from <paramref name="responseJson"/> (blocking) or
    /// <paramref name="stream"/>. Never throws (runs on success paths).</summary>
    // NOTE (v0.15.0 recalibration): with the context-full threshold now at MinPrefillReserve (128),
    // a clamped send always lands inside the full band, so the threshold predicate alone already
    // reports IsContextFull — this probe's latch can no longer be the deciding term. It is kept as
    // belt-and-suspenders (exact detection independent of the constant) at negligible cost. A reply with
    // thinking is the exception to both: the runtime removes the thinking from the KV cache before this read,
    // so the count shows neither the growth nor the full band; ReplyUsedBudget still tells that it was cut.
    private void CompleteCeilingProbe(string? responseJson, StreamState? stream)
    {
        // Runs on every completed send that no cancel consumed, so this is also where the non-blocking cache is
        // refreshed: the
        // conversation is idle again (its own send just returned), and the read is immediate as long as
        // sends on the engine are serialized, as documented. A stream's teardown can run after the consumer
        // disposed the conversation; the native object is gone then, so there is nothing to read.
        if (_disposed)
            return;
        int count = ReadTokenCount();

        if (_ceilingProbe is not { } probe)
            return;
        _ceilingProbe = null;
        if (count - probe.Used >= probe.Input + probe.Budget - LiteRtContextGuard.SafetyMargin)
            _sawCeiling = true;
        _lastReplyReachedLimit = ReplyUsedBudget(probe.Budget, responseJson, stream);
    }

    /// <summary>Whether the reply decoded the whole decode <paramref name="budget"/> the guard imposed
    /// (<see cref="LiteRtContextGuard.ReplyUsedBudget"/>). With benchmarking on, the decode turn's token count is
    /// exact: it equals the budget when the reply was cut (CPU and GPU, LiteRT-LM v0.20.0). Otherwise the answer
    /// and thinking text are tokenized again, which measured 0 to 4 tokens fewer than were decoded (the
    /// thinking-channel markers); tool calls are not counted. Never throws: a failed read counts as not
    /// cut.</summary>
    private bool ReplyUsedBudget(int budget, string? responseJson, StreamState? stream)
    {
        try
        {
            if (_engineOwner.BenchmarkEnabled && GetBenchmarkInfo() is { NumDecodeTurns: > 0 } benchmark)
                return LiteRtContextGuard.ReplyUsedBudget(benchmark.LastDecodeTokenCount, budget, exactCount: true);

            string? answer, thinking;
            if (stream is not null)
            {
                answer = stream.Answer?.ToString();
                thinking = stream.Thinking?.ToString();
            }
            else if (responseJson is not null)
            {
                LiteRtResponse response = LiteRtResponse.Parse(responseJson);
                answer = response.Text;
                thinking = response.Thinking;
            }
            else
            {
                return false;
            }
            // A reply with fewer UTF-8 bytes than the threshold skips the tokenizer.
            int bytes = Encoding.UTF8.GetByteCount(answer ?? "") + Encoding.UTF8.GetByteCount(thinking ?? "");
            if (LiteRtContextGuard.TooShortToHaveUsedBudget(bytes, budget))
                return false;
            int decoded = (string.IsNullOrEmpty(answer) ? 0 : _engineOwner.CountTokens(answer))
                        + (string.IsNullOrEmpty(thinking) ? 0 : _engineOwner.CountTokens(thinking));
            return LiteRtContextGuard.ReplyUsedBudget(decoded, budget, exactCount: false);
        }
        catch (Exception)
        {
            // A send that completed must not fail here, whatever the benchmark read, the parse of a raw reply or
            // the tokenizer throws.
            return false;
        }
    }

    /// <summary>
    /// Low-level escape hatch: sends a raw message JSON and returns the raw response JSON.
    /// Use when you need full control over the wire format.
    /// </summary>
    /// <exception cref="LiteRtContextOverflowException">Sending would overflow the KV cache sized by
    /// <see cref="LiteRtEngineOptions.MaxNumTokens"/> — the conversation is full (or the message's
    /// prefill leaves no room to reply); the runtime would reject the send (LiteRT-LM v0.20.0, CPU, gemma-4 E2B;
    /// GPU not measured). Only thrown when
    /// the engine was loaded with an explicit <c>MaxNumTokens</c>. A send carrying media or a non-null
    /// <paramref name="extraContext"/> gets only the conversation-full check (its prefill cost is not
    /// measurable managed-side) — leave headroom under the limit for those.</exception>
    public string SendRaw(string messageJson, string? extraContext = null, LiteRtSendOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messageJson);
        // Raw JSON is the one entry point that must probe for media itself (the typed overloads know
        // their attachments). extraContext is prefilled by the native send but invisible to the render
        // measurement, so it also makes the send unmeasurable.
        return SendRawCore(
            messageJson, extraContext, options,
            unmeasured: extraContext is not null || MessageHasMedia(messageJson));
    }

    /// <summary>The shared blocking-send core behind <see cref="SendRaw"/> and the typed overloads, which
    /// already know whether the send is measurable (<paramref name="unmeasured"/>) without probing JSON.</summary>
    private string SendRawCore(
        string messageJson, string? extraContext, LiteRtSendOptions? options, bool unmeasured)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messageJson);

        options = GuardContextOverflow(messageJson, options, unmeasured);
        bool hasMedia = MessageHasMedia(messageJson);
        using ConversationOptionalArgsHandle? optionalArgs = BuildOptionalArgs(options, hasMedia);
        BeginSend();
        int cancelRequestsAtStart = Volatile.Read(ref _cancelRequests);
        nint responsePtr;
        LiteRtStatusCode status;
        unsafe
        {
            status = LiteRtLmNative.litert_lm_conversation_send_message(
                _conversation.Ptr, messageJson, extraContext, optionalArgs?.Ptr ?? nint.Zero, &responsePtr);
        }
        // The send can run for seconds. Without this, a caller whose LAST use of the conversation is
        // this call could have the wrapper collected mid-call — and its SafeHandle finalizer deletes
        // the native conversation the native thread is still generating on.
        GC.KeepAlive(this);
        // A cancel requested while the send ran consumes the conversation even when the send then returns OK: a
        // cancel that lands at the end of the turn (as the reply completes, or on the background KV-cache refill of
        // a reply with channel content) leaves every later send failing with CANCELLED, and engine.h forbids
        // reusing a conversation after cancel_process anyway.
        bool cancelRequested = Volatile.Read(ref _cancelRequests) != cancelRequestsAtStart;
        if (cancelRequested)
            _cancelled = true;
        if (status != LiteRtStatusCode.Ok)
        {
            // The status is the generation error; the runtime's reason comes from this thread's error report.
            // The multimodal setup guidance goes next to it only for the unconfigured-encoder failure ("Vision
            // executor should not be null"), or when the runtime reported nothing; a specific native reason
            // (an undecodable image, a visual budget above the per-image cap) speaks for itself.
            bool smallContext = LiteRtContextGuard.IsBelowLargestKnownPrefillSignature(_engineOwner.MaxNumTokens);
            LiteRtException failure = NativeError.Exception(status, "litert_lm_conversation_send_message failed.",
                report => SendFailureHint(report?.Message, hasMedia, smallContext));
            if (failure.StatusCode == LiteRtStatusCode.Cancelled)
                _cancelled = true;
            throw failure;
        }

        using var response = new JsonResponseHandle(responsePtr);
        nint strPtr;
        unsafe
        {
            // Lent by the response, valid until it is deleted; PtrToStringUTF8 copies it out first. It fails
            // only for a null response, and a failure reads as an empty response, as a null string did.
            if (LiteRtLmNative.litert_lm_json_response_get_string(response.Ptr, &strPtr) != LiteRtStatusCode.Ok)
            {
                NativeError.Clear();
                strPtr = nint.Zero;
            }
        }
        string result = Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
        // The send completed: detect (from exact counts) whether the reply ran to the ceiling-derived
        // decode budget, latching the context-full state the same turn it happens. engine.h allows no call but
        // the delete after cancel_process, so a send that a cancel consumed skips the count read, as the stream
        // teardown does; the cached count and IsContextFull then keep their values from before the send.
        if (!cancelRequested)
            CompleteCeilingProbe(result, stream: null);
        return result;
    }

    /// <summary>
    /// Renders the user message <paramref name="text"/> to the exact templated prompt string the model
    /// would receive, <b>without sending it</b> (conversation state is unchanged). Pair with
    /// <see cref="LiteRtEngine.Tokenize"/> to measure a turn's real token cost with the chat template
    /// included, or to inspect how a system prompt / history shape the rendered turn.
    /// </summary>
    /// <remarks>The rendered string is exactly what the next send would prefill: on a conversation whose
    /// preface (system message + tools + history) has not been consumed by a first send yet, it
    /// <b>includes the preface</b>; after the first send it is the turn alone — do not add
    /// <see cref="RenderPreface"/> on top when measuring a first send's cost. Wraps a native entry point
    /// Google still marks experimental in its own bindings; the rendered format may change with the
    /// native runtime version.</remarks>
    public string RenderMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RenderMessageRaw(LiteRtJson.UserMessage(text));
    }

    /// <summary>
    /// Low-level escape hatch: renders a raw message JSON to its templated prompt string. Use when you
    /// need full control over the wire format; otherwise use <see cref="RenderMessage(string)"/>. Does
    /// not send or change conversation state.
    /// </summary>
    public string RenderMessageRaw(string messageJson)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messageJson);

        // The returned pointer is owned by the conversation and only valid until the next render call;
        // PtrToStringUTF8 copies it out here, so the managed string outlives that window.
        nint strPtr;
        unsafe
        {
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_conversation_render_message_to_string(_conversation.Ptr, messageJson, &strPtr),
                nameof(LiteRtLmNative.litert_lm_conversation_render_message_to_string));
        }
        return Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
    }

    /// <summary>
    /// Renders the conversation's <b>preface</b> — the templated preamble the model sees before the first
    /// user turn: the system message, any tools, and the restored <see cref="LiteRtConversationOptions.History"/>
    /// — to its exact prompt string, <b>without sending</b> (conversation state is unchanged). Pair with
    /// <see cref="LiteRtEngine.Tokenize"/> to measure how many tokens the system prompt / tools / history
    /// consume up front, or to inspect the templated preamble. Complements
    /// <see cref="RenderMessage(string)"/>, which renders one user turn.
    /// </summary>
    /// <remarks>Wraps a native entry point Google still marks experimental in its own bindings; the
    /// rendered format may change with the native runtime version.</remarks>
    /// <exception cref="LiteRtException">The native render call failed.</exception>
    public string RenderPreface()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The returned pointer is owned by the conversation and only valid until the next render call;
        // PtrToStringUTF8 copies it out here, so the managed string outlives that window.
        nint strPtr;
        unsafe
        {
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_conversation_render_preface_to_string(_conversation.Ptr, &strPtr),
                nameof(LiteRtLmNative.litert_lm_conversation_render_preface_to_string));
        }
        return Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
    }

    /// <summary>Whether a native failure message is the one the runtime gives when an image/audio arrives
    /// at an engine loaded without that encoder ("Vision/Audio executor should not be null").</summary>
    internal static bool IsMissingEncoderError(string nativeMessage)
        => nativeMessage.Contains("executor should not be null", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a native failure is the runtime's rejection of a sampler type it does not implement
    /// (<c>UNIMPLEMENTED: Sampler type: 1 not implemented yet.</c>; the message names the numeric type).
    /// On v0.20.0 an explicit Greedy or TopK no longer fails (measured on CPU and GPU), and the C API rejects
    /// a type outside the enum when the sampler is built. The case left is a sampler the model file declares,
    /// used when <see cref="LiteRtConversationOptions.Sampler"/> is not set or its strategy is
    /// <see cref="LiteRtSamplerType.Unspecified"/>; no model at hand declares one, so that case is unmeasured.
    /// </summary>
    internal static bool IsUnsupportedSamplerError(string? nativeReason)
        => nativeReason is not null
           && nativeReason.Contains("Sampler type:", StringComparison.OrdinalIgnoreCase)
           && nativeReason.Contains("not implemented", StringComparison.OrdinalIgnoreCase);

    /// <summary>Guidance appended when a send fails because the runtime does not implement the sampler
    /// type in use, which can be the one the model file declares.</summary>
    internal const string SamplerNotSupportedHint =
        "The runtime does not implement the sampler type this conversation uses, which can be the one the " +
        "model file declares (LiteRtModelInfo.DefaultSampler shows it). Set " +
        "LiteRtConversationOptions.Sampler to an explicit strategy. Continue on a fresh conversation, " +
        "because the one that failed is consumed.";

    /// <summary>
    /// Guidance appended when a send carrying an image/audio attachment fails the way an unconfigured
    /// multimodal engine does (the native layer reports "Vision/Audio executor should not be null").
    /// Names the usual causes so the caller does not have to dig through native stderr.
    /// </summary>
    internal const string MultimodalSendHint =
        "The engine could not process the image/audio in this message. Check that " +
        "(1) the model is multimodal (e.g. a gemma-4 E-series build), " +
        "(2) the engine was loaded with LiteRtEngineOptions.VisionBackend / AudioBackend set " +
        "(null leaves that modality off), and " +
        "(3) LiteRtEngineOptions.MaxNumTokens leaves room for the media's tokens " +
        "(an image is roughly 256 tokens).";

    /// <summary>
    /// Appended to a failed send when the engine was loaded with a <c>MaxNumTokens</c> below the largest
    /// prefill signature of the published gemma conversions (1024): the native loader accepts the smaller
    /// limit, but prefill breaks inside the native graph once an input spans more than the smallest work
    /// group (an internal <c>DYNAMIC_UPDATE_SLICE</c> error). The C API cannot report the signatures, so
    /// this is guidance on failure rather than up-front validation.
    /// </summary>
    internal const string SmallContextSendHint =
        "The engine was loaded with LiteRtEngineOptions.MaxNumTokens below 1024. The native executor " +
        "accepts that, but a send whose prefill spans more than its smallest work group then fails inside " +
        "the native graph (the published gemma conversions' largest prefill signature is 1024). If this " +
        "message was longer than a short sentence, reload the engine with MaxNumTokens >= 1024.";

    /// <summary>Whether a send failure on an engine below 1024 tokens looks like the small-context
    /// failure (v0.18.0 reports it as "Failed to invoke the compiled model Failed to allocate tensors"),
    /// or the runtime gave no reason; any other reason, such as a cancellation or a bad LoRA adapter,
    /// speaks for itself.</summary>
    internal static bool IsSmallContextFailure(string? nativeMessage) =>
        nativeMessage is null
        || nativeMessage.Contains("allocate tensors", StringComparison.OrdinalIgnoreCase)
        || nativeMessage.Contains("DYNAMIC_UPDATE_SLICE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The guidance for a failed send, shared by the blocking and the streaming paths: the multimodal setup
    /// hint when media was attached and the runtime reported the missing-encoder failure (or no reason), and
    /// the small-context hint when the engine is below 1024 tokens and the failure matches it.
    /// </summary>
    /// <param name="nativeMessage">The runtime's reason, or <c>null</c> when it gave none.</param>
    /// <param name="mediaHintApplies">Whether the multimodal hint fits this failure.</param>
    /// <param name="smallContext">Whether the engine was loaded below 1024 tokens.</param>
    internal static string? SendFailureHint(string? nativeMessage, bool mediaHintApplies, bool smallContext)
    {
        string? hint = mediaHintApplies && (nativeMessage is null || IsMissingEncoderError(nativeMessage))
            ? MultimodalSendHint : null;
        if (smallContext && IsSmallContextFailure(nativeMessage))
            hint = hint is null ? SmallContextSendHint : hint + " " + SmallContextSendHint;
        if (IsUnsupportedSamplerError(nativeMessage))
            hint = hint is null ? SamplerNotSupportedHint : hint + " " + SamplerNotSupportedHint;
        return hint;
    }

    /// <summary>
    /// Whether a user-message JSON carries an image/audio content part. Probed by the public
    /// <see cref="SendRaw"/> escape hatch for the overflow guard (the typed overloads know their
    /// attachments without parsing) and on a send-failure path to decide whether to attach
    /// <see cref="MultimodalSendHint"/>. Tolerant of any input shape (malformed JSON and non-object
    /// roots return false — raw callers may send shapes the binding never emits).
    /// </summary>
    internal static bool MessageHasMedia(string messageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
                return false;

            foreach (JsonElement part in content.EnumerateArray())
                if (part.TryGetProperty("type", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() is "image" or "audio")
                    return true;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the per-send native optional-args object, or <c>null</c> when there is nothing to set
    /// (the common case). A per-send <see cref="LiteRtSendOptions.VisualTokenBudget"/> overrides the
    /// conversation-level <see cref="LiteRtConversationOptions.VisualTokenBudget"/>; a per-send
    /// <see cref="LiteRtSendOptions.MaxOutputTokens"/> overrides the conversation-level
    /// <see cref="LiteRtConversationOptions.MaxOutputTokens"/> for this one send. The caller owns the
    /// returned handle and disposes it after the send call; both sends copy the args before they return.
    /// </summary>
    private ConversationOptionalArgsHandle? BuildOptionalArgs(LiteRtSendOptions? options, bool hasMedia)
    {
        // The budget only matters for images, and v0.18.0 validates it against the per-image cap on every send
        // it is attached to: attached to a text-only turn, a budget above the cap would fail that turn too.
        int budget = !hasMedia ? 0 : options is { VisualTokenBudget: > 0 } ? options.VisualTokenBudget : _visualTokenBudget;
        int maxOutputTokens = options is { MaxOutputTokens: > 0 } ? options.MaxOutputTokens : 0;
        bool hasDecodingOptions = options is not null &&
            (options.RepetitionPenalties is not null || options.NoRepeatNgram is not null ||
             options.SuppressTokens is { Count: > 0 } || options.EnableThinking is not null ||
             options.ThinkingTokenBudget is not null || options.Constraint is not null);
        if (budget <= 0 && maxOutputTokens <= 0 && !hasDecodingOptions)
            return null;

        if (options?.Constraint is not null && !_hasConstraintProvider)
            throw new LiteRtException(
                "LiteRtSendOptions.Constraint requires the conversation to have been created with " +
                "LiteRtConversationOptions.ConstraintProvider set (e.g. LiteRtConstraintProvider.LlGuidance) — " +
                "without a provider the native runtime has nothing to enforce the constraint with.");

        nint created;   // p is captured by a lambda below, so it cannot take the out value
        unsafe
        {
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_conversation_optional_args_create(&created),
                nameof(LiteRtLmNative.litert_lm_conversation_optional_args_create));
        }
        nint p = created;

        var handle = new ConversationOptionalArgsHandle(p);
        try
        {
            if (budget > 0)
                NativeError.ThrowIfFailed(
                    LiteRtLmNative.litert_lm_conversation_optional_args_set_visual_token_budget(p, budget),
                    nameof(LiteRtLmNative.litert_lm_conversation_optional_args_set_visual_token_budget));
            if (maxOutputTokens > 0)
                NativeError.ThrowIfFailed(
                    LiteRtLmNative.litert_lm_conversation_optional_args_set_max_output_tokens(p, maxOutputTokens),
                    nameof(LiteRtLmNative.litert_lm_conversation_optional_args_set_max_output_tokens));

            // v0.15.0 per-send decoding configs. Each native config is deep-copied by its setter, so
            // the build-set-attach-delete lifetime stays inside this method (sampler-params pattern).
            if (options?.RepetitionPenalties is { } penalties)
            {
                nint cfg;
                unsafe
                {
                    NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_repetition_penalty_config_create(&cfg),
                        nameof(LiteRtLmNative.litert_lm_repetition_penalty_config_create));
                }
                try
                {
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_repetition_penalty_config_set_repetition_penalty(cfg, penalties.RepetitionPenalty),
                        nameof(LiteRtLmNative.litert_lm_repetition_penalty_config_set_repetition_penalty));
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_repetition_penalty_config_set_presence_penalty(cfg, penalties.PresencePenalty),
                        nameof(LiteRtLmNative.litert_lm_repetition_penalty_config_set_presence_penalty));
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_repetition_penalty_config_set_frequency_penalty(cfg, penalties.FrequencyPenalty),
                        nameof(LiteRtLmNative.litert_lm_repetition_penalty_config_set_frequency_penalty));
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_repetition_penalty_config_set_window_size(cfg, penalties.WindowSize),
                        nameof(LiteRtLmNative.litert_lm_repetition_penalty_config_set_window_size));
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_optional_args_set_repetition_penalty_config(p, cfg),
                        nameof(LiteRtLmNative.litert_lm_conversation_optional_args_set_repetition_penalty_config));
                }
                finally
                {
                    LiteRtLmNative.litert_lm_repetition_penalty_config_delete(cfg);
                }
            }

            if (options?.NoRepeatNgram is { } ngram)
            {
                nint cfg;
                unsafe
                {
                    NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_no_repeat_ngram_config_create(&cfg),
                        nameof(LiteRtLmNative.litert_lm_no_repeat_ngram_config_create));
                }
                try
                {
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_no_repeat_ngram_config_set_no_repeat_ngram_size(cfg, ngram.NgramSize),
                        nameof(LiteRtLmNative.litert_lm_no_repeat_ngram_config_set_no_repeat_ngram_size));
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_no_repeat_ngram_config_set_window_size(cfg, ngram.WindowSize),
                        nameof(LiteRtLmNative.litert_lm_no_repeat_ngram_config_set_window_size));
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_optional_args_set_no_repeat_ngram_config(p, cfg),
                        nameof(LiteRtLmNative.litert_lm_conversation_optional_args_set_no_repeat_ngram_config));
                }
                finally
                {
                    LiteRtLmNative.litert_lm_no_repeat_ngram_config_delete(cfg);
                }
            }

            // The Gemma 4 template switches thinking on only in the system turn, which the runtime prefills with the
            // first message, and the runtime's decode config for a send with thinking off enforces nothing: after a
            // first reply with thinking, a follow-up with thinking off still thinks (measured on v0.20.0, CPU and
            // GPU). Suppressing the token that opens the thinking channel makes the send answer without it.
            IReadOnlyList<int>? suppressList = SendSuppressTokens(
                options?.SuppressTokens, options?.EnableThinking,
                options?.EnableThinking == false && SuppressThinkingChannelWhenThinkingOff
                    ? _engineOwner.ThinkingChannelOpenToken : null);
            if (suppressList is { Count: > 0 } suppress)
            {
                nint cfg;
                unsafe
                {
                    NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_suppress_tokens_config_create(&cfg),
                        nameof(LiteRtLmNative.litert_lm_suppress_tokens_config_create));
                }
                try
                {
                    int[] ids = suppress as int[] ?? [.. suppress];
                    unsafe
                    {
                        fixed (int* idsPtr = ids)
                        {
                            NativeError.ThrowIfFailed(
                                LiteRtLmNative.litert_lm_suppress_tokens_config_set_suppress_tokens(cfg, idsPtr, (nuint)ids.Length),
                                nameof(LiteRtLmNative.litert_lm_suppress_tokens_config_set_suppress_tokens));
                        }
                    }
                    NativeError.ThrowIfFailed(
                        LiteRtLmNative.litert_lm_conversation_optional_args_set_suppress_tokens_config(p, cfg),
                        nameof(LiteRtLmNative.litert_lm_conversation_optional_args_set_suppress_tokens_config));
                }
                finally
                {
                    LiteRtLmNative.litert_lm_suppress_tokens_config_delete(cfg);
                }
            }

            if (options is { EnableThinking: not null } or { ThinkingTokenBudget: not null })
            {
                // COMPOSE with the conversation level rather than replace it: the native per-send
                // thinking config wins wholesale (conversation.cc ResolveThinkingConfig returns it
                // with BOTH fields, never consulting the conversation config), so "null = inherit"
                // is implemented here by filling each unset field from the cached conversation-level
                // value. Only when neither level sets a field do the fallbacks apply (enable: true —
                // a budget alone implies thinking on; budget: -1 = infinite).
                ApplyThinkingConfig(
                    options!.EnableThinking ?? _thinkingEnable ?? true,
                    options.ThinkingTokenBudget ?? _thinkingBudget ?? -1,
                    cfg => LiteRtLmNative.litert_lm_conversation_optional_args_set_thinking_config(p, cfg),
                    nameof(LiteRtLmNative.litert_lm_conversation_optional_args_set_thinking_config));
            }

            if (options?.Constraint is { } constraint)
            {
                NativeError.ThrowIfFailed(
                    LiteRtLmNative.litert_lm_conversation_optional_args_set_constraint(
                        p, (LiteRtLmConstraintType)constraint.Type, constraint.Pattern),
                    nameof(LiteRtLmNative.litert_lm_conversation_optional_args_set_constraint));
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>The suppressed tokens of one send: the caller's <paramref name="callerTokens"/>, plus
    /// <paramref name="channelOpenToken"/> when the send turns thinking off (<paramref name="enableThinking"/>
    /// <c>false</c>) and the caller's list does not hold it already. Returns <paramref name="callerTokens"/>
    /// itself when nothing is added. <paramref name="channelOpenToken"/> is <c>null</c> when the model's tokenizer
    /// has no such token.</summary>
    internal static IReadOnlyList<int>? SendSuppressTokens(
        IReadOnlyList<int>? callerTokens, bool? enableThinking, int? channelOpenToken)
    {
        if (enableThinking != false || channelOpenToken is not int open || (callerTokens?.Contains(open) ?? false))
            return callerTokens;
        return callerTokens is { Count: > 0 } ? [.. callerTokens, open] : [open];
    }

    /// <summary>Whether a send with <see cref="LiteRtSendOptions.EnableThinking"/> <c>false</c> suppresses the
    /// token that opens the thinking channel. Always <c>true</c> outside tests; a test sets it to <c>false</c> to
    /// compare with what the runtime does alone.</summary>
    internal bool SuppressThinkingChannelWhenThinkingOff { get; set; } = true;

    /// <summary>Builds a native thinking config, hands it to <paramref name="attach"/> (whose native
    /// setter deep-copies it and returns its status, checked against <paramref name="attachName"/>), and
    /// deletes it — the shared create-set-attach-delete lifetime for the conversation-level and per-send
    /// thinking configs.</summary>
    private static void ApplyThinkingConfig(
        bool enableThinking, int tokenBudget, Func<nint, LiteRtStatusCode> attach, string attachName)
    {
        nint cfg;
        unsafe
        {
            NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_thinking_config_create(&cfg),
                nameof(LiteRtLmNative.litert_lm_thinking_config_create));
        }
        try
        {
            // Always set BOTH fields: the native default ctor is enabled + infinite, not zeroes.
            NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_thinking_config_set_enable_thinking(cfg, enableThinking),
                nameof(LiteRtLmNative.litert_lm_thinking_config_set_enable_thinking));
            NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_thinking_config_set_thinking_token_budget(cfg, tokenBudget),
                nameof(LiteRtLmNative.litert_lm_thinking_config_set_thinking_token_budget));
            NativeError.ThrowIfFailed(attach(cfg), attachName);
        }
        finally
        {
            LiteRtLmNative.litert_lm_thinking_config_delete(cfg);
        }
    }

    /// <summary>
    /// Sends a user message and streams the reply as <see cref="LiteRtStreamChunk"/> pieces, each
    /// tagged by <see cref="LiteRtStreamChunk.Kind"/> as answer text, reasoning ("thinking") text,
    /// or a tool call. Route on the kind rather than assuming a global order; concatenate same-kind
    /// text deltas to rebuild the answer and the thinking trace. With
    /// <see cref="LiteRtConversationOptions.EnableThinking"/> on, reasoning models emit the thinking
    /// trace before the answer. A <see cref="LiteRtStreamChunkKind.ToolCall"/> chunk only appears when
    /// the conversation was created with tools — handle it like the blocking <see cref="Send(string)"/> loop
    /// (run the tools, then <see cref="SendToolResults"/>). With
    /// <see cref="LiteRtConversationOptions.StreamToolCalls"/> on, raw
    /// <see cref="LiteRtStreamChunkKind.ToolCallDelta"/> progress fragments additionally precede that
    /// complete tool-call chunk.
    /// </summary>
    public IAsyncEnumerable<LiteRtStreamChunk> SendStreamingAsync(
        string text, CancellationToken cancellationToken = default)
        => SendStreamingAsync(text, attachments: null, options: null, cancellationToken);

    /// <summary>
    /// Streaming overload with optional image/audio <paramref name="attachments"/> (null/empty =
    /// text-only) and per-send <paramref name="options"/>. The attachments follow the text in
    /// content-part order and require the engine to have the matching modality enabled
    /// (<see cref="LiteRtEngineOptions.VisionBackend"/> / <see cref="LiteRtEngineOptions.AudioBackend"/>)
    /// on a multimodal model; otherwise the native send fails. The chunk kinds and cancellation behavior
    /// are identical to the text-only overload.
    /// </summary>
    public async IAsyncEnumerable<LiteRtStreamChunk> SendStreamingAsync(
        string text, IReadOnlyList<LiteRtAttachment>? attachments, LiteRtSendOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        string messageJson = LiteRtJson.UserMessage(text, attachments);
        // Same KV overflow guard as the blocking path; throws (on the consumer's first MoveNext)
        // before any native state is touched. The typed signature knows whether media is attached.
        options = GuardContextOverflow(messageJson, options, unmeasured: attachments is { Count: > 0 });

        var channel = Channel.CreateUnbounded<LiteRtStreamChunk>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var state = new StreamState(channel, _toolCallStreamChannel, _engineOwner.MaxNumTokens, attachments is { Count: > 0 });
        // The truncation check tokenizes the reply when the guard capped it and benchmarking is off, so only
        // then does the callback keep the text.
        if (_ceilingProbe is not null && !_engineOwner.BenchmarkEnabled)
        {
            state.Answer = new StringBuilder();
            state.Thinking = new StringBuilder();
        }
        // The native send copies the optional args before it returns; they are freed in the finally,
        // after the channel completes. Built before the GCHandle so that if native allocation fails we
        // don't leak a pinned handle.
        ConversationOptionalArgsHandle? optionalArgs = BuildOptionalArgs(options, attachments is { Count: > 0 });
        BeginSend();
        var gcHandle = GCHandle.Alloc(state);
        // A CancelProcess while the stream runs makes the teardown skip the wait (see the finally).
        int cancelRequestsAtStart = Volatile.Read(ref _cancelRequests);

        LiteRtStatusCode status;
        unsafe
        {
            status = LiteRtLmNative.litert_lm_conversation_send_message_stream(
                _conversation.Ptr, messageJson, null, optionalArgs?.Ptr ?? nint.Zero,
                &OnStreamChunk, GCHandle.ToIntPtr(gcHandle));
        }

        if (status != LiteRtStatusCode.Ok)
        {
            // The stream did not start, so the callback never runs. Read the report before any other
            // native call (disposing the args is one).
            LiteRtException failure = NativeError.Exception(status, "litert_lm_conversation_send_message_stream failed.",
                report => SendFailureHint(report?.Message, attachments is { Count: > 0 },
                    LiteRtContextGuard.IsBelowLargestKnownPrefillSignature(_engineOwner.MaxNumTokens)));
            if (gcHandle.IsAllocated) gcHandle.Free();
            optionalArgs?.Dispose();
            throw failure;
        }

        try
        {
            await foreach (LiteRtStreamChunk chunk in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            // The native streaming thread invokes the callback until is_final (or the CANCELLED error),
            // which completes the channel. Before freeing the GCHandle (and letting the caller dispose
            // the conversation) we MUST ensure that thread is done — otherwise it dereferences freed
            // state / a deleted conversation and segfaults. Chunks the consumer never read may still sit
            // in the buffer, and a channel's Completion only transitions once the writer has completed AND
            // the buffer is empty, so every path DRAINS the channel instead of awaiting Completion, which
            // would deadlock exactly when there are leftovers.
            bool freeHandle = true;
            if (!channel.Reader.Completion.IsCompleted && !state.FinalSeen)
            {
                // The consumer abandoned the stream (break or cancellation) before its final chunk: cancel the
                // native generation, then drain until the terminal chunk. engine.h forbids reusing a
                // conversation after cancel_process, and when the generation had already ended the cancel can
                // still land on the KV-cache refill that channel filtering schedules before the final chunk,
                // which leaves every later send failing with CANCELLED; so the conversation is consumed
                // whatever the drain finds. A consumer that disposed the conversation in its loop body needs no
                // cancel: the native delete waited for the generation, and the pointer is gone.
                if (!_disposed)
                    SendCancel();
                await DrainAsync(channel).ConfigureAwait(false);
                _cancelled = true;
            }
            else if (await DrainAsync(channel).ConfigureAwait(false))
            {
                // The stream ran to its natural end natively (even if the consumer stopped reading after the
                // final chunk, with chunks still buffered): no cancel. Wait for the work the runtime still runs
                // for the turn (the background KV-cache refill of a reply with channel content) and for its
                // last callback, so the token count is settled and the conversation is idle when the stream
                // returns, as it is after a blocking send. engine.h forbids the wait after a cancel, so a
                // CancelProcess recorded before the wait skips it. One from another thread can still arrive
                // while the wait runs, and it can land on the refill, which makes the runtime fail every later
                // send; the counter is read again after the wait, so either one consumes the conversation, as on
                // the blocking path. Then the same ceiling detection and cache refresh as the blocking path, so a
                // clamped stream (which just stops, with no error) still latches IsContextFull same-turn, unless
                // the reply carried thinking that the runtime removed from the KV cache.
                bool settled = true;
                if (Volatile.Read(ref _cancelRequests) != cancelRequestsAtStart)
                {
                    // A CancelProcess during the stream consumes the conversation even though the stream ended
                    // cleanly, as on the blocking path.
                    settled = false;
                    _cancelled = true;
                }
                else if (!_disposed)
                {
                    nint conversation = _conversation.Ptr;
                    LiteRtStatusCode waited = await Task.Run(() =>
                    {
                        LiteRtStatusCode s = LiteRtLmNative.litert_lm_conversation_wait_until_done(conversation);
                        if (s != LiteRtStatusCode.Ok)
                            NativeError.Clear();   // the message is on this thread; nothing reports it
                        return s;
                    }).ConfigureAwait(false);
                    if (waited != LiteRtStatusCode.Ok)
                    {
                        // The runtime gave up waiting (its fixed wait limit): a callback may still come, so the
                        // GCHandle stays allocated (a small leak rather than a callback on a freed handle), and
                        // the conversation is in an unknown state.
                        freeHandle = settled = false;
                        _cancelled = true;
                    }
                    else if (Volatile.Read(ref _cancelRequests) != cancelRequestsAtStart)
                    {
                        // A CancelProcess arrived during the wait. The wait still returned after the last callback,
                        // so the GCHandle is freed, but the conversation is consumed.
                        settled = false;
                        _cancelled = true;
                    }
                }
                if (settled)
                    CompleteCeilingProbe(responseJson: null, state);
            }
            else
            {
                // Cancelled from outside (CancelProcess on another thread), or failed during generation: the
                // conversation is consumed either way.
                _cancelled = true;
            }
            if (freeHandle && gcHandle.IsAllocated)
                gcHandle.Free();
            optionalArgs?.Dispose();
        }
    }

    /// <summary>Reads and discards every chunk left in <paramref name="channel"/> until the writer completes.
    /// <c>true</c> when the stream completed cleanly, <c>false</c> when it completed with an error.</summary>
    private static async Task<bool> DrainAsync(Channel<LiteRtStreamChunk> channel)
    {
        try
        {
            // Terminates once the writer completes: WaitToReadAsync returns false on a clean completion and
            // throws the completion error on a faulted one (the CANCELLED path).
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                while (channel.Reader.TryRead(out _)) { }
            return true;
        }
        catch
        {
            return false;   // the error was the consumer's to read; the teardown only needs the outcome
        }
    }

    /// <summary>The stream teardown's cancel. A failure (only possible for an invalid handle) means nothing was
    /// cancelled: the drain then waits for the natural end, so the status is not reported.</summary>
    private void SendCancel()
    {
        Interlocked.Increment(ref _cancelRequests);
        if (LiteRtLmNative.litert_lm_conversation_cancel_process(_conversation.Ptr) != LiteRtStatusCode.Ok)
            NativeError.Clear();
    }

    /// <summary>Unmanaged streaming callback (v0.15.0 <c>LiteRtLmStreamCallback</c> shape: callback
    /// data + an opaque chunk read through the <c>litert_lm_stream_chunk_*</c> getters; v0.14.0 passed
    /// text/is_final/error_msg as direct parameters). Recovers state from the GCHandle in
    /// <paramref name="callbackData"/>. The chunk and the strings it owns are only valid for the
    /// duration of this call — everything is copied out before returning.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnStreamChunk(nint callbackData, nint chunk)
    {
        var gcHandle = GCHandle.FromIntPtr(callbackData);
        if (gcHandle.Target is not StreamState state)
            return;

        // The whole body is guarded: an exception escaping an [UnmanagedCallersOnly] frame
        // rude-terminates the process. The body P/Invokes the chunk getters and builds managed
        // exceptions; anything that throws must surface as a faulted stream (diagnosable,
        // teardown-safe), not a fail-fast.
        try
        {
            // engine.h "Threading model": a callback calls no LiteRT-LM function on the engine, session or
            // conversation, so only the three chunk getters run here, and a failure is reported from the
            // returned status alone (reading the thread's message would be another native call). The getters
            // fail only for a null chunk or out pointer, which the guard below rules out. "Is final" starts
            // true so that a failed read ends the stream instead of leaving the consumer waiting.
            nint errorMsg = nint.Zero, text = nint.Zero;
            byte isFinalByte = 1;
            LiteRtStatusCode status = LiteRtStatusCode.Ok;
            if (chunk != nint.Zero)
            {
                status = LiteRtLmNative.litert_lm_stream_chunk_get_error(chunk, &errorMsg);
                if (status == LiteRtStatusCode.Ok)
                    status = LiteRtLmNative.litert_lm_stream_chunk_get_text(chunk, &text);
                if (status == LiteRtStatusCode.Ok)
                    status = LiteRtLmNative.litert_lm_stream_chunk_is_final(chunk, &isFinalByte);
            }
            bool isFinal = status != LiteRtStatusCode.Ok || isFinalByte != 0;

            if (status != LiteRtStatusCode.Ok)
            {
                state.Channel.Writer.TryComplete(new LiteRtException(
                    $"Reading a native stream chunk failed: {NativeError.CanonicalName(status)}.", status));
            }
            else if (errorMsg != nint.Zero)
            {
                // The streaming path carries the runtime's status text in the chunk ("CODE: reason"):
                // build the same exception as the blocking path, with the status, the one-line reason
                // and only the guidance that fits it.
                string statusText = Marshal.PtrToStringUTF8(errorMsg) ?? "";
                state.Channel.Writer.TryComplete(NativeError.FromStatusText(
                    "litert_lm_conversation_send_message_stream failed.", statusText,
                    report => SendFailureHint(report?.Message, state.HasMedia,
                        LiteRtContextGuard.IsBelowLargestKnownPrefillSignature(state.MaxNumTokens))));
            }
            else if (text != nint.Zero)
            {
                string? piece = Marshal.PtrToStringUTF8(text);
                if (!string.IsNullOrEmpty(piece))
                    foreach (LiteRtStreamChunk c in SplitMessageChunk(piece, state.ToolCallChannel))
                    {
                        if (c.Kind == LiteRtStreamChunkKind.Answer) state.Answer?.Append(c.Text);
                        else if (c.Kind == LiteRtStreamChunkKind.Thinking) state.Thinking?.Append(c.Text);
                        state.Channel.Writer.TryWrite(c);
                    }
            }

            if (isFinal)
            {
                // Set before the channel completes, so a teardown that sees chunks still buffered knows the
                // native generation has ended and sends no cancel.
                state.FinalSeen = true;
                state.Channel.Writer.TryComplete();
            }
        }
        catch (Exception ex)
        {
            // Fault the channel so the consumer gets the real exception and the teardown's drain
            // still terminates. TryComplete is first-wins, so an already-completed channel ignores it.
            state.Channel.Writer.TryComplete(ex is LiteRtException ? ex : new LiteRtException(
                $"The streaming callback failed reading the native stream chunk: {ex.Message}", ex));
        }
        // NOTE: do NOT free the GCHandle here — the async iterator's finally owns its lifetime and
        // only frees it once the channel is fully completed, so this callback can never run against
        // a freed handle.
    }

    /// <summary>
    /// Splits one streamed message-chunk JSON into tagged pieces: the reasoning ("thinking") delta
    /// first (if any), then the raw tool-call delta (only with
    /// <see cref="LiteRtConversationOptions.StreamToolCalls"/>), then either the tool calls (when
    /// present) or the answer-text delta. Content and channel values are per-chunk deltas; tool calls
    /// arrive complete. When <paramref name="toolCallChannel"/> is non-null, channel content under that
    /// name becomes a <see cref="LiteRtStreamChunkKind.ToolCallDelta"/> chunk instead of polluting the
    /// thinking concatenation. Internal so the split can be unit-tested without a model — the native
    /// callback just feeds it each raw chunk.
    /// </summary>
    internal static IReadOnlyList<LiteRtStreamChunk> SplitMessageChunk(
        string messageJson, string? toolCallChannel = null)
    {
        LiteRtResponse parsed = LiteRtResponse.Parse(messageJson);
        var chunks = new List<LiteRtStreamChunk>(capacity: 2);
        if (toolCallChannel is not null && parsed.Channels.Count > 0)
        {
            // Route the tool-call stream channel to its own kind; everything else stays "thinking"
            // (mirrors LiteRtResponse.Thinking, which concatenates all channels).
            string? thinking = null;
            string? toolCallDelta = null;
            foreach (var (name, value) in parsed.Channels)
            {
                if (string.Equals(name, toolCallChannel, StringComparison.Ordinal))
                    toolCallDelta = value;
                else
                    thinking = thinking is null ? value : thinking + value;
            }
            if (thinking is { Length: > 0 })
                chunks.Add(LiteRtStreamChunk.Thinking(thinking));
            if (toolCallDelta is { Length: > 0 })
                chunks.Add(LiteRtStreamChunk.ToolCallDelta(toolCallDelta));
        }
        else if (parsed.Thinking is { Length: > 0 } thinking)
        {
            chunks.Add(LiteRtStreamChunk.Thinking(thinking));
        }
        if (parsed.IsToolCall)
            chunks.Add(LiteRtStreamChunk.Tools(parsed.ToolCalls));
        else if (parsed.Text is { Length: > 0 } answer)
            chunks.Add(LiteRtStreamChunk.Answer(answer));
        return chunks;
    }

    private sealed class StreamState(Channel<LiteRtStreamChunk> channel, string? toolCallChannel, int maxNumTokens, bool hasMedia)
    {
        public readonly Channel<LiteRtStreamChunk> Channel = channel;
        public readonly string? ToolCallChannel = toolCallChannel;
        /// <summary>The engine's <c>MaxNumTokens</c> (0 = unknown), for the small-context hint on a failed stream.</summary>
        public readonly int MaxNumTokens = maxNumTokens;
        /// <summary>Whether the message carries an image or audio, for the multimodal hint on a failed stream.</summary>
        public readonly bool HasMedia = hasMedia;
        /// <summary>Whether the callback received the stream's final chunk.</summary>
        public volatile bool FinalSeen;
        /// <summary>The answer and thinking text the callback received, kept only when the guard capped the
        /// reply and benchmarking is off (<c>null</c> otherwise). Read after the stream completed.</summary>
        public StringBuilder? Answer, Thinking;
    }

    /// <summary>Disposes the conversation, freeing its native resources (and its config handles). Dispose
    /// a conversation, and any clones, before the <see cref="LiteRtEngine"/> it came from.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conversation.Dispose();
        _config?.Dispose();
        _sessionConfig?.Dispose();
    }
}

/// <summary>What a <see cref="LiteRtStreamChunk"/> carries.</summary>
public enum LiteRtStreamChunkKind
{
    /// <summary>A fragment of the answer text.</summary>
    Answer = 0,
    /// <summary>A fragment of the reasoning ("thinking") trace.</summary>
    Thinking = 1,
    /// <summary>One or more tool calls the model wants executed (only when the conversation has tools).</summary>
    ToolCall = 2,
    /// <summary>A raw, incremental fragment of a tool call being generated — progress feed only, not
    /// parseable on its own; the complete parsed <see cref="ToolCall"/> chunk still follows. Emitted
    /// only when the conversation was created with
    /// <see cref="LiteRtConversationOptions.StreamToolCalls"/>.</summary>
    ToolCallDelta = 3,
}

/// <summary>
/// One streamed piece of a reply from <see cref="LiteRtConversation.SendStreamingAsync(string, System.Threading.CancellationToken)"/>.
/// <see cref="Kind"/> says what it is: an <see cref="LiteRtStreamChunkKind.Answer"/> or
/// <see cref="LiteRtStreamChunkKind.Thinking"/> text delta (concatenate same-kind chunks in order
/// to rebuild each), a <see cref="LiteRtStreamChunkKind.ToolCall"/> carrying the model's tool
/// calls, or an opt-in <see cref="LiteRtStreamChunkKind.ToolCallDelta"/> raw progress fragment.
/// <see cref="Text"/> is the delta for the text kinds (empty for complete tool calls);
/// <see cref="ToolCalls"/> is populated only for the tool-call kind.
/// </summary>
public readonly record struct LiteRtStreamChunk
{
    // Stored nullable + coalesced in the getters so a default(LiteRtStreamChunk) (reachable on any
    // public value type) still honors the non-null contract below instead of NRE-ing on Text/ToolCalls.
    private readonly string? _text;
    private readonly IReadOnlyList<LiteRtToolCall>? _toolCalls;

    internal LiteRtStreamChunk(LiteRtStreamChunkKind kind, string text, IReadOnlyList<LiteRtToolCall> toolCalls)
    {
        Kind = kind;
        _text = text;
        _toolCalls = toolCalls;
    }

    /// <summary>Whether this chunk is answer text, reasoning text, or a tool call.</summary>
    public LiteRtStreamChunkKind Kind { get; }

    /// <summary>The text delta for <see cref="LiteRtStreamChunkKind.Answer"/> /
    /// <see cref="LiteRtStreamChunkKind.Thinking"/> chunks; empty for tool-call chunks.</summary>
    public string Text => _text ?? string.Empty;

    /// <summary>The tool calls for a <see cref="LiteRtStreamChunkKind.ToolCall"/> chunk; empty otherwise.</summary>
    public IReadOnlyList<LiteRtToolCall> ToolCalls => _toolCalls ?? [];

    /// <summary>Shorthand for <c>Kind == <see cref="LiteRtStreamChunkKind.Thinking"/></c>. Note that a
    /// chunk where this is <c>false</c> may be an <see cref="LiteRtStreamChunkKind.Answer"/> OR a
    /// <see cref="LiteRtStreamChunkKind.ToolCall"/>; switch on <see cref="Kind"/> when the conversation
    /// has tools.</summary>
    public bool IsThinking => Kind == LiteRtStreamChunkKind.Thinking;

    internal static LiteRtStreamChunk Answer(string text) => new(LiteRtStreamChunkKind.Answer, text, []);
    internal static LiteRtStreamChunk Thinking(string text) => new(LiteRtStreamChunkKind.Thinking, text, []);
    internal static LiteRtStreamChunk Tools(IReadOnlyList<LiteRtToolCall> toolCalls) => new(LiteRtStreamChunkKind.ToolCall, string.Empty, toolCalls);
    internal static LiteRtStreamChunk ToolCallDelta(string text) => new(LiteRtStreamChunkKind.ToolCallDelta, text, []);
}
