using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private bool _disposed;

    private LiteRtConversation(
        ConversationHandle conversation, ConversationConfigHandle? config, SessionConfigHandle? sessionConfig)
    {
        _conversation = conversation;
        _config = config;
        _sessionConfig = sessionConfig;
    }

    internal static unsafe LiteRtConversation Create(EngineHandle engine, LiteRtConversationOptions? options)
    {
        // TEMPORARY GUARD — remove when upstream republishes a fixed linux prebuilt.
        // The linux-x64 libGemmaModelConstraintProvider.so shipped with LiteRT-LM v0.13.1
        // returns half-initialized constraints (internal FST is NULL) and the process dies
        // with SIGSEGV on the first decode step — a managed exception beats a dead process.
        // Windows/macOS/Android providers are fine. See google-ai-edge/LiteRT-LM#2149.
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
            bool needsConfig = options is not null &&
                (options.SystemMessage is not null || options.Sampler is not null ||
                 options.MaxOutputTokens > 0 || hasTools || options.EnableConstrainedDecoding ||
                 extraContext is not null || options.FilterThinkingFromKvCache || historyJson is not null);

            if (needsConfig)
            {
                configPtr = LiteRtLmNative.litert_lm_conversation_config_create();
                if (configPtr == nint.Zero)
                    throw new LiteRtException("litert_lm_conversation_config_create returned null.");
                config = new ConversationConfigHandle(configPtr);

                if (options!.Sampler is not null || options.MaxOutputTokens > 0)
                {
                    nint sessionPtr = LiteRtLmNative.litert_lm_session_config_create();
                    if (sessionPtr == nint.Zero)
                        throw new LiteRtException("litert_lm_session_config_create returned null.");
                    sessionConfig = new SessionConfigHandle(sessionPtr);

                    if (options.MaxOutputTokens > 0)
                        LiteRtLmNative.litert_lm_session_config_set_max_output_tokens(sessionPtr, options.MaxOutputTokens);

                    if (options.Sampler is { } s)
                    {
                        var native = new LiteRtLmSamplerParams
                        {
                            Type = (LiteRtLmSamplerType)s.Type,
                            TopK = s.TopK,
                            TopP = s.TopP,
                            Temperature = s.Temperature,
                            Seed = s.Seed,
                        };
                        LiteRtLmNative.litert_lm_session_config_set_sampler_params(sessionPtr, &native);
                    }

                    LiteRtLmNative.litert_lm_conversation_config_set_session_config(configPtr, sessionPtr);
                }

                if (options.SystemMessage is not null)
                    LiteRtLmNative.litert_lm_conversation_config_set_system_message(configPtr, LiteRtJson.SystemMessage(options.SystemMessage));

                if (hasTools)
                    LiteRtLmNative.litert_lm_conversation_config_set_tools(configPtr, LiteRtJson.Tools(options.Tools!));

                if (options.EnableConstrainedDecoding)
                    LiteRtLmNative.litert_lm_conversation_config_set_enable_constrained_decoding(configPtr, true);

                if (extraContext is not null)
                    LiteRtLmNative.litert_lm_conversation_config_set_extra_context(configPtr, extraContext);

                if (options.FilterThinkingFromKvCache)
                    LiteRtLmNative.litert_lm_conversation_config_set_filter_channel_content_from_kv_cache(configPtr, true);

                if (historyJson is not null)
                    LiteRtLmNative.litert_lm_conversation_config_set_messages(configPtr, historyJson);
            }

            nint convPtr = LiteRtLmNative.litert_lm_conversation_create(engine.Ptr, configPtr);
            if (convPtr == nint.Zero)
                throw new LiteRtException("litert_lm_conversation_create returned null.");

            return new LiteRtConversation(new ConversationHandle(convPtr), config, sessionConfig);
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
    /// context is full and further generation degrades — manage history before that point.
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
    /// Returns benchmark timings (prefill/decode tokens-per-second, time-to-first-token, init
    /// time) for this conversation, or <c>null</c> when benchmarking was not enabled
    /// (<see cref="LiteRtEngineOptions.EnableBenchmark"/>) or no turn has completed yet.
    /// </summary>
    /// <remarks>
    /// The native per-turn accessors do not bounds-check their index, so this only reads a turn
    /// after confirming the corresponding turn count is &gt; 0. Throws
    /// <see cref="EntryPointNotFoundException"/> on native binaries predating the benchmark API.
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
    /// Call this only when the conversation is idle (no in-flight <see cref="SendMessageStreamingAsync"/>) —
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
        return new LiteRtConversation(new ConversationHandle(clonedPtr), config: null, sessionConfig: null);
    }

    /// <summary>Sends a user message and returns only the text answer (blocking).
    /// For function calling use <see cref="Send"/>, which also surfaces tool calls.</summary>
    public string SendMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Send(text).Text ?? string.Empty;
    }

    /// <summary>Sends a user message and returns the structured response (text or tool calls).</summary>
    public LiteRtResponse Send(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return LiteRtResponse.Parse(SendMessageRaw(LiteRtJson.UserMessage(text)));
    }

    /// <summary>
    /// Sends the results of executed tools back to the model and returns its next response.
    /// Call after a <see cref="LiteRtResponse"/> with <see cref="LiteRtResponse.IsToolCall"/> = true.
    /// </summary>
    public LiteRtResponse SendToolResults(IEnumerable<LiteRtToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return LiteRtResponse.Parse(SendMessageRaw(LiteRtJson.ToolResults(results)));
    }

    /// <summary>
    /// Low-level escape hatch: sends a raw message JSON and returns the raw response JSON.
    /// Use when you need full control over the wire format.
    /// </summary>
    public string SendMessageRaw(string messageJson, string? extraContext = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messageJson);

        nint responsePtr = LiteRtLmNative.litert_lm_conversation_send_message(
            _conversation.Ptr, messageJson, extraContext, nint.Zero);
        if (responsePtr == nint.Zero)
            throw new LiteRtException("litert_lm_conversation_send_message returned null.");

        using var response = new JsonResponseHandle(responsePtr);
        nint strPtr = LiteRtLmNative.litert_lm_json_response_get_string(response.Ptr);
        return Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
    }

    /// <summary>
    /// Sends a user message and streams the reply as <see cref="LiteRtStreamChunk"/> pieces, each
    /// tagged by <see cref="LiteRtStreamChunk.Kind"/> as answer text, reasoning ("thinking") text,
    /// or a tool call. Route on the kind rather than assuming a global order; concatenate same-kind
    /// text deltas to rebuild the answer and the thinking trace. With
    /// <see cref="LiteRtConversationOptions.EnableThinking"/> on, reasoning models emit the thinking
    /// trace before the answer. A <see cref="LiteRtStreamChunkKind.ToolCall"/> chunk only appears when
    /// the conversation was created with tools — handle it like the blocking <see cref="Send"/> loop
    /// (run the tools, then <see cref="SendToolResults"/>).
    /// <para>
    /// Requires a sound native build: the async decode thread crashed on the interim commit
    /// 032334d8, but works on release tags (verified on v0.13.1) and on community 0.12.0-a.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<LiteRtStreamChunk> SendMessageStreamingAsync(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        var channel = Channel.CreateUnbounded<LiteRtStreamChunk>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var state = new StreamState(channel);
        var gcHandle = GCHandle.Alloc(state);

        int rc;
        unsafe
        {
            rc = LiteRtLmNative.litert_lm_conversation_send_message_stream(
                _conversation.Ptr, LiteRtJson.UserMessage(text), null, nint.Zero,
                &OnStreamChunk, GCHandle.ToIntPtr(gcHandle));
        }

        if (rc != 0)
        {
            if (gcHandle.IsAllocated) gcHandle.Free();
            throw new LiteRtException($"litert_lm_conversation_send_message_stream failed with code {rc}.");
        }

        try
        {
            await foreach (LiteRtStreamChunk chunk in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            // The native streaming thread invokes the callback until is_final, which completes the
            // channel. Before freeing the GCHandle (and letting the caller dispose the conversation)
            // we MUST ensure that thread is done — otherwise it dereferences freed state / a deleted
            // conversation and segfaults. If the consumer abandoned early, cancel and then wait.
            if (!channel.Reader.Completion.IsCompleted)
            {
                LiteRtLmNative.litert_lm_conversation_cancel_process(_conversation.Ptr);
                try { await channel.Reader.Completion.ConfigureAwait(false); }
                catch { /* completion may surface the cancel error; ignore here */ }
            }
            if (gcHandle.IsAllocated)
                gcHandle.Free();
        }
    }

    /// <summary>Unmanaged streaming callback. Recovers state from the GCHandle in <paramref name="callbackData"/>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnStreamChunk(nint callbackData, nint chunk, byte isFinal, nint errorMsg)
    {
        var gcHandle = GCHandle.FromIntPtr(callbackData);
        if (gcHandle.Target is not StreamState state)
            return;

        if (errorMsg != nint.Zero)
        {
            string msg = Marshal.PtrToStringUTF8(errorMsg) ?? "unknown error";
            state.Channel.Writer.TryComplete(new LiteRtException(msg));
        }
        else if (chunk != nint.Zero)
        {
            string? piece = Marshal.PtrToStringUTF8(chunk);
            if (!string.IsNullOrEmpty(piece))
                foreach (LiteRtStreamChunk c in SplitMessageChunk(piece))
                    state.Channel.Writer.TryWrite(c);
        }

        if (isFinal != 0)
            state.Channel.Writer.TryComplete();
        // NOTE: do NOT free the GCHandle here — the async iterator's finally owns its lifetime and
        // only frees it once the channel is fully completed, so this callback can never run against
        // a freed handle.
    }

    /// <summary>
    /// Splits one streamed message-chunk JSON into tagged pieces: the reasoning ("thinking") delta
    /// first (if any), then either the tool calls (when present) or the answer-text delta. Content
    /// and channel values are per-chunk deltas; tool calls arrive complete. Internal so the split can
    /// be unit-tested without a model — the native callback just feeds it each raw chunk.
    /// </summary>
    internal static IReadOnlyList<LiteRtStreamChunk> SplitMessageChunk(string messageJson)
    {
        LiteRtResponse parsed = LiteRtResponse.Parse(messageJson);
        var chunks = new List<LiteRtStreamChunk>(capacity: 2);
        if (parsed.Thinking is { Length: > 0 } thinking)
            chunks.Add(LiteRtStreamChunk.Thinking(thinking));
        if (parsed.IsToolCall)
            chunks.Add(LiteRtStreamChunk.Tools(parsed.ToolCalls));
        else if (parsed.Text is { Length: > 0 } answer)
            chunks.Add(LiteRtStreamChunk.Answer(answer));
        return chunks;
    }

    private sealed class StreamState(Channel<LiteRtStreamChunk> channel)
    {
        public readonly Channel<LiteRtStreamChunk> Channel = channel;
    }

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
    Answer,
    /// <summary>A fragment of the reasoning ("thinking") trace.</summary>
    Thinking,
    /// <summary>One or more tool calls the model wants executed (only when the conversation has tools).</summary>
    ToolCall,
}

/// <summary>
/// One streamed piece of a reply from <see cref="LiteRtConversation.SendMessageStreamingAsync"/>.
/// <see cref="Kind"/> says what it is: an <see cref="LiteRtStreamChunkKind.Answer"/> or
/// <see cref="LiteRtStreamChunkKind.Thinking"/> text delta (concatenate same-kind chunks in order
/// to rebuild each), or a <see cref="LiteRtStreamChunkKind.ToolCall"/> carrying the model's tool
/// calls. <see cref="Text"/> is the delta for the text kinds (empty for tool calls);
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
}
