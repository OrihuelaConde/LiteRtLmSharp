using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>A raw prefill-and-decode session on a <see cref="LiteRtEngine"/>.</summary>
/// <remarks>
/// <para>
/// A session applies no template, keeps no message history and parses no tool calls or reasoning channels: the
/// text it prefills reaches the model as given. Create one with <see cref="LiteRtEngine.CreateSession"/>.
/// </para>
/// <para>
/// <b>One call at a time.</b> A session runs one call at a time and refuses a second call with
/// <see cref="InvalidOperationException"/> instead of waiting for the first. <see cref="State"/> and
/// <see cref="Dispose"/> can be called from any thread at any time.
/// </para>
/// <para>
/// <b>Lifetime.</b> The session keeps the native engine alive, so the engine can be disposed first. A session that
/// reaches <see cref="LiteRtSessionState.ContextFull"/>, <see cref="LiteRtSessionState.Faulted"/> or
/// <see cref="LiteRtSessionState.Cancelled"/> accepts only <see cref="Dispose"/>: dispose it and create a new
/// session.
/// </para>
/// </remarks>
public sealed class LiteRtSession : IDisposable
{
    private readonly SessionHandle _session;
    private readonly SessionStateMachine _machine = new();
    // The text of the model's start token that the first prefill refuses at its head, or null when the rule does
    // not apply. Read from the engine at creation, so it holds after the engine object is disposed.
    private readonly string? _startTokenText;
    // The one-call-at-a-time gate: 1 while a call runs. Every member except State and Dispose takes it before
    // any native call, with a compare-and-swap that refuses instead of waiting: waiting would deadlock a caller
    // that prefills from inside its own read loop.
    private int _callInProgress;
    private int _disposed;

    /// <summary>The hint added to a prefill failure that says the input does not fit the remaining
    /// context.</summary>
    internal const string DoesNotFitHint =
        "The input does not fit the remaining context. Dispose the session and prefill a shorter context on a " +
        "new one, or load the engine with a larger LiteRtEngineOptions.MaxNumTokens.";

    internal LiteRtSession(SessionHandle session, string? startTokenText)
    {
        _session = session;
        _startTokenText = startTokenText;
    }

    internal static unsafe LiteRtSession Create(LiteRtEngine engine, LiteRtSessionOptions? options)
    {
        string? startTokenText = engine.SessionStartTokenText;

        // The config is always created: the template setter needs one. The header does not say whether the
        // session keeps a reference to it, so it lives until create_session returns and is deleted then.
        nint configPtr;
        NativeError.ThrowIfFailed(
            LiteRtLmNative.litert_lm_session_config_create(&configPtr),
            nameof(LiteRtLmNative.litert_lm_session_config_create));
        using var config = new SessionConfigHandle(configPtr);
        // Always off: the text reaches the model as given, and the runtime then adds the start token on the
        // first prefill of a fresh session (measured on v0.20.0, win-x64, gemma-4 E2B, CPU and GPU).
        NativeError.ThrowIfFailed(
            LiteRtLmNative.litert_lm_session_config_set_apply_prompt_template(config.Ptr, false),
            nameof(LiteRtLmNative.litert_lm_session_config_set_apply_prompt_template));
        // Called only with a positive value: 0 means no cap and sends nothing, and LiteRtSessionOptions refuses
        // a negative value.
        if (options is { MaxOutputTokens: > 0 and var maxOutputTokens })
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_session_config_set_max_output_tokens(config.Ptr, maxOutputTokens),
                nameof(LiteRtLmNative.litert_lm_session_config_set_max_output_tokens));
        NativeSampler.Apply(config.Ptr, options?.Sampler);

        // The session keeps the native engine alive until it is deleted (SessionHandle releases this reference),
        // so disposing the engine first can never free it under a live session.
        bool engineReferenced = false;
        engine.Handle.DangerousAddRef(ref engineReferenced);
        nint sessionPtr;
        LiteRtStatusCode status = LiteRtLmNative.litert_lm_engine_create_session(engine.Handle.Ptr, config.Ptr, &sessionPtr);
        if (status != LiteRtStatusCode.Ok)
        {
            // Read the report before any other native call: the release can delete the engine.
            LiteRtException failure = NativeError.Exception(status, "litert_lm_engine_create_session failed.");
            engine.Handle.DangerousRelease();
            throw failure;
        }
        return new LiteRtSession(new SessionHandle(sessionPtr, engine.Handle), startTokenText);
    }

    /// <summary>Gets the state of the session. It can be read from any thread, also after
    /// <see cref="Dispose"/>.</summary>
    public LiteRtSessionState State => _machine.State;

    /// <summary>Appends <paramref name="text"/> to the session's context as given, and blocks until the runtime
    /// has processed it.</summary>
    /// <param name="text">The text to add, with any markup the model expects already in it.</param>
    /// <remarks>
    /// <para>
    /// On a <see cref="LiteRtSessionState.Fresh"/> session the runtime adds the model's start token before the
    /// text, so the text must not start with it (measured on LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and
    /// GPU). When the model's start token is a single token, such as <c>&lt;bos&gt;</c> on Gemma 4, the session
    /// refuses such a text. Later prefills pass the text unchanged.
    /// </para>
    /// <para>
    /// A prefill can't be cut once it starts. Any failure leaves the session
    /// <see cref="LiteRtSessionState.Faulted"/>: after a failed prefill the runtime returns OK for later prefills
    /// and decodes on the session, and a decode then delivers no reply. Input that does not fit the remaining
    /// context fails with <see cref="LiteRtStatusCode.ResourceExhausted"/>,
    /// <see cref="LiteRtStatusCode.InvalidArgument"/> or <see cref="LiteRtStatusCode.FailedPrecondition"/>,
    /// depending on how it does not fit. Both were measured on LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and
    /// GPU.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty, or the session is
    /// <see cref="LiteRtSessionState.Fresh"/> and <paramref name="text"/> starts with the model's start token,
    /// which the session adds itself.</exception>
    /// <exception cref="InvalidOperationException">Another call on the session is in progress, or the session is
    /// <see cref="LiteRtSessionState.ContextFull"/>, <see cref="LiteRtSessionState.Faulted"/> or
    /// <see cref="LiteRtSessionState.Cancelled"/>.</exception>
    /// <exception cref="LiteRtException">The runtime rejected the input. <see cref="State"/> is then
    /// <see cref="LiteRtSessionState.Faulted"/>, and <see cref="LiteRtException.StatusCode"/> holds the status
    /// the runtime returned.</exception>
    public void Prefill(string text)
    {
        CheckPrefillArguments(text);
        EnterCall();
        try
        {
            _machine.ThrowIfRefused(SessionCall.Prefill);
            PrefillNative(text);
        }
        finally
        {
            ExitCall();
        }
    }

    /// <summary>Runs <see cref="Prefill"/> on the thread pool.</summary>
    /// <param name="text">The text to add, with any markup the model expects already in it.</param>
    /// <param name="cancellationToken">Observed only before the prefill starts: a prefill that started runs to
    /// completion.</param>
    /// <returns>A task that completes when the runtime has processed the text.</returns>
    /// <remarks>See <see cref="Prefill"/> for the start token and the failures. The session counts as busy from
    /// this call until the task completes.</remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty, or the session is
    /// <see cref="LiteRtSessionState.Fresh"/> and <paramref name="text"/> starts with the model's start token,
    /// which the session adds itself.</exception>
    /// <exception cref="InvalidOperationException">Another call on the session is in progress, or the session is
    /// <see cref="LiteRtSessionState.ContextFull"/>, <see cref="LiteRtSessionState.Faulted"/> or
    /// <see cref="LiteRtSessionState.Cancelled"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the
    /// prefill started. The session is unchanged.</exception>
    /// <exception cref="LiteRtException">The runtime rejected the input. <see cref="State"/> is then
    /// <see cref="LiteRtSessionState.Faulted"/>.</exception>
    public async Task PrefillAsync(string text, CancellationToken cancellationToken = default)
    {
        CheckPrefillArguments(text);
        cancellationToken.ThrowIfCancellationRequested();
        EnterCall();
        try
        {
            _machine.ThrowIfRefused(SessionCall.Prefill);
            // A token cancelled before the work item starts cancels the task, and the prefill never runs. The
            // finally releases the gate in both cases.
            await Task.Run(() => PrefillNative(text), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitCall();
        }
    }

    /// <summary>Releases the session. It doesn't wait for a call in progress on the session: the native session
    /// is released when that call ends.</summary>
    /// <remarks>A call in progress holds a reference on the native session, so the native delete runs on the
    /// thread of that call when it ends. The native engine is released after its last conversation or session is
    /// deleted.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _session.Dispose();
    }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Whether a call holds the one-call-at-a-time gate, for tests.</summary>
    internal bool IsCallInProgress => Volatile.Read(ref _callInProgress) != 0;

    /// <summary>The native session, for the calls that hold a reference on it.</summary>
    internal SessionHandle Handle => _session;

    /// <summary>The text of the start token that a first prefill refuses at its head, or <c>null</c>.</summary>
    internal string? RefusedStartTokenText => _startTokenText;

    /// <summary>The argument checks of <see cref="Prefill"/>, in the order of the exception precedence: disposed,
    /// then the text, then the start token. The busy check and the state check come after them.</summary>
    private void CheckPrefillArguments(string text)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(text);
        // run_prefill needs at least one input item, and an empty text input is not measured.
        ArgumentException.ThrowIfNullOrEmpty(text);
        // Read without the gate, so that a bad argument is reported before a busy session. Only a prefill that
        // runs on a Fresh session at the same time can change the outcome, and that caller is refused either way.
        if (RefusesStartToken(State, text, _startTokenText))
            throw new ArgumentException(
                $"The text starts with the model's start token \"{_startTokenText}\". The session adds the start " +
                "token itself before the first prefill: remove it from the text.", nameof(text));
    }

    /// <summary>Takes the one-call-at-a-time gate.</summary>
    /// <exception cref="InvalidOperationException">Another call holds it.</exception>
    internal void EnterCall()
    {
        if (Interlocked.CompareExchange(ref _callInProgress, 1, 0) != 0)
            throw new InvalidOperationException(
                "Another call on this session is in progress. A session runs one call at a time.");
    }

    /// <summary>Releases the one-call-at-a-time gate.</summary>
    internal void ExitCall() => Volatile.Write(ref _callInProgress, 0);

    /// <summary>Runs the native prefill with the gate held and the state already checked, and moves the state to
    /// its outcome.</summary>
    private unsafe void PrefillNative(string text)
    {
        bool referenced = false;
        try
        {
            // Throws ObjectDisposedException when Dispose ran after the entry check; no native call was made, so
            // the state stays as it is.
            _session.DangerousAddRef(ref referenced);
            bool succeeded = false;
            try
            {
                using InputDataHandle input = LiteRtEmbeddingEngine.CreateTextInput(text);
                nint inputPtr = input.Ptr;
                LiteRtStatusCode status = LiteRtLmNative.litert_lm_session_run_prefill(_session.Ptr, &inputPtr, 1);
                // The exception reads the report before the input is deleted and before the release below, which
                // can delete the session and the engine.
                if (status != LiteRtStatusCode.Ok)
                    throw NativeError.Exception(
                        status, "litert_lm_session_run_prefill failed.", report => PrefillFailureHint(status, report?.Message));
                succeeded = true;
            }
            finally
            {
                // Any failure latches Faulted: after a failed prefill, a later prefill returned OK at once and a
                // decode stream did not call back within 30 s (measured on v0.20.0, win-x64, gemma-4 E2B).
                _machine.Complete(SessionCall.Prefill, succeeded ? SessionCallOutcome.Succeeded : SessionCallOutcome.Failed);
            }
        }
        finally
        {
            if (referenced)
                _session.DangerousRelease();
        }
    }

    /// <summary>The hint for a failed prefill: <see cref="DoesNotFitHint"/> for
    /// <see cref="LiteRtStatusCode.ResourceExhausted"/>, and for <see cref="LiteRtStatusCode.InvalidArgument"/>
    /// or <see cref="LiteRtStatusCode.FailedPrecondition"/> only when the runtime's reason is one of the forms the
    /// runtime gives for input that does not fit; otherwise <c>null</c>.</summary>
    /// <param name="status">The status the prefill returned.</param>
    /// <param name="message">The runtime's reason, with or without its leading status name; <c>null</c> when it
    /// gave none.</param>
    internal static string? PrefillFailureHint(LiteRtStatusCode status, string? message)
    {
        if (status == LiteRtStatusCode.ResourceExhausted)
            return DoesNotFitHint;
        string reason = NativeError.Detail(status, message);
        bool doesNotFit = status switch
        {
            LiteRtStatusCode.InvalidArgument => reason.StartsWith("Input token ids are too long", StringComparison.Ordinal),
            LiteRtStatusCode.FailedPrecondition =>
                reason.StartsWith("Prefill input length exceeds available state entries", StringComparison.Ordinal)
                || reason.StartsWith("Chosen prefill work group size exceeds available state entries", StringComparison.Ordinal),
            _ => false,
        };
        return doesNotFit ? DoesNotFitHint : null;
    }

    /// <summary>
    /// The text of <paramref name="startToken"/> that a session refuses at the head of its first prefill, or
    /// <c>null</c> when the rule does not apply: no start token, a start token of several ids, an id whose text is
    /// empty, a <see cref="LiteRtTokenKind.String"/> start token that is not one token whose id detokenizes back
    /// to it, or a tokenizer call that throws <see cref="LiteRtException"/>.
    /// </summary>
    /// <remarks>An id start token is not required to come back from <paramref name="tokenize"/>: Gemma 4's
    /// <c>&lt;bos&gt;</c> (id 2) tokenizes as plain text into three ids, yet a first prefill that starts with it
    /// takes one position more than the same text without it, which reads as a second start token (positions
    /// measured on LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and GPU; the second start token is inferred from
    /// them).</remarks>
    internal static string? StartTokenText(
        LiteRtTokenUnion? startToken, Func<string, int[]> tokenize, Func<int[], string> detokenize)
    {
        if (startToken is null)
            return null;
        try
        {
            if (startToken.Kind == LiteRtTokenKind.Ids)
            {
                if (startToken.Ids.Count != 1)
                    return null;
                string text = detokenize([startToken.Ids[0]]);
                return text.Length > 0 ? text : null;
            }
            string? literal = startToken.Text;
            return !string.IsNullOrEmpty(literal) && LiteRtEngine.SingleTokenOf(literal, tokenize, detokenize) is not null
                ? literal
                : null;
        }
        catch (LiteRtException)
        {
            return null;
        }
    }

    /// <summary>Whether a prefill of <paramref name="text"/> in <paramref name="state"/> is refused because it
    /// starts with <paramref name="startTokenText"/>: only on a <see cref="LiteRtSessionState.Fresh"/> session,
    /// and only when the model has a start-token text (<see cref="StartTokenText"/>).</summary>
    internal static bool RefusesStartToken(LiteRtSessionState state, string text, string? startTokenText) =>
        state == LiteRtSessionState.Fresh
        && startTokenText is { Length: > 0 }
        && text.StartsWith(startTokenText, StringComparison.Ordinal);
}
