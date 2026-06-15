namespace LiteRtLmSharp;

/// <summary>
/// A snapshot of the engine's benchmark counters for one conversation, returned by
/// <see cref="LiteRtConversation.GetBenchmarkInfo"/>. Only populated when the engine was created
/// with <see cref="LiteRtEngineOptions.EnableBenchmark"/> = <c>true</c>.
/// <para>
/// Prefill and decode metrics accumulate one entry per turn; the <c>Last*</c> properties report
/// the most recent turn (the headline number for "how fast was that reply"). Decode throughput
/// is the metric that <see cref="LiteRtEngineOptions.EnableSpeculativeDecoding"/> accelerates.
/// </para>
/// </summary>
public sealed record LiteRtBenchmarkInfo
{
    /// <summary>Seconds from the start of prefill to the first decoded token (excludes init).</summary>
    public double TimeToFirstTokenSeconds { get; init; }

    /// <summary>Total engine initialization time in seconds.</summary>
    public double TotalInitTimeSeconds { get; init; }

    /// <summary>Number of prefill turns recorded so far.</summary>
    public int NumPrefillTurns { get; init; }

    /// <summary>Number of decode turns recorded so far.</summary>
    public int NumDecodeTurns { get; init; }

    /// <summary>Prefill token count of the most recent prefill turn (0 if none).</summary>
    public int LastPrefillTokenCount { get; init; }

    /// <summary>Decode token count of the most recent decode turn (0 if none).</summary>
    public int LastDecodeTokenCount { get; init; }

    /// <summary>Prefill throughput (tokens/sec) of the most recent prefill turn (0 if none).</summary>
    public double LastPrefillTokensPerSecond { get; init; }

    /// <summary>Decode throughput (tokens/sec) of the most recent decode turn (0 if none).</summary>
    public double LastDecodeTokensPerSecond { get; init; }
}
