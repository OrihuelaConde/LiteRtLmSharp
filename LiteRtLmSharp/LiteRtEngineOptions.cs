namespace LiteRtLmSharp;

/// <summary>Options for creating a <see cref="LiteRtEngine"/>.</summary>
public sealed record LiteRtEngineOptions
{
    /// <summary><see cref="CacheDir"/> sentinel: disable the compiled-artifact disk cache entirely.</summary>
    public const string CacheDisabled = ":nocache";

    /// <summary><see cref="CacheDir"/> sentinel: keep the compiled-artifact cache in RAM only
    /// (CPU backend only; not available on Windows).</summary>
    public const string CacheInMemory = ":memory";

    /// <summary>Path to the <c>.litertlm</c> (or <c>.task</c>) model file. Required.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Backend to run on: <c>"cpu"</c> or <c>"gpu"</c>. Defaults to CPU.</summary>
    public string Backend { get; init; } = "cpu";

    /// <summary>
    /// Backend for the vision encoder (<c>"cpu"</c> or <c>"gpu"</c>), enabling <b>image</b> input.
    /// <c>null</c> (default) leaves vision unconfigured — image attachments will not work. Requires a
    /// multimodal model (e.g. the Gemma 4 E-series); on a text-only model setting this has no effect.
    /// </summary>
    /// <remarks>
    /// Maps to the <c>vision_backend_str</c> parameter of the C API <c>engine_settings_create</c>.
    /// May differ from <see cref="Backend"/> (e.g. main on GPU, vision on CPU). Pair with image
    /// attachments via <see cref="LiteRtAttachment.Image(System.ReadOnlySpan{byte})"/> and tune the
    /// image prefill budget with <see cref="LiteRtConversationOptions.VisualTokenBudget"/>.
    /// </remarks>
    public string? VisionBackend { get; init; }

    /// <summary>
    /// Backend for the audio encoder, enabling <b>audio</b> input. <c>null</c> (default) leaves audio
    /// unconfigured — audio attachments will not work. Requires a model with audio support (e.g. the
    /// Gemma 4 E-series); a no-op on models without it.
    /// </summary>
    /// <remarks>
    /// A model may constrain which backend its audio encoder accepts. The Gemma 4 audio sub-model
    /// requires <b>CPU</b>: passing <c>"gpu"</c> makes <c>litert_lm_engine_create</c> fail with
    /// "Audio backend constraint mismatch. Model requires one of [cpu]" even when <see cref="Backend"/>
    /// is GPU — on any platform, not a win-x64 quirk (verified 2026-06-17). Use <c>"cpu"</c> for such
    /// models; the vision encoder has no such constraint and runs on GPU. Maps to the
    /// <c>audio_backend_str</c> parameter of the C API <c>engine_settings_create</c>.
    /// </remarks>
    public string? AudioBackend { get; init; }

    /// <summary>Maximum context tokens for the engine. 0 = engine default.</summary>
    /// <remarks>
    /// For <b>multimodal</b> input give the engine room for the media tokens: an image expands to a
    /// block of vision tokens (~256 for Gemma 4), so set this to <b>4096 or more</b>. A small window
    /// such as 2048 can fail to load the vision encoder — the first image send then throws
    /// "Vision executor should not be null" (validated 2026-06-17).
    /// </remarks>
    public int MaxNumTokens { get; init; }

    /// <summary>
    /// Maximum number of images the engine accepts per turn. 0 = engine default. Kept for parity with
    /// the reference Kotlin binding, which exposes the same <c>maxNumImages</c> setting (mapping a null
    /// to the <c>-1</c> "use default" sentinel). Per the C API header this only affects the
    /// <i>legacy</i> engine implementation, so the current path ignores it — prefer
    /// <see cref="LiteRtConversationOptions.VisualTokenBudget"/> to bound image cost. Maps to
    /// <c>engine_settings_set_max_num_images</c>.
    /// </summary>
    public int MaxNumImages { get; init; }

    /// <summary>
    /// Directory for the engine's compiled-artifact cache (GPU shaders / converted weights),
    /// which speeds up subsequent loads. Null/empty = write next to the model file (the engine
    /// default). A directory path = use that directory. Two special sentinels:
    /// <see cref="CacheDisabled"/> turns the disk cache off, <see cref="CacheInMemory"/> caches in RAM.
    /// </summary>
    /// <remarks>
    /// Set this to <see cref="CacheDisabled"/> to make <see cref="EnableSpeculativeDecoding"/> work
    /// on the desktop <b>WebGPU</b> GPU backend: with the default disk cache the MTP drafter's shared
    /// weight-cache file fails to open ("Access denied") on Windows and engine creation fails. This
    /// is an upstream issue (Google's own <c>litert-lm</c> CLI fails the same way with <c>--cache disk</c>
    /// and succeeds with <c>--cache no</c>); see <c>docs/speculative-decoding.md</c>.
    /// </remarks>
    public string? CacheDir { get; init; }

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
    /// disabled — set <see cref="CacheDir"/> = <see cref="CacheDisabled"/>, otherwise the drafter's
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
    public SamplerParams? Sampler { get; init; }

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
