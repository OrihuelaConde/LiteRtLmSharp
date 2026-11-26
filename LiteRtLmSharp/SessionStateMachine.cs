namespace LiteRtLmSharp;

/// <summary>A call on a <see cref="LiteRtSession"/> that the state machine decides on. The three decode members
/// are separate values so that a refusal names the member the caller used.</summary>
internal enum SessionCall
{
    Prefill,
    Decode,
    DecodeAsync,
    DecodeStreamingAsync,
    Dispose,
    SaveCheckpoint,
    RewindTo,
    Reset,
}

/// <summary>How a native call that the state machine accepted ended.</summary>
internal enum SessionCallOutcome
{
    /// <summary>The call succeeded: a prefill, a save, a rewind or a reset returned OK, or a decode's final
    /// chunk carried no error.</summary>
    Succeeded,

    /// <summary>The call failed: a prefill, a save, a rewind or a reset returned an error,
    /// <c>run_decode_async</c> did not start, or a decode's final chunk carried an error other than the
    /// context-full one.</summary>
    Failed,

    /// <summary>A decode's final chunk said that the context is full.</summary>
    ContextFull,

    /// <summary>The decode's callback issued <c>litert_lm_session_cancel_process</c>.</summary>
    Cancelled,

    /// <summary>A rewind returned <c>NOT_FOUND</c>: the runtime no longer holds the checkpoint.</summary>
    NotFound,
}

/// <summary>What left a session <see cref="LiteRtSessionState.Faulted"/>: the call that failed, whether the
/// session was <see cref="LiteRtSessionState.Fresh"/> when it failed, and the native status, when the failure had
/// one. <see cref="SessionStateMachine.RecoversFrom"/> decides from it whether a rewind or a reset is
/// accepted.</summary>
internal sealed record SessionFault(SessionCall Call, bool OnFreshSession, LiteRtStatusCode? Status);

/// <summary>The state the machine decides on: the public state, and for <see cref="LiteRtSessionState.Faulted"/>
/// the fault that caused it. A Faulted state without a fault counts as a fault that nothing recovers.</summary>
internal readonly record struct SessionMachineState(LiteRtSessionState State, SessionFault? Fault = null)
{
    public static implicit operator SessionMachineState(LiteRtSessionState state) => new(state);

    /// <summary>Whether <see cref="SessionCall.RewindTo"/> and <see cref="SessionCall.Reset"/> are accepted from
    /// a state that refuses the other calls: a full context, or a fault that
    /// <see cref="SessionStateMachine.RecoversFrom"/> accepts.</summary>
    public bool Recoverable => State switch
    {
        LiteRtSessionState.ContextFull => true,
        LiteRtSessionState.Faulted => Fault is { } fault && SessionStateMachine.RecoversFrom(fault),
        _ => false,
    };
}

/// <summary>
/// The state of a <see cref="LiteRtSession"/> and the rules for which call it accepts next. A pure type: it makes
/// no native call. The session checks <see cref="ThrowIfRefused"/> before a native call and reports the call's
/// outcome with <see cref="Complete"/>.
/// </summary>
/// <remarks>
/// <para>
/// The native session reports success for work it skips: after a cancel or a failed prefill, a prefill returns OK
/// at once, and <c>run_decode_async</c> returns OK and did not call its callback within 30 s (measured on
/// LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and GPU). This machine is the only protection against a decode
/// that never ends, so from such a state it accepts only a rewind to a checkpoint and a reset, after any failed
/// prefill or a full context. Their recovery was measured after a full context and after the prefill failures
/// whose input did not fit; after another prefill failure they are accepted but were not measured (see
/// <see cref="RecoversFrom"/>). After a cancel, or after any other failure, it accepts only Dispose.
/// </para>
/// <para>
/// <see cref="Accepts"/> and <see cref="After"/> are the whole table. The session calls them with one call at a
/// time (its gate), so <see cref="Complete"/> needs no lock; <see cref="State"/> can be read from any thread.
/// </para>
/// </remarks>
internal sealed class SessionStateMachine
{
    private int _state;
    // Set with the Faulted state and read only by the holder of the session's gate. Written before _state.
    private SessionFault? _fault;

    /// <summary>Initializes a new instance of the <see cref="SessionStateMachine"/> class in
    /// <see cref="LiteRtSessionState.Fresh"/>.</summary>
    public SessionStateMachine() : this(LiteRtSessionState.Fresh)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SessionStateMachine"/> class in
    /// <paramref name="initial"/>, for tests that start a session in a state its native handle can't reach. A
    /// Faulted state without a fault is one that nothing recovers.</summary>
    public SessionStateMachine(SessionMachineState initial)
    {
        _fault = initial.State == LiteRtSessionState.Faulted ? initial.Fault : null;
        _state = (int)initial.State;
    }

    /// <summary>Gets the current state. It can be read from any thread.</summary>
    public LiteRtSessionState State => (LiteRtSessionState)Volatile.Read(ref _state);

    /// <summary>Gets the current state and fault, for the holder of the session's gate.</summary>
    public SessionMachineState Current
    {
        get
        {
            LiteRtSessionState state = State;
            return new SessionMachineState(state, state == LiteRtSessionState.Faulted ? Volatile.Read(ref _fault) : null);
        }
    }

    /// <summary>Whether a rewind or a reset recovers a session that <paramref name="fault"/> left Faulted: only
    /// a failed prefill. A rewind or a reset was measured to recover the session after a failed first or later
    /// prefill whose input did not fit, and after a full context; a chain that failed another way was not
    /// measured, and a decode on it may never end.</summary>
    public static bool RecoversFrom(SessionFault fault) => fault.Call == SessionCall.Prefill;

    /// <summary>Whether <paramref name="state"/> accepts <paramref name="call"/>.</summary>
    public static bool Accepts(SessionMachineState state, SessionCall call)
    {
        bool live = state.State is LiteRtSessionState.Fresh or LiteRtSessionState.Prefilled or LiteRtSessionState.Decoded;
        return call switch
        {
            SessionCall.Dispose => true,
            SessionCall.Prefill or SessionCall.SaveCheckpoint => live,
            SessionCall.Decode or SessionCall.DecodeAsync or SessionCall.DecodeStreamingAsync =>
                state.State == LiteRtSessionState.Prefilled,
            SessionCall.RewindTo or SessionCall.Reset => live || state.Recoverable,
            _ => false,
        };
    }

    /// <summary>The state after <paramref name="call"/>, accepted from <paramref name="from"/>, ended with
    /// <paramref name="outcome"/>. Dispose leaves the state as it is: the session tracks disposal itself.</summary>
    /// <param name="from">The state the call was accepted in.</param>
    /// <param name="call">The call.</param>
    /// <param name="outcome">How the call ended.</param>
    /// <param name="status">The native status of a failure, or <c>null</c> when the failure had none. Recorded
    /// in the fault.</param>
    /// <param name="rewoundTo">For a rewind that succeeded, the state the checkpoint recorded: Fresh, Prefilled
    /// or Decoded.</param>
    /// <exception cref="InvalidOperationException"><paramref name="from"/> does not accept
    /// <paramref name="call"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="call"/> cannot end with
    /// <paramref name="outcome"/>, or a rewind that succeeded has no recorded state to return to.</exception>
    public static SessionMachineState After(
        SessionMachineState from, SessionCall call, SessionCallOutcome outcome,
        LiteRtStatusCode? status = null, LiteRtSessionState? rewoundTo = null)
    {
        if (!Accepts(from, call))
            throw new InvalidOperationException($"The state {from.State} does not accept {call}.");
        SessionMachineState Fault() =>
            new(LiteRtSessionState.Faulted, new SessionFault(call, from.State == LiteRtSessionState.Fresh, status));
        return (call, outcome) switch
        {
            (SessionCall.Dispose, _) => from,
            (SessionCall.Prefill, SessionCallOutcome.Succeeded) => LiteRtSessionState.Prefilled,
            (SessionCall.Prefill, SessionCallOutcome.Failed) => Fault(),
            (SessionCall.Decode or SessionCall.DecodeAsync or SessionCall.DecodeStreamingAsync, _) => outcome switch
            {
                SessionCallOutcome.Succeeded => LiteRtSessionState.Decoded,
                SessionCallOutcome.ContextFull => LiteRtSessionState.ContextFull,
                SessionCallOutcome.Cancelled => LiteRtSessionState.Cancelled,
                SessionCallOutcome.Failed => Fault(),
                _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, $"{call} cannot end with {outcome}."),
            },
            (SessionCall.SaveCheckpoint, SessionCallOutcome.Succeeded) => from,
            (SessionCall.SaveCheckpoint, SessionCallOutcome.Failed) => Fault(),
            (SessionCall.RewindTo, SessionCallOutcome.Succeeded) => rewoundTo switch
            {
                LiteRtSessionState.Fresh or LiteRtSessionState.Prefilled or LiteRtSessionState.Decoded => rewoundTo.Value,
                _ => throw new ArgumentOutOfRangeException(nameof(rewoundTo), rewoundTo,
                    "A checkpoint records Fresh, Prefilled or Decoded."),
            },
            // The runtime changed nothing: the state and its fault stay.
            (SessionCall.RewindTo, SessionCallOutcome.NotFound) => from,
            (SessionCall.RewindTo, SessionCallOutcome.Failed) => Fault(),
            (SessionCall.Reset, SessionCallOutcome.Succeeded) => LiteRtSessionState.Fresh,
            (SessionCall.Reset, SessionCallOutcome.Failed) => Fault(),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, $"{call} cannot end with {outcome}."),
        };
    }

    /// <summary>The message of the <see cref="InvalidOperationException"/> for <paramref name="call"/> refused in
    /// <paramref name="state"/>.</summary>
    public static string RefusalMessage(SessionMachineState state, SessionCall call)
    {
        // The enum names are the public member names.
        string member = call.ToString();
        const string Recover = "Rewind it with RewindTo or clear it with Reset, or dispose it and create a new session.";
        return state.State switch
        {
            LiteRtSessionState.Fresh =>
                $"{member} needs prefilled input, and the session is Fresh. Call Prefill first.",
            LiteRtSessionState.Decoded =>
                $"{member} needs prefilled input, and the session already holds a decoded reply. Prefill more text before the next decode.",
            LiteRtSessionState.ContextFull =>
                $"The session's context is full, so it does not accept {member}. {Recover}",
            LiteRtSessionState.Faulted when state.Recoverable =>
                $"A prefill on this session failed, so it does not accept {member}. {Recover}",
            LiteRtSessionState.Faulted =>
                $"A native call on this session failed, so it does not accept {member}. Dispose it and create a new session.",
            LiteRtSessionState.Cancelled =>
                $"A decode on this session was cancelled, so it does not accept {member}. Dispose it and create a new session.",
            _ => $"The session state {state.State} does not accept {member}.",
        };
    }

    /// <summary>Throws when the current state does not accept <paramref name="call"/>.</summary>
    /// <exception cref="InvalidOperationException">The current state does not accept
    /// <paramref name="call"/>.</exception>
    public void ThrowIfRefused(SessionCall call)
    {
        SessionMachineState state = Current;
        if (!Accepts(state, call))
            throw new InvalidOperationException(RefusalMessage(state, call));
    }

    /// <summary>Moves to the state that <paramref name="call"/>, ended with <paramref name="outcome"/>, leads to
    /// from the current state, and returns it. See <see cref="After"/> for the parameters.</summary>
    public LiteRtSessionState Complete(
        SessionCall call, SessionCallOutcome outcome, LiteRtStatusCode? status = null, LiteRtSessionState? rewoundTo = null)
    {
        SessionMachineState next = After(Current, call, outcome, status, rewoundTo);
        Volatile.Write(ref _fault, next.Fault);
        Volatile.Write(ref _state, (int)next.State);
        return next.State;
    }
}

/// <summary>Classifies the final chunk of a session decode stream. A pure type: it makes no native call.</summary>
internal static class FinalChunkClassifier
{
    /// <summary>The error of a final chunk when the decode filled the context: no status prefix (measured on
    /// LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and GPU).</summary>
    internal const string MaxTokensReachedError = "Max number of tokens reached.";

    /// <summary>The error of a final chunk after a cancel: no status prefix (measured as above). A cancel is
    /// classified by whether the callback issued it, never by this text.</summary>
    internal const string CancelledError = "CANCELLED.";

    /// <summary>The outcome of a decode whose final chunk carried <paramref name="error"/>.</summary>
    /// <param name="cancelIssued">Whether the decode's callback called <c>litert_lm_session_cancel_process</c>.
    /// It decides first: a session that saw a cancel must not be reused, however the decode ended.</param>
    /// <param name="error">The final chunk's error, or <c>null</c> when the chunk carried none. An error that is
    /// not <c>null</c> is an error even when it is empty, as for conversation streams.</param>
    public static SessionCallOutcome Classify(bool cancelIssued, string? error)
    {
        if (cancelIssued)
            return SessionCallOutcome.Cancelled;
        if (error is null)
            return SessionCallOutcome.Succeeded;
        return string.Equals(error, MaxTokensReachedError, StringComparison.Ordinal)
            ? SessionCallOutcome.ContextFull
            : SessionCallOutcome.Failed;
    }
}
