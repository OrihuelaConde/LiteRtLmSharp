using System.Reflection;
using LiteRtLmSharp.Native;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The session core without a model: the options, the start-token rule, the prefill hint, the one-call-at-a-time
/// gate and the exception precedence. The session-level tests use a session around a null native handle: every
/// check before the native call runs as it does on a real session, and the native prefill fails with
/// INVALID_ARGUMENT, which exercises the Faulted latch.
/// </summary>
public sealed class SessionCoreTests
{
    static SessionCoreTests() => LiteRtEngine.SetMinLogLevel(3); // registers the resolver

    private const string Bos = "<bos>";

    /// <summary>A session whose native handle is null: SafeHandle never releases an invalid handle, so nothing
    /// native is deleted, and native calls on it fail with INVALID_ARGUMENT.</summary>
    private static LiteRtSession NullSession(string? startTokenText = Bos)
        => new(new SessionHandle(nint.Zero, new EngineHandle(nint.Zero, allowMultiple: false)), startTokenText);

    // --- Options --------------------------------------------------------------

    [Fact]
    public void Options_Defaults_SendNoSamplerAndNoCap()
    {
        var options = new LiteRtSessionOptions();
        Assert.Null(options.Sampler);
        Assert.Equal(0, options.MaxOutputTokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Options_MaxOutputTokens_AcceptsZeroAndPositive(int value)
        => Assert.Equal(value, new LiteRtSessionOptions { MaxOutputTokens = value }.MaxOutputTokens);

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Options_MaxOutputTokens_RejectsNegative(int value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtSessionOptions { MaxOutputTokens = value });

    [Fact]
    public void Options_AreAValueRecord()
    {
        var sampler = new LiteRtSamplerParams { TopK = 1 };
        Assert.Equal(new LiteRtSessionOptions { Sampler = sampler, MaxOutputTokens = 8 },
            new LiteRtSessionOptions { Sampler = sampler with { }, MaxOutputTokens = 8 });
        Assert.NotEqual(new LiteRtSessionOptions { MaxOutputTokens = 8 }, new LiteRtSessionOptions { MaxOutputTokens = 9 });
    }

    // --- Start-token rule -----------------------------------------------------

    private static LiteRtTokenUnion Ids(params int[] ids) => new(LiteRtTokenKind.Ids, text: null, ids);
    private static LiteRtTokenUnion Literal(string text) => new(LiteRtTokenKind.String, text, []);

    /// <summary>A tokenizer where <c>&lt;s&gt;</c> is token 1 and every other text is one id per character, so
    /// <c>&lt;bos&gt;</c> tokenizes into several ids, as Gemma 4's does.</summary>
    private static int[] Tokenize(string text) => text == "<s>" ? [1] : text.Select(c => 1000 + c).ToArray();

    private static string Detokenize(int[] ids) => string.Concat(ids.Select(id => id switch
    {
        1 => "<s>",
        2 => Bos,
        3 => "",
        >= 1000 => ((char)(id - 1000)).ToString(),
        _ => throw new LiteRtException("out of range", LiteRtStatusCode.InvalidArgument),
    }));

    [Fact]
    public void StartTokenText_OneId_IsItsDetokenizedText_EvenWhenItDoesNotTokenizeBack()
    {
        Assert.NotEqual(new[] { 2 }, Tokenize(Bos));
        Assert.Equal(Bos, LiteRtSession.StartTokenText(Ids(2), Tokenize, Detokenize));
        Assert.Equal("<s>", LiteRtSession.StartTokenText(Ids(1), Tokenize, Detokenize));
    }

    [Fact]
    public void StartTokenText_HasNoRule_ForSeveralIds_NoIds_OrAnEmptyText()
    {
        Assert.Null(LiteRtSession.StartTokenText(Ids(1, 2), Tokenize, Detokenize));
        Assert.Null(LiteRtSession.StartTokenText(Ids(), Tokenize, Detokenize));
        Assert.Null(LiteRtSession.StartTokenText(Ids(3), Tokenize, Detokenize));
    }

    [Fact]
    public void StartTokenText_String_NeedsOneTokenThatRoundTrips()
    {
        Assert.Equal("<s>", LiteRtSession.StartTokenText(Literal("<s>"), Tokenize, Detokenize));
        Assert.Null(LiteRtSession.StartTokenText(Literal(Bos), Tokenize, Detokenize));
        Assert.Null(LiteRtSession.StartTokenText(Literal(""), Tokenize, Detokenize));
    }

    [Fact]
    public void StartTokenText_HasNoRule_WithoutAStartToken_OrWhenTheTokenizerFails()
    {
        Assert.Null(LiteRtSession.StartTokenText(null, Tokenize, Detokenize));
        Assert.Null(LiteRtSession.StartTokenText(Ids(7), Tokenize, Detokenize));
        Assert.Null(LiteRtSession.StartTokenText(Literal("<s>"), _ => throw new LiteRtException("tokenizer"), Detokenize));
    }

    [Fact]
    public void RefusesStartToken_OnlyOnAFreshSession_AndOnlyAtTheHead()
    {
        Assert.True(LiteRtSession.RefusesStartToken(LiteRtSessionState.Fresh, "<bos>Hello", Bos));
        Assert.True(LiteRtSession.RefusesStartToken(LiteRtSessionState.Fresh, Bos, Bos));
        Assert.False(LiteRtSession.RefusesStartToken(LiteRtSessionState.Fresh, "Hello <bos>", Bos));
        Assert.False(LiteRtSession.RefusesStartToken(LiteRtSessionState.Fresh, "<BOS>Hello", Bos));
        Assert.False(LiteRtSession.RefusesStartToken(LiteRtSessionState.Fresh, "<bos>Hello", null));
        Assert.False(LiteRtSession.RefusesStartToken(LiteRtSessionState.Fresh, "<bos>Hello", ""));
        foreach (LiteRtSessionState state in Enum.GetValues<LiteRtSessionState>().Where(s => s != LiteRtSessionState.Fresh))
            Assert.False(LiteRtSession.RefusesStartToken(state, "<bos>Hello", Bos));
    }

    // --- Prefill hint ---------------------------------------------------------

    public static TheoryData<LiteRtStatusCode, string?, bool> PrefillFailures() => new()
    {
        // The four measured forms, as the thread's message gives them and with the status name stripped.
        { LiteRtStatusCode.InvalidArgument, "INVALID_ARGUMENT: Input token ids are too long. Exceeding the maximum number of tokens allowed: 1024 >= 1024", true },
        { LiteRtStatusCode.InvalidArgument, "Input token ids are too long. Exceeding the maximum number of tokens allowed: 1024 >= 1024", true },
        { LiteRtStatusCode.ResourceExhausted, "RESOURCE_EXHAUSTED: Not enough space left in the context window for the input: 601 tokens in the context + 423 input tokens >= 1024 max number of tokens.", true },
        { LiteRtStatusCode.FailedPrecondition, "FAILED_PRECONDITION: Prefill input length exceeds available state entries (remaining capacity: 423).", true },
        { LiteRtStatusCode.FailedPrecondition, "Prefill input length exceeds available state entries (remaining capacity: 423).", true },
        { LiteRtStatusCode.FailedPrecondition, "FAILED_PRECONDITION: Chosen prefill work group size exceeds available state entries (123).", true },
        // By code alone for RESOURCE_EXHAUSTED, also when the runtime gave no reason.
        { LiteRtStatusCode.ResourceExhausted, null, true },
        { LiteRtStatusCode.ResourceExhausted, "anything", true },
        // The other two codes need the measured reason.
        { LiteRtStatusCode.InvalidArgument, null, false },
        { LiteRtStatusCode.InvalidArgument, "Invalid session or inputs.", false },
        { LiteRtStatusCode.FailedPrecondition, null, false },
        { LiteRtStatusCode.FailedPrecondition, "FAILED_PRECONDITION: Something else.", false },
        { LiteRtStatusCode.Internal, "Input token ids are too long.", false },
        { LiteRtStatusCode.Unknown, "Prefill input length exceeds available state entries (remaining capacity: 1).", false },
    };

    [Theory]
    [MemberData(nameof(PrefillFailures))]
    public void PrefillFailureHint_FollowsTheMeasuredForms(LiteRtStatusCode status, string? message, bool hinted)
        => Assert.Equal(hinted ? LiteRtSession.DoesNotFitHint : null, LiteRtSession.PrefillFailureHint(status, message));

    // --- Gate and precedence --------------------------------------------------

    [Fact]
    public async Task Prefill_WhileAnotherCallHoldsTheGate_IsRefusedAsBusy()
    {
        using LiteRtSession session = NullSession();
        session.EnterCall();
        try
        {
            Assert.True(session.IsCallInProgress);
            var ex = Assert.Throws<InvalidOperationException>(() => session.Prefill("Hello"));
            Assert.Contains("in progress", ex.Message, StringComparison.Ordinal);
            var asyncEx = await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrefillAsync("Hello"));
            Assert.Contains("in progress", asyncEx.Message, StringComparison.Ordinal);
            Assert.Equal(LiteRtSessionState.Fresh, session.State);
            // A refused call leaves the gate to its holder.
            Assert.True(session.IsCallInProgress);
        }
        finally
        {
            session.ExitCall();
        }
        Assert.False(session.IsCallInProgress);
    }

    [Fact]
    public void Prefill_ReportsArgumentsBeforeABusySession()
    {
        using LiteRtSession session = NullSession();
        session.EnterCall();
        try
        {
            Assert.Throws<ArgumentNullException>(() => session.Prefill(null!));
            Assert.Throws<ArgumentException>(() => session.Prefill(""));
            var ex = Assert.Throws<ArgumentException>(() => session.Prefill("<bos>Hello"));
            Assert.Contains("adds the start token itself", ex.Message, StringComparison.Ordinal);
            Assert.Equal("text", ex.ParamName);
        }
        finally
        {
            session.ExitCall();
        }
    }

    [Fact]
    public async Task Prefill_ReportsDisposalFirst()
    {
        LiteRtSession session = NullSession();
        session.Dispose();
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Prefill(null!));
        Assert.Throws<ObjectDisposedException>(() => session.Prefill("<bos>Hello"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.PrefillAsync("Hello"));
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        Assert.True(session.IsDisposed);
    }

    /// <summary>A failed native prefill latches Faulted and throws the runtime's status; the next prefill is
    /// refused by the state before any native call, and the gate is free again after each call.</summary>
    [Fact]
    public void Prefill_ThatFailsNatively_LatchesFaulted()
    {
        using LiteRtSession session = NullSession();
        var ex = Assert.Throws<LiteRtException>(() => session.Prefill("Hello"));
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("litert_lm_session_run_prefill failed", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LiteRtSession.DoesNotFitHint, ex.Message, StringComparison.Ordinal);
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
        Assert.False(session.IsCallInProgress);

        var refused = Assert.Throws<InvalidOperationException>(() => session.Prefill("Hello"));
        Assert.Contains("A prefill on this session failed", refused.Message, StringComparison.Ordinal);
        // The start-token rule applies only on a Fresh session, so a Faulted session reports its state.
        Assert.Throws<InvalidOperationException>(() => session.Prefill("<bos>Hello"));
        Assert.False(session.IsCallInProgress);

        // Busy comes before the state refusal.
        session.EnterCall();
        try
        {
            var busy = Assert.Throws<InvalidOperationException>(() => session.Prefill("Hello"));
            Assert.Contains("in progress", busy.Message, StringComparison.Ordinal);
        }
        finally
        {
            session.ExitCall();
        }
    }

    [Fact]
    public async Task PrefillAsync_ThatFailsNatively_LatchesFaulted()
    {
        using LiteRtSession session = NullSession();
        var ex = await Assert.ThrowsAsync<LiteRtException>(() => session.PrefillAsync("Hello"));
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
        Assert.False(session.IsCallInProgress);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrefillAsync("Hello"));
    }

    [Fact]
    public async Task PrefillAsync_WithACancelledToken_ChangesNothing()
    {
        using LiteRtSession session = NullSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrefillAsync("Hello", cts.Token));
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        Assert.False(session.IsCallInProgress);
    }

    [Fact]
    public void NoStartTokenText_LetsAnyTextThrough()
    {
        using LiteRtSession session = NullSession(startTokenText: null);
        // Reaches the native call (which fails on the null handle) instead of the start-token refusal.
        Assert.Throws<LiteRtException>(() => session.Prefill("<bos>Hello"));
    }

    /// <summary>The start-token check runs again with the gate held. A reset or a rewind that ends between a
    /// prefill's argument check and its gate can make the session Fresh after the argument check saw another
    /// state; the prefill is then refused before any native call (on the null handle a native call would throw
    /// <see cref="LiteRtException"/> and latch Faulted). The test makes the session Fresh as such a reset would,
    /// and calls the gated part of the prefill directly: no public call can force that interleaving.</summary>
    [Fact]
    public void Prefill_ChecksTheStartTokenAgainWithTheGateHeld()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Decoded);
        session.EnterCall();
        session.Machine.Complete(SessionCall.Reset, SessionCallOutcome.Succeeded);
        session.ExitCall();
        Assert.Equal(LiteRtSessionState.Fresh, session.State);

        var ex = Assert.Throws<ArgumentException>(() => session.EnterPrefill("<bos>Hello"));
        Assert.Contains("adds the start token itself", ex.Message, StringComparison.Ordinal);
        Assert.Equal("text", ex.ParamName);
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        Assert.False(session.IsCallInProgress);

        // A text without the start token takes the gate, and the state accepts it.
        session.EnterPrefill("Hello");
        Assert.True(session.IsCallInProgress);
        session.ExitCall();
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
    }

    /// <summary>The gated part of a prefill reports a busy session and a refusing state, and releases nothing it
    /// did not take.</summary>
    [Fact]
    public void EnterPrefill_RefusesABusySessionAndARefusingState()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Cancelled);
        Assert.Contains("was cancelled", Assert.Throws<InvalidOperationException>(() => session.EnterPrefill("Hello")).Message,
            StringComparison.Ordinal);
        Assert.False(session.IsCallInProgress);
        session.EnterCall();
        Assert.Contains("in progress", Assert.Throws<InvalidOperationException>(() => session.EnterPrefill("Hello")).Message,
            StringComparison.Ordinal);
        // The busy refusal leaves the other call's gate held.
        Assert.True(session.IsCallInProgress);
        session.ExitCall();
    }

    // --- Checkpoints, rewind and reset ------------------------------------------

    private static LiteRtSession NullSession(SessionMachineState state)
        => new(new SessionHandle(nint.Zero, new EngineHandle(nint.Zero, allowMultiple: false)), Bos, state);

    /// <summary>A checkpoint of <paramref name="session"/> as a save would have made it; a save on a null
    /// handle fails, so the tests build it directly.</summary>
    private static LiteRtSessionCheckpoint CheckpointOf(LiteRtSession session, LiteRtSessionState recorded = LiteRtSessionState.Prefilled)
        => new(session, 0, recorded);

    /// <summary>Runs one of the three members, with a checkpoint of the session itself for RewindTo.</summary>
    private static void Run(LiteRtSession session, string member)
    {
        switch (member)
        {
            case nameof(LiteRtSession.SaveCheckpoint):
                session.SaveCheckpoint();
                break;
            case nameof(LiteRtSession.RewindTo):
                session.RewindTo(CheckpointOf(session));
                break;
            case nameof(LiteRtSession.Reset):
                session.Reset();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, null);
        }
    }

    public static TheoryData<string> CheckpointMembers() =>
        new(nameof(LiteRtSession.SaveCheckpoint), nameof(LiteRtSession.RewindTo), nameof(LiteRtSession.Reset));

    /// <summary>A failed prefill leaves a session that refuses a save, but accepts a rewind and a reset: on the
    /// null handle they reach the native call, which fails, and that failure latches a fault that nothing
    /// recovers.</summary>
    [Theory]
    [InlineData(nameof(LiteRtSession.RewindTo))]
    [InlineData(nameof(LiteRtSession.Reset))]
    public void AfterAFailedPrefill_RewindAndResetReachTheRuntime_AndTheirFailureAcceptsOnlyDispose(string member)
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Fresh);
        Assert.Throws<LiteRtException>(() => session.Prefill("Hello"));
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
        var save = Assert.Throws<InvalidOperationException>(() => session.SaveCheckpoint());
        Assert.Contains("A prefill on this session failed, so it does not accept SaveCheckpoint", save.Message, StringComparison.Ordinal);

        var ex = Assert.Throws<LiteRtException>(() => Run(session, member));
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        string function = member == nameof(LiteRtSession.Reset)
            ? "litert_lm_session_rewind_to_step failed"
            : "litert_lm_session_rewind_to_checkpoint failed";
        Assert.Contains(function, ex.Message, StringComparison.Ordinal);
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
        Assert.False(session.IsCallInProgress);

        foreach (string next in new[] { nameof(LiteRtSession.SaveCheckpoint), nameof(LiteRtSession.RewindTo), nameof(LiteRtSession.Reset) })
        {
            var refused = Assert.Throws<InvalidOperationException>(() => Run(session, next));
            Assert.Contains("A native call on this session failed", refused.Message, StringComparison.Ordinal);
            Assert.EndsWith("Dispose it and create a new session.", refused.Message, StringComparison.Ordinal);
        }
        Assert.Throws<InvalidOperationException>(() => session.Prefill("Hello"));
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
    }

    /// <summary>A save that fails natively latches a fault that nothing recovers, and the label number it took
    /// is not given out again.</summary>
    [Fact]
    public void SaveCheckpoint_ThatFailsNatively_LatchesFaulted()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        var ex = Assert.Throws<LiteRtException>(() => session.SaveCheckpoint());
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("litert_lm_session_save_checkpoint failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
        Assert.Equal(1, session.CheckpointCount);
        Assert.False(session.IsCallInProgress);
        foreach (string member in new[] { nameof(LiteRtSession.SaveCheckpoint), nameof(LiteRtSession.RewindTo), nameof(LiteRtSession.Reset) })
            Assert.Throws<InvalidOperationException>(() => Run(session, member));
    }

    /// <summary>A full context refuses a save and accepts a rewind and a reset, which reach the native call.</summary>
    [Fact]
    public void ContextFull_RefusesASave_AndAcceptsRewindAndReset()
    {
        using (LiteRtSession session = NullSession(LiteRtSessionState.ContextFull))
        {
            var save = Assert.Throws<InvalidOperationException>(() => session.SaveCheckpoint());
            Assert.Contains("context is full", save.Message, StringComparison.Ordinal);
            Assert.Equal(0, session.CheckpointCount);
            Assert.Equal(LiteRtSessionState.ContextFull, session.State);
            Assert.Throws<LiteRtException>(() => session.Reset());
        }
        using (LiteRtSession session = NullSession(LiteRtSessionState.ContextFull))
            Assert.Throws<LiteRtException>(() => session.RewindTo(CheckpointOf(session)));
    }

    /// <summary>Cancelled, and Faulted with no recorded cause, refuse the three members before any native
    /// call.</summary>
    [Theory]
    [InlineData(LiteRtSessionState.Cancelled, nameof(LiteRtSession.SaveCheckpoint), "was cancelled")]
    [InlineData(LiteRtSessionState.Cancelled, nameof(LiteRtSession.RewindTo), "was cancelled")]
    [InlineData(LiteRtSessionState.Cancelled, nameof(LiteRtSession.Reset), "was cancelled")]
    [InlineData(LiteRtSessionState.Faulted, nameof(LiteRtSession.SaveCheckpoint), "native call on this session failed")]
    [InlineData(LiteRtSessionState.Faulted, nameof(LiteRtSession.RewindTo), "native call on this session failed")]
    [InlineData(LiteRtSessionState.Faulted, nameof(LiteRtSession.Reset), "native call on this session failed")]
    public void EndStates_RefuseTheCheckpointMembers(LiteRtSessionState state, string member, string expected)
    {
        using LiteRtSession session = NullSession(state);
        var ex = Assert.Throws<InvalidOperationException>(() => Run(session, member));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains(member, ex.Message, StringComparison.Ordinal);
        Assert.Equal(state, session.State);
        Assert.False(session.IsCallInProgress);
        Assert.Equal(0, session.CheckpointCount);
    }

    [Theory]
    [MemberData(nameof(CheckpointMembers))]
    public void CheckpointMembers_WhileAnotherCallHoldsTheGate_AreRefusedAsBusy(string member)
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        session.EnterCall();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Run(session, member));
            Assert.Contains("in progress", ex.Message, StringComparison.Ordinal);
            Assert.True(session.IsCallInProgress);
            Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        }
        finally
        {
            session.ExitCall();
        }
    }

    /// <summary>Busy comes before the state refusal: a Cancelled session that is busy reports the busy call.</summary>
    [Theory]
    [MemberData(nameof(CheckpointMembers))]
    public void CheckpointMembers_ReportBusyBeforeTheState(string member)
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Cancelled);
        session.EnterCall();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Run(session, member));
            Assert.Contains("in progress", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            session.ExitCall();
        }
    }

    [Theory]
    [MemberData(nameof(CheckpointMembers))]
    public void CheckpointMembers_ReportDisposalFirst(string member)
    {
        LiteRtSession session = NullSession(LiteRtSessionState.Cancelled);
        LiteRtSessionCheckpoint own = CheckpointOf(session);
        session.Dispose();
        session.EnterCall();
        Assert.Throws<ObjectDisposedException>(() => Run(session, member));
        if (member == nameof(LiteRtSession.RewindTo))
        {
            Assert.Throws<ObjectDisposedException>(() => session.RewindTo(null!));
            using LiteRtSession other = NullSession(LiteRtSessionState.Prefilled);
            Assert.Throws<ObjectDisposedException>(() => session.RewindTo(CheckpointOf(other)));
            Assert.Throws<ObjectDisposedException>(() => session.RewindTo(own));
        }
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);
    }

    /// <summary>The argument checks of RewindTo come after disposal and before the busy and state checks: a
    /// null checkpoint, and a checkpoint saved on another session, which changes nothing on either
    /// session.</summary>
    [Fact]
    public void RewindTo_ChecksItsArgumentBeforeBusyAndState()
    {
        using LiteRtSession session = NullSession(LiteRtSessionState.Cancelled);
        using LiteRtSession other = NullSession(LiteRtSessionState.Prefilled);
        LiteRtSessionCheckpoint foreign = CheckpointOf(other);
        session.EnterCall();
        try
        {
            var nullEx = Assert.Throws<ArgumentNullException>(() => session.RewindTo(null!));
            Assert.Equal("checkpoint", nullEx.ParamName);
            var foreignEx = Assert.Throws<ArgumentException>(() => session.RewindTo(foreign));
            Assert.Equal("checkpoint", foreignEx.ParamName);
            Assert.Contains("saved on another session", foreignEx.Message, StringComparison.Ordinal);
        }
        finally
        {
            session.ExitCall();
        }
        // Free and in a state that accepts a rewind, the foreign checkpoint is still refused before any native call.
        Assert.Throws<ArgumentException>(() => other.RewindTo(CheckpointOf(session)));
        Assert.Equal(LiteRtSessionState.Prefilled, other.State);
        Assert.Equal(LiteRtSessionState.Cancelled, session.State);
        Assert.False(other.IsCallInProgress);
    }

    /// <summary>Labels are ASCII, NUL-terminated, carry the save's number and never repeat for different
    /// numbers.</summary>
    [Fact]
    public void CheckpointLabels_AreAsciiNulTerminated_AndDistinct()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (long sequence in new long[] { 0, 1, 2, 9, 10, 11, 100, long.MaxValue })
        {
            byte[] bytes = LiteRtSessionCheckpoint.LabelBytes(sequence);
            Assert.Equal(0, bytes[^1]);
            Assert.DoesNotContain((byte)0, bytes[..^1]);
            Assert.All(bytes, b => Assert.True(b < 0x80));
            string text = System.Text.Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
            Assert.Equal(LiteRtSessionCheckpoint.LabelText(sequence), text);
            Assert.StartsWith(LiteRtSessionCheckpoint.LabelPrefix, text, StringComparison.Ordinal);
            Assert.True(seen.Add(text));
        }
        using LiteRtSession session = NullSession(LiteRtSessionState.Prefilled);
        var checkpoint = new LiteRtSessionCheckpoint(session, 7, LiteRtSessionState.Decoded);
        Assert.Same(session, checkpoint.Owner);
        Assert.Equal(7, checkpoint.Sequence);
        Assert.Equal(LiteRtSessionState.Decoded, checkpoint.Recorded);
        Assert.Equal(LiteRtSessionCheckpoint.LabelBytes(7), checkpoint.Label);
    }

    /// <summary>The experimental members and the checkpoint type carry the diagnostic id and the guide link, and
    /// nothing else in the session's surface does. The checkpoint has no public constructor and no public
    /// member of its own.</summary>
    [Fact]
    public void ExperimentalMarking_IsOnTheCheckpointMembersAndType()
    {
        const string Url = "https://orihuelaconde.github.io/LiteRtLmSharp/sessions.html#experimental-checkpoints-rewind-and-reset";
        static void AssertMarked(MemberInfo member)
        {
            var attribute = member.GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal("LITERTLM001", attribute.DiagnosticId);
            Assert.Equal(Url, attribute.UrlFormat);
        }

        string[] marked = [nameof(LiteRtSession.SaveCheckpoint), nameof(LiteRtSession.RewindTo), nameof(LiteRtSession.Reset)];
        foreach (MemberInfo member in typeof(LiteRtSession).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (marked.Contains(member.Name))
                AssertMarked(member);
            else
                Assert.Null(member.GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>());
        }
        Assert.Null(typeof(LiteRtSession).GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>());
        AssertMarked(typeof(LiteRtSessionCheckpoint));
        Assert.Empty(typeof(LiteRtSessionCheckpoint).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(LiteRtSessionCheckpoint).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.True(typeof(LiteRtSessionCheckpoint).IsSealed);
    }

    // --- Create -----------------------------------------------------------------

    /// <summary>A session created on an engine whose handle is already disposed fails with
    /// <see cref="ObjectDisposedException"/> before any native call on the engine: the start token is not read,
    /// because a disposed handle can point to a native engine that was deleted. The engine wraps a null native
    /// handle, which SafeHandle never releases.</summary>
    [Fact]
    public void Create_OnADisposedEngineHandle_ReadsNothingFromTheEngine()
    {
        ConstructorInfo? constructor = typeof(LiteRtEngine).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(EngineHandle), typeof(bool), typeof(int), typeof(bool)]);
        Assert.NotNull(constructor);
        var engine = (LiteRtEngine)constructor.Invoke([new EngineHandle(nint.Zero, allowMultiple: false), false, 0, false]);
        FieldInfo? cache = typeof(LiteRtEngine).GetField("_sessionStartTokenText", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(cache);
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() => LiteRtSession.Create(engine, null));
        Assert.Null(cache.GetValue(engine));
    }
}
