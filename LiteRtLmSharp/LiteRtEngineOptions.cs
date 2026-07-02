namespace LiteRtLmSharp;

/// <summary>Options for creating a <see cref="LiteRtEngine"/>.</summary>
public sealed record LiteRtEngineOptions
{
    /// <summary>Path to the <c>.litertlm</c> (or <c>.task</c>) model file. Required.</summary>
    public required string ModelPath { get; init; }

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
    /// Budget (in tokens) that <b>image</b> attachments may consume during prefill. 0 (default) =
    /// engine default. Lower it to cap how much of the context window an image eats on a vision model;
    /// only meaningful when sending image attachments. Applied per send via the C API
    /// <c>conversation_optional_args_set_visual_token_budget</c>.
    /// </summary>
    public int VisualTokenBudget { get; init; }
}
