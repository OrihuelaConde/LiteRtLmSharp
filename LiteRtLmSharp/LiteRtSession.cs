using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>A raw prefill-and-decode session on a <see cref="LiteRtEngine"/>.</summary>
/// <remarks>
/// <para>
/// A session applies no template, keeps no message history and parses no tool calls or reasoning channels: the
/// text you prefill reaches the tokenizer as given. The runtime adds one position before the first prefill, for
/// the model's start token (see <see cref="Prefill"/>). Create a session with
/// <see cref="LiteRtEngine.CreateSession"/>.
/// </para>
/// <para>
/// <b>What was measured.</b> The runtime behavior these docs describe was measured on LiteRT-LM v0.20.0, win-x64,
/// gemma-4 E2B, on CPU and GPU. A sentence that names another model was measured on the same version, platform
/// and backends. Other platforms and models were not measured.
/// </para>
/// <para>
/// <b>Prefill, then decode.</b> Add the prompt text with <see cref="Prefill"/>, then generate the reply with
/// <see cref="Decode"/>, <see cref="DecodeAsync"/> or <see cref="DecodeStreamingAsync"/>. After a reply, prefill
/// more text before the next decode. A cancel, from a token, from leaving a stream early or from
/// <see cref="Dispose"/>, is sent to the runtime at the next piece of the reply and leaves the session
/// <see cref="LiteRtSessionState.Cancelled"/>, unless the reply had already reached its last piece. The runtime
/// stops at that piece.
/// </para>
/// <para>
/// <b>Counting the context.</b> <see cref="LiteRtEngine.Tokenize"/> does not always give the positions a prefill
/// takes. On Phi-4-mini a text that starts with a marker token, such as <c>&lt;|user|&gt;</c>, took 3 to 4
/// positions more than its tokens, and the start token takes one position although the model reports it as 7
/// tokens.
/// </para>
/// <para>
/// <b>One call at a time.</b> A session runs one call at a time and refuses a second call with
/// <see cref="InvalidOperationException"/> instead of waiting for the first. <see cref="State"/> and
/// <see cref="Dispose"/> can be called from any thread at any time.
/// </para>
/// <para>
/// <b>Lifetime.</b> The session keeps the native engine alive, so the engine can be disposed first. A session that
/// reaches <see cref="LiteRtSessionState.Cancelled"/>, or <see cref="LiteRtSessionState.Faulted"/> after any
/// failure other than a failed prefill, accepts only <see cref="Dispose"/>: dispose it and create a new session.
/// From <see cref="LiteRtSessionState.ContextFull"/>, and from <see cref="LiteRtSessionState.Faulted"/> after a
/// failed prefill, <see cref="RewindTo"/> and <see cref="Reset"/> recover the session.
/// </para>
/// <para>
/// <b>Checkpoints, rewind and reset.</b> <see cref="SaveCheckpoint"/>, <see cref="RewindTo"/> and
/// <see cref="Reset"/> are experimental: a call to them is the compile error LITERTLM001 until you suppress it,
/// and they can change or be removed in a later version.
/// </para>
/// </remarks>
public sealed class LiteRtSession : IDisposable
{
    private readonly SessionHandle _session;
    private readonly SessionStateMachine _machine;
    // The text of the model's start token that the first prefill refuses at its head, or null when the rule does
    // not apply. Read from the engine at creation, so it holds after the engine object is disposed.
    private readonly string? _startTokenText;
    // The one-call-at-a-time gate: 1 while a call runs. Every member except State and Dispose takes it before
    // any native call, with a compare-and-swap that refuses instead of waiting: waiting would deadlock a caller
    // that prefills from inside its own read loop.
    private int _callInProgress;
    private int _disposed;
    // The number of checkpoints saved on the session. Each save takes the next number for its label, so a label
    // never repeats on a session, also across Reset: the runtime accepts a second save under a label it holds and
    // then rewinds that label to the later save. Changed only with the gate held.
    private long _checkpointCount;
    // The decode that holds the gate, from before its native call until settlement, so that Dispose can request
    // its cancel. Written with a full fence on both sides (here and in Dispose), so a decode that starts while
    // Dispose runs either sees the disposal or is seen by it.
    private SessionStream? _runningDecode;

    /// <summary>The hint added to a prefill failure that says the input does not fit the remaining
    /// context.</summary>
    internal const string DoesNotFitHint =
        "The input does not fit the remaining context. Prefill a shorter text after RewindTo or Reset, or on a " +
        "new session, or load the engine with a larger LiteRtEngineOptions.MaxNumTokens.";

    internal LiteRtSession(SessionHandle session, string? startTokenText)
        : this(session, startTokenText, LiteRtSessionState.Fresh)
    {
    }

    /// <summary>Initializes a session in <paramref name="initialState"/>. Only tests start a session in another
    /// state than <see cref="LiteRtSessionState.Fresh"/>.</summary>
    internal LiteRtSession(SessionHandle session, string? startTokenText, SessionMachineState initialState)
    {
        _session = session;
        _startTokenText = startTokenText;
        _machine = new SessionStateMachine(initialState);
    }

    internal static unsafe LiteRtSession Create(LiteRtEngine engine, LiteRtSessionOptions? options)
    {
        // The reference comes before any native call on the engine, the start-token lookup included: an engine
        // disposed on another thread after the caller's check then throws ObjectDisposedException here instead
        // of leaving a pointer to a deleted engine. On success the session keeps it until it is deleted
        // (SessionHandle releases it), so disposing the engine first never frees it under a live session.
        bool engineReferenced = false;
        engine.Handle.DangerousAddRef(ref engineReferenced);
        try
        {
            string? startTokenText = engine.SessionStartTokenText;

            // The config is always created: the template setter needs one. The header does not say whether the
            // session keeps a reference to it, so it lives until create_session returns and is deleted then.
            nint configPtr;
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_session_config_create(&configPtr),
                nameof(LiteRtLmNative.litert_lm_session_config_create));
            using var config = new SessionConfigHandle(configPtr);
            // Always off: the text reaches the tokenizer as given, and the runtime then adds the start token on
            // the first prefill of a fresh session.
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_session_config_set_apply_prompt_template(config.Ptr, false),
                nameof(LiteRtLmNative.litert_lm_session_config_set_apply_prompt_template));
            // Called only with a positive value: 0 means no cap and sends nothing, and LiteRtSessionOptions
            // refuses a negative value.
            if (options is { MaxOutputTokens: > 0 and var maxOutputTokens })
                NativeError.ThrowIfFailed(
                    LiteRtLmNative.litert_lm_session_config_set_max_output_tokens(config.Ptr, maxOutputTokens),
                    nameof(LiteRtLmNative.litert_lm_session_config_set_max_output_tokens));
            NativeSampler.Apply(config.Ptr, options?.Sampler);

            nint sessionPtr;
            LiteRtStatusCode status = LiteRtLmNative.litert_lm_engine_create_session(engine.Handle.Ptr, config.Ptr, &sessionPtr);
            // The exception reads the report here, before the catch below releases the reference, which can
            // delete the engine.
            if (status != LiteRtStatusCode.Ok)
                throw NativeError.Exception(status, "litert_lm_engine_create_session failed.");
            // From here the session handle owns the reference.
            var handle = new SessionHandle(sessionPtr, engine.Handle);
            engineReferenced = false;
            return new LiteRtSession(handle, startTokenText);
        }
        catch
        {
            if (engineReferenced)
                engine.Handle.DangerousRelease();
            throw;
        }
    }

    /// <summary>Gets the state of the session. It can be read from any thread, also after
    /// <see cref="Dispose"/>.</summary>
    /// <remarks><see cref="Dispose"/> does not change the state: after it, the state is the one the session had,
    /// or <see cref="LiteRtSessionState.Cancelled"/> when the disposal cancelled a running decode.</remarks>
    public LiteRtSessionState State => _machine.State;

    /// <summary>Appends <paramref name="text"/> to the session's context as given, and blocks until the runtime
    /// has processed it.</summary>
    /// <param name="text">The text to add, with any markup the model expects already in it.</param>
    /// <remarks>
    /// <para>
    /// On a <see cref="LiteRtSessionState.Fresh"/> session the runtime adds one position before the text, for the
    /// model's start token, so the text must not start with the start token's text. When the model's start token
    /// is a single token, such as <c>&lt;bos&gt;</c> on Gemma 4, the session refuses such a text. A session is
    /// Fresh again after <see cref="Reset"/>, and after a <see cref="RewindTo"/> a checkpoint saved while it was
    /// Fresh; the runtime then adds the start token again on the next prefill.
    /// </para>
    /// <para>
    /// A later prefill is not checked, but the start token's text does not belong in it either. On gemma-4 E2B
    /// and E4B a later prefill that starts with <c>&lt;bos&gt;</c> takes one position, not the three of its plain
    /// tokens, as a second start token would. On Ministral-3-3B the tokenizer itself turns <c>&lt;s&gt;</c> into
    /// the start token.
    /// </para>
    /// <para>
    /// The call blocks the calling thread, and a prefill can't be cut once it starts. It waits while another
    /// session of the engine decodes: behind a 160-token decode it took 4.6 to 5.1 s on CPU and 1.3 s on GPU.
    /// </para>
    /// <para>
    /// Any failure leaves the session <see cref="LiteRtSessionState.Faulted"/>, because after a failed prefill the
    /// runtime returns OK for later prefills and decodes on the session, and a decode then delivers no reply. From
    /// that state <see cref="RewindTo"/> and <see cref="Reset"/> recover the session. Input that does not fit the
    /// remaining context fails with <see cref="LiteRtStatusCode.ResourceExhausted"/>,
    /// <see cref="LiteRtStatusCode.InvalidArgument"/> or <see cref="LiteRtStatusCode.FailedPrecondition"/>,
    /// depending on how it does not fit.
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
    /// <see cref="LiteRtSessionState.Faulted"/>, from which <see cref="RewindTo"/> and <see cref="Reset"/>
    /// recover the session, and <see cref="LiteRtException.StatusCode"/> holds the status the runtime
    /// returned.</exception>
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

    /// <summary>Generates a reply to the prefilled input and returns its text.</summary>
    /// <returns>The reply. When the context filled during the decode, the text up to that point, and
    /// <see cref="State"/> is <see cref="LiteRtSessionState.ContextFull"/>, from which <see cref="RewindTo"/> and
    /// <see cref="Reset"/> recover the session.</returns>
    /// <remarks>
    /// <para>
    /// The call blocks the calling thread until the decode ends. No token can cut it; a
    /// <see cref="Dispose"/> from another thread cancels it, as the class remarks describe. After a reply that
    /// ends normally, <see cref="State"/> is <see cref="LiteRtSessionState.Decoded"/>: prefill more text before
    /// the next decode.
    /// </para>
    /// <para>
    /// The reply is the text the model generates, with no template markup removed or added. After a reply that
    /// ends by itself, the context holds one position more than the prompt and the reply; that it holds the
    /// end-of-turn token is inferred from the count. So the next turn starts with the user turn and no
    /// end-of-turn marker: on Gemma 4, prefill exactly <c>&lt;|turn&gt;user\n{text}&lt;turn|&gt;\n&lt;|turn&gt;model\n</c>,
    /// with no <c>&lt;turn|&gt;</c> or line break in front, which gave the replies of a conversation. With
    /// <c>&lt;turn|&gt;\n</c> in front, as a rendered history shows it, most replies differed from the
    /// conversation's.
    /// </para>
    /// <para>
    /// A reply that reaches the <see cref="LiteRtSessionOptions.MaxOutputTokens"/> cap ends without an error, the
    /// session is <see cref="LiteRtSessionState.Decoded"/>, and the context holds no extra position after it. The
    /// same next-turn text gave the replies of a conversation after a capped reply too.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed, or <see cref="Dispose"/> cancelled
    /// the decode; in the second case <see cref="State"/> is <see cref="LiteRtSessionState.Cancelled"/>.</exception>
    /// <exception cref="InvalidOperationException">Another call on the session is in progress, or the session is
    /// not <see cref="LiteRtSessionState.Prefilled"/>.</exception>
    /// <exception cref="LiteRtException">The runtime reported an error. <see cref="State"/> is then
    /// <see cref="LiteRtSessionState.Faulted"/>, and the session accepts only <see cref="Dispose"/>.</exception>
    public string Decode()
    {
        SessionStream stream = BeginDecode(SessionCall.Decode, CancellationToken.None);
        StartDecode(stream);
        // The final chunk can arrive inside run_decode_async on this thread; the wait then returns at once.
        stream.WaitAndSettle();
        return stream.TakeResult();
    }

    /// <summary>Generates a reply to the prefilled input without blocking a thread.</summary>
    /// <param name="cancellationToken">Cancels the decode: the cancel is sent to the runtime at the next piece of
    /// the reply, and the session is then <see cref="LiteRtSessionState.Cancelled"/>, unless the reply had
    /// already reached its last piece.</param>
    /// <returns>A task whose result is the reply, as <see cref="Decode"/> returns it.</returns>
    /// <remarks>
    /// <para>
    /// The session counts as busy from this call until the task completes. A token that fires after the reply
    /// reached its last piece, and before the decode ends, does not cancel the decode: <see cref="State"/> then
    /// follows how the reply ended, and the task still throws <see cref="OperationCanceledException"/>. A token
    /// that fires after the decode ended changes nothing.
    /// </para>
    /// <para>See <see cref="Decode"/> for the other outcomes.</para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed, or <see cref="Dispose"/> cancelled
    /// the decode.</exception>
    /// <exception cref="InvalidOperationException">Another call on the session is in progress, or the session is
    /// not <see cref="LiteRtSessionState.Prefilled"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. When it was
    /// cancelled before the call, the session is unchanged.</exception>
    /// <exception cref="LiteRtException">The runtime reported an error. <see cref="State"/> is then
    /// <see cref="LiteRtSessionState.Faulted"/>, and the session accepts only <see cref="Dispose"/>.</exception>
    public async Task<string> DecodeAsync(CancellationToken cancellationToken = default)
    {
        SessionStream stream = BeginDecode(SessionCall.DecodeAsync, cancellationToken);
        try
        {
            StartDecode(stream);
            await stream.Settled.ConfigureAwait(false);
        }
        finally
        {
            stream.DisposeRegistration();
        }
        return stream.TakeResult();
    }

    /// <summary>Generates a reply to the prefilled input and streams it as text pieces.</summary>
    /// <param name="cancellationToken">Cancels the decode: the cancel is sent to the runtime at the next piece of
    /// the reply, and the session is then <see cref="LiteRtSessionState.Cancelled"/>, unless the reply had
    /// already reached its last piece.</param>
    /// <returns>The pieces of the reply, in order. Their concatenation is the reply, as <see cref="Decode"/>
    /// returns it.</returns>
    /// <remarks>
    /// <para>
    /// The decode starts at the first <see cref="IAsyncEnumerator{T}.MoveNextAsync"/>, and the session counts as
    /// busy until the decode ends. A piece holds whole characters: the bytes of a character that the runtime
    /// splits are kept until the character is complete.
    /// </para>
    /// <para>
    /// Leaving the loop before the last piece, with <c>break</c> or an exception, cancels the decode, and the
    /// enumerator's disposal waits for the last chunk of the decode from the runtime. The session is then
    /// <see cref="LiteRtSessionState.Cancelled"/>, unless the reply had already reached its last piece. An
    /// enumerator that is dropped without disposal does not cancel: the decode runs to its end and releases the
    /// session then.
    /// </para>
    /// <para>See <see cref="Decode"/> and <see cref="DecodeAsync"/> for the other outcomes.</para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed, or <see cref="Dispose"/> cancelled
    /// the decode.</exception>
    /// <exception cref="InvalidOperationException">Another call on the session is in progress, or the session is
    /// not <see cref="LiteRtSessionState.Prefilled"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="LiteRtException">The runtime reported an error. <see cref="State"/> is then
    /// <see cref="LiteRtSessionState.Faulted"/>, and the session accepts only <see cref="Dispose"/>.</exception>
    public async IAsyncEnumerable<string> DecodeStreamingAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SessionStream stream = BeginDecode(SessionCall.DecodeStreamingAsync, cancellationToken);
        ChannelReader<string> reader = stream.Reader;
        try
        {
            StartDecode(stream);
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out string? piece))
                    yield return piece;
            }
            await stream.Settled.ConfigureAwait(false);
            stream.ThrowIfFailed();
        }
        finally
        {
            // Nothing to do after settlement. Otherwise the consumer left before the end: request the cancel,
            // which the callback issues at the next chunk that is not final, and wait for the final chunk. No
            // timeout: settlement is the only point after which the session can be used or deleted safely.
            if (!stream.Settled.IsCompleted)
            {
                stream.RequestCancel(CancelRequester.Caller);
                await stream.Settled.ConfigureAwait(false);
            }
            stream.DisposeRegistration();
        }
    }

    /// <summary>Records the session's current point, so that <see cref="RewindTo"/> can return to it.</summary>
    /// <returns>A checkpoint, valid only on this session.</returns>
    /// <remarks>
    /// <para>
    /// Accepted when the session is <see cref="LiteRtSessionState.Fresh"/>, <see cref="LiteRtSessionState.Prefilled"/>
    /// or <see cref="LiteRtSessionState.Decoded"/>; the state does not change. A checkpoint saved on a Fresh session
    /// survives <see cref="Reset"/>. <see cref="LiteRtSessionCheckpoint"/> says what drops a checkpoint.
    /// </para>
    /// <para>
    /// The call blocks the calling thread. On an idle engine a save took at most 0.1 ms, and about 1 ms on a Fresh
    /// session. The first <see cref="SaveCheckpoint"/> or <see cref="RewindTo"/> on this session after another
    /// session or a conversation used the engine took 5 to 14 ms on CPU and 285 to 392 ms on GPU. Whether the call
    /// waits while another session generates was not measured.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Another call on the session is in progress, or the session is
    /// <see cref="LiteRtSessionState.ContextFull"/>, <see cref="LiteRtSessionState.Faulted"/> or
    /// <see cref="LiteRtSessionState.Cancelled"/>.</exception>
    /// <exception cref="LiteRtException">The runtime failed to save the checkpoint. <see cref="State"/> is then
    /// <see cref="LiteRtSessionState.Faulted"/>, and the session accepts only <see cref="Dispose"/>.</exception>
    [Experimental(ExperimentalApis.SessionCheckpoints, UrlFormat = ExperimentalApis.SessionCheckpointsUrl)]
    public LiteRtSessionCheckpoint SaveCheckpoint()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        EnterCall();
        try
        {
            _machine.ThrowIfRefused(SessionCall.SaveCheckpoint);
            // Numbered before the native call, so that no label is used twice, a failed save's included.
            var checkpoint = new LiteRtSessionCheckpoint(this, _checkpointCount++, _machine.State);
            RunCheckpointNative(SessionCall.SaveCheckpoint, checkpoint.Label, rewoundTo: null);
            return checkpoint;
        }
        finally
        {
            ExitCall();
        }
    }

    /// <summary>Returns the session to the point that <paramref name="checkpoint"/> recorded.</summary>
    /// <param name="checkpoint">A checkpoint that <see cref="SaveCheckpoint"/> returned on this session.</param>
    /// <remarks>
    /// <para>
    /// The session's context and <see cref="State"/> become the ones the checkpoint recorded:
    /// <see cref="LiteRtSessionState.Fresh"/>, <see cref="LiteRtSessionState.Prefilled"/> or
    /// <see cref="LiteRtSessionState.Decoded"/>. The runtime drops the checkpoints saved at a later point. After a
    /// rewind to a checkpoint saved on a Fresh session, the runtime adds the start token again on the next prefill
    /// (see <see cref="Prefill"/>). A decode after the rewind gave the same reply as the first decode from that
    /// point, and as a new session that reached the point the same way, also when another session or a
    /// conversation used the engine in between.
    /// </para>
    /// <para>
    /// Accepted when the session is Fresh, Prefilled, Decoded or <see cref="LiteRtSessionState.ContextFull"/>, and
    /// when it is <see cref="LiteRtSessionState.Faulted"/> because a prefill failed. From those last two states
    /// the rewind recovers the session. A session Faulted by any other failure, or
    /// <see cref="LiteRtSessionState.Cancelled"/>, accepts only <see cref="Dispose"/>.
    /// </para>
    /// <para>
    /// The call blocks the calling thread. Its cost is the one <see cref="SaveCheckpoint"/> describes. Whether the
    /// call waits while another session generates was not measured.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="checkpoint"/> was saved on another session.</exception>
    /// <exception cref="InvalidOperationException">Another call on the session is in progress; the session's state
    /// does not accept the call; or the runtime has dropped the checkpoint, as
    /// <see cref="LiteRtSessionCheckpoint"/> describes. In the last case
    /// <see cref="Exception.InnerException"/> is the <see cref="LiteRtException"/> with
    /// <see cref="LiteRtStatusCode.NotFound"/>, and the session is unchanged.</exception>
    /// <exception cref="LiteRtException">The runtime failed to rewind for another reason. <see cref="State"/> is
    /// then <see cref="LiteRtSessionState.Faulted"/>, and the session accepts only <see cref="Dispose"/>.</exception>
    [Experimental(ExperimentalApis.SessionCheckpoints, UrlFormat = ExperimentalApis.SessionCheckpointsUrl)]
    public void RewindTo(LiteRtSessionCheckpoint checkpoint)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!ReferenceEquals(checkpoint.Owner, this))
            throw new ArgumentException(
                "The checkpoint was saved on another session. A checkpoint is valid only on the session that saved it.",
                nameof(checkpoint));
        EnterCall();
        try
        {
            _machine.ThrowIfRefused(SessionCall.RewindTo);
            RunCheckpointNative(SessionCall.RewindTo, checkpoint.Label, checkpoint.Recorded);
        }
        finally
        {
            ExitCall();
        }
    }

    /// <summary>Clears the session's context and returns the session to
    /// <see cref="LiteRtSessionState.Fresh"/>.</summary>
    /// <remarks>
    /// <para>
    /// After a reset the runtime adds the start token again on the next prefill (see <see cref="Prefill"/>), and
    /// the next turn gave the same reply as on a new session. The runtime drops every checkpoint except those saved
    /// while the session was Fresh.
    /// </para>
    /// <para>
    /// Accepted in the same states as <see cref="RewindTo"/>: Fresh, <see cref="LiteRtSessionState.Prefilled"/>,
    /// <see cref="LiteRtSessionState.Decoded"/>, <see cref="LiteRtSessionState.ContextFull"/>, and
    /// <see cref="LiteRtSessionState.Faulted"/> because a prefill failed.
    /// </para>
    /// <para>
    /// The call blocks the calling thread. On an idle engine it took about 1 ms, and 12 to 16 ms on GPU after a
    /// prefill. Like <see cref="Prefill"/> and <see cref="LiteRtEngine.CreateSession"/>, it waits while another
    /// session of the engine generates: behind a 160-token decode it took 4.4 to 4.7 s on CPU and 1.4 to 1.5 s on
    /// GPU.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Another call on the session is in progress, or the session's
    /// state does not accept the call.</exception>
    /// <exception cref="LiteRtException">The runtime failed to reset the session. <see cref="State"/> is then
    /// <see cref="LiteRtSessionState.Faulted"/>, and the session accepts only <see cref="Dispose"/>.</exception>
    [Experimental(ExperimentalApis.SessionCheckpoints, UrlFormat = ExperimentalApis.SessionCheckpointsUrl)]
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        EnterCall();
        try
        {
            _machine.ThrowIfRefused(SessionCall.Reset);
            RunCheckpointNative(SessionCall.Reset, label: null, rewoundTo: null);
        }
        finally
        {
            ExitCall();
        }
    }

    /// <summary>Releases the session. It doesn't wait for a call in progress on the session: the native session
    /// is released when that call ends.</summary>
    /// <remarks>A running decode is cancelled as the class remarks describe, and then throws
    /// <see cref="ObjectDisposedException"/>, unless the reply had already reached its last piece. A call in
    /// progress holds a reference on the native session, so the native delete runs on the thread that ends that
    /// call. The native engine is released after its last conversation or session is deleted.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // Before the handle's disposal, so that a running decode stops at its next chunk and its settlement runs
        // the native delete.
        Volatile.Read(ref _runningDecode)?.RequestCancel(CancelRequester.Dispose);
        _session.Dispose();
    }

    /// <summary>The state machine, for the decode settlement.</summary>
    internal SessionStateMachine Machine => _machine;

    /// <summary>
    /// Checks a decode call and prepares its stream, without calling native code: disposed, then the token, then
    /// busy, then the state. On success the caller holds the gate, the stream is the running decode, holds a
    /// reference on the native session and has a <see cref="GCHandle"/>, and the token is registered. On failure
    /// nothing is held.
    /// </summary>
    internal SessionStream BeginDecode(SessionCall call, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        EnterCall();
        SessionStream? stream = null;
        bool referenced = false;
        try
        {
            _machine.ThrowIfRefused(call);
            stream = new SessionStream(this, call, cancellationToken);
            Interlocked.Exchange(ref _runningDecode, stream);
            // Throws ObjectDisposedException when the handle is already disposed.
            _session.DangerousAddRef(ref referenced);
            // A Dispose that read the running decode before it was set has already marked the session disposed.
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            stream.Attach(GCHandle.Alloc(stream), referenced);
            stream.RegisterToken();
            return stream;
        }
        catch
        {
            if (stream is not null)
            {
                stream.DisposeRegistration();
                // Native code never saw the handle, so it is freed at once.
                stream.FreeUnusedHandle();
                Interlocked.CompareExchange(ref _runningDecode, null, stream);
            }
            if (referenced)
                _session.DangerousRelease();
            ExitCall();
            throw;
        }
    }

    /// <summary>Starts the native decode of a stream from <see cref="BeginDecode"/>. A start that fails settles
    /// the stream at once as <see cref="LiteRtSessionState.Faulted"/>.</summary>
    internal unsafe void StartDecode(SessionStream stream)
    {
        LiteRtStatusCode status;
        try
        {
            status = LiteRtLmNative.litert_lm_session_run_decode_async(
                _session.Ptr, &SessionStream.OnChunk, GCHandle.ToIntPtr(stream.Handle));
        }
        catch (Exception ex)
        {
            stream.SettleFailedStart(ex);
            return;
        }
        if (status != LiteRtStatusCode.Ok)
        {
            // Read the report first, on this thread, before settlement releases the reference.
            stream.SettleFailedStart(NativeError.Exception(status, "litert_lm_session_run_decode_async failed."));
            return;
        }
        // Settlement may run as soon as this returns, so nothing of the stream is touched after it.
        stream.OnCallReturned();
    }

    /// <summary>Clears the running decode at its settlement.</summary>
    internal void ClearRunningDecode(SessionStream stream)
        => Interlocked.CompareExchange(ref _runningDecode, null, stream);

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Whether a call holds the one-call-at-a-time gate, for tests.</summary>
    internal bool IsCallInProgress => Volatile.Read(ref _callInProgress) != 0;

    /// <summary>The number of saves the session has numbered, for tests.</summary>
    internal long CheckpointCount => Volatile.Read(ref _checkpointCount);

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
            LiteRtStatusCode? failedStatus = null;
            try
            {
                using InputDataHandle input = LiteRtEmbeddingEngine.CreateTextInput(text);
                nint inputPtr = input.Ptr;
                LiteRtStatusCode status = LiteRtLmNative.litert_lm_session_run_prefill(_session.Ptr, &inputPtr, 1);
                // The exception reads the report before the input is deleted and before the release below, which
                // can delete the session and the engine.
                if (status != LiteRtStatusCode.Ok)
                {
                    failedStatus = status;
                    throw NativeError.Exception(
                        status, "litert_lm_session_run_prefill failed.", report => PrefillFailureHint(status, report?.Message));
                }
                succeeded = true;
            }
            finally
            {
                // Any failure latches Faulted: after a failed prefill, a later prefill returned OK at once and a
                // decode stream did not call back within 30 s (measured, as the class remarks say). The
                // fault records the status, for the rule on which faults a rewind or a reset recovers.
                _machine.Complete(
                    SessionCall.Prefill, succeeded ? SessionCallOutcome.Succeeded : SessionCallOutcome.Failed, failedStatus);
            }
        }
        finally
        {
            if (referenced)
                _session.DangerousRelease();
        }
    }

    /// <summary>Runs the native save, rewind or reset (<c>rewind_to_step(0)</c>) of <paramref name="call"/> with
    /// the gate held and the state already checked, and moves the state to its outcome.</summary>
    /// <param name="call"><see cref="SessionCall.SaveCheckpoint"/>, <see cref="SessionCall.RewindTo"/> or
    /// <see cref="SessionCall.Reset"/>.</param>
    /// <param name="label">The checkpoint's NUL-terminated label, for a save or a rewind; <c>null</c> for a
    /// reset.</param>
    /// <param name="rewoundTo">For a rewind, the state the checkpoint recorded.</param>
    private unsafe void RunCheckpointNative(SessionCall call, byte[]? label, LiteRtSessionState? rewoundTo)
    {
        bool referenced = false;
        try
        {
            // Throws ObjectDisposedException when Dispose ran after the entry check; no native call was made, so
            // the state stays as it is.
            _session.DangerousAddRef(ref referenced);
            SessionCallOutcome outcome = SessionCallOutcome.Failed;
            LiteRtStatusCode? failedStatus = null;
            try
            {
                LiteRtStatusCode status;
                string function;
                // The runtime copies the label during the call, so the buffer is pinned only for its duration.
                fixed (byte* labelPtr = label)
                {
                    switch (call)
                    {
                        case SessionCall.SaveCheckpoint:
                            function = nameof(LiteRtLmNative.litert_lm_session_save_checkpoint);
                            status = LiteRtLmNative.litert_lm_session_save_checkpoint(_session.Ptr, labelPtr);
                            break;
                        case SessionCall.RewindTo:
                            function = nameof(LiteRtLmNative.litert_lm_session_rewind_to_checkpoint);
                            status = LiteRtLmNative.litert_lm_session_rewind_to_checkpoint(_session.Ptr, labelPtr);
                            break;
                        case SessionCall.Reset:
                            function = nameof(LiteRtLmNative.litert_lm_session_rewind_to_step);
                            status = LiteRtLmNative.litert_lm_session_rewind_to_step(_session.Ptr, 0);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(call), call, null);
                    }
                }
                if (status == LiteRtStatusCode.Ok)
                {
                    outcome = SessionCallOutcome.Succeeded;
                    return;
                }
                failedStatus = status;
                // The exception reads the report here, before the release below, which can delete the session and
                // the engine.
                LiteRtException error = NativeError.Exception(status, $"{function} failed.");
                // A rewind to a checkpoint the runtime dropped answers NOT_FOUND and was measured to change neither
                // the session's position nor its next decode, so the state stays.
                if (call == SessionCall.RewindTo && status == LiteRtStatusCode.NotFound)
                {
                    outcome = SessionCallOutcome.NotFound;
                    throw new InvalidOperationException(
                        "The session no longer holds the checkpoint: a RewindTo an earlier checkpoint or a Reset " +
                        "dropped it. The session is unchanged.", error);
                }
                throw error;
            }
            finally
            {
                _machine.Complete(call, outcome, failedStatus, outcome == SessionCallOutcome.Succeeded ? rewoundTo : null);
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
    /// takes one position more than the same text without it, which reads as a second start token (the positions
    /// were measured, as the class remarks say; the second start token is inferred from them).</remarks>
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
