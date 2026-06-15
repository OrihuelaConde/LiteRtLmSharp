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

    /// <summary>Maximum context tokens for the engine. 0 = engine default.</summary>
    public int MaxNumTokens { get; init; }

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
}
