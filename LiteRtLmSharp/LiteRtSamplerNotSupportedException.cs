namespace LiteRtLmSharp;

/// <summary>
/// Raised when the engine's backend does not implement the sampler a conversation asked for. Derives from
/// <see cref="LiteRtException"/>, so a <c>catch (LiteRtException)</c> still covers it.
/// </summary>
/// <remarks>
/// The CPU backend of LiteRT-LM v0.14.0 and later implements <b>only</b> <see cref="LiteRtSamplerType.TopP"/>:
/// a conversation that asks for <see cref="LiteRtSamplerType.Greedy"/> or <see cref="LiteRtSamplerType.TopK"/>
/// is created happily and then fails its first send with <c>UNIMPLEMENTED: Sampler type: 3</c> — the numeric
/// type, never the word "unsupported" (measured on v0.18.0). This exception is raised at the moment the
/// problem is still visible, says it in words and names the strategy, so the failure is diagnosable without
/// native logs and without a capability probe that cannot exist: after any completed send on the engine the
/// runtime has established its own sampler and accepts (then ignores) further sampler parameters, so
/// "does the backend support X" has no stable answer. The conversation whose send was rejected stays
/// unusable — its later sends fail with <c>INTERNAL</c> — so continue on a fresh one.
/// <para>
/// Standard remedies: use <see cref="LiteRtSamplerParams.NearGreedy"/> (top-p 0.5, temperature 0.1 — the
/// closest supported profile to argmax, and honoured), leave the sampler at
/// <see cref="LiteRtSamplerType.Unspecified"/> so the model's own declared sampler applies, or run on a
/// backend that implements the strategy (a GPU backend on the platforms where it is available).
/// </para>
/// </remarks>
public sealed class LiteRtSamplerNotSupportedException : LiteRtException
{
    /// <summary>Creates the exception for the strategy a caller asked for (or <c>null</c> when the
    /// sampler came from the conversation/engine default rather than from this caller).</summary>
    public LiteRtSamplerNotSupportedException(
        string message, LiteRtSamplerType? samplerType, LiteRtStatusCode? statusCode = null)
        : base(message, statusCode ?? LiteRtStatusCode.Unimplemented)
        => SamplerType = samplerType;

    /// <summary>The sampler strategy that was rejected, when the binding knows which one was requested —
    /// <c>null</c> when the sampler came from the conversation or engine defaults instead of an explicit
    /// <see cref="LiteRtSamplerParams"/>, or when the runtime's message does not reveal the type.</summary>
    public LiteRtSamplerType? SamplerType { get; }
}
