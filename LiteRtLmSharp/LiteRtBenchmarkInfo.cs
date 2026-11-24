namespace LiteRtLmSharp;

/// <summary>
/// A snapshot of the engine's benchmark counters for one conversation, returned by
/// <see cref="LiteRtConversation.GetBenchmarkInfo"/>. Only populated when the engine was created
/// with <see cref="LiteRtEngineOptions.EnableBenchmark"/> = <c>true</c>.
/// <para>
/// Prefill and decode metrics accumulate one entry per native prefill or decode turn (a text send records one of
/// each, plus a second prefill turn after a reply with thinking; measured on LiteRT-LM v0.20.0, gemma-4 E2B, CPU and GPU); the
/// <c>Last*</c> properties report
/// the most recent turn (the headline number for "how fast was that reply"), except
/// <see cref="LastSendPrefillTokenCount"/>, which reports the last send's own message. Decode throughput
/// is the metric that <see cref="LiteRtEngineOptions.EnableSpeculativeDecoding"/> accelerates.
/// </para>
/// </summary>
public sealed record LiteRtBenchmarkInfo
{
    /// <summary>Seconds from the start of prefill to the first decoded token (excludes init).</summary>
    public double TimeToFirstTokenSeconds { get; init; }

    /// <summary>Engine initialization time in seconds.</summary>
    /// <remarks>From LiteRT-LM v0.20.0 this is the engine creation time only; LiteRT-LM v0.18.0 reported
    /// the sum of every recorded initialization phase. 0 when the runtime did not record it.</remarks>
    public double TotalInitTimeSeconds { get; init; }

    /// <summary>Number of prefill turns recorded so far.</summary>
    public int NumPrefillTurns { get; init; }

    /// <summary>Number of decode turns recorded so far.</summary>
    public int NumDecodeTurns { get; init; }

    /// <summary>Prefill token count of the most recent prefill turn (0 if none).</summary>
    /// <remarks>After a reply with thinking, the most recent prefill turn is the KV-cache refill that the runtime
    /// runs once the reply ends, not the message: <see cref="LastSendPrefillTokenCount"/> is the message's.</remarks>
    public int LastPrefillTokenCount { get; init; }

    /// <summary>Prefill token count of the most recent send's message: the first prefill turn that send recorded
    /// (0 before the first send, and after a send that the runtime rejected before its prefill). A send that the
    /// binding rejects before calling the runtime, such as one that throws
    /// <see cref="LiteRtContextOverflowException"/>, leaves the previous send's value.</summary>
    /// <remarks>A text send records one prefill turn, its message (sends with media or tool results were not
    /// measured). After a reply with thinking, the runtime records a
    /// second one, the KV-cache refill, so <see cref="LastPrefillTokenCount"/> differs from this value
    /// (measured on LiteRT-LM v0.20.0, CPU and GPU). A <see cref="LiteRtConversation.Clone"/> carries its parent's
    /// benchmark turns and reports the parent's value until it sends.</remarks>
    public int LastSendPrefillTokenCount { get; init; }

    /// <summary>Decode token count of the most recent decode turn (0 if none).</summary>
    public int LastDecodeTokenCount { get; init; }

    /// <summary>Prefill throughput (tokens/sec) of the most recent prefill turn (0 if none).</summary>
    public double LastPrefillTokensPerSecond { get; init; }

    /// <summary>Decode throughput (tokens/sec) of the most recent decode turn (0 if none).</summary>
    public double LastDecodeTokensPerSecond { get; init; }
}
