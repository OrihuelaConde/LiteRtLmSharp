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
        Assert.Contains("native call on this session failed", refused.Message, StringComparison.Ordinal);
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
