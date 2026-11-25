using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>Who asked a running session decode to stop. The first request decides the exception of a cancelled
/// decode.</summary>
internal enum CancelRequester
{
    /// <summary>No one asked.</summary>
    None = 0,

    /// <summary>The caller: the token of <see cref="LiteRtSession.DecodeAsync"/> or of the stream, or a consumer
    /// that left the stream before its final chunk.</summary>
    Caller = 1,

    /// <summary><see cref="LiteRtSession.Dispose"/>.</summary>
    Dispose = 2,
}

/// <summary>
/// The state of one session decode, from before <c>litert_lm_session_run_decode_async</c> until settlement: the
/// text sink, a stateful UTF-8 decoder, the cancel flags and the references that settlement releases.
/// </summary>
/// <remarks>
/// <para>
/// The native callback (<see cref="OnChunk(nint, nint)"/>) only reads the chunk, copies its text, and on a chunk
/// that is not final issues the cancel a caller requested. At the final chunk it hands settlement off: to the
/// thread blocked in <see cref="LiteRtSession.Decode"/>, or to the thread pool. Settlement runs once, and only
/// after both the final chunk arrived and <c>run_decode_async</c> returned, because the final chunk can arrive
/// on the calling thread inside that call (measured on LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and GPU, on a
/// session whose context was full).
/// </para>
/// <para>
/// Settlement classifies the outcome, moves the session's state, releases the stream's reference on the native
/// session, releases the session's one-call gate, and then completes the channel and <see cref="Settled"/>, in
/// that order, so a consumer that sees the end already reads the settled state. The <see cref="GCHandle"/> that
/// native code holds is retired to the <see cref="SessionHandle"/>, which frees it after the native delete.
/// After settlement this object holds no reference to the session, so a retired state never keeps the session
/// alive.
/// </para>
/// </remarks>
internal sealed class SessionStream
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly SessionCall _call;
    private readonly nint _sessionPtr;
    private readonly Decoder _decoder = Utf8.GetDecoder();
    // Completed when both arrivals happened, for the blocking Decode, which then settles on its own thread.
    private readonly TaskCompletionSource? _arrived;
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Held until settlement.
    private LiteRtSession? _owner;
    private StringBuilder? _text;
    private Channel<string>? _channel;
    private CancellationToken _token;
    private CancellationTokenRegistration _registration;
    private GCHandle _gcHandle;
    private bool _referenced;

    // Two arrivals start settlement: the final chunk, and the return of run_decode_async.
    private int _arrivals = 2;
    private int _final;
    private int _settledFlag;
    private int _requester;
    private int _tokenFired;
    private volatile bool _cancelIssued;
    private string? _finalError;
    private Exception? _fault;

    // The outcome, read by the caller after Settled completes.
    private string? _result;
    private Exception? _exception;

    internal SessionStream(LiteRtSession owner, SessionCall call, CancellationToken token)
    {
        _owner = owner;
        _call = call;
        _sessionPtr = owner.Handle.Ptr;
        _token = token;
        if (call == SessionCall.DecodeStreamingAsync)
            _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                // Nothing depends on one writer: the chunks of one stream were not seen to overlap, but that is
                // not a contract of the header.
                SingleWriter = false,
            });
        else
            _text = new StringBuilder();
        if (call == SessionCall.Decode)
            _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Completes after settlement. It never faults: the outcome is read with <see cref="TakeResult"/> or
    /// <see cref="ThrowIfFailed"/>.</summary>
    internal Task Settled => _settled.Task;

    /// <summary>The text pieces of a stream.</summary>
    internal ChannelReader<string> Reader => _channel!.Reader;

    /// <summary>Whether the callback issued <c>litert_lm_session_cancel_process</c>.</summary>
    internal bool CancelIssued => _cancelIssued;

    /// <summary>Whether the final chunk, or a read failure that counts as one, arrived, or the decode settled
    /// without one after a failed start. Chunks are ignored from then on.</summary>
    internal bool FinalSeen => Volatile.Read(ref _final) != 0;

    /// <summary>The first requester of a cancel, or <see cref="CancelRequester.None"/>.</summary>
    internal CancelRequester Requester => (CancelRequester)Volatile.Read(ref _requester);

    /// <summary>The managed state for native code. Set by <see cref="LiteRtSession"/> before
    /// <c>run_decode_async</c>, together with the stream's reference on the native session.</summary>
    internal void Attach(GCHandle gcHandle, bool referenced)
    {
        _gcHandle = gcHandle;
        _referenced = referenced;
    }

    /// <summary>The handle whose pointer is the callback data.</summary>
    internal GCHandle Handle => _gcHandle;

    /// <summary>Frees the handle of a stream whose native decode never started.</summary>
    internal void FreeUnusedHandle()
    {
        if (_gcHandle.IsAllocated)
            _gcHandle.Free();
        _gcHandle = default;
    }

    /// <summary>Registers the caller's token: when it fires, it requests the cancel. Registered before
    /// <c>run_decode_async</c>, unregistered at settlement, and disposed by the caller that awaits the
    /// stream.</summary>
    internal void RegisterToken()
    {
        if (_token.CanBeCanceled)
            _registration = _token.UnsafeRegister(static s =>
            {
                var stream = (SessionStream)s!;
                Volatile.Write(ref stream._tokenFired, 1);
                stream.RequestCancel(CancelRequester.Caller);
            }, this);
    }

    /// <summary>Disposes the token registration. A token that fires after settlement changes nothing.</summary>
    internal void DisposeRegistration() => _registration.Dispose();

    /// <summary>Requests the cancel. The callback issues it at the next chunk that is not final; the first
    /// requester is kept.</summary>
    internal void RequestCancel(CancelRequester requester)
        => Interlocked.CompareExchange(ref _requester, (int)requester, (int)CancelRequester.None);

    /// <summary>The native stream callback. It calls only the three chunk getters and, on a chunk that is not
    /// final with a cancel requested, <c>litert_lm_session_cancel_process</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void OnChunk(nint callbackData, nint chunk)
    {
        // The whole body is guarded: an exception that leaves an [UnmanagedCallersOnly] frame ends the process.
        try
        {
            if (GCHandle.FromIntPtr(callbackData).Target is SessionStream stream)
                stream.Receive(chunk);
        }
        catch
        {
            // Receive guards itself and hands settlement off; nothing is left to report here.
        }
    }

    /// <summary>Reads one native chunk and passes it to <see cref="OnChunk(ReadOnlySpan{byte}, bool, string?)"/>.
    /// A failed read counts as the final chunk, with a fault, so the decode settles instead of waiting.</summary>
    private unsafe void Receive(nint chunk)
    {
        if (FinalSeen)
            return;
        try
        {
            // engine.h "Threading model": the callback calls no function on the engine, session or conversation
            // except cancel_process, so a failure is known only from the returned status; the thread's message
            // is not read. "Is final" starts true because the getter does not write it on failure.
            nint error = nint.Zero, text = nint.Zero;
            byte isFinalByte = 1;
            LiteRtStatusCode status = LiteRtStatusCode.InvalidArgument;
            if (chunk != nint.Zero)
            {
                status = LiteRtLmNative.litert_lm_stream_chunk_get_error(chunk, &error);
                if (status == LiteRtStatusCode.Ok)
                    status = LiteRtLmNative.litert_lm_stream_chunk_get_text(chunk, &text);
                if (status == LiteRtStatusCode.Ok)
                    status = LiteRtLmNative.litert_lm_stream_chunk_is_final(chunk, &isFinalByte);
            }
            if (status != LiteRtStatusCode.Ok)
            {
                RecordFault(new LiteRtException(
                    $"Reading a native stream chunk failed: {NativeError.CanonicalName(status)}.", status));
                MarkFinal();
                return;
            }
            bool isFinal = isFinalByte != 0;
            // The chunk and its strings are valid only during this call: the text is decoded and the error copied
            // before returning.
            ReadOnlySpan<byte> bytes = text != nint.Zero
                ? MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)text)
                : default;
            string? errorText = error != nint.Zero ? Marshal.PtrToStringUTF8(error) ?? "" : null;
            OnChunk(bytes, isFinal, errorText);
        }
        catch (Exception ex)
        {
            // Whether the chunk was final is unknown here, so it counts as final: a decode that never settles
            // would hang its caller, and the fault leaves the session Faulted, which refuses every call but
            // Dispose.
            RecordFault(ex);
            MarkFinal();
        }
    }

    /// <summary>Handles one chunk: decodes its text into the sink, and either records the final chunk's error
    /// and hands settlement off, or issues a requested cancel. Called by the native callback, and by tests.</summary>
    /// <param name="text">The chunk's UTF-8 bytes, without a terminator; empty when the chunk has no text.</param>
    /// <param name="isFinal">Whether the chunk is the last one of the decode.</param>
    /// <param name="error">The chunk's error text, or <c>null</c> when it has none.</param>
    internal void OnChunk(ReadOnlySpan<byte> text, bool isFinal, string? error)
    {
        if (FinalSeen)
            return;
        try
        {
            // A stateful decoder keeps the bytes of a character that a chunk splits until the rest arrives; the
            // final chunk flushes what is left.
            Emit(DecodeText(text, flush: isFinal));
            if (isFinal)
            {
                _finalError = error;
            }
            else if (Volatile.Read(ref _requester) != (int)CancelRequester.None && !_cancelIssued)
            {
                // Set before the call: the session must not be reused once cancel_process was called, whatever
                // the decode does next. engine.h allows this one call from a callback, on a chunk that is not
                // final. Its status is not read: a failure only happens for a null session, and the session is
                // latched Cancelled either way.
                _cancelIssued = true;
                _ = LiteRtLmNative.litert_lm_session_cancel_process(_sessionPtr);
            }
        }
        catch (Exception ex)
        {
            RecordFault(ex);
        }
        finally
        {
            if (isFinal)
                MarkFinal();
        }
    }

    private string DecodeText(ReadOnlySpan<byte> bytes, bool flush)
    {
        // GetCharCount does not change the decoder's state, so GetChars runs even when no character is complete:
        // it keeps the partial bytes for the next chunk.
        int count = _decoder.GetCharCount(bytes, flush);
        if (count == 0)
        {
            _decoder.GetChars(bytes, Span<char>.Empty, flush);
            return "";
        }
        Span<char> chars = count <= 512 ? stackalloc char[count] : new char[count];
        int written = _decoder.GetChars(bytes, chars, flush);
        return new string(chars[..written]);
    }

    private void Emit(string piece)
    {
        if (piece.Length == 0)
            return;
        _text?.Append(piece);
        _channel?.Writer.TryWrite(piece);
    }

    private void RecordFault(Exception ex)
        => Interlocked.CompareExchange(ref _fault, ex is LiteRtException ? ex : new LiteRtException(
            $"The session decode callback failed while reading a native stream chunk: {ex.Message}", ex), null);

    private void MarkFinal()
    {
        if (Interlocked.Exchange(ref _final, 1) == 0)
            Arrive();
    }

    /// <summary>Records that <c>run_decode_async</c> returned OK. Settlement waits for this and for the final
    /// chunk.</summary>
    internal void OnCallReturned() => Arrive();

    private void Arrive()
    {
        if (Interlocked.Decrement(ref _arrivals) != 0)
            return;
        // The callback never settles itself: settlement can delete the native session, which a callback must not
        // do. A blocking Decode settles on its own thread, so it never needs a free pool thread.
        if (_arrived is not null)
            _arrived.TrySetResult();
        else
            ThreadPool.UnsafeQueueUserWorkItem(static stream => stream.Settle(), this, preferLocal: false);
    }

    /// <summary>For the blocking <see cref="LiteRtSession.Decode"/>: waits for the final chunk and the return of
    /// <c>run_decode_async</c>, then settles on the calling thread. Returns at once when the decode already
    /// settled inline.</summary>
    internal void WaitAndSettle()
    {
        if (Settled.IsCompleted)
            return;
        _arrived!.Task.GetAwaiter().GetResult();
        Settle();
    }

    /// <summary>Settles a decode whose native start failed: <c>run_decode_async</c> did not return OK, so the
    /// callback was not seen to run (measured with a null session on LiteRT-LM v0.20.0, win-x64).</summary>
    internal void SettleFailedStart(Exception failure) => Settle(failure);

    /// <summary>Settles the decode, once: classifies the outcome, moves the state, releases the stream's
    /// reference and the gate, then completes the channel and <see cref="Settled"/>.</summary>
    private void Settle(Exception? startFailure = null)
    {
        if (Interlocked.Exchange(ref _settledFlag, 1) != 0)
            return;
        // Marks the decode finished without a second arrival, so that a chunk after this point, which only a
        // failed start can leave possible, makes no native call: the reference on the session goes below.
        Volatile.Write(ref _final, 1);
        LiteRtSession owner = _owner!;
        string? result = null;
        Exception? exception = null;
        try
        {
            (SessionCallOutcome outcome, exception) = Classify(startFailure);
            owner.Machine.Complete(_call, outcome);
            result = _text?.ToString();
        }
        catch (Exception ex)
        {
            exception ??= ex;
        }
        finally
        {
            _result = result;
            _exception = exception;
            // After the classification has read whether the token fired. Unregister does not wait for a token
            // callback that is running, so settlement never blocks on the token's thread; a callback that runs
            // late only sets flags on this settled stream. The caller's DisposeRegistration stays, and does
            // nothing more.
            _registration.Unregister();
            _text = null;
            _token = default;
            _owner = null;
            Channel<string>? channel = _channel;
            _channel = null;
            try
            {
                owner.ClearRunningDecode(this);
                SessionHandle handle = owner.Handle;
                // Retire before the release: the release can run the native delete, and the handle must still be
                // allocated until then.
                if (_gcHandle.IsAllocated)
                    handle.Retire(_gcHandle);
                if (_referenced)
                {
                    _referenced = false;
                    handle.DangerousRelease();
                }
            }
            finally
            {
                owner.ExitCall();
                channel?.Writer.TryComplete();
                _settled.TrySetResult();
            }
        }
    }

    /// <summary>The outcome and the exception of the decode: the final-chunk classification, except for a failed
    /// start or a fault; a caller's token that fired turns any exception that is not a cancel by
    /// <see cref="LiteRtSession.Dispose"/> into <see cref="OperationCanceledException"/>.</summary>
    private (SessionCallOutcome Outcome, Exception? Exception) Classify(Exception? startFailure)
    {
        SessionCallOutcome outcome;
        Exception? exception = null;
        if (startFailure is not null)
        {
            outcome = SessionCallOutcome.Failed;
            exception = startFailure;
        }
        else
        {
            outcome = FinalChunkClassifier.Classify(_cancelIssued, _finalError);
            if (outcome != SessionCallOutcome.Cancelled && _fault is not null)
            {
                outcome = SessionCallOutcome.Failed;
                exception = _fault;
            }
            else if (outcome == SessionCallOutcome.Failed)
            {
                // Final-chunk errors carry no status prefix (measured), so the text becomes the message.
                exception = NativeError.FromStatusText(
                    "litert_lm_session_run_decode_async failed.", _finalError ?? "", _ => null);
            }
        }

        bool tokenFired = Volatile.Read(ref _tokenFired) != 0;
        if (outcome == SessionCallOutcome.Cancelled && Requester == CancelRequester.Dispose)
            exception = new ObjectDisposedException(nameof(LiteRtSession),
                "The session was disposed during the decode, which cancelled the decode.");
        else if (outcome == SessionCallOutcome.Cancelled || tokenFired)
            exception = tokenFired
                ? new OperationCanceledException("The decode was cancelled.", _token)
                : new OperationCanceledException("The decode was cancelled because the stream was left before its end.");
        return (outcome, exception);
    }

    /// <summary>Returns the text of a settled decode, or throws its exception.</summary>
    internal string TakeResult()
    {
        ThrowIfFailed();
        string result = _result ?? "";
        _result = null;
        return result;
    }

    /// <summary>Throws the exception of a settled decode, if any.</summary>
    internal void ThrowIfFailed()
    {
        Exception? exception = _exception;
        _exception = null;
        if (exception is not null)
            throw exception;
    }
}
