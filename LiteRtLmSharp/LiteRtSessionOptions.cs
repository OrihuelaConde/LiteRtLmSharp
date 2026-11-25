namespace LiteRtLmSharp;

/// <summary>Settings for <see cref="LiteRtEngine.CreateSession"/>, fixed for the session's lifetime.</summary>
/// <remarks>A session always runs with the prompt template off: the text it prefills reaches the tokenizer as
/// given. See <see cref="LiteRtSession.Prefill"/> for the position that the runtime adds before the first
/// prefill.</remarks>
public sealed record LiteRtSessionOptions
{
    /// <summary>Gets the sampling settings; <c>null</c> uses the engine's default sampling.</summary>
    /// <remarks>
    /// <para>
    /// The session sends them the same way a conversation sends <see cref="LiteRtConversationOptions.Sampler"/>:
    /// <c>null</c> and <see cref="LiteRtSamplerType.Unspecified"/> send no sampler parameters, and any other
    /// strategy sends all four fields.
    /// </para>
    /// <para>
    /// The engine's default is not the default of a new <see cref="LiteRtSamplerParams"/>. On gemma-4 E2B,
    /// <c>null</c> gave the same reply as <see cref="LiteRtSamplerType.Greedy"/>, and
    /// <c>new LiteRtSamplerParams()</c>, which sends TopP sampling, gave another reply (measured on LiteRT-LM
    /// v0.20.0, win-x64, CPU and GPU, with one prompt). Other models were not measured.
    /// </para>
    /// </remarks>
    public LiteRtSamplerParams? Sampler { get; init; }

    private readonly int _maxOutputTokens;

    /// <summary>Gets the maximum number of tokens one decode generates; 0 sends no cap to the runtime.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaxOutputTokens
    {
        get => _maxOutputTokens;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxOutputTokens = value;
        }
    }
}
