using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>Shared pieces of the checkpoint, rewind and reset tests on a model.</summary>
internal static class SessionCheckpointHelpers
{
    /// <summary>The output cap of these tests: long enough for whole replies to the prompts below, short enough
    /// that a few turns fit the fixture's context.</summary>
    public const int Cap = 160;

    public const string FirstUser = "Name three primary colors and say one thing about each, in three short sentences.";
    public const string SecondUser = "Now do the same for three secondary colors.";
    public const string ThirdUser = "Which of all six colors do you like best, and why? Answer in two sentences.";
    public const string ResetUser = "What is the capital of France? Answer in two full sentences.";
    public const string OtherUser = "Say hello in five words.";

    public static LiteRtSession Session(LiteRtEngine engine, int cap = Cap) => SessionDecodeHelpers.Session(engine, cap);

    /// <summary>The reply of a new session that prefills each text in turn and decodes after each one: the
    /// reference for a session that reached the same point the same way.</summary>
    public static Task<string> Twin(LiteRtEngine engine, params string[] turns) => TwinWith(Cap, engine, turns);

    /// <summary><see cref="Twin"/> with the output cap <paramref name="cap"/>, the cap of the session it is
    /// compared with.</summary>
    public static async Task<string> TwinWith(int cap, LiteRtEngine engine, params string[] turns)
    {
        using LiteRtSession twin = Session(engine, cap);
        string reply = "";
        foreach (string turn in turns)
        {
            await SessionDecodeHelpers.Within(() => twin.Prefill(turn));
            reply = await SessionDecodeHelpers.DecodeWithin(twin);
            Assert.Equal(LiteRtSessionState.Decoded, twin.State);
        }
        return reply;
    }

    /// <summary>Another session that prefills and decodes on the same engine, and is disposed.</summary>
    public static async Task OtherSessionGenerates(LiteRtEngine engine)
    {
        using LiteRtSession other = Session(engine, 16);
        await SessionDecodeHelpers.Within(() => other.Prefill(SessionDecodeHelpers.Prompt(engine, OtherUser)));
        Assert.False(string.IsNullOrEmpty(await SessionDecodeHelpers.DecodeWithin(other)));
    }

    /// <summary>Asserts that <paramref name="call"/> throws the exception of a rewind to a dropped checkpoint:
    /// <see cref="InvalidOperationException"/> around the runtime's NOT_FOUND, and no change to the session.</summary>
    public static async Task AssertDropped(LiteRtSession session, LiteRtSessionCheckpoint checkpoint)
    {
        LiteRtSessionState before = session.State;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint)));
        var inner = Assert.IsType<LiteRtException>(ex.InnerException);
        Assert.Equal(LiteRtStatusCode.NotFound, inner.StatusCode);
        Assert.Contains("litert_lm_session_rewind_to_checkpoint failed", inner.Message, StringComparison.Ordinal);
        Assert.Equal(before, session.State);
        Assert.False(session.IsCallInProgress);
    }

    /// <summary>Asserts that the start-token refusal of a first prefill applies: the session is Fresh, and when
    /// the model has a start-token text, a prefill that starts with it is refused and changes nothing.</summary>
    public static void AssertFirstPrefillRule(LiteRtSession session)
    {
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        if (session.RefusedStartTokenText is { } start)
        {
            var ex = Assert.Throws<ArgumentException>(() => session.Prefill(start + "Hello"));
            Assert.Contains("adds the start token itself", ex.Message, StringComparison.Ordinal);
            Assert.Equal(LiteRtSessionState.Fresh, session.State);
        }
    }

    /// <summary>Raw text that, prefilled on a session that already holds about <paramref name="used"/>
    /// positions, leaves the end of the context to a decode: one sentence repeated, then the start of the next
    /// repeat, so the model goes on with the repeat until the context is full. The runtime rounds a later
    /// prefill up to a multiple of 128 positions and refuses one that does not fit after the rounding, so the
    /// text stays one multiple of 128 under what is left, less a margin for the re-tokenized count.</summary>
    public static string FillerFor(LiteRtEngine engine, int used)
    {
        const string Opening = "The quick brown fox";
        int left = SessionEngineFixture.Context - used;
        int tokens = (left - 16) / 128 * 128 - 8;
        string filler = SessionModelHelpers.TextOf(engine, tokens - engine.Tokenize(Opening).Length, atLeast: false);
        return filler + Opening;
    }
}

/// <summary>
/// Checkpoints, rewind and reset on a model, on one shared engine with a 1024-token context. The reference for a
/// decode after a rewind is the same session's first decode from that point, and a new session that reached the
/// point the same way (prefill and decode), never a session prefilled with reply text. The equalities are asserted
/// on every platform: these tests are the measurement off win-x64.
/// </summary>
public sealed class SessionCheckpointModelTests(SessionEngineFixture fixture, ITestOutputHelper output) : IClassFixture<SessionEngineFixture>
{
    private LiteRtEngine Engine
    {
        get
        {
            Skip.If(fixture.Engine is null, SessionModelHelpers.SkipReason);
            return fixture.Engine;
        }
    }

    /// <summary>A checkpoint saved after the first prefill: decode, rewind, decode again. The second decode equals
    /// the first and a new session's; without the rewind the session would refuse the second decode.</summary>
    [SkippableFact]
    public async Task RewindTo_ACheckpointAfterAPrefill_DecodesTheSameReplyAgain()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, prompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        session.Prefill(prompt);
        LiteRtSessionCheckpoint checkpoint = session.SaveCheckpoint();
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        string first = await SessionDecodeHelpers.DecodeWithin(session);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SessionDecodeHelpers.DecodeWithin(session));

        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        string again = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"first: {first}\nagain: {again}\ntwin:  {twin}");

        Assert.False(string.IsNullOrEmpty(first));
        Assert.Equal(first, again);
        Assert.Equal(twin, again);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
    }

    /// <summary>A checkpoint saved after a decode: run the next turn and one more, rewind, run the same next turn.
    /// It equals the first pass of that turn and a new session's. Two turns run before the rewind because after
    /// one the same turn was measured to give the same text on CPU even without the rewind.</summary>
    [SkippableFact]
    public async Task RewindTo_ACheckpointAfterADecode_RunsTheSameNextTurnAgain()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string second = SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.SecondUser);
        string third = SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.ThirdUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, prompt, second);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        session.Prefill(prompt);
        await SessionDecodeHelpers.DecodeWithin(session);
        LiteRtSessionCheckpoint checkpoint = session.SaveCheckpoint();
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        session.Prefill(second);
        string firstPass = await SessionDecodeHelpers.DecodeWithin(session);
        session.Prefill(third);
        string later = await SessionDecodeHelpers.DecodeWithin(session);

        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        session.Prefill(second);
        string again = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"first pass: {firstPass}\nlater turn: {later}\nagain:      {again}\ntwin:       {twin}");

        Assert.False(string.IsNullOrEmpty(firstPass));
        Assert.Equal(firstPass, again);
        Assert.Equal(twin, again);
    }

    /// <summary>The rewind of the first test with another session generating on the engine, once between the
    /// save and the rewind and once right before the save. The reply after the rewind still equals the session's
    /// first decode and a new session's that ran alone.</summary>
    [SkippableFact]
    public async Task RewindTo_WithAnotherSessionGeneratingAroundTheSave_DecodesTheSameReplyAgain()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, prompt);

        foreach (bool otherBeforeTheSave in new[] { false, true })
        {
            using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
            session.Prefill(prompt);
            if (otherBeforeTheSave)
                await SessionCheckpointHelpers.OtherSessionGenerates(engine);
            LiteRtSessionCheckpoint checkpoint = await Task.Run(session.SaveCheckpoint).WaitAsync(SessionDecodeHelpers.DrainLimit);
            string first = await SessionDecodeHelpers.DecodeWithin(session);
            if (!otherBeforeTheSave)
                await SessionCheckpointHelpers.OtherSessionGenerates(engine);
            await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
            Assert.Equal(LiteRtSessionState.Prefilled, session.State);
            string again = await SessionDecodeHelpers.DecodeWithin(session);
            output.WriteLine($"other {(otherBeforeTheSave ? "before the save" : "between save and rewind")}\nfirst: {first}\nagain: {again}");

            Assert.Equal(first, again);
            Assert.Equal(twin, again);
        }
    }

    /// <summary>A rewind drops the checkpoints saved at a later point: a rewind to one of them throws
    /// <see cref="InvalidOperationException"/> around the runtime's NOT_FOUND, changes nothing, and the next decode
    /// still equals the first decode from the rewound point.</summary>
    [SkippableFact]
    public async Task RewindTo_ADroppedCheckpoint_IsRefused_AndChangesNothing()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, prompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        session.Prefill(prompt);
        LiteRtSessionCheckpoint early = session.SaveCheckpoint();
        string first = await SessionDecodeHelpers.DecodeWithin(session);
        session.Prefill(SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.SecondUser));
        LiteRtSessionCheckpoint late = session.SaveCheckpoint();

        await SessionDecodeHelpers.Within(() => session.RewindTo(early));
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        await SessionCheckpointHelpers.AssertDropped(session, late);
        // The rewound point is still there, and a failed rewind leaves the session usable.
        string again = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"first: {first}\nagain: {again}");
        Assert.Equal(first, again);
        Assert.Equal(twin, again);
        // A checkpoint saved at the rewound point itself is not dropped.
        await SessionDecodeHelpers.Within(() => session.RewindTo(early));
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
    }

    /// <summary>A reset after one turn and after three returns the session to Fresh: the start-token rule of a
    /// first prefill applies again, and the next turn equals a new session's.</summary>
    [SkippableFact]
    public async Task Reset_AfterOneTurnAndAfterSeveral_GivesAFreshSession()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, resetPrompt);

        foreach (int turns in new[] { 1, 3 })
        {
            using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
            string[] texts = [prompt, SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.SecondUser), SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.ThirdUser)];
            for (int i = 0; i < turns; i++)
            {
                session.Prefill(texts[i]);
                await SessionDecodeHelpers.DecodeWithin(session);
            }
            await SessionDecodeHelpers.Within(session.Reset);
            SessionCheckpointHelpers.AssertFirstPrefillRule(session);
            session.Prefill(resetPrompt);
            string reply = await SessionDecodeHelpers.DecodeWithin(session);
            output.WriteLine($"after {turns} turn(s): {reply}");
            Assert.Equal(twin, reply);
        }
    }

    /// <summary>A reset drops every checkpoint except those saved on the Fresh session: a later one is refused as
    /// dropped, the Fresh one still rewinds, and the turn after it equals a new session's.</summary>
    [SkippableFact]
    public async Task Reset_KeepsOnlyTheCheckpointsSavedWhileFresh()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, resetPrompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        LiteRtSessionCheckpoint fresh = session.SaveCheckpoint();
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        session.Prefill(prompt);
        LiteRtSessionCheckpoint prefilled = session.SaveCheckpoint();
        await SessionDecodeHelpers.DecodeWithin(session);
        await SessionDecodeHelpers.Within(session.Reset);
        Assert.Equal(LiteRtSessionState.Fresh, session.State);

        session.Prefill(prompt);
        await SessionCheckpointHelpers.AssertDropped(session, prefilled);
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        await SessionDecodeHelpers.Within(() => session.RewindTo(fresh));
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        session.Prefill(resetPrompt);
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
    }

    /// <summary>A save after a reset gets a label that no earlier save had. The runtime accepts a second save under
    /// a label it holds and then rewinds that label to the later save, so a repeated label would move the
    /// checkpoint saved on the Fresh session to the later point.</summary>
    [SkippableFact]
    public async Task SaveCheckpoint_AfterAReset_NeverReusesALabel()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, resetPrompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        LiteRtSessionCheckpoint fresh = session.SaveCheckpoint();
        session.Prefill(prompt);
        LiteRtSessionCheckpoint beforeTheReset = session.SaveCheckpoint();
        await SessionDecodeHelpers.Within(session.Reset);
        session.Prefill(prompt);
        LiteRtSessionCheckpoint afterTheReset = session.SaveCheckpoint();
        await SessionDecodeHelpers.DecodeWithin(session);

        Assert.Equal(3, new[] { fresh, beforeTheReset, afterTheReset }.Select(c => Convert.ToHexString(c.Label)).Distinct().Count());
        await SessionDecodeHelpers.Within(() => session.RewindTo(fresh));
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        session.Prefill(resetPrompt);
        Assert.Equal(twin, await SessionDecodeHelpers.DecodeWithin(session));
    }

    /// <summary>A full context recovers by a rewind: save after the first turn's prefill, decode, prefill raw text
    /// that leaves the end of the context to the next decode, decode until the context is full, rewind. The
    /// decode after the rewind equals the session's first decode and a new session's. A checkpoint dropped by an
    /// earlier rewind is refused from ContextFull too, and changes nothing.</summary>
    [SkippableFact]
    public async Task ContextFull_RewindTo_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string twin = await SessionCheckpointHelpers.TwinWith(0, engine, prompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine, 0);
        await SessionDecodeHelpers.Within(() => session.Prefill(prompt));
        LiteRtSessionCheckpoint checkpoint = session.SaveCheckpoint();
        string first = await SessionDecodeHelpers.DecodeWithin(session);
        // Saved at a later point than the checkpoint, so the rewind below drops it.
        LiteRtSessionCheckpoint dropped = session.SaveCheckpoint();
        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        Assert.Equal(first, await SessionDecodeHelpers.DecodeWithin(session));

        // The start position, the prompt, the reply and the position that ends the model's turn.
        int used = 1 + engine.Tokenize(prompt + first).Length + 1;
        string filler = SessionCheckpointHelpers.FillerFor(engine, used);
        await SessionDecodeHelpers.Within(() => session.Prefill(filler));
        string partial = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"{used} positions before the filler, {engine.Tokenize(filler).Length} filler tokens, " +
                         $"{engine.Tokenize(partial).Length} reply tokens, state {session.State}");
        Skip.If(session.State == LiteRtSessionState.Decoded,
            $"The model ended its reply before the context filled: reply \"{partial}\".");
        await SessionDecodeHelpers.AssertAcceptsOnlyRewindResetAndDispose(session, LiteRtSessionState.ContextFull);

        await SessionCheckpointHelpers.AssertDropped(session, dropped);
        Assert.Equal(LiteRtSessionState.ContextFull, session.State);
        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        string again = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"first: {first}\nagain: {again}");
        Assert.Equal(first, again);
        Assert.Equal(twin, again);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
    }

    /// <summary>A full context recovers by a reset: fill the context with the raw repeated text of the stable
    /// context-full test, reset, and the next turn equals a new session's; the start-token rule applies again.</summary>
    [SkippableFact]
    public async Task ContextFull_Reset_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.TwinWith(0, engine, resetPrompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine, 0);
        // A fresh session holds only its start position.
        await SessionDecodeHelpers.Within(() => session.Prefill(SessionContextFill.FirstPrefill(engine)));
        string partial = await SessionDecodeHelpers.DecodeWithin(session);
        Skip.If(session.State == LiteRtSessionState.Decoded,
            $"The model ended its reply before the context filled: reply \"{partial}\".");
        await SessionDecodeHelpers.AssertAcceptsOnlyRewindResetAndDispose(session, LiteRtSessionState.ContextFull);

        await SessionDecodeHelpers.Within(session.Reset);
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionDecodeHelpers.Within(() => session.Prefill(resetPrompt));
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
    }

    /// <summary>A second prefill that is longer than the context fails, leaves the session Faulted, and a rewind
    /// to the checkpoint saved before it recovers the session: the decode equals a new session's.</summary>
    [SkippableFact]
    public async Task FailedSecondPrefill_RewindTo_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, prompt);
        string tooLong = SessionModelHelpers.TextOf(engine, 1500, atLeast: true);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionDecodeHelpers.Within(() => session.Prefill(prompt));
        LiteRtSessionCheckpoint checkpoint = session.SaveCheckpoint();
        var ex = await Assert.ThrowsAsync<LiteRtException>(() => SessionDecodeHelpers.Within(() => session.Prefill(tooLong)));
        output.WriteLine($"{ex.StatusCode}: {ex.Message}");
        // The status was measured on win-x64; elsewhere the test needs only the failed prefill.
        if (SessionDecodeHelpers.SameBytesMeasured)
        {
            Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
            Assert.Contains("Input token ids are too long", ex.Message, StringComparison.Ordinal);
        }
        await SessionDecodeHelpers.AssertAcceptsOnlyRewindResetAndDispose(session, LiteRtSessionState.Faulted);

        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
    }

    /// <summary>The same failed second prefill, recovered by a reset: the next turn equals a new session's, and the
    /// start-token rule applies again.</summary>
    [SkippableFact]
    public async Task FailedSecondPrefill_Reset_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, resetPrompt);
        string tooLong = SessionModelHelpers.TextOf(engine, 1500, atLeast: true);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionDecodeHelpers.Within(() => session.Prefill(prompt));
        var ex = await Assert.ThrowsAsync<LiteRtException>(() => SessionDecodeHelpers.Within(() => session.Prefill(tooLong)));
        output.WriteLine($"{ex.StatusCode}: {ex.Message}");
        if (SessionDecodeHelpers.SameBytesMeasured)
            Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        await SessionDecodeHelpers.AssertAcceptsOnlyRewindResetAndDispose(session, LiteRtSessionState.Faulted);

        await SessionDecodeHelpers.Within(session.Reset);
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionDecodeHelpers.Within(() => session.Prefill(resetPrompt));
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
    }

    /// <summary>A Cancelled session refuses a save, a rewind to a checkpoint it saved, and a reset, before any
    /// native call.</summary>
    [SkippableFact]
    public async Task Cancelled_RefusesSaveRewindAndReset()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, SessionDecodeHelpers.CancelCap);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt));
        LiteRtSessionCheckpoint checkpoint = session.SaveCheckpoint();
        List<string> pieces = await SessionDecodeHelpers.StreamWithin(session, onPiece: count => count < 2);
        Assert.False(session.IsCallInProgress);
        Skip.If(pieces.Count < 2, "The reply ended before its second piece, so the loop was not left early.");
        Skip.If(session.State == LiteRtSessionState.Decoded, "The reply ended before the cancel reached the runtime.");
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);

        foreach (Action call in new Action[] { () => session.SaveCheckpoint(), () => session.RewindTo(checkpoint), session.Reset })
            Assert.Contains("was cancelled", Assert.Throws<InvalidOperationException>(call).Message, StringComparison.Ordinal);
        await SessionDecodeHelpers.AssertAcceptsOnlyDispose(session, LiteRtSessionState.Cancelled);
    }

    /// <summary>A save, a rewind and a reset during a running stream are refused as busy, from the read loop
    /// itself; the stream then ends normally and the rewind works.</summary>
    [SkippableFact]
    public async Task CheckpointMembers_DuringARunningStream_AreRefusedAsBusy()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, 24);
        session.Prefill(SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser));
        LiteRtSessionCheckpoint checkpoint = session.SaveCheckpoint();
        long saves = session.CheckpointCount;
        var refusals = new List<string>();
        List<string> pieces = await SessionDecodeHelpers.StreamWithin(session, onPiece: count =>
        {
            if (count == 1)
                foreach (Action call in new Action[] { () => session.SaveCheckpoint(), () => session.RewindTo(checkpoint), session.Reset })
                    refusals.Add(Assert.Throws<InvalidOperationException>(call).Message);
            return true;
        });
        Assert.NotEmpty(pieces);
        Assert.Equal(3, refusals.Count);
        Assert.All(refusals, message => Assert.Contains("in progress", message, StringComparison.Ordinal));
        Assert.Equal(saves, session.CheckpointCount);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        Assert.False(session.IsCallInProgress);

        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
    }
}

/// <summary>The raw repeated text of the stable context-full test, as a first prefill.</summary>
internal static class SessionContextFill
{
    /// <summary>One sentence repeated up to about a dozen positions from the end of the context, then the start
    /// of the next repeat, for a Fresh session (which holds one start position).</summary>
    public static string FirstPrefill(LiteRtEngine engine)
    {
        const int FreePositions = 12;
        const string Opening = "The quick brown fox";
        int Tokens(string filler) => engine.Tokenize(filler + Opening).Length;
        int target = SessionEngineFixture.Context - 1 - FreePositions;
        string filler = SessionModelHelpers.TextOf(engine, target - Tokens(""), atLeast: false);
        while (Tokens(filler + SessionModelHelpers.Sentence) <= target)
            filler += SessionModelHelpers.Sentence;
        return filler + Opening;
    }
}

/// <summary>
/// Recovery from a failed first prefill and from the two other failure statuses of a prefill that does not fit,
/// in a class of their own, with an engine apart from the other session tests: a decode that never ends would
/// hold its session, and with it the engine, until the process ends. The recovered decode equals a new
/// session's.
/// </summary>
public sealed class SessionRecoveryModelTests(SessionEngineFixture fixture, ITestOutputHelper output) : IClassFixture<SessionEngineFixture>
{
    private LiteRtEngine Engine
    {
        get
        {
            Skip.If(fixture.Engine is null, SessionModelHelpers.SkipReason);
            return fixture.Engine;
        }
    }

    /// <summary>Fails a prefill of <paramref name="text"/>. The status and the hint are checked only where they
    /// were measured (win-x64); elsewhere the test needs only a failed prefill, whose status it logs.</summary>
    private async Task<LiteRtException> FailPrefill(LiteRtSession session, string text, LiteRtStatusCode measured)
    {
        var ex = await Assert.ThrowsAsync<LiteRtException>(() => SessionDecodeHelpers.Within(() => session.Prefill(text)));
        output.WriteLine($"{ex.StatusCode}: {ex.Message}");
        if (SessionDecodeHelpers.SameBytesMeasured)
        {
            Assert.Equal(measured, ex.StatusCode);
            Assert.Contains(LiteRtSession.DoesNotFitHint, ex.Message, StringComparison.Ordinal);
        }
        await SessionDecodeHelpers.AssertAcceptsOnlyRewindResetAndDispose(session, LiteRtSessionState.Faulted);
        return ex;
    }

    /// <summary>Prefills the reset prompt on a recovered Fresh session and decodes; the reply must equal a new
    /// session's.</summary>
    private async Task AssertTurnEqualsANewSession(LiteRtEngine engine, LiteRtSession session)
    {
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, resetPrompt);
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionDecodeHelpers.Within(() => session.Prefill(resetPrompt));
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
    }

    /// <summary>A first prefill longer than the context, then a reset.</summary>
    [SkippableFact]
    public async Task FailedFirstPrefill_Reset_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await FailPrefill(session, SessionModelHelpers.TextOf(engine, SessionEngineFixture.Context + 100, atLeast: true),
            LiteRtStatusCode.InvalidArgument);
        await SessionDecodeHelpers.Within(session.Reset);
        await AssertTurnEqualsANewSession(engine, session);
    }

    /// <summary>A first prefill longer than the context, then a rewind to the checkpoint saved on the Fresh
    /// session.</summary>
    [SkippableFact]
    public async Task FailedFirstPrefill_RewindToAFreshCheckpoint_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        LiteRtSessionCheckpoint fresh = session.SaveCheckpoint();
        await FailPrefill(session, SessionModelHelpers.TextOf(engine, SessionEngineFixture.Context + 100, atLeast: true),
            LiteRtStatusCode.InvalidArgument);
        await SessionDecodeHelpers.Within(() => session.RewindTo(fresh));
        await AssertTurnEqualsANewSession(engine, session);
    }

    /// <summary>A second prefill that does not fit the context left by the first, which the runtime refuses with
    /// RESOURCE_EXHAUSTED, then a reset.</summary>
    [SkippableFact]
    public async Task PrefillResourceExhausted_Reset_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionDecodeHelpers.Within(() => session.Prefill(SessionModelHelpers.TextOf(engine, 600, atLeast: false)));
        await FailPrefill(session, SessionModelHelpers.TextOf(engine, 600, atLeast: true), LiteRtStatusCode.ResourceExhausted);
        await SessionDecodeHelpers.Within(session.Reset);
        await AssertTurnEqualsANewSession(engine, session);
    }

    /// <summary>A second prefill that fits the context by its count but not after the runtime rounds it up to a
    /// multiple of 128, which the runtime refuses with FAILED_PRECONDITION, then a reset.</summary>
    [SkippableFact]
    public async Task PrefillFailedPrecondition_Reset_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        string first = SessionModelHelpers.TextOf(engine, 600, atLeast: false);
        string second = SessionModelHelpers.TextOf(engine, 400, atLeast: true);
        int left = SessionEngineFixture.Context - 1 - engine.Tokenize(first).Length;
        int count = engine.Tokenize(second).Length;
        output.WriteLine($"{left} positions left, {count} input tokens, rounded up to {(count + 127) / 128 * 128}");
        // The test's own arithmetic: the input fits by its count and not after the rounding. The rounding is
        // inferred from the measured thresholds on win-x64.
        Assert.True(count < left && (count + 127) / 128 * 128 > left, $"{count} tokens with {left} positions left.");

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionDecodeHelpers.Within(() => session.Prefill(first));
        await FailPrefill(session, second, LiteRtStatusCode.FailedPrecondition);
        await SessionDecodeHelpers.Within(session.Reset);
        await AssertTurnEqualsANewSession(engine, session);
    }
}
