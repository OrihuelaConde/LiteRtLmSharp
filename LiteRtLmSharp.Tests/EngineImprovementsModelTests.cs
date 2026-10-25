using LiteRtLmSharp;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-backed tests for the engine improvements: several live engines, the non-blocking token count, the
/// sampler probe and continue-after-cancel via <see cref="LiteRtConversation.Snapshot(string?)"/> /
/// <see cref="LiteRtConversation.Resume(LiteRtConversationSnapshot)"/>.
/// Set LITERTLM_TEST_MODEL to a .litertlm file to run (LITERTLM_TEST_BACKEND selects cpu/gpu).
/// </summary>
public sealed class EngineImprovementsModelTests(ITestOutputHelper output)
{
    /// <summary>The gemma-4 E2B conversions need at least 1024 context; the tests stay small to keep the
    /// per-engine footprint down (the multi-engine test holds two engines at once).</summary>
    private const int Context = 1024;

    private static LiteRtEngineOptions? Options()
    {
        string? modelPath = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            return null;
        LiteRtEngine.SetMinLogLevel(3);
        return new LiteRtEngineOptions
        {
            ModelPath = modelPath,
            Backend = LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu"),
            MaxNumTokens = Context,
        };
    }

    private static bool IsCpu =>
        LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu") == LiteRtBackend.Cpu;

    /// <summary>Exact argmax over the TopP sampler (TopK = 1), so replies can be compared byte for byte.</summary>
    private static readonly LiteRtSamplerParams Argmax = new() { Strategy = LiteRtSamplerType.TopP, TopK = 1 };

    private const string StoryPrompt = "Write a short story about a lighthouse keeper who finds a message in a bottle.";

    private static string Story(LiteRtEngine engine)
    {
        using var conv = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 48 });
        return conv.Send(StoryPrompt).Text ?? "";
    }

    /// <summary>
    /// Two engines loaded with <see cref="LiteRtEngineOptions.AllowMultipleEngines"/>, created from two threads
    /// at once, generating at the same time and disposed independently: every reply must be byte-identical to
    /// the reply of a single engine (argmax), which is what shows the engines do not share state. Both engines
    /// stay at 1024 tokens of context, so the test fits in a machine with ~6 GB free.
    /// </summary>
    [SkippableFact]
    public void MultipleEngines_ShouldMatchASingleEngine_WhenGeneratingAtTheSameTime()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        options = options with { AllowMultipleEngines = true };

        string reference;
        using (var alone = LiteRtEngine.Load(options))
            reference = Story(alone);
        Assert.False(string.IsNullOrWhiteSpace(reference));

        var engines = new LiteRtEngine?[2];
        try
        {
            Parallel.For(0, 2, i => engines[i] = LiteRtEngine.Load(options));
            Assert.Equal(2, LiteRtEngine.LiveEngineCount);

            var replies = new string[2];
            Parallel.For(0, 2, i => replies[i] = Story(engines[i]!));
            Assert.Equal(reference, replies[0]);
            Assert.Equal(reference, replies[1]);

            // Disposing one engine must leave the other fully usable, with the same output.
            engines[1]!.Dispose();
            Assert.Equal(1, LiteRtEngine.LiveEngineCount);
            Assert.Equal(reference, Story(engines[0]!));
            output.WriteLine($"Reply on every engine: {reference}");
        }
        finally
        {
            foreach (var e in engines)
                e?.Dispose();
        }
        Assert.Equal(0, LiteRtEngine.LiveEngineCount);
    }

    /// <summary>
    /// The opt-in is per engine: an engine loaded without it stays alone, and an engine without it cannot join
    /// engines that opted in. Each refusal says which side did not opt in.
    /// </summary>
    [SkippableFact]
    public void MultipleEngines_ShouldRefuseToMixWithAnEngineThatDidNotOptIn()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        LiteRtEngineOptions optedIn = options with { AllowMultipleEngines = true };

        using (var alone = LiteRtEngine.Load(options))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => LiteRtEngine.Load(optedIn));
            Assert.Contains("loaded without LiteRtEngineOptions.AllowMultipleEngines is still alive", ex.Message, StringComparison.Ordinal);
            Assert.Equal(1, LiteRtEngine.LiveEngineCount);
        }

        using (var shared = LiteRtEngine.Load(optedIn))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => LiteRtEngine.Load(options));
            Assert.Contains("this engine was loaded without", ex.Message, StringComparison.Ordinal);
            Assert.Equal(1, LiteRtEngine.LiveEngineCount);
        }
        Assert.Equal(0, LiteRtEngine.LiveEngineCount);
    }

    /// <summary>
    /// The non-blocking count: <see cref="LiteRtConversation.TokenCountCached"/> starts at 0 (creating a
    /// conversation prefills nothing — not even its <c>History</c>) and tracks
    /// <see cref="LiteRtConversation.TokenCount"/> after each completed send, so a UI can poll it without
    /// queueing behind a generation running elsewhere on the engine.
    /// </summary>
    [SkippableFact]
    public void TokenCountCached_ShouldTrackCompletedSends_WithoutReadingNative()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var conv = engine.CreateConversation(new LiteRtConversationOptions
        {
            MaxOutputTokens = 24,
            // History is applied by the first send, not by creation — the cache starts at 0 either way.
            History = [LiteRtMessage.User("Remember: my favourite colour is teal."), LiteRtMessage.Model("Noted.")],
        });

        Assert.Equal(0, conv.TokenCountCached);
        Assert.Equal(0, conv.TokenCount);          // the native count agrees: nothing is prefilled yet

        conv.Send("Reply with one short word: hello");

        int exact = conv.TokenCount;               // one (explicit) native read, which also refreshes the cache
        Assert.True(exact > 0, "A completed send must have left tokens in the KV cache.");
        Assert.Equal(exact, conv.TokenCountCached);
        Assert.False(conv.IsContextFull);
    }

    /// <summary>
    /// The sampler story the CPU backend forces callers to know (measured on native v0.18.0, gemma-4 E2B):
    /// a conversation asking for Greedy is created without complaint and fails its FIRST send with the opaque
    /// <c>UNIMPLEMENTED: Sampler type: 3</c> — which the binding turns into a
    /// <see cref="LiteRtSamplerNotSupportedException"/> naming the strategy and the way out. TopP is
    /// implemented and honoured, and the engine is unharmed by the rejection.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: the retry/second-conversation behavior around the rejection is native state (the
    /// runtime establishes its sampler once per engine), and pinning it here would make the suite fail on a
    /// later native version that changes it without any regression in this binding. The two facts a caller
    /// depends on are asserted instead.
    /// </remarks>
    [SkippableFact]
    public void UnsupportedSampler_ShouldFailTheFirstSendWithAReadableDiagnosis()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.IfNot(IsCpu, "The Greedy/TopK gap is a CPU-backend limitation (the GPU backend implements them).");

        using var engine = LiteRtEngine.Load(options);

        using var greedy = engine.CreateConversation(new LiteRtConversationOptions
        {
            Sampler = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Greedy },
            MaxOutputTokens = 8,
        });

        // Creation succeeds; the failure only appears when generation starts.
        var ex = Assert.Throws<LiteRtSamplerNotSupportedException>(() => greedy.Send("hi"));
        Assert.Equal(LiteRtSamplerType.Greedy, ex.SamplerType);
        Assert.Equal(LiteRtStatusCode.Unimplemented, ex.StatusCode);
        // The runtime's own words survive (the numeric type is the only clue it gives), next to the
        // explanation and the way out.
        Assert.Contains("Sampler type: 3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NearGreedy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("consumed", ex.Message, StringComparison.Ordinal);

        // TopP is the strategy that works — and the engine is unharmed by the rejected conversation.
        using var topP = engine.CreateConversation(new LiteRtConversationOptions
        {
            Sampler = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.TopP },
            MaxOutputTokens = 8,
        });
        Assert.False(string.IsNullOrWhiteSpace(topP.Send("Reply with one short word: hello").Text));

        using var fine = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 8 });
        Assert.False(string.IsNullOrWhiteSpace(fine.Send("Reply with one short word: hello").Text));
    }

    /// <summary>
    /// Continue-after-cancel: the recipe the binding now carries. Cancelling mid-generation kills the
    /// conversation, <see cref="LiteRtConversation.Snapshot(string?)"/> keeps the turns plus the partial
    /// reply, and <see cref="LiteRtConversation.Resume(LiteRtConversationSnapshot)"/> rebuilds a usable
    /// conversation that still remembers the earlier turn.
    /// </summary>
    [SkippableFact]
    public async Task SnapshotResume_ShouldContinueAConversationThatWasCutShort()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions
        {
            MaxOutputTokens = 32,
            SystemMessage = "You are a concise assistant.",
        });

        var state = new List<string>();
        chat.Send("Remember this: my favourite colour is teal. Acknowledge in two words.");

        using var streamCts = new CancellationTokenSource();
        var partial = new System.Text.StringBuilder();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync(
                "Now count upward from 1, one number per line, and do not stop.", streamCts.Token))
            {
                if (chunk.Kind == LiteRtStreamChunkKind.Answer)
                {
                    partial.Append(chunk.Text);
                    // Cut the generation short as soon as it produced something: the eviction path a host
                    // uses when a higher-priority job needs the engine.
                    if (partial.Length > 8)
                        streamCts.Cancel();
                }
            }
        });

        string received = partial.ToString();
        output.WriteLine($"Partial reply captured by the binding: '{received}'");
        Assert.False(string.IsNullOrEmpty(received), "The stream produced nothing before the cancel.");

        // The binding captured the partial text itself for a streaming send — no argument needed.
        LiteRtConversationSnapshot snapshot = chat.Snapshot();
        Assert.NotEmpty(snapshot.Turns);
        Assert.Equal(received, snapshot.PartialResponse);
        chat.Dispose();

        // The cancelled conversation itself is unusable; the resume is a fresh one on the same engine.
        using var resumed = LiteRtConversation.Resume(snapshot);
        Assert.True(resumed.TokenCountCached >= 0);
        LiteRtResponse answer = resumed.Send("What is my favourite colour? One word.");
        output.WriteLine($"Resumed reply: {answer.Text}");
        // The recalled fact proves the replayed history (including the system message) reached the model.
        Assert.Contains("teal", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A snapshot taken after plain, completed turns resumes into a conversation that replays them — the
    /// "<c>History</c> = the turns I have" recipe, without the caller building the list.
    /// </summary>
    [SkippableFact]
    public void SnapshotResume_ShouldCarryCompletedTurns_AndRejectAnEmptySnapshot()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 24 });
        chat.Send("Remember this: the passphrase is ORANGE-LANTERN. Acknowledge briefly.");

        LiteRtConversationSnapshot snapshot = chat.Snapshot();
        Assert.Equal(2, snapshot.Turns.Count);                 // the user turn and the model turn
        Assert.Null(snapshot.PartialResponse);                 // nothing was cut short
        Assert.Same(engine, snapshot.Engine);
        Assert.True(snapshot.TokenCount > 0);                  // the non-blocking cache, refreshed by the send

        using var resumed = LiteRtConversation.Resume(snapshot);
        LiteRtResponse answer = resumed.Send("What is the passphrase? Reply with just the passphrase.");
        output.WriteLine($"Resumed reply: {answer.Text}");
        Assert.Contains("ORANGE-LANTERN", answer.Text, StringComparison.OrdinalIgnoreCase);

        // A snapshot with nothing in it is a caller bug, not an empty conversation to resume.
        var empty = new LiteRtConversationSnapshot { Engine = engine };
        Assert.Throws<ArgumentException>(() => LiteRtConversation.Resume(empty));
    }
}
