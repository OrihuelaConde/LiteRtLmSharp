using LiteRtLmSharp;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-backed tests for the engine improvements: several live engines, the non-blocking token count, the
/// sampler mapping, and continuing after a cancellation on a <see cref="LiteRtConversation.Clone"/>
/// checkpoint.
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
    /// The cache is refreshed by every completed send, streaming included, and a clone starts with its
    /// parent's count (it holds the same tokens), so its <see cref="LiteRtConversation.IsContextFull"/> agrees.
    /// </summary>
    [SkippableFact]
    public async Task TokenCountCached_ShouldRefreshAfterAStream_AndCarryOverToAClone()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var conv = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 24 });
        await foreach (LiteRtStreamChunk _ in conv.SendStreamingAsync("Reply with one short word: hello")) { }

        int afterStream = conv.TokenCountCached;
        Assert.True(afterStream > 0, "A completed stream must refresh the cache.");
        Assert.Equal(conv.TokenCount, afterStream);

        using var clone = conv.Clone();
        Assert.Equal(afterStream, clone.TokenCountCached);
        Assert.Equal(clone.TokenCount, clone.TokenCountCached);
        Assert.Equal(conv.IsContextFull, clone.IsContextFull);
    }

    /// <summary>
    /// The cache does not depend on the overflow guard: on an engine loaded without
    /// <see cref="LiteRtEngineOptions.MaxNumTokens"/> a completed send still refreshes it.
    /// </summary>
    [SkippableFact]
    public void TokenCountCached_ShouldRefreshWithoutMaxNumTokens()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options with { MaxNumTokens = 0 });
        using var conv = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 16 });
        conv.Send("Reply with one short word: hello");

        Assert.True(conv.TokenCountCached > 0, "A completed send must refresh the cache without MaxNumTokens.");
        Assert.False(conv.IsContextFull);   // no known limit: the guard is off
    }

    /// <summary>
    /// Greedy and TopK work on every backend because the binding sends them as TopP (measured on LiteRT-LM
    /// v0.18.0: the CPU backend rejects the raw types with UNIMPLEMENTED on a fresh engine, and the GPU backend
    /// runs them as TopP). On a fresh engine, Greedy must decode exact argmax: with a hot temperature and two
    /// different seeds it still gives the argmax reply, which a sampled TopP reply would not.
    /// </summary>
    [SkippableFact]
    public void GreedyAndTopK_ShouldWorkOnAFreshEngine_AndGreedyShouldBeArgmax()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        string Reply(LiteRtSamplerParams sampler)
        {
            using var conv = engine.CreateConversation(new LiteRtConversationOptions { Sampler = sampler, MaxOutputTokens = 48 });
            return conv.Send(StoryPrompt).Text ?? "";
        }

        // The first conversation on the fresh engine asks for Greedy: the case the CPU backend rejected.
        var hotGreedy = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Greedy, Temperature = 1.6f };
        string greedy7 = Reply(hotGreedy with { Seed = 7 });
        string greedy8 = Reply(hotGreedy with { Seed = 8 });
        string argmax = Reply(Argmax);
        Assert.Equal(argmax, greedy7);
        Assert.Equal(argmax, greedy8);

        string topK = Reply(new LiteRtSamplerParams { Strategy = LiteRtSamplerType.TopK, TopK = 8 });
        Assert.False(string.IsNullOrWhiteSpace(topK));
        output.WriteLine($"Greedy/argmax reply: {argmax}");
    }

    private const string Fact = "Remember this: my favourite colour is teal and my cat is called Miso. Reply OK.";
    private const string Count = "Count upward starting from 400, one number per line, and never stop.";

    /// <summary>
    /// The checkpoint pattern: a clone taken before a send survives the cancellation of the original, keeps
    /// its own token count (it never saw the cut turn) and still knows the earlier turns. The cancelled
    /// conversation answers CANCELLED to every later send, and cloning it is refused (the runtime would
    /// return an empty clone that also answers CANCELLED).
    /// </summary>
    [SkippableFact]
    public async Task Checkpoint_ShouldContinueAfterACancelledStream_AndTheCancelledConversationShouldRefuseToClone()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 256 });
        chat.Send(Fact);
        using var checkpoint = chat.Clone();
        int before = checkpoint.TokenCount;

        using var cts = new CancellationTokenSource();
        int chunks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync(Count, cts.Token))
                if (chunk.Kind == LiteRtStreamChunkKind.Answer && ++chunks == 12)
                    cts.Cancel();
        });

        var refused = Assert.Throws<InvalidOperationException>(() => chat.Clone());
        Assert.Contains("before the send", refused.Message, StringComparison.Ordinal);
        var cancelled = Assert.Throws<LiteRtException>(() => chat.Send("hi"));
        Assert.Equal(LiteRtStatusCode.Cancelled, cancelled.StatusCode);

        Assert.Equal(before, checkpoint.TokenCount);
        string colour = checkpoint.Send("What is my favourite colour? One word.").Text ?? "";
        Assert.Contains("teal", colour, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A blocking send cancelled through its token consumes the conversation too, so a later Clone() is refused.
    /// </summary>
    [SkippableFact]
    public async Task CancelledBlockingSend_ShouldMakeCloneRefuse()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 900 });
        chat.Send(Fact);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.SendAsync(Count, cts.Token));

        Assert.Throws<InvalidOperationException>(() => chat.Clone());
    }

    /// <summary>
    /// CancelProcess() on an idle conversation is a documented no-op, so it must not mark the conversation as
    /// cancelled: Clone() still works and the clone generates.
    /// </summary>
    [SkippableFact]
    public void CancelProcess_OnAnIdleConversation_ShouldNotBlockClone()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 24 });
        chat.Send(Fact);
        chat.CancelProcess();

        using var clone = chat.Clone();
        string cat = clone.Send("What is my cat called? One word.").Text ?? "";
        Assert.Contains("Miso", cat, StringComparison.OrdinalIgnoreCase);
    }
}
