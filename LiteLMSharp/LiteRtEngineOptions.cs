namespace LiteLMSharp;

/// <summary>Options for creating a <see cref="LiteRtEngine"/>.</summary>
public sealed record LiteRtEngineOptions
{
    /// <summary>Path to the <c>.litertlm</c> (or <c>.task</c>) model file. Required.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Backend to run on: <c>"cpu"</c> or <c>"gpu"</c>. Defaults to CPU.</summary>
    public string Backend { get; init; } = "cpu";

    /// <summary>Maximum context tokens for the engine. 0 = engine default.</summary>
    public int MaxNumTokens { get; init; }
}

/// <summary>
/// Per-conversation options (system prompt, sampler, output limit).
/// <para>
/// WARNING: on the current prebuilt binary (flutter_gemma <c>native-v0.12.0-a</c>) the underlying
/// <c>litert_lm_conversation_config_*</c> functions access-violate due to an ABI skew with the
/// <c>main</c> header. Until Fase 2 ships a version-matched build, prefer
/// <see cref="LiteRtEngine.CreateConversation"/> with no options. See <c>docs/native-abi.md</c>.
/// </para>
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
    public bool EnableConstrainedDecoding { get; init; }
}
