using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>Shared pieces of the checkpoint, rewind and reset tests on a model.</summary>
internal static class SessionCheckpointHelpers
{
    /// <summary>The output cap of these tests. The decoded tokens are almost all of their cost, and no test needs a
    /// whole reply: a rewind or reset that does nothing shows as a refused decode, a decode that never returns, a
    /// dropped checkpoint that is not refused, or a reply that differs within its first tokens, because the turn
    /// before it is <see cref="FrenchUser"/> or the turn after it is <see cref="PreviousUser"/>. A test that relies
    /// on a reply that differs also runs its texts with the call left out, and fails as an unmet precondition when
    /// that reply does not differ on the platform where it runs (<see cref="AssertTheCallChangesTheReply"/>).</summary>
    public const int Cap = 16;

    public const string FirstUser = "Name three primary colors and say one thing about each, in three short sentences.";
    public const string SecondUser = "Now do the same for three secondary colors.";

    /// <summary>A turn that changes every later reply. When a rewind or reset that does nothing leaves it in the
    /// context, the next reply is in French from its first token; this was measured on win-x64, CPU and GPU, with
    /// caps of 16 and 24, after one turn and after three, and as a prefill that was not decoded.</summary>
    public const string FrenchUser = "From now on, answer every question only in French.";

    public const string ResetUser = "What is the capital of France? Answer in two full sentences.";

    /// <summary>A turn whose reply on a fresh session differs from its reply after an earlier turn: on a fresh
    /// session the model names this question, after a turn it names that turn. Measured on win-x64, CPU and GPU, as
    /// a first turn and after one or three turns; as a repeated later turn the reply did not always change, so it is
    /// used only as the first turn of a fresh point.</summary>
    public const string PreviousUser = "What was my previous question? Answer in one short sentence.";

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

    /// <summary>The replies of a new session that prefills each text in turn and decodes after each one, with the
    /// output cap <see cref="Cap"/>. Reply <c>i</c> is the reference for a session that reached turn <c>i</c> the
    /// same way.</summary>
    public static async Task<string[]> Replies(LiteRtEngine engine, params string[] turns)
    {
        using LiteRtSession session = Session(engine);
        var replies = new string[turns.Length];
        for (int i = 0; i < turns.Length; i++)
        {
            await Prefill(session, turns[i]);
            replies[i] = await SessionDecodeHelpers.DecodeWithin(session);
            Assert.Equal(LiteRtSessionState.Decoded, session.State);
        }
        return replies;
    }

    /// <summary>Asserts that <paramref name="withoutTheCall"/>, the reply of a session that ran the test's texts
    /// in the same order with <paramref name="call"/> left out, differs from <paramref name="expected"/>. That
    /// difference is what makes the test fail when the call does nothing, so a reply that is the same fails the
    /// test with a message that names the precondition, before the call runs.</summary>
    /// <param name="call">The call that the session left out, as the failure message names it.</param>
    public static void AssertTheCallChangesTheReply(string expected, string withoutTheCall, string call, ITestOutputHelper output)
    {
        output.WriteLine($"without {call}: {withoutTheCall}");
        Assert.True(withoutTheCall != expected,
            $"Precondition not met, so the test cannot tell {call} from one that does nothing: with {call} left " +
            $"out, the capped reply is the same, \"{withoutTheCall}\".");
    }

    /// <summary>Prefills <paramref name="text"/> with the drain limit.</summary>
    public static Task Prefill(LiteRtSession session, string text) => SessionDecodeHelpers.Within(() => session.Prefill(text));

    /// <summary>Saves a checkpoint with the drain limit.</summary>
    public static Task<LiteRtSessionCheckpoint> Save(LiteRtSession session)
        => Task.Run(session.SaveCheckpoint).WaitAsync(SessionDecodeHelpers.DrainLimit);

    /// <summary>Another session that prefills and decodes on the same engine, and is disposed.</summary>
    public static async Task OtherSessionGenerates(LiteRtEngine engine)
    {
        using LiteRtSession other = Session(engine, 16);
        await SessionDecodeHelpers.Within(() => other.Prefill(SessionDecodeHelpers.Prompt(engine, OtherUser)));
        Assert.False(string.IsNullOrEmpty(await SessionDecodeHelpers.DecodeWithin(other)));
    }

    /// <summary>A conversation that sends one message on the same engine, and is disposed.</summary>
    public static async Task ConversationGenerates(LiteRtEngine engine)
    {
        using LiteRtConversation conversation = engine.CreateConversation(
            new LiteRtConversationOptions { Sampler = SessionDecodeHelpers.Argmax, MaxOutputTokens = 16 });
        Assert.False(string.IsNullOrEmpty(await SessionDecodeHelpers.SendWithin(conversation, OtherUser)));
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

    /// <summary>Prefills <paramref name="text"/>, which the runtime must refuse: the precondition of a recovery
    /// test. A prefill that the runtime accepts, or that throws another exception, fails the test with a message
    /// that names the precondition, so that a red run is not read as a failure of the recovery.</summary>
    /// <param name="what">The prefill, as the failure message names it.</param>
    public static async Task<LiteRtException> FailPrefill(LiteRtSession session, string text, string what, ITestOutputHelper output)
    {
        Exception? error = await Record.ExceptionAsync(() => SessionDecodeHelpers.Within(() => session.Prefill(text)));
        string outcome = error is null ? "succeeded" : $"threw {error.GetType().Name}: {error.Message}";
        Assert.True(error is LiteRtException,
            $"Precondition not met, so the recovery did not run: {what} was expected to fail in the runtime, and it " +
            $"{outcome}. State {session.State}.");
        var failure = (LiteRtException)error!;
        output.WriteLine($"{what}: {failure.StatusCode}: {failure.Message}");
        return failure;
    }

    /// <summary>Asserts that the decode before a recovery filled the context, the precondition of the
    /// ContextFull tests. A reply that ended first fails the test with a message that names the precondition,
    /// so that a red run is not read as a failure of the recovery.</summary>
    public static void AssertContextFilled(LiteRtSession session, string partial)
        => Assert.True(session.State == LiteRtSessionState.ContextFull,
            $"Precondition not met, so the recovery did not run: the decode was expected to fill the context, and the " +
            $"session is {session.State} with the reply \"{partial}\".");

    /// <summary>Raw text that, prefilled on a session that already holds about <paramref name="used"/>
    /// positions, leaves the end of the context to a decode: one sentence repeated, then the start of the next
    /// repeat, so the model goes on with the repeat until the context is full. The runtime rounds a later
    /// prefill up to a multiple of 128 positions and refuses one that does not fit after the rounding, so the
    /// text stays one multiple of 128 under what is left, less a margin for the re-tokenized count. The rounding
    /// is inferred from the thresholds measured on win-x64.</summary>
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
        await SessionCheckpointHelpers.Prefill(session, prompt);
        LiteRtSessionCheckpoint checkpoint = await SessionCheckpointHelpers.Save(session);
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

    /// <summary>A checkpoint saved after a decode: run the next turn, then the French turn, rewind, run the same
    /// next turn. It equals the first pass of that turn and a new session's. Without the rewind the repeated turn
    /// follows the French turn and gets a French reply, which a new session that runs the same turns checks before
    /// the rewind. The French turn is there because, after the next turn alone, the same turn was measured to give
    /// the same text without the rewind: in full on CPU, and in its first 8 tokens on CPU and GPU.</summary>
    [SkippableFact]
    public async Task RewindTo_ACheckpointAfterADecode_RunsTheSameNextTurnAgain()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string second = SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.SecondUser);
        string french = SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.FrenchUser);
        // The session's texts with the rewind left out: the second reply is the new session's reference, and the
        // last is the reply that a rewind that does nothing would give.
        string[] replies = await SessionCheckpointHelpers.Replies(engine, prompt, second, french, second);
        string twin = replies[1];
        SessionCheckpointHelpers.AssertTheCallChangesTheReply(twin, replies[3], "the rewind", output);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionCheckpointHelpers.Prefill(session, prompt);
        await SessionDecodeHelpers.DecodeWithin(session);
        LiteRtSessionCheckpoint checkpoint = await SessionCheckpointHelpers.Save(session);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        await SessionCheckpointHelpers.Prefill(session, second);
        string firstPass = await SessionDecodeHelpers.DecodeWithin(session);
        await SessionCheckpointHelpers.Prefill(session, french);
        string later = await SessionDecodeHelpers.DecodeWithin(session);

        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        await SessionCheckpointHelpers.Prefill(session, second);
        string again = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"first pass: {firstPass}\nlater turn: {later}\nagain:      {again}\ntwin:       {twin}");

        Assert.False(string.IsNullOrEmpty(firstPass));
        Assert.Equal(firstPass, again);
        Assert.Equal(twin, again);
    }

    /// <summary>The rewind of the first test with another user of the engine: another session that generates
    /// between the save and the rewind, another session that generates right before the save, and a conversation
    /// that sends between the save and the rewind. The reply after the rewind still equals the session's first
    /// decode and a new session's that ran alone.</summary>
    [SkippableFact]
    public async Task RewindTo_WithAnotherSessionGeneratingAroundTheSave_DecodesTheSameReplyAgain()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, prompt);

        foreach (string other in new[] { "session between save and rewind", "session before the save", "conversation between save and rewind" })
        {
            using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
            await SessionCheckpointHelpers.Prefill(session, prompt);
            if (other == "session before the save")
                await SessionCheckpointHelpers.OtherSessionGenerates(engine);
            LiteRtSessionCheckpoint checkpoint = await SessionCheckpointHelpers.Save(session);
            string first = await SessionDecodeHelpers.DecodeWithin(session);
            if (other == "session between save and rewind")
                await SessionCheckpointHelpers.OtherSessionGenerates(engine);
            else if (other == "conversation between save and rewind")
                await SessionCheckpointHelpers.ConversationGenerates(engine);
            await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
            Assert.Equal(LiteRtSessionState.Prefilled, session.State);
            string again = await SessionDecodeHelpers.DecodeWithin(session);
            output.WriteLine($"other {other}\nfirst: {first}\nagain: {again}");

            Assert.Equal(first, again);
            Assert.Equal(twin, again);
        }
    }

    /// <summary>A rewind drops the checkpoints saved at a later point: a rewind to one of them throws
    /// <see cref="InvalidOperationException"/> around the runtime's NOT_FOUND, changes nothing, and the next decode
    /// still equals the first decode from the rewound point. A checkpoint saved at that point itself is not
    /// dropped: a rewind to it after that decode gives the same decode again.</summary>
    [SkippableFact]
    public async Task RewindTo_ADroppedCheckpoint_IsRefused_AndChangesNothing()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, prompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionCheckpointHelpers.Prefill(session, prompt);
        LiteRtSessionCheckpoint early = await SessionCheckpointHelpers.Save(session);
        string first = await SessionDecodeHelpers.DecodeWithin(session);
        await SessionCheckpointHelpers.Prefill(session, SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.SecondUser));
        LiteRtSessionCheckpoint late = await SessionCheckpointHelpers.Save(session);

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
        Assert.Equal(first, await SessionDecodeHelpers.DecodeWithin(session));
    }

    /// <summary>A rewind and a reset on a Fresh session are accepted and leave it Fresh: the start-token rule of
    /// a first prefill applies, and the next turn equals a new session's.</summary>
    [SkippableFact]
    public async Task RewindToAndReset_OnAFreshSession_LeaveItFresh()
    {
        LiteRtEngine engine = Engine;
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, resetPrompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        LiteRtSessionCheckpoint fresh = await SessionCheckpointHelpers.Save(session);
        await SessionDecodeHelpers.Within(() => session.RewindTo(fresh));
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionDecodeHelpers.Within(session.Reset);
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionCheckpointHelpers.Prefill(session, resetPrompt);
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
    }

    /// <summary>A reset after one turn and after three returns the session to Fresh: the start-token rule of a
    /// first prefill applies again, and the next turn equals a new session's. The last turn before each reset is
    /// the French turn, so a reset that does nothing gives a French reply, which a new session that runs the same
    /// turns checks before the reset.</summary>
    [SkippableFact]
    public async Task Reset_AfterOneTurnAndAfterSeveral_GivesAFreshSession()
    {
        LiteRtEngine engine = Engine;
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, resetPrompt);
        string[] oneTurn = [SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FrenchUser)];
        string[] threeTurns =
        [
            SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser),
            SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.SecondUser),
            SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.FrenchUser),
        ];

        foreach (string[] texts in new[] { oneTurn, threeTurns })
        {
            string[] withoutTheReset = await SessionCheckpointHelpers.Replies(engine, [.. texts, resetPrompt]);
            SessionCheckpointHelpers.AssertTheCallChangesTheReply(
                twin, withoutTheReset[^1], $"the reset after {texts.Length} turn(s)", output);

            using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
            foreach (string text in texts)
            {
                await SessionCheckpointHelpers.Prefill(session, text);
                await SessionDecodeHelpers.DecodeWithin(session);
            }
            await SessionDecodeHelpers.Within(session.Reset);
            SessionCheckpointHelpers.AssertFirstPrefillRule(session);
            await SessionCheckpointHelpers.Prefill(session, resetPrompt);
            string reply = await SessionDecodeHelpers.DecodeWithin(session);
            output.WriteLine($"after {texts.Length} turn(s): {reply}");
            Assert.True(twin == reply, $"After {texts.Length} turn(s) and a reset, expected \"{twin}\", got \"{reply}\".");
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
        LiteRtSessionCheckpoint fresh = await SessionCheckpointHelpers.Save(session);
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        await SessionCheckpointHelpers.Prefill(session, prompt);
        LiteRtSessionCheckpoint prefilled = await SessionCheckpointHelpers.Save(session);
        await SessionDecodeHelpers.DecodeWithin(session);
        await SessionDecodeHelpers.Within(session.Reset);
        Assert.Equal(LiteRtSessionState.Fresh, session.State);

        await SessionCheckpointHelpers.Prefill(session, prompt);
        await SessionCheckpointHelpers.AssertDropped(session, prefilled);
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        await SessionDecodeHelpers.Within(() => session.RewindTo(fresh));
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionCheckpointHelpers.Prefill(session, resetPrompt);
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
    }

    /// <summary>A save after a reset gets a label that no earlier save had. The runtime accepts a second save under
    /// a label it holds and then rewinds that label to the later save, so a repeated label would move the
    /// checkpoint saved on the Fresh session to the later point. The turn after the reset equals a new
    /// session's. The label assert catches a repeated label. The reset runs from Prefilled: the session holds the
    /// French turn, prefilled and not decoded, so a reset that does nothing gives a French reply. After the rewind
    /// to the Fresh checkpoint the turn asks for the previous question, so a rewind that does nothing gives a reply
    /// about the first prompt. New sessions that run the same texts with the reset or the rewind left out check
    /// both differences before the session runs.</summary>
    [SkippableFact]
    public async Task SaveCheckpoint_AfterAReset_NeverReusesALabel()
    {
        LiteRtEngine engine = Engine;
        string french = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FrenchUser);
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string previous = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.PreviousUser);
        // The texts after the reset with the rewind left out: the first reply is the reference after the reset,
        // and the second is the reply that a rewind that does nothing would give.
        string[] withoutTheRewind = await SessionCheckpointHelpers.Replies(engine, prompt, previous);
        string promptTwin = withoutTheRewind[0];
        string twin = await SessionCheckpointHelpers.Twin(engine, previous);
        SessionCheckpointHelpers.AssertTheCallChangesTheReply(twin, withoutTheRewind[1], "the rewind", output);
        string withoutTheReset;
        using (LiteRtSession control = SessionCheckpointHelpers.Session(engine))
        {
            await SessionCheckpointHelpers.Prefill(control, french);
            await SessionCheckpointHelpers.Prefill(control, prompt);
            withoutTheReset = await SessionDecodeHelpers.DecodeWithin(control);
        }
        SessionCheckpointHelpers.AssertTheCallChangesTheReply(promptTwin, withoutTheReset, "the reset", output);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        LiteRtSessionCheckpoint fresh = await SessionCheckpointHelpers.Save(session);
        await SessionCheckpointHelpers.Prefill(session, french);
        LiteRtSessionCheckpoint beforeTheReset = await SessionCheckpointHelpers.Save(session);
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        await SessionDecodeHelpers.Within(session.Reset);
        await SessionCheckpointHelpers.Prefill(session, prompt);
        LiteRtSessionCheckpoint afterTheReset = await SessionCheckpointHelpers.Save(session);
        string afterReset = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"after the reset: {afterReset}");
        Assert.Equal(promptTwin, afterReset);

        Assert.Equal(3, new[] { fresh, beforeTheReset, afterTheReset }.Select(c => Convert.ToHexString(c.Label)).Distinct().Count());
        await SessionDecodeHelpers.Within(() => session.RewindTo(fresh));
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionCheckpointHelpers.Prefill(session, previous);
        string afterRewind = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"after the rewind to the Fresh checkpoint: {afterRewind}");
        Assert.Equal(twin, afterRewind);
    }

    /// <summary>A full context recovers by a rewind: save after the first turn's prefill, decode, prefill raw text
    /// that leaves the end of the context to the next decode, decode until the context is full, rewind. The
    /// decode after the rewind equals the session's first decode and a new session's. A checkpoint dropped by an
    /// earlier rewind is refused from ContextFull too, and changes nothing. A filler prefill that fails, or a
    /// decode that does not fill the context, fails the test as an unmet precondition. The session has no output
    /// cap, so that the decode can fill the context; the first turn is the reset prompt, whose reply is short.</summary>
    [SkippableFact]
    public async Task ContextFull_RewindTo_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.TwinWith(0, engine, prompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine, 0);
        await SessionCheckpointHelpers.Prefill(session, prompt);
        LiteRtSessionCheckpoint checkpoint = await SessionCheckpointHelpers.Save(session);
        string first = await SessionDecodeHelpers.DecodeWithin(session);
        // Saved at a later point than the checkpoint, so the rewind below drops it.
        LiteRtSessionCheckpoint dropped = await SessionCheckpointHelpers.Save(session);
        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        Assert.Equal(first, await SessionDecodeHelpers.DecodeWithin(session));

        // The start position, the prompt, the reply and the position that ends the model's turn.
        int used = 1 + engine.Tokenize(prompt + first).Length + 1;
        string filler = SessionCheckpointHelpers.FillerFor(engine, used);
        int fillerTokens = engine.Tokenize(filler).Length;
        Exception? fillerError = await Record.ExceptionAsync(() => SessionCheckpointHelpers.Prefill(session, filler));
        Assert.True(fillerError is null,
            $"Precondition not met, so the recovery did not run: the filler prefill of {fillerTokens} tokens with about " +
            $"{SessionEngineFixture.Context - used} positions left was expected to succeed, and it threw " +
            $"{fillerError?.GetType().Name}: {fillerError?.Message}. Its size assumes that the runtime rounds a later " +
            "prefill up to a multiple of 128 positions.");
        string partial = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine($"{used} positions before the filler, {fillerTokens} filler tokens, " +
                         $"{engine.Tokenize(partial).Length} reply tokens, state {session.State}");
        SessionCheckpointHelpers.AssertContextFilled(session, partial);
        await SessionDecodeHelpers.AssertRefusesAllButRewindResetAndDispose(session, LiteRtSessionState.ContextFull);

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
    /// context-full test, reset, and the next turn equals a new session's; the start-token rule applies again. A
    /// decode that does not fill the context fails the test as an unmet precondition.</summary>
    [SkippableFact]
    public async Task ContextFull_Reset_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.TwinWith(0, engine, resetPrompt);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine, 0);
        // A fresh session holds only its start position.
        await SessionCheckpointHelpers.Prefill(session, SessionContextFill.FirstPrefill(engine));
        string partial = await SessionDecodeHelpers.DecodeWithin(session);
        SessionCheckpointHelpers.AssertContextFilled(session, partial);
        await SessionDecodeHelpers.AssertRefusesAllButRewindResetAndDispose(session, LiteRtSessionState.ContextFull);

        await SessionDecodeHelpers.Within(session.Reset);
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionCheckpointHelpers.Prefill(session, resetPrompt);
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
        await SessionCheckpointHelpers.Prefill(session, prompt);
        LiteRtSessionCheckpoint checkpoint = await SessionCheckpointHelpers.Save(session);
        var ex = await SessionCheckpointHelpers.FailPrefill(session, tooLong, "a second prefill of 1,500 tokens", output);
        // The status was measured on win-x64; elsewhere the test needs only the failed prefill.
        if (SessionDecodeHelpers.SameBytesMeasured)
        {
            Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
            Assert.Contains("Input token ids are too long", ex.Message, StringComparison.Ordinal);
        }
        await SessionDecodeHelpers.AssertRefusesAllButRewindResetAndDispose(session, LiteRtSessionState.Faulted);

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
        await SessionCheckpointHelpers.Prefill(session, prompt);
        var ex = await SessionCheckpointHelpers.FailPrefill(session, tooLong, "a second prefill of 1,500 tokens", output);
        if (SessionDecodeHelpers.SameBytesMeasured)
            Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        await SessionDecodeHelpers.AssertRefusesAllButRewindResetAndDispose(session, LiteRtSessionState.Faulted);

        await SessionDecodeHelpers.Within(session.Reset);
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionCheckpointHelpers.Prefill(session, resetPrompt);
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
        await SessionCheckpointHelpers.Prefill(session, SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt));
        LiteRtSessionCheckpoint checkpoint = await SessionCheckpointHelpers.Save(session);
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
    /// itself; the stream then ends normally, and a rewind to the checkpoint saved before it gives the same reply
    /// again. The count prompt keeps the decode running past the first piece; a decode that still ended before
    /// the three calls skips the test.</summary>
    [SkippableFact]
    public async Task CheckpointMembers_DuringARunningStream_AreRefusedAsBusy()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionDecodeHelpers.Session(engine, SessionDecodeHelpers.CancelCap);
        await SessionCheckpointHelpers.Prefill(session, SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt));
        LiteRtSessionCheckpoint checkpoint = await SessionCheckpointHelpers.Save(session);
        long saves = session.CheckpointCount;
        var outcomes = new List<Exception?>();
        bool busyAfterTheCalls = false;
        List<string> pieces = await SessionDecodeHelpers.StreamWithin(session, onPiece: count =>
        {
            if (count == 1)
            {
                foreach (Action call in new Action[] { () => session.SaveCheckpoint(), () => session.RewindTo(checkpoint), session.Reset })
                    outcomes.Add(Record.Exception(call));
                // The gate is held from the decode's start to its settlement, so a gate still held after the
                // calls was held during them.
                busyAfterTheCalls = session.IsCallInProgress;
            }
            return true;
        });
        Assert.NotEmpty(pieces);
        Assert.False(session.IsCallInProgress);
        Skip.If(!busyAfterTheCalls, $"The decode ended before the three calls were made ({pieces.Count} pieces).");
        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, error =>
            Assert.Contains("in progress", Assert.IsType<InvalidOperationException>(error).Message, StringComparison.Ordinal));
        Assert.Equal(saves, session.CheckpointCount);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);

        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        Assert.Equal(string.Concat(pieces), await SessionDecodeHelpers.DecodeWithin(session));
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
/// Recovery from a failed prefill, in a class of their own, with an engine apart from the other session tests: a
/// decode that never ends would hold its session, and with it the engine, until the process ends. The cases are a
/// failed first prefill, the two other failure statuses of a prefill that does not fit, a rewind to a checkpoint
/// saved after a decode, and a rewind to a dropped checkpoint before the recovery. The recovered decode equals a
/// new session's.
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

    /// <summary>Fails a prefill of <paramref name="text"/> as the precondition of the test. The status and the
    /// hint are checked only where they were measured (win-x64); elsewhere the test needs only a failed prefill,
    /// whose status it logs.</summary>
    private async Task<LiteRtException> FailPrefill(LiteRtSession session, string text, string what, LiteRtStatusCode measured)
    {
        LiteRtException ex = await SessionCheckpointHelpers.FailPrefill(session, text, what, output);
        if (SessionDecodeHelpers.SameBytesMeasured)
        {
            Assert.Equal(measured, ex.StatusCode);
            Assert.Contains(LiteRtSession.DoesNotFitHint, ex.Message, StringComparison.Ordinal);
        }
        await SessionDecodeHelpers.AssertRefusesAllButRewindResetAndDispose(session, LiteRtSessionState.Faulted);
        return ex;
    }

    /// <summary>Prefills the reset prompt on a recovered Fresh session and decodes; the reply must equal a new
    /// session's.</summary>
    private async Task AssertTurnEqualsANewSession(LiteRtEngine engine, LiteRtSession session)
    {
        string resetPrompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.ResetUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, resetPrompt);
        SessionCheckpointHelpers.AssertFirstPrefillRule(session);
        await SessionCheckpointHelpers.Prefill(session, resetPrompt);
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
    }

    /// <summary>Decodes on a session that a rewind returned to the point after <paramref name="first"/>'s prefill;
    /// the reply must equal a new session's that prefilled <paramref name="first"/>.</summary>
    private async Task AssertDecodeEqualsANewSession(LiteRtEngine engine, LiteRtSession session, string first)
    {
        string twin = await SessionCheckpointHelpers.Twin(engine, first);
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
    }

    /// <summary>Texts of 600 and about 400 tokens: the second fits the context left by the first by its count,
    /// but not after the runtime rounds it up to a multiple of 128 positions, which the runtime refuses with
    /// FAILED_PRECONDITION. The rounding is inferred from the thresholds measured on win-x64.</summary>
    private (string First, string Second, string What) FailedPreconditionTexts(LiteRtEngine engine)
    {
        string first = SessionModelHelpers.TextOf(engine, 600, atLeast: false);
        string second = SessionModelHelpers.TextOf(engine, 400, atLeast: true);
        int left = SessionEngineFixture.Context - 1 - engine.Tokenize(first).Length;
        int count = engine.Tokenize(second).Length;
        string what = $"a second prefill of {count} tokens with {left} positions left (rounded up to " +
                      $"{(count + 127) / 128 * 128})";
        output.WriteLine(what);
        // The test's own arithmetic: the input fits by its count and not after the rounding.
        Assert.True(count < left && (count + 127) / 128 * 128 > left,
            $"Precondition not met, so the recovery did not run: {count} tokens with {left} positions left.");
        return (first, second, what);
    }

    /// <summary>A first prefill longer than the context, then a reset.</summary>
    [SkippableFact]
    public async Task FailedFirstPrefill_Reset_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await FailPrefill(session, SessionModelHelpers.TextOf(engine, SessionEngineFixture.Context + 100, atLeast: true),
            $"a first prefill of {SessionEngineFixture.Context + 100} tokens", LiteRtStatusCode.InvalidArgument);
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
        LiteRtSessionCheckpoint fresh = await SessionCheckpointHelpers.Save(session);
        await FailPrefill(session, SessionModelHelpers.TextOf(engine, SessionEngineFixture.Context + 100, atLeast: true),
            $"a first prefill of {SessionEngineFixture.Context + 100} tokens", LiteRtStatusCode.InvalidArgument);
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
        await SessionCheckpointHelpers.Prefill(session, SessionModelHelpers.TextOf(engine, 600, atLeast: false));
        await FailPrefill(session, SessionModelHelpers.TextOf(engine, 600, atLeast: true),
            "a second prefill of 600 tokens after 600", LiteRtStatusCode.ResourceExhausted);
        await SessionDecodeHelpers.Within(session.Reset);
        await AssertTurnEqualsANewSession(engine, session);
    }

    /// <summary>The RESOURCE_EXHAUSTED failure, then a rewind to the checkpoint saved after the first prefill:
    /// the decode equals a new session's.</summary>
    [SkippableFact]
    public async Task PrefillResourceExhausted_RewindTo_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        string first = SessionModelHelpers.TextOf(engine, 600, atLeast: false);
        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionCheckpointHelpers.Prefill(session, first);
        LiteRtSessionCheckpoint checkpoint = await SessionCheckpointHelpers.Save(session);
        await FailPrefill(session, SessionModelHelpers.TextOf(engine, 600, atLeast: true),
            "a second prefill of 600 tokens after 600", LiteRtStatusCode.ResourceExhausted);
        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        await AssertDecodeEqualsANewSession(engine, session, first);
    }

    /// <summary>A second prefill that fits the context by its count but not after the runtime rounds it up to a
    /// multiple of 128, which the runtime refuses with FAILED_PRECONDITION, then a reset.</summary>
    [SkippableFact]
    public async Task PrefillFailedPrecondition_Reset_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        (string first, string second, string what) = FailedPreconditionTexts(engine);
        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionCheckpointHelpers.Prefill(session, first);
        await FailPrefill(session, second, what, LiteRtStatusCode.FailedPrecondition);
        await SessionDecodeHelpers.Within(session.Reset);
        await AssertTurnEqualsANewSession(engine, session);
    }

    /// <summary>The FAILED_PRECONDITION failure, then a rewind to the checkpoint saved after the first prefill:
    /// the decode equals a new session's.</summary>
    [SkippableFact]
    public async Task PrefillFailedPrecondition_RewindTo_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        (string first, string second, string what) = FailedPreconditionTexts(engine);
        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionCheckpointHelpers.Prefill(session, first);
        LiteRtSessionCheckpoint checkpoint = await SessionCheckpointHelpers.Save(session);
        await FailPrefill(session, second, what, LiteRtStatusCode.FailedPrecondition);
        await SessionDecodeHelpers.Within(() => session.RewindTo(checkpoint));
        await AssertDecodeEqualsANewSession(engine, session, first);
    }

    /// <summary>A prefill that fails after a decode, then a rewind to the checkpoint saved after that decode: the
    /// session is Decoded again, and the next turn equals a new session's.</summary>
    [SkippableFact]
    public async Task FailedPrefill_RewindToACheckpointAfterADecode_RecoversTheSession()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);
        string second = SessionDecodeHelpers.NextTurn(SessionCheckpointHelpers.SecondUser);
        string twin = await SessionCheckpointHelpers.Twin(engine, prompt, second);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionCheckpointHelpers.Prefill(session, prompt);
        await SessionDecodeHelpers.DecodeWithin(session);
        LiteRtSessionCheckpoint decoded = await SessionCheckpointHelpers.Save(session);
        await FailPrefill(session, SessionModelHelpers.TextOf(engine, 1500, atLeast: true),
            "a prefill of 1,500 tokens after a decode", LiteRtStatusCode.InvalidArgument);
        await SessionDecodeHelpers.Within(() => session.RewindTo(decoded));
        Assert.Equal(LiteRtSessionState.Decoded, session.State);
        await SessionCheckpointHelpers.Prefill(session, second);
        string reply = await SessionDecodeHelpers.DecodeWithin(session);
        output.WriteLine(reply);
        Assert.Equal(twin, reply);
    }

    /// <summary>On a session that a failed prefill left Faulted, a rewind to a checkpoint an earlier rewind
    /// dropped throws <see cref="InvalidOperationException"/> around the runtime's NOT_FOUND and leaves the
    /// session Faulted; a rewind to a checkpoint the session still holds then recovers it, and the decode equals
    /// a new session's.</summary>
    [SkippableFact]
    public async Task FailedPrefill_RewindToADroppedCheckpoint_IsRefused_AndARewindStillRecovers()
    {
        LiteRtEngine engine = Engine;
        string prompt = SessionDecodeHelpers.Prompt(engine, SessionCheckpointHelpers.FirstUser);

        using LiteRtSession session = SessionCheckpointHelpers.Session(engine);
        await SessionCheckpointHelpers.Prefill(session, prompt);
        LiteRtSessionCheckpoint kept = await SessionCheckpointHelpers.Save(session);
        await SessionDecodeHelpers.DecodeWithin(session);
        LiteRtSessionCheckpoint dropped = await SessionCheckpointHelpers.Save(session);
        await SessionDecodeHelpers.Within(() => session.RewindTo(kept));
        await FailPrefill(session, SessionModelHelpers.TextOf(engine, 1500, atLeast: true),
            "a second prefill of 1,500 tokens", LiteRtStatusCode.InvalidArgument);

        await SessionCheckpointHelpers.AssertDropped(session, dropped);
        await SessionDecodeHelpers.AssertRefusesAllButRewindResetAndDispose(session, LiteRtSessionState.Faulted);
        await SessionDecodeHelpers.Within(() => session.RewindTo(kept));
        await AssertDecodeEqualsANewSession(engine, session, prompt);
    }
}
