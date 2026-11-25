using System.Runtime.InteropServices;
using System.Text;
using LiteRtLmSharp.Native;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The session decode without a model: the refusals, a native start that fails, and the stream state and its
/// settlement driven by simulated chunks. The sessions wrap a null native handle: every check before the native
/// call runs as on a real session, <c>run_decode_async</c> fails with INVALID_ARGUMENT, and the chunks of a
/// decode prepared with <see cref="LiteRtSession.BeginDecode"/> are fed to
/// <see cref="SessionStream.OnChunk(ReadOnlySpan{byte}, bool, string?)"/> by the test.
/// </summary>
public sealed class SessionStreamTests
{
    static SessionStreamTests() => LiteRtEngine.SetMinLogLevel(3); // registers the resolver

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>U+FFFD, which a UTF-8 decoder writes for bytes that are not a whole character.</summary>
    private const char Replacement = (char)0xFFFD;

    private static LiteRtSession NullSession(LiteRtSessionState state)
        => new(new SessionHandle(nint.Zero, new EngineHandle(nint.Zero, allowMultiple: false)), "<bos>", state);

    private static readonly SessionCall[] DecodeCalls =
        [SessionCall.Decode, SessionCall.DecodeAsync, SessionCall.DecodeStreamingAsync];

    /// <summary>Runs one decode member to its end, with a time limit, and returns its text.</summary>
    private static async Task<string> RunDecode(LiteRtSession session, SessionCall call, CancellationToken token = default)
    {
        switch (call)
        {
            case SessionCall.Decode:
                return await Task.Run(session.Decode).WaitAsync(Limit);
            case SessionCall.DecodeAsync:
                return await session.DecodeAsync(token).WaitAsync(Limit);
            default:
                var sb = new StringBuilder();
                await Task.Run(async () =>
                {
                    await foreach (string piece in session.DecodeStreamingAsync(token))
                        sb.Append(piece);
                }).WaitAsync(Limit);
                return sb.ToString();
        }
    }

    // --- Refusals and precedence ----------------------------------------------

    public static TheoryData<LiteRtSessionState, string> RefusedDecodes()
    {
        var data = new TheoryData<LiteRtSessionState, string>();
        foreach (LiteRtSessionState state in Enum.GetValues<LiteRtSessionState>())
            if (state != LiteRtSessionState.Prefilled)
                foreach (SessionCall call in DecodeCalls)
                    data.Add(state, call.ToString());
        return data;
    }

    /// <summary>Every state but Prefilled refuses the three decodes before any native call: the gate is free
    /// again, the state is unchanged, and no stream was retired.</summary>
    [Theory]
    [MemberData(nameof(RefusedDecodes))]
    public async Task Decode_OutsidePrefilled_IsRefusedBeforeAnyNativeCall(LiteRtSessionState state, string callName)
    {
        SessionCall call = Enum.Parse<SessionCall>(callName);
        using LiteRtSession session = NullSession(state);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunDecode(session, call));
        Assert.Contains(call.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Equal(state, session.State);
        Assert.False(session.IsCallInProgress);
        Assert.Equal(0, session.Handle.RetiredCount);
    }

    [Theory]
    [InlineData(nameof(SessionCall.Decode))]
    [InlineData(nameof(SessionCall.DecodeAsync))]
    [InlineData(nameof(SessionCall.DecodeStreamingAsync))]
    public async Task Decode_OnADisposedSession_ThrowsObjectDisposedFirst(string callName)
    {
        SessionCall call = Enum.Parse<SessionCall>(callName);
        LiteRtSession session = NullSession(LiteRtSessionState.Fresh);
        session.Dispose();
        session.EnterCall();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => RunDecode(session, call, cts.Token));
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
    }

    /// <summary>A token cancelled before the call throws before the busy and state checks, and changes
    /// nothing.</summary>
    [Theory]
    [InlineData(nameof(SessionCall.DecodeAsync))]
    [InlineData(nameof(SessionCall.DecodeStreamingAsync))]
    public async Task Decode_WithATokenCancelledBeforeTheCall_ChangesNothing(string callName)
    {
        SessionCall call = Enum.Parse<SessionCall>(callName);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using (LiteRtSession prefilled = NullSession(LiteRtSessionState.Prefilled))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunDecode(prefilled, call, cts.Token));
            Assert.Equal(LiteRtSessionState.Prefilled, prefilled.State);
            Assert.False(prefilled.IsCallInProgress);
            Assert.Equal(0, prefilled.Handle.RetiredCount);
        }
        using LiteRtSession busy = NullSession(LiteRtSessionState.Fresh);
        busy.EnterCall();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunDecode(busy, call, cts.Token));
        Assert.True(busy.IsCallInProgress);
    }

    [Theory]
    [InlineData(nameof(SessionCall.Decode))]
    [InlineData(nameof(SessionCall.DecodeAsync))]
    [InlineData(nameof(SessionCall.DecodeStreamingAsync))]
    public async Task Decode_WhileAnotherCallHoldsTheGate_IsRefusedAsBusy(string callName)
    {
        SessionCall call = Enum.Parse<SessionCall>(callName);
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        session.EnterCall();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunDecode(session, call));
        Assert.Contains("in progress", ex.Message, StringComparison.Ordinal);
        Assert.True(session.IsCallInProgress);
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
    }

    // --- A native start that fails ----------------------------------------------

    /// <summary>A <c>run_decode_async</c> that does not return OK settles at once: the runtime's status, Faulted,
    /// the gate free, the stream's handle retired to the session handle, and no running decode left.</summary>
    [Theory]
    [InlineData(nameof(SessionCall.Decode))]
    [InlineData(nameof(SessionCall.DecodeAsync))]
    [InlineData(nameof(SessionCall.DecodeStreamingAsync))]
    public async Task Decode_WhoseNativeStartFails_SettlesAsFaulted(string callName)
    {
        SessionCall call = Enum.Parse<SessionCall>(callName);
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        var ex = await Assert.ThrowsAsync<LiteRtException>(() => RunDecode(session, call));
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("litert_lm_session_run_decode_async failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
        Assert.False(session.IsCallInProgress);
        Assert.Equal(1, session.Handle.RetiredCount);
        // A decode that did not start leaves a session that accepts only Dispose.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunDecode(session, call));
        Assert.Throws<InvalidOperationException>(() => session.Prefill("Hello"));
        Assert.Throws<InvalidOperationException>(() => session.SaveCheckpoint());
        Assert.Throws<InvalidOperationException>(() => session.Reset());
        Assert.Throws<InvalidOperationException>(() => session.RewindTo(new LiteRtSessionCheckpoint(session, 0, LiteRtSessionState.Prefilled)));
        Assert.Equal(1, session.Handle.RetiredCount);
    }

    // --- Simulated chunks -------------------------------------------------------

    /// <summary>Feeds <paramref name="pieces"/> as non-final chunks and then a final chunk with
    /// <paramref name="finalError"/>, then reports the return of <c>run_decode_async</c>, and waits for
    /// settlement.</summary>
    private static async Task Feed(SessionStream stream, IEnumerable<byte[]> pieces, string? finalError)
    {
        foreach (byte[] piece in pieces)
            stream.OnChunk(piece, isFinal: false, error: null);
        stream.OnChunk([], isFinal: true, finalError);
        stream.OnCallReturned();
        await stream.Settled.WaitAsync(Limit);
    }

    private static string Drain(System.Threading.Channels.ChannelReader<string> reader)
    {
        var sb = new StringBuilder();
        while (reader.TryRead(out string? piece))
            sb.Append(piece);
        Assert.True(reader.Completion.IsCompleted);
        return sb.ToString();
    }

    private static IEnumerable<byte[]> Utf8(params string[] pieces) => pieces.Select(p => Encoding.UTF8.GetBytes(p));

    public static TheoryData<string?, LiteRtSessionState, Type?> FinalChunks() => new()
    {
        { null, LiteRtSessionState.Decoded, null },
        { FinalChunkClassifier.MaxTokensReachedError, LiteRtSessionState.ContextFull, null },
        // A cancel is told by the flag, never by the text: without an issued cancel this text is a failure.
        { FinalChunkClassifier.CancelledError, LiteRtSessionState.Faulted, typeof(LiteRtException) },
        { "INTERNAL: something failed", LiteRtSessionState.Faulted, typeof(LiteRtException) },
        { "Max number of tokens reached", LiteRtSessionState.Faulted, typeof(LiteRtException) },
        { "", LiteRtSessionState.Faulted, typeof(LiteRtException) },
    };

    /// <summary>The final chunk decides the state and the exception when no cancel was issued; a full context
    /// returns the partial text as a normal end.</summary>
    [Theory]
    [MemberData(nameof(FinalChunks))]
    public async Task Settlement_ClassifiesTheFinalChunk(string? finalError, LiteRtSessionState expected, Type? exceptionType)
    {
        foreach (SessionCall call in DecodeCalls)
        {
            using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
            SessionStream stream = session.BeginDecode(call, CancellationToken.None);
            // The stream reads its pieces through the reader it took at the start; settlement drops the channel.
            var reader = call == SessionCall.DecodeStreamingAsync ? stream.Reader : null;
            Assert.True(session.IsCallInProgress);
            if (call == SessionCall.Decode)
            {
                foreach (byte[] piece in Utf8("Hello", ", world"))
                    stream.OnChunk(piece, isFinal: false, error: null);
                stream.OnChunk([], isFinal: true, finalError);
                stream.OnCallReturned();
                stream.WaitAndSettle();
            }
            else
            {
                await Feed(stream, Utf8("Hello", ", world"), finalError);
            }

            Assert.Equal(expected, session.State);
            Assert.False(session.IsCallInProgress);
            Assert.Equal(1, session.Handle.RetiredCount);
            string text = reader is not null ? Drain(reader) : "";
            Exception? error = Record.Exception(() =>
            {
                if (call == SessionCall.DecodeStreamingAsync) stream.ThrowIfFailed();
                else text = stream.TakeResult();
            });
            if (exceptionType is null)
            {
                Assert.Null(error);
                Assert.Equal("Hello, world", text);
            }
            else
            {
                Assert.IsType(exceptionType, error);
                Assert.Contains("litert_lm_session_run_decode_async failed", error!.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Settlement_ParsesTheStatusOfAFinalChunkError()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, CancellationToken.None);
        await Feed(stream, [], "INTERNAL: something failed");
        var ex = Assert.Throws<LiteRtException>(() => stream.TakeResult());
        Assert.Equal(LiteRtStatusCode.Internal, ex.StatusCode);
        Assert.Contains("something failed", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A multibyte character split across chunks, at every byte, comes out whole: no U+FFFD, and the
    /// stream pieces concatenate to the original text.</summary>
    [Fact]
    public async Task Chunks_ThatSplitACharacter_DecodeToWholeCharacters()
    {
        const string Text = "Añ€ 日本 😀👍🏽 done";
        byte[] bytes = Encoding.UTF8.GetBytes(Text);
        foreach (int size in new[] { 1, 2, 3, 5 })
        {
            var chunks = new List<byte[]>();
            for (int i = 0; i < bytes.Length; i += size)
                chunks.Add(bytes[i..Math.Min(bytes.Length, i + size)]);

            using LiteRtSession streamed = NullSession(LiteRtSessionState.Prefilled);
            SessionStream stream = streamed.BeginDecode(SessionCall.DecodeStreamingAsync, CancellationToken.None);
            var reader = stream.Reader;
            await Feed(stream, chunks, null);
            var pieces = new List<string>();
            while (reader.TryRead(out string? piece))
                pieces.Add(piece);
            Assert.All(pieces, p => Assert.DoesNotContain(Replacement, p));
            Assert.All(pieces, p => Assert.NotEqual(0, p.Length));
            Assert.Equal(Text, string.Concat(pieces));

            using LiteRtSession collected = NullSession(LiteRtSessionState.Prefilled);
            SessionStream blocking = collected.BeginDecode(SessionCall.DecodeAsync, CancellationToken.None);
            await Feed(blocking, chunks, null);
            Assert.Equal(Text, blocking.TakeResult());
        }
    }

    /// <summary>Bytes of a character that never completes are flushed at the final chunk as U+FFFD.</summary>
    [Fact]
    public async Task Chunks_ThatEndInsideACharacter_FlushAReplacementCharacter()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, CancellationToken.None);
        byte[] euro = Encoding.UTF8.GetBytes("€");
        await Feed(stream, [Encoding.UTF8.GetBytes("ok"), euro[..2]], null);
        Assert.Equal("ok" + Replacement, stream.TakeResult());
    }

    /// <summary>A token that fires while the decode runs is issued at the next chunk that is not final, and the
    /// decode ends Cancelled with <see cref="OperationCanceledException"/> for that token.</summary>
    [Fact]
    public async Task TokenCancel_IssuedAtTheNextChunk_EndsCancelled()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        using var cts = new CancellationTokenSource();
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, cts.Token);
        stream.OnChunk(Encoding.UTF8.GetBytes("one"), isFinal: false, error: null);
        Assert.False(stream.CancelIssued);
        cts.Cancel();
        Assert.Equal(CancelRequester.Caller, stream.Requester);
        Assert.False(stream.CancelIssued);
        stream.OnChunk(Encoding.UTF8.GetBytes(" two"), isFinal: false, error: null);
        Assert.True(stream.CancelIssued);
        // Issued once, even if another chunk came.
        stream.OnChunk(Encoding.UTF8.GetBytes(" three"), isFinal: false, error: null);
        await Feed(stream, [], FinalChunkClassifier.CancelledError);
        stream.DisposeRegistration();

        var ex = Assert.Throws<OperationCanceledException>(() => stream.TakeResult());
        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);
        Assert.False(session.IsCallInProgress);
    }

    /// <summary>An issued cancel latches Cancelled even when the final chunk carries no error.</summary>
    [Fact]
    public async Task IssuedCancel_WithACleanFinalChunk_StillEndsCancelled()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        using var cts = new CancellationTokenSource();
        SessionStream stream = session.BeginDecode(SessionCall.DecodeStreamingAsync, cts.Token);
        cts.Cancel();
        await Feed(stream, Utf8("piece"), null);
        stream.DisposeRegistration();
        Assert.True(stream.CancelIssued);
        Assert.Throws<OperationCanceledException>(stream.ThrowIfFailed);
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);
    }

    /// <summary>A token that fires after the last chunk that is not final issues no cancel: the state follows the
    /// final chunk, and the call still throws <see cref="OperationCanceledException"/>.</summary>
    [Theory]
    [InlineData(null, LiteRtSessionState.Decoded)]
    [InlineData(FinalChunkClassifier.MaxTokensReachedError, LiteRtSessionState.ContextFull)]
    [InlineData("INTERNAL: something failed", LiteRtSessionState.Faulted)]
    public async Task TokenCancel_RequestedButNeverIssued_FollowsTheFinalChunk(string? finalError, LiteRtSessionState expected)
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        using var cts = new CancellationTokenSource();
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, cts.Token);
        stream.OnChunk(Encoding.UTF8.GetBytes("all of it"), isFinal: false, error: null);
        cts.Cancel();
        await Feed(stream, [], finalError);
        stream.DisposeRegistration();

        Assert.False(stream.CancelIssued);
        Assert.Throws<OperationCanceledException>(() => stream.TakeResult());
        Assert.Equal(expected, session.State);
        Assert.False(session.IsCallInProgress);
    }

    /// <summary>A <see cref="LiteRtSession.Dispose"/> during a decode requests the cancel and defers the native
    /// release to settlement; an issued cancel then throws <see cref="ObjectDisposedException"/>.</summary>
    [Fact]
    public async Task Dispose_DuringADecode_CancelsIt_AndTheDecodeThrowsObjectDisposed()
    {
        LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, CancellationToken.None);
        session.Dispose();
        Assert.Equal(CancelRequester.Dispose, stream.Requester);
        Assert.True(session.IsCallInProgress);
        await Feed(stream, Utf8("cut"), FinalChunkClassifier.CancelledError);
        Assert.True(stream.CancelIssued);
        Assert.Throws<ObjectDisposedException>(() => stream.TakeResult());
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);
        Assert.False(session.IsCallInProgress);
        Assert.Equal(1, session.Handle.RetiredCount);
    }

    /// <summary>A <see cref="LiteRtSession.Dispose"/> whose cancel was never issued (the decode had reached its
    /// final chunk) leaves the reply as a normal result.</summary>
    [Fact]
    public async Task Dispose_AfterTheLastChunk_LeavesANormalResult()
    {
        LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, CancellationToken.None);
        stream.OnChunk(Encoding.UTF8.GetBytes("whole reply"), isFinal: false, error: null);
        stream.OnChunk([], isFinal: true, error: null);
        session.Dispose();
        stream.OnCallReturned();
        await stream.Settled.WaitAsync(Limit);
        Assert.False(stream.CancelIssued);
        Assert.Equal("whole reply", stream.TakeResult());
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        Assert.False(session.IsCallInProgress);
    }

    /// <summary>A final chunk that arrives before <c>run_decode_async</c> returns (as on a session whose context
    /// is full) does not settle the decode until the call has returned, so the gate and the native reference
    /// stay held for the whole native call.</summary>
    [Fact]
    public async Task FinalChunk_InsideTheNativeCall_SettlesOnlyAfterTheCallReturns()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, CancellationToken.None);
        stream.OnChunk([], isFinal: true, FinalChunkClassifier.MaxTokensReachedError);
        await Task.Delay(100);
        Assert.False(stream.Settled.IsCompleted);
        Assert.True(session.IsCallInProgress);
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        stream.OnCallReturned();
        await stream.Settled.WaitAsync(Limit);
        Assert.Equal(LiteRtSessionState.ContextFull, session.State);
        Assert.Equal("", stream.TakeResult());

        // The blocking form does not wait on its own thread for that case either.
        using LiteRtSession blocking = NullSession(LiteRtSessionState.Prefilled);
        SessionStream sync = blocking.BeginDecode(SessionCall.Decode, CancellationToken.None);
        sync.OnChunk(Encoding.UTF8.GetBytes("x"), isFinal: true, error: null);
        sync.OnCallReturned();
        sync.WaitAndSettle();
        Assert.Equal("x", sync.TakeResult());
        Assert.Equal(LiteRtSessionState.Decoded, blocking.State);
    }

    /// <summary>A chunk after the final one is ignored: no text, no cancel, no second settlement.</summary>
    [Fact]
    public async Task Chunks_AfterTheFinalOne_AreIgnored()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        using var cts = new CancellationTokenSource();
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, cts.Token);
        await Feed(stream, Utf8("done"), null);
        cts.Cancel();
        stream.OnChunk(Encoding.UTF8.GetBytes(" late"), isFinal: false, error: null);
        stream.OnChunk([], isFinal: true, "INTERNAL: late");
        stream.DisposeRegistration();
        Assert.False(stream.CancelIssued);
        Assert.Equal("done", stream.TakeResult());
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        Assert.Equal(1, session.Handle.RetiredCount);
    }

    /// <summary>A chunk that arrives after a native start that failed is ignored, even with a cancel requested:
    /// by then the stream released its reference on the native session, so it must make no native call.</summary>
    [Fact]
    public async Task Chunks_AfterAFailedStart_AreIgnored()
    {
        LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, CancellationToken.None);
        session.Dispose();
        Assert.Equal(CancelRequester.Dispose, stream.Requester);
        session.StartDecode(stream);
        await stream.Settled.WaitAsync(Limit);
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
        Assert.True(stream.FinalSeen);

        stream.OnChunk(Encoding.UTF8.GetBytes("late"), isFinal: false, error: null);
        Assert.False(stream.CancelIssued);
        Assert.Throws<LiteRtException>(() => stream.TakeResult());
    }

    /// <summary>Settlement ends the token registration itself, so a stream whose caller never disposes it (an
    /// enumerator that is dropped) leaves nothing registered on a token that lives longer.</summary>
    [Fact]
    public async Task Settlement_EndsTheTokenRegistration()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        using var cts = new CancellationTokenSource();
        SessionStream stream = session.BeginDecode(SessionCall.DecodeStreamingAsync, cts.Token);
        await Feed(stream, Utf8("done"), null);
        cts.Cancel();
        Assert.Equal(CancelRequester.None, stream.Requester);
        stream.ThrowIfFailed();
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
    }

    /// <summary>A <see cref="LiteRtSession.Dispose"/> during the blocking <see cref="LiteRtSession.Decode"/> is
    /// issued at the next chunk, and the blocking wait then throws <see cref="ObjectDisposedException"/> with the
    /// session Cancelled.</summary>
    [Fact]
    public void Dispose_DuringABlockingDecode_ThrowsObjectDisposed()
    {
        LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.Decode, CancellationToken.None);
        session.Dispose();
        stream.OnChunk(Encoding.UTF8.GetBytes("cut"), isFinal: false, error: null);
        Assert.True(stream.CancelIssued);
        stream.OnChunk([], isFinal: true, FinalChunkClassifier.CancelledError);
        stream.OnCallReturned();
        stream.WaitAndSettle();
        Assert.Throws<ObjectDisposedException>(() => stream.TakeResult());
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);
        Assert.False(session.IsCallInProgress);
    }

    /// <summary>A consumer that leaves the stream after its last piece requests a cancel that no later chunk
    /// issues: the reply ends as a normal result, Decoded, with no exception.</summary>
    [Fact]
    public async Task LeavingTheStreamAfterItsLastPiece_LeavesANormalResult()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.DecodeStreamingAsync, CancellationToken.None);
        stream.OnChunk(Encoding.UTF8.GetBytes("all of it"), isFinal: false, error: null);
        stream.RequestCancel(CancelRequester.Caller);
        stream.OnChunk([], isFinal: true, error: null);
        stream.OnCallReturned();
        await stream.Settled.WaitAsync(Limit);
        Assert.False(stream.CancelIssued);
        stream.ThrowIfFailed();
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        Assert.False(session.IsCallInProgress);
    }

    /// <summary>The native callback entry with a chunk it cannot read (null) counts it as the final chunk with a
    /// fault: the decode settles Faulted instead of waiting, and the process keeps running.</summary>
    [Fact]
    public async Task CallbackEntry_WithAChunkItCannotRead_SettlesAsFaulted()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.DecodeAsync, CancellationToken.None);
        CallTheCallbackEntry(GCHandle.ToIntPtr(stream.Handle), chunk: nint.Zero);
        Assert.True(stream.FinalSeen);
        stream.OnCallReturned();
        await stream.Settled.WaitAsync(Limit);
        var ex = Assert.Throws<LiteRtException>(() => stream.TakeResult());
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
        Assert.False(session.IsCallInProgress);
    }

    /// <summary>Calls the <c>[UnmanagedCallersOnly]</c> callback through its native entry point, as the runtime
    /// does.</summary>
    private static unsafe void CallTheCallbackEntry(nint callbackData, nint chunk)
    {
        delegate* unmanaged[Cdecl]<nint, nint, void> entry = &SessionStream.OnChunk;
        entry(callbackData, chunk);
    }

    /// <summary>The gate is held from the start of a decode until its settlement: a prefill, a second decode, a
    /// save, a rewind or a reset in between is refused as busy.</summary>
    [Fact]
    public async Task Gate_IsHeldUntilSettlement()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        SessionStream stream = session.BeginDecode(SessionCall.DecodeStreamingAsync, CancellationToken.None);
        Assert.Contains("in progress", Assert.Throws<InvalidOperationException>(() => session.Prefill("Hello")).Message,
            StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => session.Decode());
        var checkpoint = new LiteRtSessionCheckpoint(session, 0, LiteRtSessionState.Prefilled);
        foreach (Action call in new Action[] { () => session.SaveCheckpoint(), () => session.RewindTo(checkpoint), session.Reset })
            Assert.Contains("in progress", Assert.Throws<InvalidOperationException>(call).Message, StringComparison.Ordinal);
        Assert.Equal(0, session.CheckpointCount);
        stream.OnChunk(Encoding.UTF8.GetBytes("a"), isFinal: false, error: null);
        Assert.True(session.IsCallInProgress);
        await Feed(stream, [], null);
        Assert.False(session.IsCallInProgress);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        // Decoded accepts a prefill; on the null handle it then fails natively.
        Assert.Throws<LiteRtException>(() => session.Prefill("Hello"));
    }
}
