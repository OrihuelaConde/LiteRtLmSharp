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
    /// <summary>Sample probabilistically among the top-k tokens. <b>Not implemented by the CPU backend of
    /// LiteRT-LM v0.14.0–v0.18.0</b> (see <see cref="LiteRtSamplerNotSupportedException"/>): the first send
    /// that uses an explicit sampler on a fresh engine fails with <c>UNIMPLEMENTED: Sampler type: 1</c>.</summary>
    TopK = 1,
    /// <summary>Nucleus (top-p) sampling after a top-k cut. The only strategy the CPU backend implements —
    /// prefer <see cref="LiteRtSamplerParams.NearGreedy"/> when argmax-like output is wanted.</summary>
    TopP = 2,
    /// <summary>Always pick the highest-probability token (argmax). <b>Not implemented by the CPU backend of
    /// LiteRT-LM v0.14.0–v0.18.0</b> (see <see cref="LiteRtSamplerNotSupportedException"/>): the first send
    /// that uses an explicit sampler on a fresh engine fails with <c>UNIMPLEMENTED: Sampler type: 3</c>.</summary>
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
/// <b>Which strategies actually run.</b> The CPU backend of LiteRT-LM v0.14.0–v0.18.0 implements only
/// <see cref="LiteRtSamplerType.TopP"/>. An explicit <see cref="LiteRtSamplerType.Greedy"/> or
/// <see cref="LiteRtSamplerType.TopK"/> fails the first send that uses an explicit sampler on a fresh
/// engine with <see cref="LiteRtSamplerNotSupportedException"/> — and that conversation stays unusable
/// afterwards. Once any send has completed on the engine, the runtime has established its own sampler and
/// a later request for the same strategy is accepted and silently ignored, so the requested sampler is
/// simply not applied. Prefer <see cref="NearGreedy"/> (an honoured TopP profile), or leave the strategy
/// <see cref="LiteRtSamplerType.Unspecified"/> and let the model's declared sampler apply.
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

    /// <summary>Sampling temperature: higher is more random, 0 is greedy/argmax. Must be a
    /// non-negative number. Default 1.0.</summary>
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

    /// <summary>
    /// The "almost greedy" profile: <see cref="LiteRtSamplerType.TopP"/> with <c>TopK = 40</c>,
    /// <c>TopP = 0.5</c>, <c>Temperature = 0.1</c>, deterministic seed. Use it wherever a
    /// transcription/translation-style task wants argmax-like output.
    /// </summary>
    /// <remarks>
    /// <b>Why not <see cref="LiteRtSamplerType.Greedy"/>:</b> the CPU backend of LiteRT-LM v0.14.0–v0.18.0
    /// does not implement it (nor TopK): the request fails the conversation's first send with
    /// <c>UNIMPLEMENTED: Sampler type: 3</c> (surfaced as
    /// <see cref="LiteRtSamplerNotSupportedException"/>), and once any send has completed on the engine the
    /// runtime ignores further sampler parameters instead — so the strategy is either rejected or silently
    /// not applied. TopP is the one strategy that is implemented and honoured there, and this profile is the
    /// closest thing to an argmax over it: a deliberately narrow top-p distribution with a low temperature.
    /// </remarks>
    public static LiteRtSamplerParams NearGreedy { get; } = new()
    {
        Strategy = LiteRtSamplerType.TopP,
        TopK = 40,
        TopP = 0.5f,
        Temperature = 0.1f,
        Seed = 0,
    };
}
