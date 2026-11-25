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
}

/// <summary>How a native call that the state machine accepted ended.</summary>
internal enum SessionCallOutcome
{
    /// <summary>The call succeeded: a prefill returned OK, or a decode's final chunk carried no error.</summary>
    Succeeded,

    /// <summary>The call failed: a prefill returned an error, <c>run_decode_async</c> did not start, or a
    /// decode's final chunk carried an error other than the context-full one.</summary>
    Failed,

    /// <summary>A decode's final chunk said that the context is full.</summary>
    ContextFull,

    /// <summary>The decode's callback issued <c>litert_lm_session_cancel_process</c>.</summary>
    Cancelled,
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
/// that never ends, so it accepts no call from a state where the runtime would skip the work.
/// </para>
/// <para>
/// <see cref="Accepts"/> and <see cref="After"/> are the whole table. The session calls them with one call at a
/// time (its gate), so <see cref="Complete"/> needs no lock; <see cref="State"/> can be read from any thread.
/// </para>
/// </remarks>
internal sealed class SessionStateMachine
{
    private int _state;

    /// <summary>Initializes a new instance of the <see cref="SessionStateMachine"/> class in
    /// <see cref="LiteRtSessionState.Fresh"/>.</summary>
    public SessionStateMachine() : this(LiteRtSessionState.Fresh)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SessionStateMachine"/> class in
    /// <paramref name="initial"/>, for tests that start a session in a state its native handle can't reach.</summary>
    public SessionStateMachine(LiteRtSessionState initial) => _state = (int)initial;

    /// <summary>Gets the current state. It can be read from any thread.</summary>
    public LiteRtSessionState State => (LiteRtSessionState)Volatile.Read(ref _state);

    /// <summary>Whether <paramref name="state"/> accepts <paramref name="call"/>.</summary>
    public static bool Accepts(LiteRtSessionState state, SessionCall call) => call switch
    {
        SessionCall.Dispose => true,
        SessionCall.Prefill => state is LiteRtSessionState.Fresh or LiteRtSessionState.Prefilled or LiteRtSessionState.Decoded,
        SessionCall.Decode or SessionCall.DecodeAsync or SessionCall.DecodeStreamingAsync => state == LiteRtSessionState.Prefilled,
        _ => false,
    };

    /// <summary>The state after <paramref name="call"/>, accepted from <paramref name="from"/>, ended with
    /// <paramref name="outcome"/>. Dispose leaves the state as it is: the session tracks disposal itself.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="from"/> does not accept
    /// <paramref name="call"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="call"/> cannot end with
    /// <paramref name="outcome"/>.</exception>
    public static LiteRtSessionState After(LiteRtSessionState from, SessionCall call, SessionCallOutcome outcome)
    {
        if (!Accepts(from, call))
            throw new InvalidOperationException($"The state {from} does not accept {call}.");
        return (call, outcome) switch
        {
            (SessionCall.Dispose, _) => from,
            (SessionCall.Prefill, SessionCallOutcome.Succeeded) => LiteRtSessionState.Prefilled,
            (SessionCall.Prefill, SessionCallOutcome.Failed) => LiteRtSessionState.Faulted,
            (SessionCall.Decode or SessionCall.DecodeAsync or SessionCall.DecodeStreamingAsync, _) => outcome switch
            {
                SessionCallOutcome.Succeeded => LiteRtSessionState.Decoded,
                SessionCallOutcome.ContextFull => LiteRtSessionState.ContextFull,
                SessionCallOutcome.Cancelled => LiteRtSessionState.Cancelled,
                SessionCallOutcome.Failed => LiteRtSessionState.Faulted,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, $"{call} cannot end with {outcome}."),
        };
    }

    /// <summary>The message of the <see cref="InvalidOperationException"/> for <paramref name="call"/> refused in
    /// <paramref name="state"/>.</summary>
    public static string RefusalMessage(LiteRtSessionState state, SessionCall call)
    {
        // The enum names are the public member names.
        string member = call.ToString();
        return state switch
        {
            LiteRtSessionState.Fresh =>
                $"{member} needs prefilled input, and the session is Fresh. Call Prefill first.",
            LiteRtSessionState.Decoded =>
                $"{member} needs prefilled input, and the session already holds a decoded reply. Prefill more text before the next decode.",
            LiteRtSessionState.ContextFull =>
                $"The session's context is full, so it does not accept {member}. Dispose it and create a new session.",
            LiteRtSessionState.Faulted =>
                $"A native call on this session failed, so it does not accept {member}. Dispose it and create a new session.",
            LiteRtSessionState.Cancelled =>
                $"A decode on this session was cancelled, so it does not accept {member}. Dispose it and create a new session.",
            _ => $"The session state {state} does not accept {member}.",
        };
    }

    /// <summary>Throws when the current state does not accept <paramref name="call"/>.</summary>
    /// <exception cref="InvalidOperationException">The current state does not accept
    /// <paramref name="call"/>.</exception>
    public void ThrowIfRefused(SessionCall call)
    {
        LiteRtSessionState state = State;
        if (!Accepts(state, call))
            throw new InvalidOperationException(RefusalMessage(state, call));
    }

    /// <summary>Moves to the state that <paramref name="call"/>, ended with <paramref name="outcome"/>, leads to
    /// from the current state, and returns it.</summary>
    public LiteRtSessionState Complete(SessionCall call, SessionCallOutcome outcome)
    {
        LiteRtSessionState next = After(State, call, outcome);
        Volatile.Write(ref _state, (int)next);
        return next;
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
