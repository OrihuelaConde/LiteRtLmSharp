namespace LiteRtLmSharp;

/// <summary>Options for creating a <see cref="LiteRtEngine"/>.</summary>
public sealed record LiteRtEngineOptions
{
    /// <summary>Path to the <c>.litertlm</c> (or <c>.task</c>) model file. Must be set —
    /// <see cref="LiteRtEngine.Load"/> throws <see cref="System.ArgumentException"/> when it is empty.
    /// (Deliberately not <c>required</c>: future native versions add alternate model sources — e.g.
    /// loading from a file descriptor — which will land here as sibling properties.)</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>Backend to run the model on. Defaults to <see cref="LiteRtBackend.Cpu"/>. Use
    /// <see cref="LiteRtBackend.Gpu"/>, <see cref="LiteRtBackend.Npu"/>, or
    /// <see cref="LiteRtBackend.Custom"/> for a backend exposed by your own native build.</summary>
    public LiteRtBackend Backend { get; init; } = LiteRtBackend.Cpu;

    /// <summary>
    /// Backend for the vision encoder, enabling <b>image</b> input. <c>null</c> (default) leaves vision
    /// unconfigured — image attachments will not work. Requires a multimodal model (e.g. the Gemma 4
    /// E-series); on a text-only model setting this has no effect.
    /// </summary>
    /// <remarks>
    /// Maps to the <c>vision_backend_str</c> parameter of the C API <c>engine_settings_create</c>.
    /// May differ from <see cref="Backend"/> (e.g. main on <see cref="LiteRtBackend.Gpu"/>, vision on
    /// <see cref="LiteRtBackend.Cpu"/>). Pair with image attachments via
    /// <see cref="LiteRtAttachment.Image(System.ReadOnlySpan{byte})"/> and tune the image prefill budget
    /// with <see cref="LiteRtConversationOptions.VisualTokenBudget"/>.
    /// </remarks>
    public LiteRtBackend? VisionBackend { get; init; }

    /// <summary>
    /// Backend for the audio encoder, enabling <b>audio</b> input. <c>null</c> (default) leaves audio
    /// unconfigured — audio attachments will not work. Requires a model with audio support (e.g. the
    /// Gemma 4 E-series); a no-op on models without it.
    /// </summary>
    /// <remarks>
    /// A model may constrain which backend its audio encoder accepts. The Gemma 4 audio sub-model
    /// requires <b>CPU</b>: passing <see cref="LiteRtBackend.Gpu"/> makes <c>litert_lm_engine_create</c>
    /// fail with "Audio backend constraint mismatch. Model requires one of [cpu]" even when
    /// <see cref="Backend"/> is GPU — on any platform, not a win-x64 quirk. Use
    /// <see cref="LiteRtBackend.Cpu"/> for such models; the vision encoder has no such constraint and
    /// runs on GPU. Maps to the <c>audio_backend_str</c> parameter of the C API
    /// <c>engine_settings_create</c>.
    /// </remarks>
    public LiteRtBackend? AudioBackend { get; init; }

    /// <summary>
    /// The total context window in tokens: prompt + generated replies, accumulated across every turn of
    /// a conversation. 0 = engine default.
    /// </summary>
    /// <remarks>
    /// This is the hard ceiling for a conversation's KV cache. Size it for the longest exchange you
    /// expect — the system prompt, any restored <see cref="LiteRtConversationOptions.History"/>, and each
    /// user turn and reply all count against it and accumulate. As the running total
    /// (<see cref="LiteRtConversation.TokenCount"/>) nears the limit, generation degrades, so manage the
    /// conversation before then: trim history, cap replies with
    /// <see cref="LiteRtConversationOptions.MaxOutputTokens"/>, or start a fresh conversation. A larger
    /// window costs more memory and a slower prefill, so prefer the smallest that fits your use case. For
    /// <b>multimodal</b> input leave room for the media on top of the text — an image expands to roughly
    /// 256 vision tokens — for which 4096 is a comfortable starting point.
    /// <para><b>Setting this also arms the binding's KV overflow guard.</b> The native runtime does not
    /// police the limit: a send that grows past it writes beyond the allocated cache and corrupts native
    /// memory (typically a deferred <c>0xC0000005</c> process crash on a later call). With an explicit
    /// value here, the send methods instead clamp each reply to the remaining context and throw
    /// <see cref="LiteRtContextOverflowException"/> when the conversation is full. <b>The clamp cannot
    /// cover sends whose prefill cost is unmeasurable managed-side</b> — media attachments and
    /// <c>SendRaw</c>'s per-send <c>extraContext</c> get only the conversation-full check, so keep extra
    /// headroom under the limit on those paths (an image is roughly 256 tokens). With 0 the effective
    /// limit is internal to the engine (the C API exposes no getter), so the guard stays off.</para>
    /// </remarks>
    public int MaxNumTokens { get; init; }

    /// <summary>
    /// Maximum number of images the engine accepts per turn. 0 = engine default. <b>On the standard
    /// binaries this setting has no effect</b> — see the remarks for the case where it applies. To bound
    /// how much of the context window images consume, use
    /// <see cref="LiteRtConversationOptions.VisualTokenBudget"/> instead.
    /// </summary>
    /// <remarks>
    /// The native library compiles several engine implementations into one binary and picks one per
    /// backend. This value always reaches the engine settings, but only the <i>legacy TFLite</i>
    /// implementation reads it; the standard binaries select the modern CompiledModel engines for
    /// CPU/GPU, which ignore it. It matters only when a custom native build routes your backend
    /// (e.g. one targeted via <see cref="LiteRtBackend.Custom"/>) through the legacy engine. Kept for
    /// parity with the reference Kotlin binding's <c>maxNumImages</c>. Maps to
    /// <c>engine_settings_set_max_num_images</c>.
    /// </remarks>
    public int MaxNumImages { get; init; }

    /// <summary>
    /// Where the engine keeps its compiled-artifact cache (GPU shaders / converted weights), which
    /// speeds up subsequent loads. Defaults to <see cref="LiteRtCache.Default"/> (written next to the
    /// model file). Use <see cref="LiteRtCache.Disabled"/>, <see cref="LiteRtCache.InMemory"/>, or
    /// <see cref="LiteRtCache.Directory"/> for an explicit path.
    /// </summary>
    /// <remarks>
    /// Set this to <see cref="LiteRtCache.Disabled"/> to make <see cref="EnableSpeculativeDecoding"/>
    /// work on the desktop <b>WebGPU</b> GPU backend: with the default disk cache the MTP drafter's
    /// shared weight-cache file fails to open ("Access denied") on Windows and engine creation fails.
    /// This is an upstream issue (Google's own <c>litert-lm</c> CLI fails the same way with
    /// <c>--cache disk</c> and succeeds with <c>--cache no</c>); see <c>docs/speculative-decoding.md</c>.
    /// </remarks>
    public LiteRtCache Cache { get; init; }

    /// <summary>
    /// Enable speculative decoding — the model drafts several tokens ahead with a small
    /// Multi-Token-Prediction (MTP) drafter and the main model verifies them in one step,
    /// giving a large decode-throughput win (≈3× on supported models, per LiteRT-LM#2211).
    /// </summary>
    /// <remarks>
    /// Requires a <c>.litertlm</c> that ships an MTP drafter (e.g. the Gemma 4 E2B/E4B/12B
    /// builds). On a model without one the flag is a no-op (no speedup, no error). The setting
    /// is fixed at engine creation. Pair with <see cref="EnableBenchmark"/> to measure the gain
    /// (see <see cref="LiteRtConversation.GetBenchmarkInfo"/>).
    /// <para>
    /// Backend caveats (measured against LiteRT-LM v0.13.1 — see
    /// <c>docs/speculative-decoding.md</c>): the win comes from memory-bound accelerator decode.
    /// On desktop <b>CPU</b> it can REGRESS throughput (the drafter + verification overhead is not
    /// amortized). On the desktop <b>WebGPU</b> GPU backend it works, but only with the disk cache
    /// disabled — set <see cref="Cache"/> = <see cref="LiteRtCache.Disabled"/>, otherwise the drafter's
    /// shared weight-cache file fails to open ("Access denied") and engine creation fails (an
    /// upstream issue that reproduces in Google's own CLI).
    /// </para>
    /// </remarks>
    public bool EnableSpeculativeDecoding { get; init; }

    /// <summary>
    /// Enable benchmark instrumentation so <see cref="LiteRtConversation.GetBenchmarkInfo"/>
    /// returns prefill/decode tokens-per-second, time-to-first-token and init time. Fixed at
    /// engine creation; the overhead is timing bookkeeping only.
    /// </summary>
    public bool EnableBenchmark { get; init; }

    /// <summary>
    /// Whether the engine loads the <c>.litertlm</c> file's sections in parallel during startup.
    /// <c>null</c> (default) leaves the engine default (on). When on, the tokenizer section is parsed on
    /// a background thread while the model is built, shortening cold-start init; set <c>false</c> to load
    /// it serially on the calling thread (single-threaded environments, or to avoid the brief concurrent
    /// init peak, at the cost of a slower start). Maps to
    /// <c>engine_settings_set_parallel_file_section_loading</c>. See <c>docs/engine-tuning.md</c>.
    /// </summary>
    public bool? ParallelFileSectionLoading { get; init; }

    /// <summary>
    /// Activation tensor precision. <c>null</c> (default) uses the engine default (F16 for the text
    /// executor on GPU). <b>Only the GPU backend honors this, and only as F32 vs F16</b>:
    /// <see cref="LiteRtActivationDataType.Float32"/> is higher precision at more memory and lower speed,
    /// <see cref="LiteRtActivationDataType.Float16"/> is the faster default. On CPU it is a <b>no-op</b>,
    /// and <see cref="LiteRtActivationDataType.Int16"/> / <see cref="LiteRtActivationDataType.Int8"/> are
    /// accepted by the native API but not distinctly implemented by the shipped executors (folded to F16
    /// on GPU). Maps to <c>engine_settings_set_activation_data_type</c>. See <c>docs/engine-tuning.md</c>.
    /// </summary>
    public LiteRtActivationDataType? ActivationDataType { get; init; }

    /// <summary>
    /// Maximum prompt tokens prefilled per step. 0 (default) = no chunking (the whole prompt is
    /// prefilled at once). <b>CPU + dynamic models only</b> — ignored on GPU and on static models. A
    /// smaller chunk lowers peak memory during prefill and allows more timely cancellation of a long
    /// prompt, at the cost of more prefill iterations (potentially slower). Maps to
    /// <c>engine_settings_set_prefill_chunk_size</c>. See <c>docs/engine-tuning.md</c>.
    /// </summary>
    public int PrefillChunkSize { get; init; }

    /// <summary>
    /// Synthetic prefill token count for benchmarking. 0 (default) = off (normal inference).
    /// </summary>
    /// <remarks>
    /// When &gt; 0 the engine runs a <b>synthetic benchmark</b> instead of answering: the real prompt is
    /// truncated or padded to exactly this many tokens for prefill, and decoding runs exactly
    /// <see cref="BenchmarkDecodeTokens"/> tokens (ignoring the stop token). So
    /// <see cref="LiteRtConversation.GetBenchmarkInfo"/> reports prefill/decode throughput at FIXED token
    /// counts — independent of the prompt — which is useful for device throughput benchmarking and for
    /// measuring the effect of the tuning settings reproducibly. The reply text is <b>not</b> a real
    /// answer. Setting either this or <see cref="BenchmarkDecodeTokens"/> also turns benchmark mode on
    /// (the same switch as <see cref="EnableBenchmark"/>). Use a dedicated engine instance — do not reuse
    /// it for real chat. Verified observable through the Conversation API on win-x64 CPU (the default
    /// engine reads these during prefill/decode). Maps to <c>engine_settings_set_num_prefill_tokens</c>.
    /// </remarks>
    public int BenchmarkPrefillTokens { get; init; }

    /// <summary>
    /// Synthetic decode token count for benchmarking — the decode half of
    /// <see cref="BenchmarkPrefillTokens"/>. 0 (default) = off. See <see cref="BenchmarkPrefillTokens"/>
    /// for the full behavior. Maps to <c>engine_settings_set_num_decode_tokens</c>.
    /// </summary>
    public int BenchmarkDecodeTokens { get; init; }

    private readonly int? _numThreads;

    /// <summary>
    /// Number of CPU threads for the <b>text</b> executor. <c>null</c> (default) leaves the engine
    /// default. <b>CPU-backend only</b>: the native setter reads the executor's <c>CpuConfig</c> and is a
    /// no-op when the text backend is not CPU (verified in engine.cc <c>set_num_threads</c>). Maps to
    /// <c>engine_settings_set_num_threads</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? NumThreads
    {
        get => _numThreads;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _numThreads = value;
        }
    }

    private readonly int? _audioNumThreads;

    /// <summary>
    /// Number of CPU threads for the <b>audio</b> executor. <c>null</c> (default) leaves the engine
    /// default. Only applies when an audio executor is configured (<see cref="AudioBackend"/> set), and
    /// only reaches the audio backend's CPU compilation options — a no-op otherwise (verified in
    /// engine.cc <c>set_audio_num_threads</c> → audio_executor CPU options). Maps to
    /// <c>engine_settings_set_audio_num_threads</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? AudioNumThreads
    {
        get => _audioNumThreads;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _audioNumThreads = value;
        }
    }

    private readonly int? _loraRank;

    /// <summary>
    /// LoRA rank for the <b>text</b> executor. <c>null</c> (default) leaves the engine default (LoRA
    /// disabled). Requires a LoRA-enabled model and a matching adapter passed per conversation via
    /// <see cref="LiteRtConversationOptions.LoraPath"/>. Maps to <c>engine_settings_set_lora_rank</c>.
    /// </summary>
    /// <remarks>The full LoRA path (rank here + adapter file on the conversation) has not yet been
    /// validated end-to-end in this binding — no LoRA adapter artifact was available at the time of
    /// writing — so treat it as wired-through but unverified against a real adapter.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? LoraRank
    {
        get => _loraRank;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _loraRank = value;
        }
    }

    private readonly IReadOnlyList<int>? _supportedLoraRanks;

    /// <summary>
    /// The set of LoRA ranks the <b>text</b> executor should support (for switching adapters of different
    /// ranks). <c>null</c> (default) leaves the engine default. <b>Only honored on the GPU (Artisan)
    /// backend</b>: on other backends the native layer accepts and silently ignores it (verified in
    /// <c>llm_executor_settings.h SetSupportedLoraRanks</c>). Every element must be positive and the list
    /// non-empty. Maps to <c>engine_settings_set_supported_lora_ranks</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Any element is zero or negative.</exception>
    public IReadOnlyList<int>? SupportedLoraRanks
    {
        get => _supportedLoraRanks;
        init
        {
            ValidateRanks(value);
            _supportedLoraRanks = value;
        }
    }

    private readonly int? _audioLoraRank;

    /// <summary>
    /// LoRA rank for the <b>audio</b> executor. <c>null</c> (default) leaves the engine default. Only
    /// applies when an audio executor is configured (<see cref="AudioBackend"/> set). Maps to
    /// <c>engine_settings_set_audio_lora_rank</c>. See <see cref="LoraRank"/> for the not-yet-validated
    /// caveat.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? AudioLoraRank
    {
        get => _audioLoraRank;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _audioLoraRank = value;
        }
    }

    private readonly IReadOnlyList<int>? _supportedAudioLoraRanks;

    /// <summary>
    /// The set of LoRA ranks the <b>audio</b> executor should support. <c>null</c> (default) leaves the
    /// engine default. Requires an audio executor (<see cref="AudioBackend"/> set); see
    /// <see cref="SupportedLoraRanks"/> for the GPU-backend caveat. Every element must be positive and the
    /// list non-empty. Maps to <c>engine_settings_set_supported_audio_lora_ranks</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Any element is zero or negative.</exception>
    public IReadOnlyList<int>? SupportedAudioLoraRanks
    {
        get => _supportedAudioLoraRanks;
        init
        {
            ValidateRanks(value);
            _supportedAudioLoraRanks = value;
        }
    }

    private readonly int? _gpuDecodeStepsPerSync;

    /// <summary>
    /// Decode steps the GPU runs between host syncs. <c>null</c> (default) = engine default. Only
    /// honored by the Artisan GPU backend (the mobile GPU stack); the desktop WebGPU backend ignores
    /// it. Requires native LiteRT-LM v0.15.0+.
    /// </summary>
    /// <remarks>Maps to <c>engine_settings_set_gpu_decode_steps_per_sync</c>. Larger values reduce
    /// sync overhead but coarsen streaming/cancellation granularity.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? GpuDecodeStepsPerSync
    {
        get => _gpuDecodeStepsPerSync;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _gpuDecodeStepsPerSync = value;
        }
    }

    /// <summary>
    /// Whether engine load blocks until GPU weight uploads complete. <c>null</c> (default) = engine
    /// default. Only honored by the Artisan GPU backend; the desktop WebGPU backend ignores it.
    /// Requires native LiteRT-LM v0.15.0+.
    /// </summary>
    /// <remarks>Maps to <c>engine_settings_set_gpu_wait_for_weight_uploads</c>.</remarks>
    public bool? GpuWaitForWeightUploads { get; init; }

    /// <summary>
    /// Store the KV cache of local-attention layers in ringbuffers sized to what those layers
    /// actually need, instead of allocating the full context length — lower memory for long
    /// contexts, at the cost of instant rewinding. <c>null</c> (default) = off. Backend-agnostic in
    /// interface but currently only the Artisan GPU backend implements it; unsupported
    /// models/backends log a native warning and ignore it. Requires native LiteRT-LM v0.15.0+.
    /// </summary>
    /// <remarks>Maps to <c>engine_settings_set_use_ringbuffers_local_attention</c> (the knob the JS
    /// API exposes as <c>use_autosized_ringbuffers</c>).</remarks>
    public bool? UseRingbuffersLocalAttention { get; init; }

    private static void ValidateRanks(IReadOnlyList<int>? ranks)
    {
        if (ranks is null)
            return;
        if (ranks.Count == 0)
            throw new ArgumentException("The supported LoRA ranks list must not be empty (use null to leave it unset).", nameof(ranks));
        foreach (int r in ranks)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(r);
    }
}

/// <summary>
/// Activation tensor precision for <see cref="LiteRtEngineOptions.ActivationDataType"/>, mirroring
/// upstream's <c>ActivationDataType</c> enum. Only <see cref="Float32"/> and <see cref="Float16"/> are
/// distinctly honored, and only on the GPU backend (see
/// <see cref="LiteRtEngineOptions.ActivationDataType"/>).
/// </summary>
public enum LiteRtActivationDataType
{
    /// <summary>32-bit float — higher precision, more memory, slower (GPU).</summary>
    Float32 = 0,

    /// <summary>16-bit float — the faster GPU default for the text executor.</summary>
    Float16 = 1,

    /// <summary>16-bit integer — present for parity with the native enum, but not distinctly implemented
    /// by the shipped executors (folded to F16 on GPU, ignored on CPU).</summary>
    Int16 = 2,

    /// <summary>8-bit integer — present for parity with the native enum, but not distinctly implemented
    /// by the shipped executors (folded to F16 on GPU, ignored on CPU).</summary>
    Int8 = 3,
}

/// <summary>
/// Per-conversation options: system prompt, sampler, output limit, and tools (function calling).
/// Requires native binaries version-matched to the bindings (the official builds from this repo's
/// releases are); see <c>docs/native-abi.md</c> for the ABI history.
/// </summary>
public sealed record LiteRtConversationOptions
{
    /// <summary>Optional system prompt applied to the conversation.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>Optional sampler parameters. Null = engine default.</summary>
    public LiteRtSamplerParams? Sampler { get; init; }

    /// <summary>Maximum output tokens per response. 0 = engine default.</summary>
    public int MaxOutputTokens { get; init; }

    /// <summary>Tools the model may call (function calling). Null/empty = no tools.</summary>
    public IReadOnlyList<LiteRtTool>? Tools { get; init; }

    /// <summary>
    /// Conversation history to restore (or a few-shot preface) — the prior turns are re-prefilled into
    /// the KV cache when the conversation is created, so the model continues as if they had just
    /// happened. Build the list with <see cref="LiteRtMessage"/> factories and capture assistant turns
    /// with <see cref="LiteRtResponse.ToMessage"/>; persist and reload via
    /// <see cref="LiteRtMessage.Serialize"/> / <see cref="LiteRtMessage.Deserialize"/>.
    /// </summary>
    /// <remarks>
    /// The C API has no history getter, so the round-trip is caller-owned: record each turn yourself.
    /// Restoring is a <i>replay through prefill</i>, not a zero-cost snapshot — it costs a prefill of the
    /// history and counts against <see cref="LiteRtEngineOptions.MaxNumTokens"/>. Keep the system prompt
    /// in <see cref="SystemMessage"/> OR as a leading <see cref="LiteRtMessageRole.System"/> message, not
    /// both (the native side prepends <see cref="SystemMessage"/> before the history). Takes precedence
    /// over <see cref="HistoryJson"/> when it is non-empty. Maps to the C API
    /// <c>conversation_config_set_messages</c>.
    /// </remarks>
    public IReadOnlyList<LiteRtMessage>? History { get; init; }

    /// <summary>
    /// Raw escape hatch for <see cref="History"/>: the messages as a JSON <b>array</b> string (the format
    /// <see cref="LiteRtMessage.Serialize"/> produces). Use it to pass persisted history verbatim without
    /// re-parsing into typed messages, or to include content the typed model does not cover yet (e.g.
    /// image/audio parts). Validated as a JSON array when the conversation is created — throws
    /// <see cref="ArgumentException"/> otherwise. Ignored when <see cref="History"/> is non-empty.
    /// </summary>
    public string? HistoryJson { get; init; }

    /// <summary>
    /// Force the model to emit valid (schema-constrained) output. Strongly recommended when
    /// <see cref="Tools"/> are set so tool-call arguments parse reliably.
    /// </summary>
    /// <remarks>
    /// Temporarily throws <see cref="PlatformNotSupportedException"/> on linux-x64: the
    /// upstream prebuilt constraint provider shipped with LiteRT-LM v0.13.1 returns broken
    /// constraints and crashes the native process (google-ai-edge/LiteRT-LM#2149). Tools work
    /// with this set to <c>false</c>. The guard is removed once upstream ships a fixed binary.
    /// </remarks>
    public bool EnableConstrainedDecoding { get; init; }

    /// <summary>
    /// Toggles the model's reasoning ("thinking") mode by setting <c>enable_thinking</c> in the
    /// conversation's <see cref="ExtraContext"/>. <c>null</c> (default) leaves it unset so the
    /// model uses its own default; <c>true</c>/<c>false</c> force reasoning on/off.
    /// </summary>
    /// <remarks>
    /// This is the canonical use of extra context for the Gemma reasoning builds: the chat
    /// template branches on <c>{% if enable_thinking %}</c>. On a model whose template does not
    /// reference the key it is a harmless no-op. Pair with <see cref="FilterThinkingFromKvCache"/>
    /// to keep the (often long) reasoning out of the KV cache on later turns. When both this and
    /// <see cref="ExtraContext"/> set <c>enable_thinking</c>, this flag wins.
    /// </remarks>
    public bool? EnableThinking { get; init; }

    private readonly int? _thinkingTokenBudget;

    /// <summary>
    /// Caps how many tokens the model may spend on its thinking block per reply: the decode loop
    /// tracks the model's thinking start/end token ids and cuts the block off at the budget.
    /// <c>null</c> (default) = no cap; <c>-1</c> = explicitly infinite. Only meaningful with
    /// <see cref="EnableThinking"/> on (setting a budget does not itself turn thinking on).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="MaxOutputTokens"/> — which caps the whole reply and starves the answer when
    /// thinking runs long — this bounds only the reasoning share. Maps to the C API thinking config
    /// (<c>litert_lm_thinking_config_*</c>, native v0.15.0+; older binaries throw
    /// <see cref="System.EntryPointNotFoundException"/> at conversation creation). The per-send
    /// <see cref="LiteRtSendOptions.ThinkingTokenBudget"/> overrides this for one send.
    /// </remarks>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is negative and not -1.</exception>
    public int? ThinkingTokenBudget
    {
        get => _thinkingTokenBudget;
        init
        {
            if (value is { } v && v < -1)
                throw new ArgumentOutOfRangeException(nameof(value), v, "ThinkingTokenBudget must be positive, 0, or -1 (infinite).");
            _thinkingTokenBudget = value;
        }
    }

    /// <summary>
    /// Overrides the chat prompt template for this conversation with a custom Jinja template string.
    /// <c>null</c> (default) = the template the model file (or engine) provides. For advanced use —
    /// a template that does not match what the model was trained on degrades output quality.
    /// </summary>
    /// <remarks>Maps to the C API <c>conversation_config_set_prompt_template</c> (native v0.15.0+).
    /// Inspect the result with <see cref="LiteRtConversation.RenderMessage"/> /
    /// <see cref="LiteRtConversation.RenderPreface"/> before relying on it.</remarks>
    public string? PromptTemplate { get; init; }

    /// <summary>
    /// Enables <b>custom</b> constrained decoding for this conversation: with a provider set, each
    /// send may carry a <see cref="LiteRtSendOptions.Constraint"/> (regex / JSON Schema) that the
    /// provider enforces during sampling. <c>null</c> (default) = no custom constraints.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="EnableConstrainedDecoding"/> (the tool-calling
    /// constrained-decoding path) — the native runtime supports one per conversation, and
    /// <see cref="LiteRtEngine.CreateConversation"/> throws <see cref="ArgumentException"/> when both
    /// are set. Maps to the C API <c>conversation_config_set_constraint_provider</c> (native
    /// v0.15.0+). Unlike the tool-calling path, the LlGuidance provider is compiled from source into
    /// the native library, so it does not inherit the linux-x64 prebuilt-provider crash guard.
    /// </remarks>
    public LiteRtConstraintProvider? ConstraintProvider { get; init; }

    /// <summary>
    /// Extra context merged into the conversation preface and passed to the prompt-template
    /// renderer — a raw JSON <b>object</b> string, e.g. <c>{"user_name":"Alice"}</c>. Template
    /// variables are referenced Jinja-style (<c>{{ user_name }}</c>). Null/empty = none.
    /// </summary>
    /// <remarks>
    /// For the common <c>enable_thinking</c> toggle prefer the typed <see cref="EnableThinking"/>;
    /// this is the general escape hatch for arbitrary template variables. Must be a JSON object —
    /// <see cref="LiteRtEngine.CreateConversation"/> throws <see cref="ArgumentException"/> otherwise
    /// (validated when the conversation is created, not when this property is set). Maps to the C API
    /// <c>conversation_config_set_extra_context</c>.
    /// </remarks>
    public string? ExtraContext { get; init; }

    /// <summary>
    /// Drop channel content — in practice the thinking channel — from the KV cache, so a long
    /// reasoning block does not eat into the context window on subsequent turns. Default
    /// <c>false</c>. Only meaningful alongside <see cref="EnableThinking"/>.
    /// </summary>
    /// <remarks>Maps to the C API <c>conversation_config_set_filter_channel_content_from_kv_cache</c>.</remarks>
    public bool FilterThinkingFromKvCache { get; init; }

    /// <summary>
    /// Stream the raw text of a tool call <b>while the model is generating it</b>. Default <c>false</c>:
    /// during a tool-call block the stream goes silent until the block completes and the parsed
    /// <see cref="LiteRtStreamChunkKind.ToolCall"/> chunk arrives whole. With this on,
    /// <see cref="LiteRtConversation.SendStreamingAsync(string, System.Threading.CancellationToken)"/>
    /// additionally yields <see cref="LiteRtStreamChunkKind.ToolCallDelta"/> chunks — incremental,
    /// unparsed fragments of the tool-call text — as they are produced, followed by the usual complete
    /// <see cref="LiteRtStreamChunkKind.ToolCall"/> chunk. Use the deltas for progress display only;
    /// act on the final parsed chunk. Only meaningful when <see cref="Tools"/> are set.
    /// </summary>
    /// <remarks>
    /// Maps to the C API <c>conversation_config_set_stream_tool_calls</c> (requires native LiteRT-LM
    /// v0.14.0+; older binaries throw <see cref="System.EntryPointNotFoundException"/> at conversation
    /// creation). The deltas do not affect the blocking <see cref="LiteRtConversation.Send(string)"/> path.
    /// </remarks>
    public bool StreamToolCalls { get; init; }

    /// <summary>
    /// Budget (in tokens) that <b>image</b> attachments may consume during prefill. 0 (default) =
    /// engine default. Lower it to cap how much of the context window an image eats on a vision model;
    /// only meaningful when sending image attachments. Applied per send via the C API
    /// <c>conversation_optional_args_set_visual_token_budget</c>.
    /// </summary>
    public int VisualTokenBudget { get; init; }

    /// <summary>
    /// Path to a <b>text</b> LoRA adapter (weights file) to apply to this conversation. <c>null</c>
    /// (default) = no adapter. Requires a LoRA-enabled model, and the engine loaded with a matching
    /// <see cref="LiteRtEngineOptions.LoraRank"/>.
    /// </summary>
    /// <remarks>
    /// The native side opens the file when the conversation is created, so a missing/unreadable path
    /// fails fast with <see cref="LiteRtException"/> at <see cref="LiteRtEngine.CreateConversation"/> —
    /// not silently at first send. Maps to the C API <c>session_config_set_lora_path</c>. <b>Not yet
    /// validated end-to-end</b> in this binding (no LoRA adapter artifact was available at the time of
    /// writing): the plumbing is in place and a bad path is reported coherently, but a successful load
    /// against a real adapter has not been exercised here.
    /// </remarks>
    public string? LoraPath { get; init; }

    /// <summary>
    /// Path to an <b>audio</b> LoRA adapter (weights file) to apply to this conversation. <c>null</c>
    /// (default) = no adapter. Requires an audio-capable, LoRA-enabled model and a matching
    /// <see cref="LiteRtEngineOptions.AudioLoraRank"/>. Opened at conversation creation (see
    /// <see cref="LoraPath"/> for the fail-fast and not-yet-validated notes). Maps to the C API
    /// <c>session_config_set_audio_lora_path</c>.
    /// </summary>
    public string? AudioLoraPath { get; init; }
}
