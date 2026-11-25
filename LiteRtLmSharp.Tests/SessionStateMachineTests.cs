using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The session state machine and the final-chunk classifier, without a model: every (state, call) cell of the
/// stable tier, every outcome of an accepted call, and every refusal.
/// </summary>
public sealed class SessionStateMachineTests
{
    // The internal enums travel as their names: a public test method cannot take an internal type.
    private static readonly SessionCall[] Decodes = [SessionCall.Decode, SessionCall.DecodeAsync, SessionCall.DecodeStreamingAsync];

    public static TheoryData<LiteRtSessionState, string, bool> AcceptanceTable()
    {
        var data = new TheoryData<LiteRtSessionState, string, bool>();
        foreach (LiteRtSessionState state in Enum.GetValues<LiteRtSessionState>())
        {
            bool live = state is LiteRtSessionState.Fresh or LiteRtSessionState.Prefilled or LiteRtSessionState.Decoded;
            data.Add(state, nameof(SessionCall.Prefill), live);
            foreach (SessionCall decode in Decodes)
                data.Add(state, decode.ToString(), state == LiteRtSessionState.Prefilled);
            data.Add(state, nameof(SessionCall.Dispose), true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AcceptanceTable))]
    public void Accepts_FollowsTheTable(LiteRtSessionState state, string callName, bool accepted)
    {
        SessionCall call = Enum.Parse<SessionCall>(callName);
        Assert.Equal(accepted, SessionStateMachine.Accepts(state, call));
        if (!accepted)
            Assert.Throws<InvalidOperationException>(() => SessionStateMachine.After(state, call, SessionCallOutcome.Succeeded));
    }

    /// <summary>The three end states accept nothing but Dispose.</summary>
    [Theory]
    [InlineData(LiteRtSessionState.ContextFull)]
    [InlineData(LiteRtSessionState.Faulted)]
    [InlineData(LiteRtSessionState.Cancelled)]
    public void EndStates_AcceptOnlyDispose(LiteRtSessionState state)
    {
        SessionCall[] accepted = Enum.GetValues<SessionCall>().Where(call => SessionStateMachine.Accepts(state, call)).ToArray();
        Assert.Equal([SessionCall.Dispose], accepted);
    }

    [Theory]
    [InlineData(LiteRtSessionState.Fresh, nameof(SessionCallOutcome.Succeeded), LiteRtSessionState.Prefilled)]
    [InlineData(LiteRtSessionState.Prefilled, nameof(SessionCallOutcome.Succeeded), LiteRtSessionState.Prefilled)]
    [InlineData(LiteRtSessionState.Decoded, nameof(SessionCallOutcome.Succeeded), LiteRtSessionState.Prefilled)]
    [InlineData(LiteRtSessionState.Fresh, nameof(SessionCallOutcome.Failed), LiteRtSessionState.Faulted)]
    [InlineData(LiteRtSessionState.Prefilled, nameof(SessionCallOutcome.Failed), LiteRtSessionState.Faulted)]
    [InlineData(LiteRtSessionState.Decoded, nameof(SessionCallOutcome.Failed), LiteRtSessionState.Faulted)]
    public void Prefill_MovesToItsOutcome(LiteRtSessionState from, string outcome, LiteRtSessionState to)
        => Assert.Equal(to, SessionStateMachine.After(from, SessionCall.Prefill, Enum.Parse<SessionCallOutcome>(outcome)));

    /// <summary>A prefill has no callback, so it cannot end as a full context or a cancel.</summary>
    [Theory]
    [InlineData(nameof(SessionCallOutcome.ContextFull))]
    [InlineData(nameof(SessionCallOutcome.Cancelled))]
    public void Prefill_CannotEndWithADecodeOutcome(string outcome)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionStateMachine.After(LiteRtSessionState.Fresh, SessionCall.Prefill, Enum.Parse<SessionCallOutcome>(outcome)));

    public static TheoryData<string, string, LiteRtSessionState> DecodeOutcomes()
    {
        var data = new TheoryData<string, string, LiteRtSessionState>();
        foreach (SessionCall decode in Decodes)
        {
            data.Add(decode.ToString(), nameof(SessionCallOutcome.Succeeded), LiteRtSessionState.Decoded);
            data.Add(decode.ToString(), nameof(SessionCallOutcome.ContextFull), LiteRtSessionState.ContextFull);
            data.Add(decode.ToString(), nameof(SessionCallOutcome.Cancelled), LiteRtSessionState.Cancelled);
            data.Add(decode.ToString(), nameof(SessionCallOutcome.Failed), LiteRtSessionState.Faulted);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(DecodeOutcomes))]
    public void Decode_MovesToItsOutcome(string decode, string outcome, LiteRtSessionState to)
        => Assert.Equal(to, SessionStateMachine.After(
            LiteRtSessionState.Prefilled, Enum.Parse<SessionCall>(decode), Enum.Parse<SessionCallOutcome>(outcome)));

    /// <summary>Dispose is accepted in every state and leaves the state as it was: disposal is the session's
    /// own flag, so <see cref="SessionStateMachine.State"/> still reads the last state.</summary>
    [Theory]
    [InlineData(LiteRtSessionState.Fresh)]
    [InlineData(LiteRtSessionState.Prefilled)]
    [InlineData(LiteRtSessionState.Decoded)]
    [InlineData(LiteRtSessionState.ContextFull)]
    [InlineData(LiteRtSessionState.Faulted)]
    [InlineData(LiteRtSessionState.Cancelled)]
    public void Dispose_KeepsTheState(LiteRtSessionState state)
    {
        foreach (SessionCallOutcome outcome in Enum.GetValues<SessionCallOutcome>())
            Assert.Equal(state, SessionStateMachine.After(state, SessionCall.Dispose, outcome));
    }

    public static TheoryData<LiteRtSessionState, string, string> Refusals() => new()
    {
        { LiteRtSessionState.Fresh, nameof(SessionCall.Decode), "Call Prefill first" },
        { LiteRtSessionState.Fresh, nameof(SessionCall.DecodeAsync), "DecodeAsync needs prefilled input" },
        { LiteRtSessionState.Decoded, nameof(SessionCall.DecodeStreamingAsync), "Prefill more text before the next decode" },
        { LiteRtSessionState.ContextFull, nameof(SessionCall.Prefill), "context is full" },
        { LiteRtSessionState.ContextFull, nameof(SessionCall.Decode), "context is full" },
        { LiteRtSessionState.Faulted, nameof(SessionCall.Prefill), "native call on this session failed" },
        { LiteRtSessionState.Faulted, nameof(SessionCall.DecodeAsync), "native call on this session failed" },
        { LiteRtSessionState.Cancelled, nameof(SessionCall.Prefill), "was cancelled" },
        { LiteRtSessionState.Cancelled, nameof(SessionCall.DecodeStreamingAsync), "was cancelled" },
    };

    /// <summary>A refusal names the member the caller used and what to do instead.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ThrowIfRefused_SaysWhy(LiteRtSessionState state, string callName, string expected)
    {
        SessionCall call = Enum.Parse<SessionCall>(callName);
        var machine = Drive(state);
        var ex = Assert.Throws<InvalidOperationException>(() => machine.ThrowIfRefused(call));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains(call.ToString(), ex.Message, StringComparison.Ordinal);
        if (state is LiteRtSessionState.ContextFull or LiteRtSessionState.Faulted or LiteRtSessionState.Cancelled)
            Assert.Contains("Dispose it and create a new session", ex.Message, StringComparison.Ordinal);
        Assert.Equal(state, machine.State);
    }

    [Fact]
    public void Machine_StartsFresh_AndFollowsAWholeRun()
    {
        var machine = new SessionStateMachine();
        Assert.Equal(LiteRtSessionState.Fresh, machine.State);
        machine.ThrowIfRefused(SessionCall.Prefill);
        Assert.Equal(LiteRtSessionState.Prefilled, machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded));
        machine.ThrowIfRefused(SessionCall.Decode);
        Assert.Equal(LiteRtSessionState.Decoded, machine.Complete(SessionCall.Decode, SessionCallOutcome.Succeeded));
        Assert.Throws<InvalidOperationException>(() => machine.ThrowIfRefused(SessionCall.DecodeAsync));
        Assert.Equal(LiteRtSessionState.Prefilled, machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded));
        Assert.Equal(LiteRtSessionState.ContextFull, machine.Complete(SessionCall.DecodeStreamingAsync, SessionCallOutcome.ContextFull));
        Assert.Throws<InvalidOperationException>(() => machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded));
        Assert.Equal(LiteRtSessionState.ContextFull, machine.State);
    }

    /// <summary>A machine driven into <paramref name="state"/> through accepted calls only.</summary>
    internal static SessionStateMachine Drive(LiteRtSessionState state)
    {
        var machine = new SessionStateMachine();
        if (state == LiteRtSessionState.Fresh)
            return machine;
        if (state == LiteRtSessionState.Faulted)
        {
            machine.Complete(SessionCall.Prefill, SessionCallOutcome.Failed);
            return machine;
        }
        machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded);
        SessionCallOutcome? decode = state switch
        {
            LiteRtSessionState.Decoded => SessionCallOutcome.Succeeded,
            LiteRtSessionState.ContextFull => SessionCallOutcome.ContextFull,
            LiteRtSessionState.Cancelled => SessionCallOutcome.Cancelled,
            _ => null,
        };
        if (decode is { } outcome)
            machine.Complete(SessionCall.Decode, outcome);
        Assert.Equal(state, machine.State);
        return machine;
    }

    public static TheoryData<bool, string?, string> FinalChunks() => new()
    {
        { false, null, nameof(SessionCallOutcome.Succeeded) },
        { true, null, nameof(SessionCallOutcome.Cancelled) },
        { false, FinalChunkClassifier.MaxTokensReachedError, nameof(SessionCallOutcome.ContextFull) },
        { true, FinalChunkClassifier.MaxTokensReachedError, nameof(SessionCallOutcome.Cancelled) },
        // Only the managed flag classifies a cancel: the cancel text without a cancel issued is a failure.
        { false, FinalChunkClassifier.CancelledError, nameof(SessionCallOutcome.Failed) },
        { true, FinalChunkClassifier.CancelledError, nameof(SessionCallOutcome.Cancelled) },
        { false, "CANCELLED: by the runtime", nameof(SessionCallOutcome.Failed) },
        { true, "CANCELLED: by the runtime", nameof(SessionCallOutcome.Cancelled) },
        { false, "INTERNAL: Session is not prefilled yet.", nameof(SessionCallOutcome.Failed) },
        { true, "INTERNAL: Session is not prefilled yet.", nameof(SessionCallOutcome.Cancelled) },
        // The constant matches exactly: a prefix, other case or extra text is another error.
        { false, "RESOURCE_EXHAUSTED: Max number of tokens reached.", nameof(SessionCallOutcome.Failed) },
        { false, "max number of tokens reached.", nameof(SessionCallOutcome.Failed) },
        { false, "Max number of tokens reached", nameof(SessionCallOutcome.Failed) },
        { false, " Max number of tokens reached. ", nameof(SessionCallOutcome.Failed) },
        { false, "\u0001garbage�", nameof(SessionCallOutcome.Failed) },
        { true, "\u0001garbage�", nameof(SessionCallOutcome.Cancelled) },
        // An error that is present but empty is still an error, as for conversation streams.
        { false, "", nameof(SessionCallOutcome.Failed) },
        { true, "", nameof(SessionCallOutcome.Cancelled) },
    };

    [Theory]
    [MemberData(nameof(FinalChunks))]
    public void FinalChunk_IsClassifiedByTheCancelFlagFirst(bool cancelIssued, string? error, string expected)
        => Assert.Equal(Enum.Parse<SessionCallOutcome>(expected), FinalChunkClassifier.Classify(cancelIssued, error));

    /// <summary>The two constants are the measured final-chunk errors, with no status prefix.</summary>
    [Fact]
    public void FinalChunk_ConstantsAreTheMeasuredTexts()
    {
        Assert.Equal("Max number of tokens reached.", FinalChunkClassifier.MaxTokensReachedError);
        Assert.Equal("CANCELLED.", FinalChunkClassifier.CancelledError);
    }
}
