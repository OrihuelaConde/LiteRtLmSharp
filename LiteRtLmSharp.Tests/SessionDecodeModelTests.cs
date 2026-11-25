using System.Diagnostics;
using System.Text;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>Shared pieces of the session decode tests on a model.</summary>
internal static class SessionDecodeHelpers
{
    /// <summary>Exact argmax over the TopP sampler (TopK = 1), so replies repeat from run to run.</summary>
    public static readonly LiteRtSamplerParams Argmax = new() { Strategy = LiteRtSamplerType.TopP, TopK = 1 };

    /// <summary>The test-only limit for one decode: a regression fails the test instead of hanging the suite.</summary>
    public static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(120);

    /// <summary>A prompt whose reply runs long, so that a cancel lands while it runs.</summary>
    public const string CountPrompt = "Count upward starting from 1, one number per line, and never stop.";

    /// <summary>Whether the tests run on the CPU backend.</summary>
    public static bool IsCpu => LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu") == LiteRtBackend.Cpu;

    /// <summary>Whether two decode paths were measured to give the same bytes on this backend and platform: on
    /// CPU, and on GPU on Windows. On other GPU platforms the tests assert states and text instead.</summary>
    public static bool SameBytesMeasured => IsCpu || OperatingSystem.IsWindows();

    /// <summary>The prompt text of one user turn for Gemma 4: <see cref="LiteRtConversation.RenderMessage"/> of a
    /// fresh conversation without a system message. It ends with the model-turn prefix and holds no start
    /// token.</summary>
    public static string Prompt(LiteRtEngine engine, string user)
    {
        using LiteRtConversation conversation = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax });
        return conversation.RenderMessage(user);
    }

    public static LiteRtSession Session(LiteRtEngine engine, int maxOutputTokens)
        => engine.CreateSession(new LiteRtSessionOptions { Sampler = Argmax, MaxOutputTokens = maxOutputTokens });

    public static Task<string> DecodeWithin(LiteRtSession session) => Task.Run(session.Decode).WaitAsync(DrainLimit);

    /// <summary>Reads a stream to its end, with the drain limit, and returns its pieces.
    /// <paramref name="onPiece"/> runs in the loop body after each piece; returning <c>false</c> leaves the
    /// loop.</summary>
    public static async Task<List<string>> StreamWithin(
        LiteRtSession session, CancellationToken token = default, Func<int, bool>? onPiece = null)
    {
        var pieces = new List<string>();
        await Task.Run(async () =>
        {
            await foreach (string piece in session.DecodeStreamingAsync(token))
            {
                pieces.Add(piece);
                if (onPiece is not null && !onPiece(pieces.Count))
                    break;
            }
        }).WaitAsync(DrainLimit);
        return pieces;
    }

    /// <summary>Asserts that every member except Dispose refuses with <see cref="InvalidOperationException"/>
    /// before any native call, and leaves the state and the gate as they are.</summary>
    public static async Task AssertAcceptsOnlyDispose(LiteRtSession session, LiteRtSessionState state)
    {
        Assert.Equal(state, session.State);
        Assert.Throws<InvalidOperationException>(() => session.Prefill("Hello"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrefillAsync("Hello").WaitAsync(DrainLimit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DecodeWithin(session));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DecodeAsync().WaitAsync(DrainLimit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => StreamWithin(session));
        Assert.Equal(state, session.State);
        Assert.False(session.IsCallInProgress);
    }

    /// <summary>Waits until <paramref name="condition"/> holds, for a settlement that runs on the thread
    /// pool.</summary>
    public static async Task Eventually(Func<bool> condition, string what)
    {
        var wait = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(wait.Elapsed < DrainLimit, $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    /// <summary>The reply of the blocking native decode, which no member calls: a regression check that it
    /// gives the same text as the stream that every decode member uses.</summary>
    public static unsafe string NativeBlockingDecode(LiteRtSession session)
    {
        session.EnterCall();
        bool referenced = false;
        try
        {
            session.Handle.DangerousAddRef(ref referenced);
            nint responses;
            NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_session_run_decode(session.Handle.Ptr, &responses),
                nameof(LiteRtLmNative.litert_lm_session_run_decode));
            try
            {
                nuint count;
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_responses_get_num_candidates(responses, &count),
                    nameof(LiteRtLmNative.litert_lm_responses_get_num_candidates));
                Assert.Equal((nuint)1, count);
                nint text;
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_responses_get_response_text_at(responses, 0, &text),
                    nameof(LiteRtLmNative.litert_lm_responses_get_response_text_at));
                return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text) ?? "";
            }
            finally
            {
                LiteRtLmNative.litert_lm_responses_delete(responses);
            }
        }
        finally
        {
            if (referenced)
                session.Handle.DangerousRelease();
            session.ExitCall();
        }
    }
}

/// <summary>
/// The session decode on a model, on one shared engine with a 1024-token context: the decode members against a
/// conversation and against each other, a cut reply continued on a new session, refusals, cancels, a full
/// context, the output cap, disposal during a stream, a conversation on the same engine, and UTF-8. The asserts
/// are about the binding; where a test needs the model to do something, a skip guards that precondition.
/// </summary>
public sealed class SessionDecodeModelTests(SessionEngineFixture fixture, ITestOutputHelper output) : IClassFixture<SessionEngineFixture>
{
    private static readonly string[] OraclePrompts =
    [
        "Explain in three sentences why the sky is blue.",
        "Explica en tres frases cómo funciona un volcán.",
    ];

    private LiteRtEngine Engine
    {
        get
        {
            Skip.If(fixture.Engine is null, SessionModelHelpers.SkipReason);
            return fixture.Engine;
        }
    }

    private string ConversationReply(string user)
    {
        using LiteRtConversation conversation = Engine.CreateConversation(
            new LiteRtConversationOptions { Sampler = SessionDecodeHelpers.Argmax, MaxOutputTokens = 400 });
        return conversation.Send(user).Text ?? "";
    }

    /// <summary>A session that prefills the conversation's rendered turn and decodes gives the conversation's
    /// reply byte for byte (measured equal on LiteRT-LM v0.20.0, win-x64, CPU and GPU).</summary>
    [SkippableFact]
    public async Task Decode_OfTheRenderedTurn_EqualsTheConversationReply()
    {
        LiteRtEngine engine = Engine;
        foreach (string user in OraclePrompts)
        {
            string expected = ConversationReply(user);
            using LiteRtSession session = SessionDecodeHelpers.Session(engine, 400);
            session.Prefill(SessionDecodeHelpers.Prompt(engine, user));
            string reply = await SessionDecodeHelpers.DecodeWithin(session);
            output.WriteLine($"conversation: {expected}\nsession:      {reply}");

            Assert.Equal(LiteRtSessionState.Decoded, session.State);
            Assert.False(session.IsCallInProgress);
            if (SessionDecodeHelpers.SameBytesMeasured)
                Assert.Equal(expected, reply);
            else
                Assert.False(string.IsNullOrWhiteSpace(reply));
        }
    }

    /// <summary><see cref="LiteRtSession.Decode"/>, <see cref="LiteRtSession.DecodeAsync"/> and the concatenated
    /// stream give the same text, and so does the runtime's blocking decode, which no member calls (measured equal
    /// on LiteRT-LM v0.20.0, win-x64, CPU and GPU).</summary>
    [SkippableFact]
    public async Task DecodeMembers_AndTheNativeBlockingDecode_GiveTheSameText()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, "Describe a sunrise in Spanish and in Japanese, one sentence each.");

        async Task<string> Run(Func<LiteRtSession, Task<string>> decode)
        {
            using LiteRtSession session = SessionDecodeHelpers.Session(engine, 48);
            session.Prefill(prompt);
            string text = await decode(session);
            Assert.Equal(LiteRtSessionState.Decoded, session.State);
            return text;
        }

        string blocking = await Run(SessionDecodeHelpers.DecodeWithin);
        string async = await Run(s => s.DecodeAsync().WaitAsync(SessionDecodeHelpers.DrainLimit));
        string streamed = await Run(async s => string.Concat(await SessionDecodeHelpers.StreamWithin(s)));
        string native;
        using (LiteRtSession session = SessionDecodeHelpers.Session(engine, 48))
        {
            session.Prefill(prompt);
            native = await Task.Run(() => SessionDecodeHelpers.NativeBlockingDecode(session)).WaitAsync(SessionDecodeHelpers.DrainLimit);
        }
        output.WriteLine($"Decode: {blocking}\nnative: {native}");

        Assert.False(string.IsNullOrEmpty(blocking));
        Assert.Equal(blocking, streamed);
        if (SessionDecodeHelpers.SameBytesMeasured)
        {
            Assert.Equal(blocking, async);
            Assert.Equal(blocking, native);
        }
    }

    /// <summary>A conversation stream cut at a chunk boundary continues on a new session prefilled with the
    /// rendered turn and the partial reply. On CPU the joined text equals the uncut reply byte for byte (measured
    /// on LiteRT-LM v0.20.0, win-x64); on GPU it was measured to differ, so only the end state is
    /// asserted.</summary>
    [SkippableFact]
    public async Task CutReply_ContinuedOnANewSession_CompletesTheReply()
    {
        LiteRtEngine engine = Engine;
        string user = OraclePrompts[0];
        var conversationOptions = new LiteRtConversationOptions { Sampler = SessionDecodeHelpers.Argmax, MaxOutputTokens = 400 };

        var full = new List<string>();
        using (LiteRtConversation conversation = engine.CreateConversation(conversationOptions))
        {
            await Task.Run(async () =>
            {
                await foreach (LiteRtStreamChunk chunk in conversation.SendStreamingAsync(user))
                    if (chunk.Kind == LiteRtStreamChunkKind.Answer)
                        full.Add(chunk.Text);
            }).WaitAsync(SessionDecodeHelpers.DrainLimit);
        }
        Skip.If(full.Count < 6, $"The reply has {full.Count} pieces, too few to cut.");
        int cut = full.Count / 3;

        var partial = new StringBuilder();
        using (LiteRtConversation conversation = engine.CreateConversation(conversationOptions))
        {
            int taken = 0;
            await Task.Run(async () =>
            {
                await foreach (LiteRtStreamChunk chunk in conversation.SendStreamingAsync(user))
                {
                    if (chunk.Kind != LiteRtStreamChunkKind.Answer)
                        continue;
                    partial.Append(chunk.Text);
                    if (++taken == cut)
                        break;
                }
            }).WaitAsync(SessionDecodeHelpers.DrainLimit);
        }

        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 400);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, user) + partial);
        string continued = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"partial: {partial}\ncontinued: {continued}");

        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        Assert.False(string.IsNullOrEmpty(continued));
        if (SessionDecodeHelpers.IsCpu)
            Assert.Equal(string.Concat(full), partial + continued);
    }

    /// <summary>A decode on a Fresh or a Decoded session is refused before any native call, by all three members,
    /// and nothing hangs; the session then still accepts a prefill.</summary>
    [SkippableFact]
    public async Task Decode_OnFreshOrDecoded_IsRefused_AndTheSessionStillWorks()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 8);

        async Task AssertDecodesRefused(LiteRtSessionState state)
        {
            Assert.Equal(state, session.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => SessionDecodeHelpers.DecodeWithin(session));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.DecodeAsync().WaitAsync(SessionDecodeHelpers.DrainLimit));
            await Assert.ThrowsAsync<InvalidOperationException>(() => SessionDecodeHelpers.StreamWithin(session));
            Assert.Equal(state, session.State);
            Assert.False(session.IsCallInProgress);
        }

        await AssertDecodesRefused(LiteRtSessionState.Fresh);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, "Say hello."));
        await SessionDecodeHelpers.DecodeWithin(session);
        await AssertDecodesRefused(LiteRtSessionState.Decoded);
        session.Prefill("\n");
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
    }

    /// <summary>After a prefill that does not fit, the session is Faulted and refuses every member, decodes
    /// included, before any native call (the runtime was measured to skip a decode there and never call
    /// back).</summary>
    [SkippableFact]
    public async Task Faulted_RefusesEveryMember()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 8);
        Assert.Throws<LiteRtException>(() =>
            session.Prefill(SessionModelHelpers.TextOf(engine, SessionEngineFixture.Context + 100, atLeast: true)));
        await SessionDecodeHelpers.AssertAcceptsOnlyDispose(session, LiteRtSessionState.Faulted);
    }

    /// <summary>The token of <see cref="LiteRtSession.DecodeAsync"/> cancels a running reply: the task throws
    /// <see cref="OperationCanceledException"/> and the session is Cancelled and refuses every member.</summary>
    [SkippableFact]
    public async Task DecodeAsync_TokenCancel_EndsCancelled()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 200);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.DecodeAsync(cts.Token).WaitAsync(SessionDecodeHelpers.DrainLimit));
        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.False(session.IsCallInProgress);
        Skip.If(session.State == LiteRtSessionState.Decoded, "The reply ended before the cancel reached the runtime.");
        await SessionDecodeHelpers.AssertAcceptsOnlyDispose(session, LiteRtSessionState.Cancelled);
    }

    /// <summary>The stream's token, cancelled in the loop body, ends the stream with
    /// <see cref="OperationCanceledException"/> and leaves the session Cancelled.</summary>
    [SkippableFact]
    public async Task Stream_TokenCancel_EndsCancelled()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 200);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt));
        using var cts = new CancellationTokenSource();
        int before = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionDecodeHelpers.StreamWithin(session, cts.Token, count =>
        {
            if (count == 3)
            {
                before = count;
                cts.Cancel();
            }
            return true;
        }));
        Assert.Equal(3, before);
        Assert.False(session.IsCallInProgress);
        Skip.If(session.State == LiteRtSessionState.Decoded, "The reply ended before the cancel reached the runtime.");
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);
    }

    /// <summary>A stream left with <c>break</c> before its end cancels the decode: the enumerator's disposal waits
    /// for the runtime, so the session is already Cancelled and free when the loop is left.</summary>
    [SkippableFact]
    public async Task Stream_LeftWithBreak_EndsCancelled()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 200);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt));
        List<string> pieces = await SessionDecodeHelpers.StreamWithin(session, onPiece: count => count < 2);
        Assert.Equal(2, pieces.Count);
        Assert.False(session.IsCallInProgress);
        Skip.If(session.State == LiteRtSessionState.Decoded, "The reply ended before the cancel reached the runtime.");
        await SessionDecodeHelpers.AssertAcceptsOnlyDispose(session, LiteRtSessionState.Cancelled);
    }

    /// <summary>A stream read to its end leaves the session Decoded, and the session accepts another prefill and
    /// decode: two turns on one session.</summary>
    [SkippableFact]
    public async Task Stream_ReadToTheEnd_LeavesASessionForTheNextTurn()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 32);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, "Name three primary colors, separated by commas."));
        List<string> first = await SessionDecodeHelpers.StreamWithin(session);
        Assert.NotEmpty(first);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        Assert.False(session.IsCallInProgress);

        await session.PrefillAsync("\n<|turn>user\nName one more color.<turn|>\n<|turn>model\n");
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        string second = await session.DecodeAsync().WaitAsync(SessionDecodeHelpers.DrainLimit);
        output.WriteLine($"first: {string.Concat(first)}\nsecond: {second}");
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
    }

    /// <summary>A decode that fills the context returns the text up to that point as a normal end and leaves the
    /// session ContextFull, which refuses every member. This pins the runtime's context-full text.</summary>
    [SkippableFact]
    public async Task Decode_ThatFillsTheContext_ReturnsThePartialText_AndEndsContextFull()
    {
        LiteRtEngine engine = Engine;
        string filler = SessionModelHelpers.TextOf(engine, 900, atLeast: false);
        string prompt = SessionDecodeHelpers.Prompt(engine, filler + "\n\nIgnore the text above. " + SessionDecodeHelpers.CountPrompt);
        int promptTokens = engine.Tokenize(prompt).Length;
        Assert.True(promptTokens < SessionEngineFixture.Context - 2, $"The prompt has {promptTokens} tokens.");
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 0);
        session.Prefill(prompt);

        string partial = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"{promptTokens} prompt tokens, {engine.Tokenize(partial).Length} reply tokens, state {session.State}");
        Skip.If(session.State == LiteRtSessionState.Decoded, "The model ended its reply before the context filled.");
        Assert.Equal(LiteRtSessionState.ContextFull, session.State);
        Assert.False(string.IsNullOrEmpty(partial));
        await SessionDecodeHelpers.AssertAcceptsOnlyDispose(session, LiteRtSessionState.ContextFull);
    }

    /// <summary><see cref="LiteRtSessionOptions.MaxOutputTokens"/> caps the reply, which ends Decoded without an
    /// error. The count prompt does not end by itself within the cap, so the cap is what stops it.</summary>
    [SkippableFact]
    public async Task MaxOutputTokens_CapsTheReply()
    {
        LiteRtEngine engine = Engine;
        const int Cap = 8;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, Cap);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt));
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        int tokens = engine.Tokenize(reply).Length;
        output.WriteLine($"{tokens} tokens: {reply}");
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        // Re-tokenizing the text can merge or split tokens at the edges, so allow a small margin.
        Assert.InRange(tokens, 1, Cap + 2);
    }

    /// <summary>A <see cref="LiteRtSession.Dispose"/> in the read loop cancels the decode: the stream throws
    /// <see cref="ObjectDisposedException"/>, and the native session is released when the decode settles.</summary>
    [SkippableFact]
    public async Task Dispose_InsideTheReadLoop_EndsTheStream()
    {
        LiteRtEngine engine = Engine;
        LiteRtSession session = SessionDecodeHelpers.Session(engine, 200);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt));
        Exception? error = await Record.ExceptionAsync(() => SessionDecodeHelpers.StreamWithin(session, onPiece: count =>
        {
            if (count == 2)
                session.Dispose();
            return true;
        }));
        output.WriteLine($"outcome: {error?.GetType().Name ?? "completed"}, state {session.State}");
        Assert.False(session.IsCallInProgress);
        Assert.True(session.Handle.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => session.Prefill("Hello"));
        Skip.If(error is null && session.State == LiteRtSessionState.Decoded, "The reply ended before the cancel reached the runtime.");
        Assert.IsType<ObjectDisposedException>(error);
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);
    }

    /// <summary>A <see cref="LiteRtSession.Dispose"/> from another thread during a stream returns without waiting
    /// for the decode, and the stream ends with <see cref="ObjectDisposedException"/>.</summary>
    [SkippableFact]
    public async Task Dispose_FromAnotherThreadDuringAStream_EndsTheStream()
    {
        LiteRtEngine engine = Engine;
        LiteRtSession session = SessionDecodeHelpers.Session(engine, 200);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt));
        var firstPiece = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<List<string>> reader = SessionDecodeHelpers.StreamWithin(session, onPiece: _ =>
        {
            firstPiece.TrySetResult();
            return true;
        });
        await firstPiece.Task.WaitAsync(SessionDecodeHelpers.DrainLimit);
        var dispose = Stopwatch.StartNew();
        await Task.Run(session.Dispose);
        dispose.Stop();
        Exception? error = await Record.ExceptionAsync(() => reader);
        output.WriteLine($"dispose {dispose.Elapsed.TotalMilliseconds:F1} ms, outcome {error?.GetType().Name ?? "completed"}, state {session.State}");
        Assert.False(session.IsCallInProgress);
        Assert.True(session.Handle.IsClosed);
        Skip.If(error is null && session.State == LiteRtSessionState.Decoded, "The reply ended before the cancel reached the runtime.");
        Assert.IsType<ObjectDisposedException>(error);
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);
    }

    /// <summary>A session and a conversation used in turn on one engine keep their own replies: each equals its run
    /// alone (measured equal on LiteRT-LM v0.20.0, win-x64, CPU and GPU, with calls one after another).</summary>
    [SkippableFact]
    public async Task SessionAndConversation_Interleaved_KeepTheirOwnReplies()
    {
        LiteRtEngine engine = Engine;
        const string SessionUser = "List five kinds of fruit, one per line.";
        const string First = "My code word is TANGERINE. Reply OK.", Second = "What is my code word? One word.";
        var conversationOptions = new LiteRtConversationOptions { Sampler = SessionDecodeHelpers.Argmax, MaxOutputTokens = 32 };
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionUser);

        string sessionAlone;
        using (LiteRtSession alone = SessionDecodeHelpers.Session(engine, 96))
        {
            alone.Prefill(prompt);
            sessionAlone = await SessionDecodeHelpers.DecodeWithin(alone);
        }
        string firstAlone, secondAlone;
        using (LiteRtConversation alone = engine.CreateConversation(conversationOptions))
        {
            firstAlone = alone.Send(First).Text ?? "";
            secondAlone = alone.Send(Second).Text ?? "";
        }

        using LiteRtConversation conversation = engine.CreateConversation(conversationOptions);
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 96);
        session.Prefill(prompt);
        string first = conversation.Send(First).Text ?? "";
        string sessionReply = await SessionDecodeHelpers.DecodeWithin(session);
        string second = conversation.Send(Second).Text ?? "";
        output.WriteLine($"session: {sessionReply}\nconversation: {first} / {second}");

        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        if (SessionDecodeHelpers.SameBytesMeasured)
        {
            Assert.Equal(sessionAlone, sessionReply);
            Assert.Equal(firstAlone, first);
            Assert.Equal(secondAlone, second);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(sessionReply));
            Assert.False(string.IsNullOrWhiteSpace(second));
        }
    }

    /// <summary>A streamed reply with accented letters, CJK and emoji arrives as whole characters: no piece holds
    /// U+FFFD or a lone surrogate, and the pieces join to the blocking decode's text.</summary>
    [SkippableFact]
    public async Task Stream_OfMultibyteText_HasNoBrokenCharacters()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine,
            "Repeat this text exactly, with nothing else: Café, niño, señal. 日本語のテキスト。中文文本。😀👍🎉");
        List<string> pieces;
        using (LiteRtSession session = SessionDecodeHelpers.Session(engine, 64))
        {
            session.Prefill(prompt);
            pieces = await SessionDecodeHelpers.StreamWithin(session);
            Assert.Equal(LiteRtSessionState.Decoded, session.State);
        }
        string text = string.Concat(pieces);
        output.WriteLine($"{pieces.Count} pieces: {text}");
        Skip.If(!text.Any(c => c > 0x7F), "The model answered with ASCII text only.");

        const char Replacement = (char)0xFFFD;
        foreach (string piece in pieces)
        {
            Assert.DoesNotContain(Replacement, piece);
            Assert.False(char.IsLowSurrogate(piece[0]), $"A piece starts with a lone low surrogate: {piece}");
            Assert.False(char.IsHighSurrogate(piece[^1]), $"A piece ends with a lone high surrogate: {piece}");
        }

        using LiteRtSession blocking = SessionDecodeHelpers.Session(engine, 64);
        blocking.Prefill(prompt);
        string whole = await SessionDecodeHelpers.DecodeWithin(blocking);
        Assert.DoesNotContain(Replacement, whole);
        Assert.Equal(whole, text);
    }
}

/// <summary>
/// Session decode lifetime against its engine, on engines these tests load themselves (one live engine at a
/// time): a stream whose enumerator is dropped, and an engine disposed before its session during and after a
/// decode. <see cref="LiteRtEngine.LiveEngineCount"/> shows when the native engine is deleted.
/// </summary>
public sealed class SessionDecodeLifetimeModelTests(ITestOutputHelper output)
{
    /// <summary>A stream whose enumerator is dropped without disposal still settles: the decode runs to its end
    /// and frees the gate. A second dropped stream, then <see cref="LiteRtSession.Dispose"/>, settles too, and the
    /// native session and engine are deleted.</summary>
    [SkippableFact]
    public async Task Stream_DroppedWithoutDisposal_StillSettles()
    {
        LiteRtEngineOptions? options = SessionModelHelpers.Options();
        Skip.If(options is null, SessionModelHelpers.SkipReason);
        int before = LiteRtEngine.LiveEngineCount;
        LiteRtEngine engine = LiteRtEngine.Load(options);
        string shortPrompt = SessionDecodeHelpers.Prompt(engine, "Say hello in five words.");
        string longPrompt = SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt);
        LiteRtSession runsToItsEnd = SessionDecodeHelpers.Session(engine, 16);
        LiteRtSession disposedWhileRunning = SessionDecodeHelpers.Session(engine, 200);
        engine.Dispose();
        Assert.Equal(before + 1, LiteRtEngine.LiveEngineCount);

        runsToItsEnd.Prefill(shortPrompt);
        await StartAndDrop(runsToItsEnd);
        await SessionDecodeHelpers.Eventually(() => !runsToItsEnd.IsCallInProgress, "the dropped stream to settle");
        Assert.Equal(LiteRtSessionState.Decoded, runsToItsEnd.State);
        runsToItsEnd.Dispose();

        disposedWhileRunning.Prefill(longPrompt);
        await StartAndDrop(disposedWhileRunning);
        disposedWhileRunning.Dispose();
        await SessionDecodeHelpers.Eventually(() => !disposedWhileRunning.IsCallInProgress, "the disposed stream to settle");
        output.WriteLine($"dropped then disposed: {disposedWhileRunning.State}");
        Assert.True(disposedWhileRunning.State is LiteRtSessionState.Cancelled or LiteRtSessionState.Decoded,
            $"Unexpected state {disposedWhileRunning.State}.");
        await SessionDecodeHelpers.Eventually(() => LiteRtEngine.LiveEngineCount == before, "the native engine to be deleted");
    }

    /// <summary>Starts a stream, reads one piece, and drops the enumerator without disposing it.</summary>
    private static async Task StartAndDrop(LiteRtSession session)
    {
        IAsyncEnumerator<string> enumerator = session.DecodeStreamingAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(SessionDecodeHelpers.DrainLimit));
        Assert.True(session.IsCallInProgress || session.State != LiteRtSessionState.Prefilled);
    }

    /// <summary>The engine disposed during a stream does not stop it, and the session decodes again afterwards;
    /// the native engine is deleted only when the session is disposed.</summary>
    [SkippableFact]
    public async Task Engine_DisposedDuringAndAfterADecode_TheSessionKeepsWorking()
    {
        LiteRtEngineOptions? options = SessionModelHelpers.Options();
        Skip.If(options is null, SessionModelHelpers.SkipReason);
        int before = LiteRtEngine.LiveEngineCount;
        LiteRtEngine engine = LiteRtEngine.Load(options);
        string prompt = SessionDecodeHelpers.Prompt(engine, "Name three primary colors, separated by commas.");
        LiteRtSession session = SessionDecodeHelpers.Session(engine, 24);
        session.Prefill(prompt);

        List<string> pieces = await SessionDecodeHelpers.StreamWithin(session, onPiece: count =>
        {
            if (count == 1)
                engine.Dispose();
            return true;
        });
        Assert.NotEmpty(pieces);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        Assert.Equal(before + 1, LiteRtEngine.LiveEngineCount);

        session.Prefill("\n<|turn>user\nName one more color.<turn|>\n<|turn>model\n");
        await SessionDecodeHelpers.DecodeWithin(session);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        Assert.Equal(before + 1, LiteRtEngine.LiveEngineCount);

        session.Dispose();
        Assert.Equal(before, LiteRtEngine.LiveEngineCount);
    }
}
