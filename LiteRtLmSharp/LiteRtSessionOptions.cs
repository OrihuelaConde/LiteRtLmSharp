namespace LiteRtLmSharp;

/// <summary>Settings for <see cref="LiteRtEngine.CreateSession"/>, fixed for the session's lifetime.</summary>
/// <remarks>A session always runs with the prompt template off: the text it prefills reaches the model as
/// given.</remarks>
public sealed record LiteRtSessionOptions
{
    /// <summary>Gets the sampling settings; <c>null</c> uses the engine's default sampling.</summary>
    /// <remarks>The session sends them the same way a conversation sends
    /// <see cref="LiteRtConversationOptions.Sampler"/>: <see cref="LiteRtSamplerType.Unspecified"/> sends no
    /// sampler parameters, and any other strategy sends all four fields.</remarks>
    public LiteRtSamplerParams? Sampler { get; init; }

    private readonly int _maxOutputTokens;

    /// <summary>Gets the maximum number of tokens one decode generates; 0 sets no cap other than the
    /// context.</summary>
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
