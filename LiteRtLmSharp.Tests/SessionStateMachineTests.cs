using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The session state machine and the final-chunk classifier, without a model: every (state, call) cell, every
/// outcome of an accepted call, both kinds of Faulted state, and every refusal.
/// </summary>
public sealed class SessionStateMachineTests
{
    // The internal enums and states travel as their names: a public test method cannot take an internal type.
    private static readonly SessionCall[] Decodes = [SessionCall.Decode, SessionCall.DecodeAsync, SessionCall.DecodeStreamingAsync];

    private static readonly SessionCall[] Recoveries = [SessionCall.RewindTo, SessionCall.Reset];

    /// <summary>The machine states the tests name: the public states, a Faulted state for each call that can
    /// fail, and a Faulted state without a recorded fault (what a test constructor makes).</summary>
    private static readonly Dictionary<string, SessionMachineState> States = new()
    {
        ["Fresh"] = LiteRtSessionState.Fresh,
        ["Prefilled"] = LiteRtSessionState.Prefilled,
        ["Decoded"] = LiteRtSessionState.Decoded,
        ["ContextFull"] = LiteRtSessionState.ContextFull,
        ["Cancelled"] = LiteRtSessionState.Cancelled,
        ["FaultedByFirstPrefill"] = Faulted(SessionCall.Prefill, onFresh: true, LiteRtStatusCode.InvalidArgument),
        ["FaultedByLaterPrefill"] = Faulted(SessionCall.Prefill, onFresh: false, LiteRtStatusCode.ResourceExhausted),
        ["FaultedByPrefillWithoutStatus"] = Faulted(SessionCall.Prefill, onFresh: false, null),
        ["FaultedByDecode"] = Faulted(SessionCall.Decode, onFresh: false, null),
        ["FaultedByDecodeStream"] = Faulted(SessionCall.DecodeStreamingAsync, onFresh: false, null),
        ["FaultedBySave"] = Faulted(SessionCall.SaveCheckpoint, onFresh: true, LiteRtStatusCode.Internal),
        ["FaultedByRewind"] = Faulted(SessionCall.RewindTo, onFresh: false, LiteRtStatusCode.Internal),
        ["FaultedByReset"] = Faulted(SessionCall.Reset, onFresh: false, LiteRtStatusCode.Internal),
        ["FaultedWithoutCause"] = LiteRtSessionState.Faulted,
    };

    private static SessionMachineState Faulted(SessionCall call, bool onFresh, LiteRtStatusCode? status)
        => new(LiteRtSessionState.Faulted, new SessionFault(call, onFresh, status));

    private static bool IsLive(SessionMachineState state)
        => state.State is LiteRtSessionState.Fresh or LiteRtSessionState.Prefilled or LiteRtSessionState.Decoded;

    /// <summary>Whether the named state is one a rewind or a reset recovers: a full context or a failed
    /// prefill.</summary>
    private static bool IsRecoverable(string name)
        => name is "ContextFull" or "FaultedByFirstPrefill" or "FaultedByLaterPrefill" or "FaultedByPrefillWithoutStatus";

    public static TheoryData<string, string, bool> AcceptanceTable()
    {
        var data = new TheoryData<string, string, bool>();
        foreach ((string name, SessionMachineState state) in States)
        {
            bool live = IsLive(state);
            data.Add(name, nameof(SessionCall.Prefill), live);
            foreach (SessionCall decode in Decodes)
                data.Add(name, decode.ToString(), state.State == LiteRtSessionState.Prefilled);
            data.Add(name, nameof(SessionCall.SaveCheckpoint), live);
            foreach (SessionCall recovery in Recoveries)
                data.Add(name, recovery.ToString(), live || IsRecoverable(name));
            data.Add(name, nameof(SessionCall.Dispose), true);
        }
        return data;
    }

    [Fact]
    public void AcceptanceTable_CoversEveryCall()
    {
        var calls = AcceptanceTable().Select(row => (string)row[1]).Distinct().Order().ToArray();
        Assert.Equal(Enum.GetNames<SessionCall>().Order().ToArray(), calls);
    }

    [Theory]
    [MemberData(nameof(AcceptanceTable))]
    public void Accepts_FollowsTheTable(string stateName, string callName, bool accepted)
    {
        SessionMachineState state = States[stateName];
        SessionCall call = Enum.Parse<SessionCall>(callName);
        Assert.Equal(accepted, SessionStateMachine.Accepts(state, call));
        if (!accepted)
        {
            Assert.Throws<InvalidOperationException>(() => SessionStateMachine.After(state, call, SessionCallOutcome.Succeeded));
            Assert.Throws<InvalidOperationException>(() => new SessionStateMachine(state).ThrowIfRefused(call));
        }
    }

    /// <summary>Cancelled, and Faulted by anything but a failed prefill, accept nothing but Dispose.</summary>
    [Theory]
    [InlineData("Cancelled")]
    [InlineData("FaultedByDecode")]
    [InlineData("FaultedByDecodeStream")]
    [InlineData("FaultedBySave")]
    [InlineData("FaultedByRewind")]
    [InlineData("FaultedByReset")]
    [InlineData("FaultedWithoutCause")]
    public void EndStates_AcceptOnlyDispose(string stateName)
    {
        SessionMachineState state = States[stateName];
        SessionCall[] accepted = Enum.GetValues<SessionCall>().Where(call => SessionStateMachine.Accepts(state, call)).ToArray();
        Assert.Equal([SessionCall.Dispose], accepted);
        Assert.False(state.Recoverable);
    }

    /// <summary>A full context and a failed prefill accept a rewind, a reset and Dispose, and nothing else.</summary>
    [Theory]
    [InlineData("ContextFull")]
    [InlineData("FaultedByFirstPrefill")]
    [InlineData("FaultedByLaterPrefill")]
    [InlineData("FaultedByPrefillWithoutStatus")]
    public void RecoverableStates_AcceptRewindResetAndDispose(string stateName)
    {
        SessionMachineState state = States[stateName];
        SessionCall[] accepted = Enum.GetValues<SessionCall>().Where(call => SessionStateMachine.Accepts(state, call)).ToArray();
        Assert.Equal([SessionCall.Dispose, SessionCall.RewindTo, SessionCall.Reset], accepted);
        Assert.True(state.Recoverable);
    }

    [Fact]
    public void RecoversFrom_OnlyAFailedPrefill()
    {
        foreach (SessionCall call in Enum.GetValues<SessionCall>())
            foreach (bool onFresh in new[] { true, false })
                Assert.Equal(call == SessionCall.Prefill, SessionStateMachine.RecoversFrom(new SessionFault(call, onFresh, LiteRtStatusCode.Internal)));
    }

    [Theory]
    [InlineData("Fresh", nameof(SessionCallOutcome.Succeeded), LiteRtSessionState.Prefilled)]
    [InlineData("Prefilled", nameof(SessionCallOutcome.Succeeded), LiteRtSessionState.Prefilled)]
    [InlineData("Decoded", nameof(SessionCallOutcome.Succeeded), LiteRtSessionState.Prefilled)]
    [InlineData("Fresh", nameof(SessionCallOutcome.Failed), LiteRtSessionState.Faulted)]
    [InlineData("Prefilled", nameof(SessionCallOutcome.Failed), LiteRtSessionState.Faulted)]
    [InlineData("Decoded", nameof(SessionCallOutcome.Failed), LiteRtSessionState.Faulted)]
    public void Prefill_MovesToItsOutcome(string from, string outcome, LiteRtSessionState to)
        => Assert.Equal(to, SessionStateMachine.After(States[from], SessionCall.Prefill, Enum.Parse<SessionCallOutcome>(outcome)).State);

    /// <summary>A failed prefill records its fault: the call, whether the session was Fresh, and the status; a
    /// rewind or a reset is then accepted.</summary>
    [Theory]
    [InlineData("Fresh", true)]
    [InlineData("Prefilled", false)]
    [InlineData("Decoded", false)]
    public void Prefill_ThatFails_RecordsARecoverableFault(string from, bool onFresh)
    {
        SessionMachineState after = SessionStateMachine.After(
            States[from], SessionCall.Prefill, SessionCallOutcome.Failed, LiteRtStatusCode.ResourceExhausted);
        Assert.Equal(new SessionFault(SessionCall.Prefill, onFresh, LiteRtStatusCode.ResourceExhausted), after.Fault);
        Assert.True(after.Recoverable);
    }

    /// <summary>A prefill has no callback, so it cannot end as a full context, a cancel or a missing
    /// checkpoint.</summary>
    [Theory]
    [InlineData(nameof(SessionCallOutcome.ContextFull))]
    [InlineData(nameof(SessionCallOutcome.Cancelled))]
    [InlineData(nameof(SessionCallOutcome.NotFound))]
    public void Prefill_CannotEndWithAnotherCallsOutcome(string outcome)
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
            LiteRtSessionState.Prefilled, Enum.Parse<SessionCall>(decode), Enum.Parse<SessionCallOutcome>(outcome)).State);

    /// <summary>A failed decode leaves a fault that nothing recovers.</summary>
    [Theory]
    [InlineData(nameof(SessionCall.Decode))]
    [InlineData(nameof(SessionCall.DecodeAsync))]
    [InlineData(nameof(SessionCall.DecodeStreamingAsync))]
    public void Decode_ThatFails_RecordsAFaultNothingRecovers(string decode)
    {
        SessionCall call = Enum.Parse<SessionCall>(decode);
        SessionMachineState after = SessionStateMachine.After(LiteRtSessionState.Prefilled, call, SessionCallOutcome.Failed);
        Assert.Equal(new SessionFault(call, false, null), after.Fault);
        Assert.False(after.Recoverable);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionStateMachine.After(LiteRtSessionState.Prefilled, call, SessionCallOutcome.NotFound));
    }

    // --- Checkpoints, rewind and reset ------------------------------------------

    /// <summary>A save that succeeds leaves the state as it was; one that fails latches a fault that nothing
    /// recovers.</summary>
    [Theory]
    [InlineData("Fresh")]
    [InlineData("Prefilled")]
    [InlineData("Decoded")]
    public void SaveCheckpoint_MovesToItsOutcome(string from)
    {
        SessionMachineState state = States[from];
        Assert.Equal(state, SessionStateMachine.After(state, SessionCall.SaveCheckpoint, SessionCallOutcome.Succeeded));
        SessionMachineState failed = SessionStateMachine.After(
            state, SessionCall.SaveCheckpoint, SessionCallOutcome.Failed, LiteRtStatusCode.Internal);
        Assert.Equal(LiteRtSessionState.Faulted, failed.State);
        Assert.Equal(new SessionFault(SessionCall.SaveCheckpoint, from == "Fresh", LiteRtStatusCode.Internal), failed.Fault);
        Assert.False(failed.Recoverable);
        foreach (string outcome in new[] { nameof(SessionCallOutcome.ContextFull), nameof(SessionCallOutcome.Cancelled), nameof(SessionCallOutcome.NotFound) })
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SessionStateMachine.After(state, SessionCall.SaveCheckpoint, Enum.Parse<SessionCallOutcome>(outcome)));
    }

    public static TheoryData<string, LiteRtSessionState> RewindSources()
    {
        var data = new TheoryData<string, LiteRtSessionState>();
        foreach (string from in new[] { "Fresh", "Prefilled", "Decoded", "ContextFull", "FaultedByFirstPrefill", "FaultedByLaterPrefill", "FaultedByPrefillWithoutStatus" })
            foreach (LiteRtSessionState recorded in new[] { LiteRtSessionState.Fresh, LiteRtSessionState.Prefilled, LiteRtSessionState.Decoded })
                data.Add(from, recorded);
        return data;
    }

    /// <summary>A rewind that succeeds goes to the state the checkpoint recorded and clears any fault; one the
    /// runtime answers NOT_FOUND keeps the state and its fault; any other failure latches a fault that nothing
    /// recovers.</summary>
    [Theory]
    [MemberData(nameof(RewindSources))]
    public void RewindTo_MovesToItsOutcome(string from, LiteRtSessionState recorded)
    {
        SessionMachineState state = States[from];
        Assert.Equal(new SessionMachineState(recorded),
            SessionStateMachine.After(state, SessionCall.RewindTo, SessionCallOutcome.Succeeded, rewoundTo: recorded));
        Assert.Equal(state, SessionStateMachine.After(
            state, SessionCall.RewindTo, SessionCallOutcome.NotFound, LiteRtStatusCode.NotFound));
        SessionMachineState failed = SessionStateMachine.After(
            state, SessionCall.RewindTo, SessionCallOutcome.Failed, LiteRtStatusCode.Internal);
        Assert.Equal(LiteRtSessionState.Faulted, failed.State);
        Assert.Equal(new SessionFault(SessionCall.RewindTo, from == "Fresh", LiteRtStatusCode.Internal), failed.Fault);
        Assert.False(failed.Recoverable);
    }

    /// <summary>A checkpoint records only Fresh, Prefilled or Decoded, so a rewind to anything else, or with no
    /// recorded state, is a bug in the caller.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(LiteRtSessionState.ContextFull)]
    [InlineData(LiteRtSessionState.Faulted)]
    [InlineData(LiteRtSessionState.Cancelled)]
    public void RewindTo_NeedsAStateACheckpointCanRecord(LiteRtSessionState? recorded)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionStateMachine.After(LiteRtSessionState.Prefilled, SessionCall.RewindTo, SessionCallOutcome.Succeeded, rewoundTo: recorded));
        foreach (string outcome in new[] { nameof(SessionCallOutcome.ContextFull), nameof(SessionCallOutcome.Cancelled) })
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SessionStateMachine.After(LiteRtSessionState.Prefilled, SessionCall.RewindTo, Enum.Parse<SessionCallOutcome>(outcome)));
    }

    /// <summary>A reset that succeeds goes to Fresh and clears any fault; one that fails latches a fault that
    /// nothing recovers.</summary>
    [Theory]
    [InlineData("Fresh")]
    [InlineData("Prefilled")]
    [InlineData("Decoded")]
    [InlineData("ContextFull")]
    [InlineData("FaultedByFirstPrefill")]
    [InlineData("FaultedByLaterPrefill")]
    [InlineData("FaultedByPrefillWithoutStatus")]
    public void Reset_MovesToItsOutcome(string from)
    {
        SessionMachineState state = States[from];
        Assert.Equal(new SessionMachineState(LiteRtSessionState.Fresh),
            SessionStateMachine.After(state, SessionCall.Reset, SessionCallOutcome.Succeeded));
        SessionMachineState failed = SessionStateMachine.After(state, SessionCall.Reset, SessionCallOutcome.Failed, LiteRtStatusCode.Internal);
        Assert.Equal(new SessionFault(SessionCall.Reset, from == "Fresh", LiteRtStatusCode.Internal), failed.Fault);
        Assert.False(failed.Recoverable);
        foreach (string outcome in new[] { nameof(SessionCallOutcome.ContextFull), nameof(SessionCallOutcome.Cancelled), nameof(SessionCallOutcome.NotFound) })
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SessionStateMachine.After(state, SessionCall.Reset, Enum.Parse<SessionCallOutcome>(outcome)));
    }

    /// <summary>Dispose is accepted in every state and leaves the state and its fault as they were: disposal is the
    /// session's own flag, so <see cref="SessionStateMachine.State"/> still reads the last state.</summary>
    [Fact]
    public void Dispose_KeepsTheState()
    {
        foreach (SessionMachineState state in States.Values)
            foreach (SessionCallOutcome outcome in Enum.GetValues<SessionCallOutcome>())
                Assert.Equal(state, SessionStateMachine.After(state, SessionCall.Dispose, outcome));
    }

    /// <summary>The machine keeps the fault with the Faulted state and drops it on the way out.</summary>
    [Fact]
    public void Machine_KeepsTheFaultUntilARecovery()
    {
        var machine = new SessionStateMachine();
        machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded);
        machine.Complete(SessionCall.Prefill, SessionCallOutcome.Failed, LiteRtStatusCode.FailedPrecondition);
        Assert.Equal(LiteRtSessionState.Faulted, machine.State);
        Assert.Equal(new SessionFault(SessionCall.Prefill, false, LiteRtStatusCode.FailedPrecondition), machine.Current.Fault);
        // A rewind to a dropped checkpoint changes nothing, the fault included.
        Assert.Equal(LiteRtSessionState.Faulted, machine.Complete(SessionCall.RewindTo, SessionCallOutcome.NotFound, LiteRtStatusCode.NotFound));
        Assert.True(machine.Current.Recoverable);
        Assert.Equal(LiteRtSessionState.Decoded, machine.Complete(SessionCall.RewindTo, SessionCallOutcome.Succeeded, rewoundTo: LiteRtSessionState.Decoded));
        Assert.Null(machine.Current.Fault);
        // A Faulted state given to the constructor without a fault is one nothing recovers.
        Assert.False(new SessionStateMachine(LiteRtSessionState.Faulted).Current.Recoverable);
        // A fault given with a state that is not Faulted is not kept.
        Assert.Null(new SessionStateMachine(new SessionMachineState(LiteRtSessionState.Fresh, new SessionFault(SessionCall.Prefill, true, null))).Current.Fault);
    }

    public static TheoryData<string, string, string> Refusals() => new()
    {
        { "Fresh", nameof(SessionCall.Decode), "Call Prefill first" },
        { "Fresh", nameof(SessionCall.DecodeAsync), "DecodeAsync needs prefilled input" },
        { "Decoded", nameof(SessionCall.DecodeStreamingAsync), "Prefill more text before the next decode" },
        { "ContextFull", nameof(SessionCall.Prefill), "context is full" },
        { "ContextFull", nameof(SessionCall.Decode), "context is full" },
        { "ContextFull", nameof(SessionCall.SaveCheckpoint), "context is full" },
        { "FaultedByFirstPrefill", nameof(SessionCall.Prefill), "A prefill on this session failed" },
        { "FaultedByLaterPrefill", nameof(SessionCall.DecodeAsync), "A prefill on this session failed" },
        { "FaultedByLaterPrefill", nameof(SessionCall.SaveCheckpoint), "A prefill on this session failed" },
        { "FaultedByDecode", nameof(SessionCall.Prefill), "native call on this session failed" },
        { "FaultedByDecodeStream", nameof(SessionCall.RewindTo), "native call on this session failed" },
        { "FaultedBySave", nameof(SessionCall.Reset), "native call on this session failed" },
        { "FaultedByRewind", nameof(SessionCall.RewindTo), "native call on this session failed" },
        { "FaultedByReset", nameof(SessionCall.SaveCheckpoint), "native call on this session failed" },
        { "FaultedWithoutCause", nameof(SessionCall.Reset), "native call on this session failed" },
        { "Cancelled", nameof(SessionCall.Prefill), "was cancelled" },
        { "Cancelled", nameof(SessionCall.DecodeStreamingAsync), "was cancelled" },
        { "Cancelled", nameof(SessionCall.SaveCheckpoint), "was cancelled" },
        { "Cancelled", nameof(SessionCall.RewindTo), "was cancelled" },
        { "Cancelled", nameof(SessionCall.Reset), "was cancelled" },
    };

    /// <summary>A refusal names the member the caller used and what to do instead: a recoverable state names the
    /// rewind and the reset, the others only a new session.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ThrowIfRefused_SaysWhy(string stateName, string callName, string expected)
    {
        SessionMachineState state = States[stateName];
        SessionCall call = Enum.Parse<SessionCall>(callName);
        var machine = Drive(stateName);
        Assert.Equal(state, machine.Current);
        var ex = Assert.Throws<InvalidOperationException>(() => machine.ThrowIfRefused(call));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains(call.ToString(), ex.Message, StringComparison.Ordinal);
        if (IsRecoverable(stateName))
            Assert.EndsWith("Rewind it with RewindTo or clear it with Reset, or dispose it and create a new session.", ex.Message, StringComparison.Ordinal);
        else if (!IsLive(state))
            Assert.EndsWith("Dispose it and create a new session.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(state, machine.Current);
    }

    [Fact]
    public void Machine_StartsFresh_AndFollowsAWholeRun()
    {
        var machine = new SessionStateMachine();
        Assert.Equal(LiteRtSessionState.Fresh, machine.State);
        machine.ThrowIfRefused(SessionCall.SaveCheckpoint);
        Assert.Equal(LiteRtSessionState.Fresh, machine.Complete(SessionCall.SaveCheckpoint, SessionCallOutcome.Succeeded));
        machine.ThrowIfRefused(SessionCall.Prefill);
        Assert.Equal(LiteRtSessionState.Prefilled, machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded));
        machine.ThrowIfRefused(SessionCall.Decode);
        Assert.Equal(LiteRtSessionState.Decoded, machine.Complete(SessionCall.Decode, SessionCallOutcome.Succeeded));
        Assert.Throws<InvalidOperationException>(() => machine.ThrowIfRefused(SessionCall.DecodeAsync));
        Assert.Equal(LiteRtSessionState.Decoded, machine.Complete(SessionCall.SaveCheckpoint, SessionCallOutcome.Succeeded));
        Assert.Equal(LiteRtSessionState.Prefilled, machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded));
        Assert.Equal(LiteRtSessionState.ContextFull, machine.Complete(SessionCall.DecodeStreamingAsync, SessionCallOutcome.ContextFull));
        Assert.Throws<InvalidOperationException>(() => machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded));
        Assert.Throws<InvalidOperationException>(() => machine.ThrowIfRefused(SessionCall.SaveCheckpoint));
        Assert.Equal(LiteRtSessionState.ContextFull, machine.State);
        Assert.Equal(LiteRtSessionState.Decoded, machine.Complete(SessionCall.RewindTo, SessionCallOutcome.Succeeded, rewoundTo: LiteRtSessionState.Decoded));
        Assert.Equal(LiteRtSessionState.Prefilled, machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded));
        Assert.Equal(LiteRtSessionState.Faulted, machine.Complete(SessionCall.Prefill, SessionCallOutcome.Failed, LiteRtStatusCode.ResourceExhausted));
        Assert.Equal(LiteRtSessionState.Fresh, machine.Complete(SessionCall.Reset, SessionCallOutcome.Succeeded));
        Assert.Equal(LiteRtSessionState.Faulted, machine.Complete(SessionCall.Reset, SessionCallOutcome.Failed, LiteRtStatusCode.Internal));
        Assert.Throws<InvalidOperationException>(() => machine.ThrowIfRefused(SessionCall.Reset));
    }

    /// <summary>A machine driven into the named state through accepted calls only.</summary>
    internal static SessionStateMachine Drive(string stateName)
    {
        var machine = new SessionStateMachine();
        void Prefilled() => machine.Complete(SessionCall.Prefill, SessionCallOutcome.Succeeded);
        switch (stateName)
        {
            case "Fresh":
                break;
            case "Prefilled":
                Prefilled();
                break;
            case "Decoded":
                Prefilled();
                machine.Complete(SessionCall.Decode, SessionCallOutcome.Succeeded);
                break;
            case "ContextFull":
                Prefilled();
                machine.Complete(SessionCall.Decode, SessionCallOutcome.ContextFull);
                break;
            case "Cancelled":
                Prefilled();
                machine.Complete(SessionCall.Decode, SessionCallOutcome.Cancelled);
                break;
            case "FaultedByFirstPrefill":
                machine.Complete(SessionCall.Prefill, SessionCallOutcome.Failed, LiteRtStatusCode.InvalidArgument);
                break;
            case "FaultedByLaterPrefill":
                Prefilled();
                machine.Complete(SessionCall.Prefill, SessionCallOutcome.Failed, LiteRtStatusCode.ResourceExhausted);
                break;
            case "FaultedByPrefillWithoutStatus":
                Prefilled();
                machine.Complete(SessionCall.Prefill, SessionCallOutcome.Failed);
                break;
            case "FaultedByDecode":
                Prefilled();
                machine.Complete(SessionCall.Decode, SessionCallOutcome.Failed);
                break;
            case "FaultedByDecodeStream":
                Prefilled();
                machine.Complete(SessionCall.DecodeStreamingAsync, SessionCallOutcome.Failed);
                break;
            case "FaultedBySave":
                machine.Complete(SessionCall.SaveCheckpoint, SessionCallOutcome.Failed, LiteRtStatusCode.Internal);
                break;
            case "FaultedByRewind":
                Prefilled();
                machine.Complete(SessionCall.RewindTo, SessionCallOutcome.Failed, LiteRtStatusCode.Internal);
                break;
            case "FaultedByReset":
                Prefilled();
                machine.Complete(SessionCall.Reset, SessionCallOutcome.Failed, LiteRtStatusCode.Internal);
                break;
            case "FaultedWithoutCause":
                return new SessionStateMachine(LiteRtSessionState.Faulted);
            default:
                throw new ArgumentOutOfRangeException(nameof(stateName), stateName, null);
        }
        Assert.Equal(States[stateName], machine.Current);
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
