using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// <see cref="LiteRtSession.Dispose"/> during a stream, repeated: from the loop body, from another thread, and
/// from another thread around the end of a short reply, in turns, each on a new session. The suite runs 20
/// iterations; set LITERTLM_TEST_SESSION_STRESS to another count and LITERTLM_TEST_SESSION_STRESS_MINUTES to a
/// time limit to run a longer series. Each iteration has its own limit, so a hang fails the test with the
/// iteration that hung.
/// </summary>
public sealed class SessionStressModelTests(ITestOutputHelper output)
{
    private static readonly TimeSpan IterationLimit = TimeSpan.FromSeconds(60);

    [SkippableFact]
    public async Task Dispose_DuringStreams_NeitherCrashesNorHangsNorLeaks()
    {
        LiteRtEngineOptions? options = SessionModelHelpers.Options();
        Skip.If(options is null, SessionModelHelpers.SkipReason);
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("LITERTLM_TEST_SESSION_STRESS"), out int n) && n > 0 ? n : 20;
        TimeSpan timeLimit = double.TryParse(Environment.GetEnvironmentVariable("LITERTLM_TEST_SESSION_STRESS_MINUTES"),
            System.Globalization.CultureInfo.InvariantCulture, out double minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(10);
        int before = LiteRtEngine.LiveEngineCount;

        var outcomes = new Dictionary<string, int>();
        var random = new Random(1234);
        var total = Stopwatch.StartNew();
        int done = 0;
        double shortReplyMs = 0;
        using (LiteRtEngine engine = LiteRtEngine.Load(options))
        {
            string longPrompt = SessionDecodeHelpers.Prompt(engine, SessionDecodeHelpers.CountPrompt);
            string shortPrompt = SessionDecodeHelpers.Prompt(engine, "Say hello in five words.");

            // The time from the first piece of a short reply to its end, so that the third mode disposes around
            // the end of the reply.
            using (LiteRtSession probe = SessionDecodeHelpers.Session(engine, 8))
            {
                probe.Prefill(shortPrompt);
                Stopwatch? watch = null;
                await SessionDecodeHelpers.StreamWithin(probe, onPiece: count =>
                {
                    if (count == 1)
                        watch = Stopwatch.StartNew();
                    return true;
                });
                shortReplyMs = watch?.Elapsed.TotalMilliseconds ?? 0;
            }

            for (; done < iterations && total.Elapsed < timeLimit; done++)
            {
                int mode = done % 3;
                using LiteRtSession session = SessionDecodeHelpers.Session(engine, mode == 2 ? 8 : 64);
                session.Prefill(mode == 2 ? shortPrompt : longPrompt);
                Task<Exception?> run = mode switch
                {
                    0 => DisposeInTheLoop(session, 1 + random.Next(4)),
                    1 => DisposeFromAnotherThread(session, TimeSpan.FromMilliseconds(random.Next(0, 200))),
                    _ => DisposeFromAnotherThread(session, TimeSpan.FromMilliseconds(shortReplyMs * 1.4 * random.NextDouble())),
                };
                Exception? error;
                try
                {
                    error = await run.WaitAsync(IterationLimit);
                }
                catch (TimeoutException)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Iteration {done} (mode {mode}) did not end within {IterationLimit.TotalSeconds} s; state {session.State}.");
                }
                session.Dispose();
                await SessionDecodeHelpers.Eventually(() => !session.IsCallInProgress, $"iteration {done} to settle");

                string key = $"mode {mode}: {error?.GetType().Name ?? "completed"}, {session.State}";
                outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
                Assert.True(error is null or ObjectDisposedException, $"Iteration {done} (mode {mode}) threw {error}");
                Assert.True(session.State is LiteRtSessionState.Cancelled or LiteRtSessionState.Decoded,
                    $"Iteration {done} (mode {mode}) ended {session.State}.");
                SessionDecodeHelpers.AssertNativeSessionDeleted(session);
            }
        }
        await SessionDecodeHelpers.Eventually(() => LiteRtEngine.LiveEngineCount == before, "every native session and the engine to be deleted");

        output.WriteLine($"{done} of {iterations} iterations in {total.Elapsed.TotalSeconds:F0} s (limit {timeLimit.TotalMinutes} min); " +
                         $"short reply after its first piece {shortReplyMs:F0} ms; live engines after: {LiteRtEngine.LiveEngineCount} (before {before})");
        foreach (KeyValuePair<string, int> outcome in outcomes.OrderBy(o => o.Key, StringComparer.Ordinal))
            output.WriteLine($"  {outcome.Value} x {outcome.Key}");
        Assert.True(done > 0);
    }

    /// <summary>Reads a stream and disposes the session in the loop body after <paramref name="pieces"/>
    /// pieces.</summary>
    private static async Task<Exception?> DisposeInTheLoop(LiteRtSession session, int pieces)
        => await Record.ExceptionAsync(() => SessionDecodeHelpers.StreamWithin(session, onPiece: count =>
        {
            if (count == pieces)
                session.Dispose();
            return true;
        }));

    /// <summary>Reads a stream while another thread disposes the session after <paramref name="delay"/> from the
    /// first piece.</summary>
    private static async Task<Exception?> DisposeFromAnotherThread(LiteRtSession session, TimeSpan delay)
    {
        var firstPiece = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<List<string>> reader = SessionDecodeHelpers.StreamWithin(session, onPiece: _ =>
        {
            firstPiece.TrySetResult();
            return true;
        });
        Task disposer = Task.Run(async () =>
        {
            await Task.WhenAny(firstPiece.Task, reader);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
            session.Dispose();
        });
        Exception? error = await Record.ExceptionAsync(() => reader);
        await disposer;
        return error;
    }
}
