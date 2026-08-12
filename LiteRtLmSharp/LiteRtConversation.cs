using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>
/// A stateful conversation over a <see cref="LiteRtEngine"/>. Handles chat templating
/// internally (mirrors the Gemini Chat APIs). Not thread-safe: serialize calls per instance.
/// </summary>
public sealed class LiteRtConversation : IDisposable
{
    private readonly ConversationHandle _conversation;
    private readonly ConversationConfigHandle? _config;
    private readonly SessionConfigHandle? _sessionConfig;
    // The engine this conversation was spawned from: carries the configured context limit
    // (MaxNumTokens, 0 = unknown) and the tokenizer the overflow guard measures prefill with.
    // Inherited by Clone(). Conversations are documented to be disposed before their engine, so the
    // guard treats a disposed engine as "cannot measure" rather than an error.
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

        // TEMPORARY GUARD — remove when upstream republishes a fixed linux prebuilt.
        // The linux-x64 libGemmaModelConstraintProvider.so shipped with LiteRT-LM v0.13.1
        // returns half-initialized constraints (internal FST is NULL) and the process dies
        // with SIGSEGV on the first decode step — a managed exception beats a dead process.
        // Windows/macOS/Android providers are fine. See google-ai-edge/LiteRT-LM#2149.
        // Scoped to the TOOL-CALLING provider path only: the ConstraintProvider (LlGuidance) path is
        // compiled from source and does not touch the broken prebuilt.
        if (options is { EnableConstrainedDecoding: true }
            && OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            throw new PlatformNotSupportedException(
                "EnableConstrainedDecoding is temporarily blocked on linux-x64: the upstream " +
                "prebuilt constraint provider (LiteRT-LM v0.13.1) returns broken constraints and " +
                "the native process crashes on the first decode step (google-ai-edge/LiteRT-LM#2149). " +
                "Set EnableConstrainedDecoding = false — tools still work; arguments are just not " +
                "grammar-constrained. This guard will be removed once upstream ships a fixed binary.");
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
            bool needsSessionConfig =
                options?.Sampler is not null || options?.MaxOutputTokens > 0
                || options?.LoraPath is not null || options?.AudioLoraPath is not null
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
                configPtr = LiteRtLmNative.litert_lm_conversation_config_create();
                if (configPtr == nint.Zero)
                    throw new LiteRtException("litert_lm_conversation_config_create returned null.");
                config = new ConversationConfigHandle(configPtr);

                if (needsSessionConfig)
                {
                    nint sessionPtr = LiteRtLmNative.litert_lm_session_config_create();
                    if (sessionPtr == nint.Zero)
                        throw new LiteRtException("litert_lm_session_config_create returned null.");
                    sessionConfig = new SessionConfigHandle(sessionPtr);

                    if (options?.MaxOutputTokens > 0)
                        LiteRtLmNative.litert_lm_session_config_set_max_output_tokens(sessionPtr, options.MaxOutputTokens);

                    // Unspecified = "let the engine choose": no sampler params are sent at all, so the
                    // executor's internal default sampling applies — the same effective behavior as
                    // 1.0.0/v0.13.1, where the native unspecified type made the sampler factory return
                    // null (sampler_factory.cc CreateCpuSampler) and the numeric fields went unused.
                    // v0.14.0 removed the native unspecified member, so "don't set" is the only faithful
                    // mapping; forcing TopP here would switch CPU decoding to an explicit top-p sampler.
                    if (options?.Sampler is { Strategy: not LiteRtSamplerType.Unspecified } s)
                    {
                        // v0.14.0: build the opaque sampler params, set ALL four fields (create() zeroes
                        // them — not the ecosystem defaults our record always carries), copy them into the
                        // session config, then delete immediately (try/finally so it can't leak).
                        nint samplerParams = LiteRtLmNative.litert_lm_sampler_params_create((LiteRtLmSamplerType)s.Strategy);
                        if (samplerParams == nint.Zero)
                            throw new LiteRtException("litert_lm_sampler_params_create returned null.");
                        try
                        {
                            LiteRtLmNative.litert_lm_sampler_params_set_top_k(samplerParams, s.TopK);
                            LiteRtLmNative.litert_lm_sampler_params_set_top_p(samplerParams, s.TopP);
                            LiteRtLmNative.litert_lm_sampler_params_set_temperature(samplerParams, s.Temperature);
                            // Seed 0 (the default) = deterministic, matching the engine default and
                            // Google's official bindings (Kotlin defaults 0; python maps unset -> 0).
                            LiteRtLmNative.litert_lm_sampler_params_set_seed(samplerParams, s.Seed);
                            LiteRtLmNative.litert_lm_session_config_set_sampler_params(sessionPtr, samplerParams);
                        }
                        finally
                        {
                            LiteRtLmNative.litert_lm_sampler_params_delete(samplerParams);
                        }
                    }

                    // LoRA adapters (text / audio). The native side opens the file at set time, so a bad
                    // path surfaces here as a clear LiteRtException rather than a later create failure.
                    if (options?.LoraPath is { } loraPath)
                    {
                        int rc = LiteRtLmNative.litert_lm_session_config_set_lora_path(sessionPtr, loraPath);
                        if (rc != 0)
                            throw new LiteRtException(
                                $"litert_lm_session_config_set_lora_path failed (returned {rc}) for '{loraPath}'. " +
                                "The path must point to a readable LoRA weights file, and the model must be LoRA-enabled.");
                    }

                    if (options?.AudioLoraPath is { } audioLoraPath)
                    {
                        int rc = LiteRtLmNative.litert_lm_session_config_set_audio_lora_path(sessionPtr, audioLoraPath);
                        if (rc != 0)
                            throw new LiteRtException(
                                $"litert_lm_session_config_set_audio_lora_path failed (returned {rc}) for '{audioLoraPath}'. " +
                                "The path must point to a readable audio LoRA weights file, and the model must be LoRA-enabled.");
                    }
                    // Multimodal-only (no sampler/output cap/LoRA): the session config stays bare — its
                    // mere presence is what lets the encoder executor load.

                    LiteRtLmNative.litert_lm_conversation_config_set_session_config(configPtr, sessionPtr);
                }

                // LiteRtJson.SystemMessage wraps the text in a content-parts ARRAY — a bare part object
                // gets dropped by the chat template and the system turn renders empty (the old
                // "SystemMessage silently ignored" bug; see the builder's doc for the full story).
                if (options?.SystemMessage is not null)
                    LiteRtLmNative.litert_lm_conversation_config_set_system_message(configPtr, LiteRtJson.SystemMessage(options.SystemMessage));

                if (hasTools)
                    LiteRtLmNative.litert_lm_conversation_config_set_tools(configPtr, LiteRtJson.Tools(options!.Tools!));

                if (options?.EnableConstrainedDecoding == true)
                    LiteRtLmNative.litert_lm_conversation_config_set_enable_constrained_decoding(configPtr, true);

                if (extraContext is not null)
                    LiteRtLmNative.litert_lm_conversation_config_set_extra_context(configPtr, extraContext);

                if (options?.FilterThinkingFromKvCache == true)
                    LiteRtLmNative.litert_lm_conversation_config_set_filter_channel_content_from_kv_cache(configPtr, true);

                if (options?.StreamToolCalls == true)
                    LiteRtLmNative.litert_lm_conversation_config_set_stream_tool_calls(
                        configPtr, true, ToolCallStreamChannelName);

                if (historyJson is not null)
                    LiteRtLmNative.litert_lm_conversation_config_set_messages(configPtr, historyJson);

                if (options?.PromptTemplate is { } promptTemplate)
                    LiteRtLmNative.litert_lm_conversation_config_set_prompt_template(configPtr, promptTemplate);

                if (options?.ConstraintProvider is { } provider)
                {
                    unsafe
                    {
                        int providerValue = (int)provider;
                        LiteRtLmNative.litert_lm_conversation_config_set_constraint_provider(configPtr, &providerValue);
                    }
                }

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
                        cfg => LiteRtLmNative.litert_lm_conversation_config_set_thinking_config(configPtr, cfg));
                }
            }

            nint convPtr = LiteRtLmNative.litert_lm_conversation_create(engine.Handle.Ptr, configPtr);
            if (convPtr == nint.Zero)
                throw new LiteRtException("litert_lm_conversation_create returned null.");

            return new LiteRtConversation(
                new ConversationHandle(convPtr), config, sessionConfig, engine,
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
    /// throw <see cref="LiteRtContextOverflowException"/> rather than let a send overflow the cache
    /// (which corrupts the native runtime).
    /// </summary>
    public int TokenCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return LiteRtLmNative.litert_lm_conversation_get_token_count(_conversation.Ptr);
        }
    }

    /// <summary>
    /// Whether this conversation's context is full: a completed send was detected to have run its reply
    /// into the KV overflow guard's ceiling (exact detection from the guard's own counts), or
    /// <see cref="TokenCount"/> has reached that ceiling (the engine's
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
    public bool IsContextFull
        => _sawCeiling || LiteRtContextGuard.IsContextFull(TokenCount, _engineOwner.MaxNumTokens);

    /// <summary>
    /// Returns benchmark timings (prefill/decode tokens-per-second, time-to-first-token, init
    /// time) for this conversation, or <c>null</c> when benchmarking was not enabled
    /// (<see cref="LiteRtEngineOptions.EnableBenchmark"/>) or no turn has completed yet.
    /// </summary>
    /// <remarks>
    /// The native per-turn accessors do not bounds-check their index, so this only reads a turn
    /// after confirming the corresponding turn count is &gt; 0. Throws
    /// <see cref="EntryPointNotFoundException"/> on native binaries predating the benchmark API.
    /// Wraps a native surface Google still marks experimental in its own bindings; the reported
    /// values may change with the native runtime version.
    /// </remarks>
    public LiteRtBenchmarkInfo? GetBenchmarkInfo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint infoPtr = LiteRtLmNative.litert_lm_conversation_get_benchmark_info(_conversation.Ptr);
        if (infoPtr == nint.Zero)
            return null;

        using var info = new BenchmarkInfoHandle(infoPtr);
        int prefillTurns = LiteRtLmNative.litert_lm_benchmark_info_get_num_prefill_turns(info.Ptr);
        int decodeTurns = LiteRtLmNative.litert_lm_benchmark_info_get_num_decode_turns(info.Ptr);

        return new LiteRtBenchmarkInfo
        {
            TimeToFirstTokenSeconds = LiteRtLmNative.litert_lm_benchmark_info_get_time_to_first_token(info.Ptr),
            TotalInitTimeSeconds = LiteRtLmNative.litert_lm_benchmark_info_get_total_init_time_in_second(info.Ptr),
            NumPrefillTurns = prefillTurns,
            NumDecodeTurns = decodeTurns,
            // Per-turn getters are unguarded natively — never pass a negative index.
            LastPrefillTokenCount = prefillTurns > 0
                ? LiteRtLmNative.litert_lm_benchmark_info_get_prefill_token_count_at(info.Ptr, prefillTurns - 1) : 0,
            LastDecodeTokenCount = decodeTurns > 0
                ? LiteRtLmNative.litert_lm_benchmark_info_get_decode_token_count_at(info.Ptr, decodeTurns - 1) : 0,
            LastPrefillTokensPerSecond = prefillTurns > 0
                ? LiteRtLmNative.litert_lm_benchmark_info_get_prefill_tokens_per_sec_at(info.Ptr, prefillTurns - 1) : 0,
            LastDecodeTokensPerSecond = decodeTurns > 0
                ? LiteRtLmNative.litert_lm_benchmark_info_get_decode_tokens_per_sec_at(info.Ptr, decodeTurns - 1) : 0,
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
    /// </remarks>
    /// <exception cref="LiteRtException">
    /// The native clone failed. The usual cause is an engine/backend whose executor does not implement
    /// cloning (the native layer returns <c>Unimplemented</c>). The standard executors do — cloning is
    /// verified on both CPU and GPU (win-x64 WebGPU).
    /// </exception>
    public LiteRtConversation Clone()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint clonedPtr = LiteRtLmNative.litert_lm_conversation_clone(_conversation.Ptr);
        if (clonedPtr == nint.Zero)
            throw new LiteRtException(
                "litert_lm_conversation_clone returned null. Cloning duplicates the conversation's " +
                "prefilled KV-cache state into a new conversation, but some engines/backends do not " +
                "implement it (the native layer returns 'Unimplemented'). To restore a conversation " +
                "from persisted messages instead, create one with LiteRtConversationOptions.History.");

        // The clone is a fully independent native conversation; it does not share or need the parent's
        // config handles (those are only read at create time). Each conversation frees its own native
        // object on Dispose, so the clone outlives a disposed parent as long as the engine is alive.
        // It inherits the parent's engine (context limit + tokenizer for the overflow guard), visual
        // token budget, output cap, tool-call stream channel, constraint-provider flag and thinking
        // settings, so attachments, the guard, streaming, per-send constraints and per-send thinking
        // overrides behave the same on the branch (the native clone copies the whole config —
        // including the constraint provider, conversation.cc CloneInternal recreates it).
        return new LiteRtConversation(
            new ConversationHandle(clonedPtr), config: null, sessionConfig: null, _engineOwner,
            _visualTokenBudget, _maxOutputTokens, _toolCallStreamChannel, _hasConstraintProvider,
            _thinkingEnable, _thinkingBudget);
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
    /// crashing the native runtime. Only thrown when the engine was loaded with an explicit
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
    /// as consumed: dispose it and continue on a fresh one (restore prior turns via
    /// <see cref="LiteRtConversationOptions.History"/> if needed) — sending again on it hangs inside the
    /// native runtime (LiteRT-LM v0.13.1, reproduced with Google's own binaries); the engine itself is
    /// unaffected. While the send is in flight, do not touch the conversation from other threads —
    /// cancellation is the only supported concurrent operation.
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
    /// <see cref="SendStreamingAsync(string, System.Threading.CancellationToken)"/> stream. This is the
    /// one operation that is safe to call from any thread while a send is in flight; when nothing is in
    /// flight it is a no-op. A cancelled blocking send fails with <see cref="LiteRtException"/> (the
    /// async variants translate that to <see cref="OperationCanceledException"/>). Afterwards, treat the
    /// conversation as consumed: dispose it and continue on a fresh one (restore prior turns via
    /// <see cref="LiteRtConversationOptions.History"/> if needed) — sending again on a cancelled
    /// conversation hangs inside the native runtime (LiteRT-LM v0.13.1, reproduced with Google's own
    /// binaries); the engine itself is unaffected. Mirrors the native <c>cancel_process</c> (the Kotlin
    /// binding's <c>cancelProcess</c> / the JS binding's <c>cancel</c>).
    /// </summary>
    public void CancelProcess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LiteRtLmNative.litert_lm_conversation_cancel_process(_conversation.Ptr);
    }

    /// <summary>
    /// Runs a blocking send on the thread pool with the cancellation contract of the Async sends: the
    /// token triggers the native cancel; a send that then fails while the token is set surfaces as
    /// <see cref="OperationCanceledException"/> (the native CANCELLED state reaches the C API as a null
    /// response, i.e. a <see cref="LiteRtException"/> here).
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

    // Set by GuardContextOverflow on each measured, ceiling-clamped send (pre-send token count, measured
    // prefill, imposed decode budget) and consumed after the send completes to detect — exactly, not by
    // threshold — that the reply saturated its budget and the context is full. Conversations are not
    // thread-safe (documented), so plain fields are safe here.
    private (int Used, int Input, int Budget)? _ceilingProbe;
    // Latched true once a send is detected to have filled the context (probe above, ± the guard's safety
    // margin of measurement drift). A context never shrinks, so the latch never resets.
    private bool _sawCeiling;

    /// <summary>
    /// The KV overflow guard (see <see cref="LiteRtContextOverflowException"/>), run before every send.
    /// Active only when the engine was loaded with an explicit <see cref="LiteRtEngineOptions.MaxNumTokens"/>
    /// (the C API exposes no getter for the engine's internal default, so an unset limit is unknowable).
    /// Two layers:
    /// (1) a hard stop — a conversation at the guard ceiling (the shared predicate behind
    ///     <see cref="IsContextFull"/>, so signal and stop can never disagree) throws instead of reaching
    ///     native code, which is the exact state that crashes the runtime on the next send;
    /// (2) a decode clamp — the message's real prefill cost is measured (render + tokenize, no inference)
    ///     and the remaining context becomes this send's <c>max_output_tokens</c>, so decode physically
    ///     cannot grow the KV cache past the limit. A message whose prefill alone leaves no room throws.
    /// The measurement is best-effort: sends flagged <paramref name="unmeasured"/> (media attachments or a
    /// per-send extra context, whose token costs are not measurable managed-side) and natives that cannot
    /// render/tokenize fall back to layer (1) alone — leave headroom under the limit for those.
    /// </summary>
    private LiteRtSendOptions? GuardContextOverflow(string messageJson, LiteRtSendOptions? options, bool unmeasured)
    {
        int limit = _engineOwner.MaxNumTokens;
        if (limit <= 0)
            return options;

        _ceilingProbe = null;
        int used = TokenCount;
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
        catch (Exception e) when (e is LiteRtException or EntryPointNotFoundException or ObjectDisposedException)
        {
            // Older natives without the render/tokenize entry points, a message shape the template cannot
            // render, or an engine disposed out of order: the guard cannot measure, the hard stop above
            // still protects the known-fatal state.
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
    /// its own. Never throws (runs on success paths).</summary>
    // NOTE (v0.15.0 recalibration): with the context-full threshold now at MinPrefillReserve (128),
    // a clamped send always lands inside the full band, so the threshold predicate alone already
    // reports IsContextFull — this probe's latch can no longer be the deciding term. It is kept as
    // belt-and-suspenders (exact detection independent of the constant) at negligible cost.
    private void CompleteCeilingProbe()
    {
        if (_ceilingProbe is not { } probe)
            return;
        _ceilingProbe = null;
        try
        {
            if (TokenCount - probe.Used >= probe.Input + probe.Budget - LiteRtContextGuard.SafetyMargin)
                _sawCeiling = true;
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Low-level escape hatch: sends a raw message JSON and returns the raw response JSON.
    /// Use when you need full control over the wire format.
    /// </summary>
    /// <exception cref="LiteRtContextOverflowException">Sending would overflow the KV cache sized by
    /// <see cref="LiteRtEngineOptions.MaxNumTokens"/> — the conversation is full (or the message's
    /// prefill leaves no room to reply); continuing would corrupt the native runtime. Only thrown when
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
        using ConversationOptionalArgsHandle? optionalArgs = BuildOptionalArgs(options);
        nint responsePtr = LiteRtLmNative.litert_lm_conversation_send_message(
            _conversation.Ptr, messageJson, extraContext, optionalArgs?.Ptr ?? nint.Zero);
        // The send can run for seconds. Without this, a caller whose LAST use of the conversation is
        // this call could have the wrapper collected mid-call — and its SafeHandle finalizer deletes
        // the native conversation the native thread is still generating on.
        GC.KeepAlive(this);
        if (responsePtr == nint.Zero)
        {
            // The blocking send returns null with no error string (the native reason goes to stderr).
            // When the message carried media, the usual cause is a multimodal-setup problem, so name it.
            string msg = "litert_lm_conversation_send_message returned null.";
            if (MessageHasMedia(messageJson))
                msg += " " + MultimodalSendHint;
            throw new LiteRtException(msg);
        }

        using var response = new JsonResponseHandle(responsePtr);
        nint strPtr = LiteRtLmNative.litert_lm_json_response_get_string(response.Ptr);
        string result = Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
        // The send completed: detect (from exact counts) whether the reply ran to the ceiling-derived
        // decode budget, latching the context-full state the same turn it happens.
        CompleteCeilingProbe();
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
        nint strPtr = LiteRtLmNative.litert_lm_conversation_render_message_to_string(_conversation.Ptr, messageJson);
        if (strPtr == nint.Zero)
            throw new LiteRtException("litert_lm_conversation_render_message_to_string returned null.");
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
    /// rendered format may change with the native runtime version. Requires native LiteRT-LM v0.14.0+
    /// (throws <see cref="EntryPointNotFoundException"/> on older binaries).</remarks>
    /// <exception cref="LiteRtException">The native render call returned null.</exception>
    public string RenderPreface()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The returned pointer is owned by the conversation and only valid until the next render call;
        // PtrToStringUTF8 copies it out here, so the managed string outlives that window.
        nint strPtr = LiteRtLmNative.litert_lm_conversation_render_preface_to_string(_conversation.Ptr);
        if (strPtr == nint.Zero)
            throw new LiteRtException("litert_lm_conversation_render_preface_to_string returned null.");
        return Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
    }

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
    /// returned handle: dispose it after the send completes (for streaming, only after the native
    /// decode thread is done — it reads the args during prefill).
    /// </summary>
    private ConversationOptionalArgsHandle? BuildOptionalArgs(LiteRtSendOptions? options)
    {
        int budget = options is { VisualTokenBudget: > 0 } ? options.VisualTokenBudget : _visualTokenBudget;
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

        nint p = LiteRtLmNative.litert_lm_conversation_optional_args_create();
        if (p == nint.Zero)
            throw new LiteRtException("litert_lm_conversation_optional_args_create returned null.");

        var handle = new ConversationOptionalArgsHandle(p);
        try
        {
            if (budget > 0)
                LiteRtLmNative.litert_lm_conversation_optional_args_set_visual_token_budget(p, budget);
            if (maxOutputTokens > 0)
                LiteRtLmNative.litert_lm_conversation_optional_args_set_max_output_tokens(p, maxOutputTokens);

            // v0.15.0 per-send decoding configs. Each native config is deep-copied by its setter, so
            // the build-set-attach-delete lifetime stays inside this method (sampler-params pattern).
            if (options?.RepetitionPenalties is { } penalties)
            {
                nint cfg = LiteRtLmNative.litert_lm_repetition_penalty_config_create();
                if (cfg == nint.Zero)
                    throw new LiteRtException("litert_lm_repetition_penalty_config_create returned null.");
                try
                {
                    LiteRtLmNative.litert_lm_repetition_penalty_config_set_repetition_penalty(cfg, penalties.RepetitionPenalty);
                    LiteRtLmNative.litert_lm_repetition_penalty_config_set_presence_penalty(cfg, penalties.PresencePenalty);
                    LiteRtLmNative.litert_lm_repetition_penalty_config_set_frequency_penalty(cfg, penalties.FrequencyPenalty);
                    LiteRtLmNative.litert_lm_repetition_penalty_config_set_window_size(cfg, penalties.WindowSize);
                    LiteRtLmNative.litert_lm_conversation_optional_args_set_repetition_penalty_config(p, cfg);
                }
                finally
                {
                    LiteRtLmNative.litert_lm_repetition_penalty_config_delete(cfg);
                }
            }

            if (options?.NoRepeatNgram is { } ngram)
            {
                nint cfg = LiteRtLmNative.litert_lm_no_repeat_ngram_config_create();
                if (cfg == nint.Zero)
                    throw new LiteRtException("litert_lm_no_repeat_ngram_config_create returned null.");
                try
                {
                    LiteRtLmNative.litert_lm_no_repeat_ngram_config_set_no_repeat_ngram_size(cfg, ngram.NgramSize);
                    LiteRtLmNative.litert_lm_no_repeat_ngram_config_set_window_size(cfg, ngram.WindowSize);
                    LiteRtLmNative.litert_lm_conversation_optional_args_set_no_repeat_ngram_config(p, cfg);
                }
                finally
                {
                    LiteRtLmNative.litert_lm_no_repeat_ngram_config_delete(cfg);
                }
            }

            if (options?.SuppressTokens is { Count: > 0 } suppress)
            {
                nint cfg = LiteRtLmNative.litert_lm_suppress_tokens_config_create();
                if (cfg == nint.Zero)
                    throw new LiteRtException("litert_lm_suppress_tokens_config_create returned null.");
                try
                {
                    int[] ids = suppress as int[] ?? [.. suppress];
                    unsafe
                    {
                        fixed (int* idsPtr = ids)
                        {
                            LiteRtLmNative.litert_lm_suppress_tokens_config_set_suppress_tokens(cfg, idsPtr, (nuint)ids.Length);
                        }
                    }
                    LiteRtLmNative.litert_lm_conversation_optional_args_set_suppress_tokens_config(p, cfg);
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
                    cfg => LiteRtLmNative.litert_lm_conversation_optional_args_set_thinking_config(p, cfg));
            }

            if (options?.Constraint is { } constraint)
            {
                LiteRtLmNative.litert_lm_conversation_optional_args_set_constraint(
                    p, (LiteRtLmConstraintType)constraint.Type, constraint.Pattern);
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Builds a native thinking config, hands it to <paramref name="attach"/> (whose native
    /// setter deep-copies it), and deletes it — the shared create-set-attach-delete lifetime for the
    /// conversation-level and per-send thinking configs.</summary>
    private static void ApplyThinkingConfig(bool enableThinking, int tokenBudget, Action<nint> attach)
    {
        nint cfg = LiteRtLmNative.litert_lm_thinking_config_create();
        if (cfg == nint.Zero)
            throw new LiteRtException("litert_lm_thinking_config_create returned null.");
        try
        {
            // Always set BOTH fields: the native default ctor is enabled + infinite, not zeroes.
            LiteRtLmNative.litert_lm_thinking_config_set_enable_thinking(cfg, enableThinking);
            LiteRtLmNative.litert_lm_thinking_config_set_thinking_token_budget(cfg, tokenBudget);
            attach(cfg);
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
        var state = new StreamState(channel, _toolCallStreamChannel);
        // The optional args (visual token budget) must stay alive for the whole stream: the native
        // decode thread reads them during prefill. Freed in the finally, after the channel completes.
        // Built before the GCHandle so that if native allocation fails we don't leak a pinned handle.
        ConversationOptionalArgsHandle? optionalArgs = BuildOptionalArgs(options);
        var gcHandle = GCHandle.Alloc(state);

        int rc;
        unsafe
        {
            rc = LiteRtLmNative.litert_lm_conversation_send_message_stream(
                _conversation.Ptr, messageJson, null, optionalArgs?.Ptr ?? nint.Zero,
                &OnStreamChunk, GCHandle.ToIntPtr(gcHandle));
        }

        if (rc != 0)
        {
            if (gcHandle.IsAllocated) gcHandle.Free();
            optionalArgs?.Dispose();
            throw new LiteRtException($"litert_lm_conversation_send_message_stream failed with code {rc}.");
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
            // state / a deleted conversation and segfaults. If the consumer abandoned early (break or
            // cancellation), cancel the native inference, then DRAIN the channel: chunks the consumer
            // never read may still sit in the buffer, and a channel's Completion only transitions once
            // the writer has completed AND the buffer is empty — awaiting Completion without draining
            // deadlocks exactly when there are leftovers.
            if (!channel.Reader.Completion.IsCompleted)
            {
                LiteRtLmNative.litert_lm_conversation_cancel_process(_conversation.Ptr);
                try
                {
                    // Terminates once the writer completes: WaitToReadAsync returns false on a clean
                    // completion and throws the completion error on a faulted one (the CANCELLED path).
                    while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                        while (channel.Reader.TryRead(out _)) { }
                }
                catch { /* the cancel error is expected here; the consumer already gave up */ }
            }
            else if (channel.Reader.Completion.IsCompletedSuccessfully)
            {
                // The stream ran to its natural end: same ceiling detection as the blocking path, so a
                // clamped stream (which just stops, with no error) still latches IsContextFull same-turn.
                // Abandoned/faulted streams skip it — the conversation is consumed anyway.
                CompleteCeilingProbe();
            }
            if (gcHandle.IsAllocated)
                gcHandle.Free();
            optionalArgs?.Dispose();
        }
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
        // rude-terminates the process. Unlike the v0.14.0 callback (whose payload arrived as plain
        // parameters), this body P/Invokes the chunk getters — against a mismatched older native
        // that lacks them, that's an EntryPointNotFoundException on the native decode thread, which
        // must surface as a faulted stream (diagnosable, teardown-safe), not a fail-fast.
        try
        {
            nint errorMsg = chunk != nint.Zero ? LiteRtLmNative.litert_lm_stream_chunk_get_error(chunk) : nint.Zero;
            nint text = chunk != nint.Zero ? LiteRtLmNative.litert_lm_stream_chunk_get_text(chunk) : nint.Zero;
            bool isFinal = chunk == nint.Zero || LiteRtLmNative.litert_lm_stream_chunk_is_final(chunk);

            if (errorMsg != nint.Zero)
            {
                string msg = Marshal.PtrToStringUTF8(errorMsg) ?? "unknown error";
                // The streaming path DOES surface the native string; when it is the unconfigured-multimodal
                // failure ("Vision/Audio executor should not be null"), append the same setup guidance.
                if (msg.Contains("executor should not be null", StringComparison.OrdinalIgnoreCase))
                    msg += " " + MultimodalSendHint;
                state.Channel.Writer.TryComplete(new LiteRtException(msg));
            }
            else if (text != nint.Zero)
            {
                string? piece = Marshal.PtrToStringUTF8(text);
                if (!string.IsNullOrEmpty(piece))
                    foreach (LiteRtStreamChunk c in SplitMessageChunk(piece, state.ToolCallChannel))
                        state.Channel.Writer.TryWrite(c);
            }

            if (isFinal)
                state.Channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            // Fault the channel so the consumer gets the real exception and the teardown's drain
            // still terminates. TryComplete is first-wins, so an already-completed channel ignores it.
            state.Channel.Writer.TryComplete(ex is LiteRtException ? ex : new LiteRtException(
                $"The streaming callback failed reading the native stream chunk: {ex.Message} " +
                "(a managed/native version mismatch is the most likely cause — the v0.15.0+ managed " +
                "binding requires the same-version native library).", ex));
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

    private sealed class StreamState(Channel<LiteRtStreamChunk> channel, string? toolCallChannel)
    {
        public readonly Channel<LiteRtStreamChunk> Channel = channel;
        public readonly string? ToolCallChannel = toolCallChannel;
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
