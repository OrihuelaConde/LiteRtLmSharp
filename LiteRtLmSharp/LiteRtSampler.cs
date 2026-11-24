namespace LiteRtLmSharp;

/// <summary>Token sampling strategy. Mirrors LiteRT-LM's <c>LiteRtLmSamplerType</c>.</summary>
public enum LiteRtSamplerType
{
    /// <summary>Let the engine choose its default: the binding sends <b>no</b> sampler parameters at
    /// all, so the engine's own default sampling applies and the other
    /// <see cref="LiteRtSamplerParams"/> fields are not forwarded.</summary>
    Unspecified = 0,
    /// <summary>Sample probabilistically among the top-k tokens. The runtime ignores
    /// <see cref="LiteRtSamplerParams.TopP"/> for this type: a TopK reply is byte-identical to a
    /// <see cref="TopP"/> reply with <see cref="LiteRtSamplerParams.TopP"/> = 1 and the same top-k,
    /// temperature and seed (see <see cref="LiteRtSamplerParams"/>).</summary>
    TopK = 1,
    /// <summary>Nucleus (top-p) sampling after a top-k cut.</summary>
    TopP = 2,
    /// <summary>Always pick the highest-probability token (argmax). The runtime ignores
    /// <see cref="LiteRtSamplerParams.Temperature"/>, <see cref="LiteRtSamplerParams.TopP"/> and
    /// <see cref="LiteRtSamplerParams.Seed"/> for this type: a Greedy reply is byte-identical to a
    /// <see cref="TopP"/> reply with <see cref="LiteRtSamplerParams.TopK"/> = 1 (see
    /// <see cref="LiteRtSamplerParams"/>).</summary>
    Greedy = 3,
}

/// <summary>
/// Sampler parameters for a conversation. Construct one and set it on
/// <see cref="LiteRtConversationOptions.Sampler"/>. A constructed instance sends <b>all</b> fields to
/// the engine — replacing any sampler parameters the model file itself carries — so set the ones you
/// care about and leave the rest at their defaults. The defaults below (<see cref="LiteRtSamplerType.TopP"/>,
/// 40, 0.95, 1.0, seed 0) are the same values Google's official bindings fill in for unset fields, so a
/// partial configuration behaves identically across the LiteRT-LM ecosystem.
/// </summary>
/// <remarks>
/// <para>
/// <b>How strategies reach the runtime.</b> The binding sends each explicit strategy and its fields as
/// given. Measured on LiteRT-LM v0.20.0 (win-x64; CPU with gemma-4 E2B, Phi-4-mini and Ministral-3-3B, GPU
/// with gemma-4 E2B), also as the first conversation on a fresh engine: a
/// <see cref="LiteRtSamplerType.Greedy"/> reply is the argmax reply whatever the temperature and seed, and a
/// <see cref="LiteRtSamplerType.TopK"/> reply is byte-identical to a <see cref="LiteRtSamplerType.TopP"/>
/// reply with <see cref="TopP"/> = 1 and the same top-k, temperature and seed.
/// <see cref="LiteRtSamplerType.Unspecified"/> leaves the choice to the engine; for a model file that
/// declares a sampler, <see cref="LiteRtModelInfo.DefaultSampler"/> shows it. No model at hand declares
/// one, so how the runtime handles a declared TopK or Greedy sampler is not measured.
/// </para>
/// <para>
/// <b>Per conversation and per engine.</b> Measured on LiteRT-LM v0.20.0 (gemma-4 E2B, win-x64, CPU and GPU):
/// a conversation's parameters take effect on a fresh engine, after a conversation with other parameters on
/// the same engine (a Greedy conversation after a sampled one returns the argmax reply, and a TopK
/// conversation after a TopP one returns the reply it gives on a fresh engine), and with two live
/// conversations whose samplers differ, sent turn by turn.
/// </para>
/// <para>
/// <b>Where the runtime decodes greedily.</b> In the LiteRT-LM v0.18.0 and v0.18.1 sources, speculative
/// decoding (<see cref="LiteRtEngineOptions.EnableSpeculativeDecoding"/>) verifies the drafted tokens
/// greedily, so only the first token of each turn uses the conversation's sampler (upstream changed this
/// after v0.18.1), and the NPU backend always decodes greedily.
/// </para>
/// </remarks>
public sealed record LiteRtSamplerParams
{
    /// <summary>The sampling strategy. Default <see cref="LiteRtSamplerType.TopP"/> (the value the
    /// official bindings use). With <see cref="LiteRtSamplerType.Unspecified"/> no sampler parameters
    /// are sent at all — the engine's own default applies and the fields below are ignored.</summary>
    public LiteRtSamplerType Strategy { get; init; } = LiteRtSamplerType.TopP;

    private readonly int _topK = 40;

    /// <summary>Top-k cutoff: how many of the highest-probability tokens to consider. Must be
    /// positive. Default 40.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int TopK
    {
        get => _topK;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _topK = value;
        }
    }

    private readonly float _topP = 0.95f;

    /// <summary>Nucleus (top-p) probability mass, in the range [0, 1]. Default 0.95.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is NaN or outside [0, 1].</exception>
    public float TopP
    {
        get => _topP;
        init
        {
            if (float.IsNaN(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value), value, "TopP must be in the range [0, 1].");
            _topP = value;
        }
    }

    private readonly float _temperature = 1.0f;

    /// <summary>Sampling temperature: higher is more random, 0 is greedy/argmax (measured: with TopP,
    /// temperature 0 gives the same replies as <see cref="LiteRtSamplerType.Greedy"/>, whatever the seed).
    /// Must be a non-negative number. Default 1.0.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is NaN or negative.</exception>
    public float Temperature
    {
        get => _temperature;
        init
        {
            if (float.IsNaN(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Temperature must be a non-negative number.");
            _temperature = value;
        }
    }

    /// <summary>
    /// Random seed for sampling. Default <c>0</c> — the same default as the engine and Google's
    /// official bindings — which makes sampling <b>deterministic</b>: the same conversation state and
    /// prompt reproduce the same reply. Pass a different value (e.g. <c>Random.Shared.Next()</c>) for
    /// output that varies between runs.
    /// </summary>
    public int Seed { get; init; }
}
