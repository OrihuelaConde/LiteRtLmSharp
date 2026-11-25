using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>One engine for the session tests, with a 1024-token context so that an input that does not fit stays
/// small. Loaded only when LITERTLM_TEST_MODEL names a model file (LITERTLM_TEST_BACKEND selects cpu or
/// gpu).</summary>
public sealed class SessionEngineFixture : IDisposable
{
    public const int Context = 1024;

    public LiteRtEngine? Engine { get; } = SessionModelHelpers.Options() is { } options ? LiteRtEngine.Load(options) : null;

    public void Dispose() => Engine?.Dispose();
}

internal static class SessionModelHelpers
{
    public const string SkipReason = "Set LITERTLM_TEST_MODEL to a .litertlm file to run.";

    private const string Sentence = "The quick brown fox jumps over the lazy dog near the quiet river bank. ";

    public static LiteRtEngineOptions? Options()
    {
        string? modelPath = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            return null;
        LiteRtEngine.SetMinLogLevel(3);
        return new LiteRtEngineOptions
        {
            ModelPath = modelPath,
            Backend = LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu"),
            MaxNumTokens = SessionEngineFixture.Context,
        };
    }

    /// <summary>Plain text of about <paramref name="tokens"/> tokens by the engine's tokenizer: at least
    /// <paramref name="tokens"/> when <paramref name="atLeast"/> is set, otherwise at most.</summary>
    public static string TextOf(LiteRtEngine engine, int tokens, bool atLeast)
    {
        int per = engine.Tokenize(Sentence).Length;
        int repeats = atLeast ? (tokens + per - 1) / per : tokens / per;
        string text = string.Concat(Enumerable.Repeat(Sentence, repeats));
        while (engine.Tokenize(text).Length > tokens && !atLeast)
            text = text[..^Sentence.Length];
        while (engine.Tokenize(text).Length < tokens && atLeast)
            text += Sentence;
        return text;
    }
}

/// <summary>
/// The session core on a model: create and dispose, prefill, the start-token refusal, a prefill that does not fit,
/// and a cancelled <see cref="LiteRtSession.PrefillAsync"/>. The asserts are about the binding (states,
/// exceptions, refusals), not about what the model writes.
/// </summary>
public sealed class SessionModelTests(SessionEngineFixture fixture, ITestOutputHelper output) : IClassFixture<SessionEngineFixture>
{
    private static readonly LiteRtSamplerParams Argmax = new() { Strategy = LiteRtSamplerType.TopP, TopK = 1 };

    [SkippableFact]
    public void CreateSession_StartsFresh_AndDisposeIsIdempotent()
    {
        Skip.If(fixture.Engine is null, SessionModelHelpers.SkipReason);

        using (LiteRtSession plain = fixture.Engine.CreateSession())
            Assert.Equal(LiteRtSessionState.Fresh, plain.State);

        LiteRtSession session = fixture.Engine.CreateSession(new LiteRtSessionOptions { Sampler = Argmax, MaxOutputTokens = 8 });
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        session.Dispose();
        session.Dispose();
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        Assert.Throws<ObjectDisposedException>(() => session.Prefill("Hello"));

        // An Unspecified sampler sends no sampler parameters and still creates a session.
        using LiteRtSession unspecified = fixture.Engine.CreateSession(
            new LiteRtSessionOptions { Sampler = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Unspecified } });
        Assert.Equal(LiteRtSessionState.Fresh, unspecified.State);
    }

    [SkippableFact]
    public async Task Prefill_MovesFreshToPrefilled_AndAcceptsMorePrefills()
    {
        Skip.If(fixture.Engine is null, SessionModelHelpers.SkipReason);

        using LiteRtSession session = fixture.Engine.CreateSession();
        session.Prefill("<|turn>user\nHello<turn|>\n");
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        Assert.False(session.IsCallInProgress);
        await session.PrefillAsync("<|turn>model\n");
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
        Assert.False(session.IsCallInProgress);
    }

    /// <summary>A first prefill that starts with the model's start token is refused before any native call, and
    /// leaves the session Fresh; once the session holds input, the same text passes unchanged. Needs a model
    /// whose start token is one id (Gemma 4: <c>&lt;bos&gt;</c>).</summary>
    [SkippableFact]
    public async Task Prefill_ThatStartsWithTheStartToken_IsRefusedOnlyOnAFreshSession()
    {
        Skip.If(fixture.Engine is null, SessionModelHelpers.SkipReason);
        LiteRtEngine engine = fixture.Engine;
        LiteRtTokenUnion? startToken = engine.GetStartToken();
        using LiteRtSession session = engine.CreateSession();
        if (startToken is not { Kind: LiteRtTokenKind.Ids, Ids.Count: 1 })
        {
            Assert.Null(session.RefusedStartTokenText);
            Skip.If(true, $"The model's start token is not one id ({startToken?.ToString() ?? "none"}).");
        }

        string startText = engine.Detokenize([startToken!.Ids[0]]);
        output.WriteLine($"start token {startToken.Ids[0]} = \"{startText}\"");
        Assert.Equal(startText, session.RefusedStartTokenText);

        var ex = Assert.Throws<ArgumentException>(() => session.Prefill(startText + "Hello"));
        Assert.Contains("adds the start token itself", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => session.PrefillAsync(startText + "Hello"));
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        Assert.False(session.IsCallInProgress);

        session.Prefill("Hello");
        session.Prefill(startText + "Hello again");
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
    }

    /// <summary>A prefill that does not fit throws <see cref="LiteRtException"/> with the runtime's status, latches
    /// Faulted, and refuses the next prefill; the engine still serves a new session and a conversation. Both ways
    /// of not fitting are tried: one input longer than the context on a fresh session, and a second input that
    /// does not fit after the first.</summary>
    [SkippableFact]
    public void Prefill_ThatDoesNotFit_FaultsTheSession_AndTheEngineStillServes()
    {
        Skip.If(fixture.Engine is null, SessionModelHelpers.SkipReason);
        LiteRtEngine engine = fixture.Engine;

        using (LiteRtSession tooLong = engine.CreateSession())
            AssertFaultsOnOverflow(tooLong, SessionModelHelpers.TextOf(engine, SessionEngineFixture.Context + 100, atLeast: true));

        using (LiteRtSession filled = engine.CreateSession())
        {
            filled.Prefill(SessionModelHelpers.TextOf(engine, 600, atLeast: false));
            Assert.Equal(LiteRtSessionState.Prefilled, filled.State);
            AssertFaultsOnOverflow(filled, SessionModelHelpers.TextOf(engine, 500, atLeast: true));
        }

        using (LiteRtSession next = engine.CreateSession())
        {
            next.Prefill("Hello");
            Assert.Equal(LiteRtSessionState.Prefilled, next.State);
        }
        using LiteRtConversation chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 8 });
        Assert.False(string.IsNullOrWhiteSpace(chat.Send("Reply with one short word: hello").Text), "The engine did not serve a conversation.");
    }

    private void AssertFaultsOnOverflow(LiteRtSession session, string text)
    {
        var ex = Assert.Throws<LiteRtException>(() => session.Prefill(text));
        output.WriteLine($"{ex.StatusCode}: {ex.Message}");
        Assert.NotNull(ex.StatusCode);
        Assert.NotEqual(LiteRtStatusCode.Ok, ex.StatusCode);
        Assert.Contains("litert_lm_session_run_prefill failed", ex.Message, StringComparison.Ordinal);
        if (ex.StatusCode == LiteRtStatusCode.ResourceExhausted)
            Assert.Contains(LiteRtSession.DoesNotFitHint, ex.Message, StringComparison.Ordinal);
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
        Assert.False(session.IsCallInProgress);
        Assert.Throws<InvalidOperationException>(() => session.Prefill("Hello"));
        Assert.Equal(LiteRtSessionState.Faulted, session.State);
    }

    [SkippableFact]
    public async Task PrefillAsync_WithATokenCancelledBeforeItStarts_ChangesNothing()
    {
        Skip.If(fixture.Engine is null, SessionModelHelpers.SkipReason);

        using LiteRtSession session = fixture.Engine.CreateSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrefillAsync("Hello", cts.Token));
        Assert.Equal(LiteRtSessionState.Fresh, session.State);
        Assert.False(session.IsCallInProgress);

        await session.PrefillAsync("Hello", CancellationToken.None);
        Assert.Equal(LiteRtSessionState.Prefilled, session.State);
    }
}

/// <summary>
/// Session lifetime against its engine, on engines these tests load themselves (one live engine at a time): a
/// session keeps the native engine alive, frees its retired stream handles after the native delete, and survives
/// a <see cref="LiteRtSession.Dispose"/> from another thread during a prefill.
/// </summary>
public sealed class SessionLifetimeModelTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Engine_DisposedBeforeItsSessions_IsReleasedByTheLastOne()
    {
        LiteRtEngineOptions? options = SessionModelHelpers.Options();
        Skip.If(options is null, SessionModelHelpers.SkipReason);
        int before = LiteRtEngine.LiveEngineCount;

        using LiteRtEngine engine = LiteRtEngine.Load(options);
        using LiteRtSession first = engine.CreateSession();
        using LiteRtSession second = engine.CreateSession(new LiteRtSessionOptions { MaxOutputTokens = 8 });
        string? startText = second.RefusedStartTokenText;
        WeakReference retired = RetireAnObject(first.Handle);
        Assert.Equal(1, first.Handle.RetiredCount);

        engine.Dispose();
        Assert.Equal(before + 1, LiteRtEngine.LiveEngineCount);
        Assert.Throws<ObjectDisposedException>(() => engine.CreateSession());

        // The sessions still work on the native engine they hold, start-token rule included.
        first.Prefill("Hello");
        Assert.Equal(LiteRtSessionState.Prefilled, first.State);
        if (startText is not null)
            Assert.Throws<ArgumentException>(() => second.Prefill(startText + "Hello"));
        second.Prefill("Hello");
        Assert.Equal(LiteRtSessionState.Prefilled, second.State);

        first.Dispose();
        Assert.Equal(before + 1, LiteRtEngine.LiveEngineCount);
        // The retired handle was freed after the native delete, so its object can be collected.
        Assert.Equal(0, first.Handle.RetiredCount);
        AssertCollected(retired);

        second.Dispose();
        Assert.Equal(before, LiteRtEngine.LiveEngineCount);
        // A handle retired after the delete is freed at once.
        WeakReference late = RetireAnObject(second.Handle);
        Assert.Equal(0, second.Handle.RetiredCount);
        AssertCollected(late);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetireAnObject(SessionHandle handle)
    {
        object target = new byte[64];
        handle.Retire(GCHandle.Alloc(target));
        return new WeakReference(target);
    }

    private static void AssertCollected(WeakReference reference)
    {
        for (int i = 0; i < 3 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(reference.IsAlive, "The retired GCHandle was not freed.");
    }

    /// <summary>A <see cref="LiteRtSession.Dispose"/> from another thread while a prefill runs neither crashes nor
    /// waits for the prefill: the prefill either ends normally or, when the dispose came before its native call,
    /// throws <see cref="ObjectDisposedException"/>, and the native session and engine are released once it
    /// ends.</summary>
    [SkippableFact]
    public async Task Dispose_FromAnotherThreadDuringAPrefill_DoesNotCrash()
    {
        LiteRtEngineOptions? options = SessionModelHelpers.Options();
        Skip.If(options is null, SessionModelHelpers.SkipReason);
        int before = LiteRtEngine.LiveEngineCount;

        LiteRtEngine engine = LiteRtEngine.Load(options);
        string text = SessionModelHelpers.TextOf(engine, 900, atLeast: false);
        LiteRtSession session = engine.CreateSession();
        // The session then holds the last reference on the native engine, so the count shows when it is deleted.
        engine.Dispose();
        Assert.Equal(before + 1, LiteRtEngine.LiveEngineCount);

        Task prefill = Task.Run(() => session.Prefill(text));
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!session.IsCallInProgress && !prefill.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(10))
            Thread.SpinWait(100);
        var dispose = System.Diagnostics.Stopwatch.StartNew();
        session.Dispose();
        dispose.Stop();
        bool disposedDuringCall = !prefill.IsCompleted;

        Exception? error = await Record.ExceptionAsync(() => prefill.WaitAsync(TimeSpan.FromSeconds(60)));
        output.WriteLine($"dispose took {dispose.Elapsed.TotalMilliseconds:F1} ms, during the call: {disposedDuringCall}, " +
                         $"prefill: {error?.GetType().Name ?? "completed"}, state: {session.State}");
        Assert.True(error is null or ObjectDisposedException, $"Unexpected prefill outcome: {error}");
        Assert.Equal(error is null ? LiteRtSessionState.Prefilled : LiteRtSessionState.Fresh, session.State);
        Assert.False(session.IsCallInProgress);
        Assert.Equal(before, LiteRtEngine.LiveEngineCount);
        Skip.If(!disposedDuringCall, "The prefill ended before Dispose ran, so the dispose did not land during the call.");
    }
}
