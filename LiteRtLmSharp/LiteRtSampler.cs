namespace LiteRtLmSharp;

/// <summary>Token sampling strategy. Mirrors LiteRT-LM's <c>LiteRtLmSamplerType</c>.</summary>
public enum LiteRtSamplerType
{
    /// <summary>Let the engine choose its default: the binding sends <b>no</b> sampler parameters at
    /// all, so the executor's internal default sampling applies and the other
    /// <see cref="LiteRtSamplerParams"/> fields are not forwarded. (Native v0.14.0 removed its
    /// unspecified sampler type, and v0.18.0 declares it again; not sending parameters preserves the
    /// pre-v0.14.0 behavior, where the unspecified type also deferred to the executor.)</summary>
    Unspecified = 0,
    /// <summary>Sample probabilistically among the top-k tokens. Sent to the runtime as
    /// <see cref="TopP"/> with <see cref="LiteRtSamplerParams.TopP"/> = 1, which is the same sampling
    /// (see <see cref="LiteRtSamplerParams"/>).</summary>
    TopK = 1,
    /// <summary>Nucleus (top-p) sampling after a top-k cut. The one type every LiteRT-LM backend
    /// builds.</summary>
    TopP = 2,
    /// <summary>Always pick the highest-probability token (argmax). Sent to the runtime as
    /// <see cref="TopP"/> with <see cref="LiteRtSamplerParams.TopK"/> = 1, which is exact argmax whatever the
    /// temperature and seed (see <see cref="LiteRtSamplerParams"/>).</summary>
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
/// <b>How strategies reach the runtime.</b> The LiteRT-LM CPU backend builds only TopP samplers (a Greedy or
/// TopK session fails its first send with <c>UNIMPLEMENTED</c>), and the GPU backend runs every type as TopP.
/// The binding therefore sends <see cref="LiteRtSamplerType.Greedy"/> as TopP with <see cref="TopK"/> = 1 and
/// <see cref="LiteRtSamplerType.TopK"/> as TopP with <see cref="TopP"/> = 1, changing only the field that
/// defines the strategy, so each strategy behaves as named on every backend.
/// </para>
/// <para>
/// <b>Per conversation and per engine.</b> Measured on LiteRT-LM v0.18.0: each conversation's parameters
/// (TopK, TopP, temperature and seed) take effect, also on an engine where other conversations already
/// generated, but the runtime fixes the sampler <i>type</i> per engine at its first decode. With the mapping
/// above every explicit strategy is TopP, so this only matters next to a conversation that used the model's
/// own sampler (<see cref="LiteRtSamplerType.Unspecified"/>).
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
