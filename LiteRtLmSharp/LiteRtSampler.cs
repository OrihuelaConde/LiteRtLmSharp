namespace LiteRtLmSharp;

/// <summary>Token sampling strategy. Mirrors LiteRT-LM's <c>LiteRtLmSamplerType</c>.</summary>
public enum LiteRtSamplerType
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

/// <summary>
/// Sampler parameters for a conversation. Construct one and set it on
/// <see cref="LiteRtConversationOptions.Sampler"/>. A constructed instance sends <b>all</b> fields to
/// the engine (each unset field uses its default below), so set the ones you care about and leave the
/// rest at their defaults.
/// </summary>
public sealed record LiteRtSamplerParams
{
    /// <summary>The sampling strategy. Default <see cref="LiteRtSamplerType.TopP"/>.</summary>
    public LiteRtSamplerType Strategy { get; init; } = LiteRtSamplerType.TopP;

    private readonly int _topK = 40;

    /// <summary>Top-k cutoff: how many of the highest-probability tokens to consider. Must be
    /// non-negative (0 lets the engine decide). Default 40.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is negative.</exception>
    public int TopK
    {
        get => _topK;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _topK = value;
        }
    }

    private readonly float _topP = 0.95f;

    /// <summary>Nucleus (top-p) probability mass, in the range [0, 1]. Default 0.95.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is outside [0, 1].</exception>
    public float TopP
    {
        get => _topP;
        init
        {
            if (value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value), value, "TopP must be in the range [0, 1].");
            _topP = value;
        }
    }

    private readonly float _temperature = 1.0f;

    /// <summary>Sampling temperature: higher is more random, 0 is greedy/argmax. Must be non-negative.
    /// Default 1.0.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is negative.</exception>
    public float Temperature
    {
        get => _temperature;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _temperature = value;
        }
    }

    /// <summary>Random seed for reproducible sampling. <c>null</c> (default) reseeds randomly for each
    /// conversation, so output varies between runs; set a value to make sampling deterministic.</summary>
    public int? Seed { get; init; }
}
