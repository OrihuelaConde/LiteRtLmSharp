namespace LiteLMSharp;

/// <summary>Token sampling strategy. Mirrors LiteRT-LM's <c>LiteRtLmSamplerType</c>.</summary>
public enum SamplerType
{
    /// <summary>Let the engine choose its default.</summary>
    Unspecified = 0,
    /// <summary>Sample probabilistically among the top-k tokens.</summary>
    TopK = 1,
    /// <summary>Nucleus (top-p) sampling after a top-k cut.</summary>
    TopP = 2,
    /// <summary>Always pick the highest-probability token (argmax).</summary>
    Greedy = 3,
}

/// <summary>Sampler parameters for a session/conversation.</summary>
public sealed record SamplerParams
{
    public SamplerType Type { get; init; } = SamplerType.TopP;
    public int TopK { get; init; } = 40;
    public float TopP { get; init; } = 0.95f;
    public float Temperature { get; init; } = 1.0f;
    public int Seed { get; init; }
}
