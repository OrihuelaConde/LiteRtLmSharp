using LiteRtLmSharp;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-backed tests for the engine improvements: several live engines, the non-blocking token count, the
/// sampler strategies, and continuing after a cancellation on a <see cref="LiteRtConversation.Clone"/>
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
            // The using disposes the engine if a regression lets the load succeed, so one failure does not cascade.
            var ex = Assert.Throws<InvalidOperationException>(() => { using LiteRtEngine leaked = LiteRtEngine.Load(optedIn); });
            Assert.Contains("loaded without LiteRtEngineOptions.AllowMultipleEngines is still alive", ex.Message, StringComparison.Ordinal);
            Assert.Equal(1, LiteRtEngine.LiveEngineCount);
        }

        using (var shared = LiteRtEngine.Load(optedIn))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => { using LiteRtEngine leaked = LiteRtEngine.Load(options); });
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

        int cached = conv.TokenCountCached;        // read first: the native read below also refreshes the cache
        Assert.True(cached > 0, "A completed send must have left tokens in the KV cache.");
        Assert.Equal(conv.TokenCount, cached);
        Assert.False(conv.IsContextFull);
    }

    /// <summary>
    /// The cache is refreshed by every completed send, streaming included, and a clone starts with its
    /// parent's count (it holds the same tokens).
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
        Assert.Equal(afterStream, clone.TokenCount);
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
    /// Greedy and TopK are sent to the runtime as given and work as named as the first conversation on a fresh
    /// engine, on the CPU and GPU backends. Greedy must decode exact argmax: with a hot temperature and two
    /// different seeds it still gives the argmax reply, which a sampled reply would not. TopK, the first
    /// conversation on its own engine too, must sample exactly like TopP with <c>TopP = 1</c>. Without the
    /// runtime's support for these types, the first send fails with <c>UNIMPLEMENTED: Sampler type: N</c> on
    /// the CPU backend, and Greedy gives sampled text on the GPU backend (both measured on LiteRT-LM v0.18.0).
    /// </summary>
    [SkippableFact]
    public void GreedyAndTopK_ShouldWorkOnAFreshEngine_AndGreedyShouldBeArgmax()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        string argmax;
        using (var engine = LiteRtEngine.Load(options))
        {
            // The first conversation on the fresh engine asks for Greedy.
            var hotGreedy = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Greedy, Temperature = 1.6f };
            string greedy7 = Reply(engine, hotGreedy with { Seed = 7 });
            string greedy8 = Reply(engine, hotGreedy with { Seed = 8 });
            argmax = Reply(engine, Argmax);
            output.WriteLine($"Greedy seed 7: {greedy7}");
            output.WriteLine($"Greedy seed 8: {greedy8}");
            output.WriteLine($"Argmax:        {argmax}");
            Assert.Equal(argmax, greedy7);
            Assert.Equal(argmax, greedy8);
        }

        using (var engine = LiteRtEngine.Load(options))
        {
            var sampled = new LiteRtSamplerParams { TopK = 8, Temperature = 1.0f, Seed = 7 };
            string topK = Reply(engine, sampled with { Strategy = LiteRtSamplerType.TopK });
            string topP = Reply(engine, sampled with { Strategy = LiteRtSamplerType.TopP, TopP = 1.0f });
            output.WriteLine($"TopK k 8:      {topK}");
            output.WriteLine($"TopP k 8, p 1: {topP}");
            Assert.False(string.IsNullOrWhiteSpace(topK));
            Assert.Equal(topP, topK);
        }
    }

    private static string Reply(LiteRtEngine engine, LiteRtSamplerParams sampler)
    {
        using var conv = engine.CreateConversation(new LiteRtConversationOptions { Sampler = sampler, MaxOutputTokens = 48 });
        return conv.Send(StoryPrompt).Text ?? "";
    }

    /// <summary>
    /// Each conversation's parameters take effect on an engine where others already generated: a Greedy
    /// conversation after a sampled one gives the reply of a separate engine that never sampled, and so does
    /// one sent turn by turn next to a live sampled conversation. If the runtime kept the sampler of an earlier
    /// conversation on the engine, the Greedy reply would be sampled text (LiteRT-LM v0.18.0 on the CPU backend
    /// did that). The TopK half pins LiteRT-LM v0.20.0 behaviour: a TopK conversation after a TopP one gives
    /// the reply a TopK conversation gives as the first on a fresh engine. If the runtime kept the TopP
    /// sampler, the TopK reply would come from it instead.
    /// </summary>
    [SkippableFact]
    public void Greedy_ShouldBeArgmax_AfterASampledConversation_AndNextToALiveOne()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        string argmax;
        using (var alone = LiteRtEngine.Load(options))
            argmax = Reply(alone, Argmax);

        var topKSampler = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.TopK, TopK = 8, Temperature = 1.0f, Seed = 7 };
        string topKAlone;
        using (var fresh = LiteRtEngine.Load(options))
            topKAlone = Reply(fresh, topKSampler);
        output.WriteLine($"Argmax, own engine: {argmax}");
        output.WriteLine($"TopK, fresh engine: {topKAlone}");
        Assert.NotEqual(argmax, topKAlone);   // the TopK reply samples, so the comparison below can fail

        var hotTopP = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.TopP, TopK = 64, TopP = 1.0f, Temperature = 1.6f, Seed = 7 };
        var hotGreedy = hotTopP with { Strategy = LiteRtSamplerType.Greedy, Seed = 8 };
        using var engine = LiteRtEngine.Load(options);
        Assert.NotEqual(argmax, Reply(engine, hotTopP));
        Assert.Equal(argmax, Reply(engine, hotGreedy));

        Assert.NotEqual(argmax, Reply(engine, hotTopP));
        string topKAfter = Reply(engine, topKSampler);
        output.WriteLine($"TopK after TopP:    {topKAfter}");
        Assert.Equal(topKAlone, topKAfter);

        using var sampled = engine.CreateConversation(new LiteRtConversationOptions { Sampler = hotTopP, MaxOutputTokens = 48 });
        using var greedy = engine.CreateConversation(new LiteRtConversationOptions { Sampler = hotGreedy, MaxOutputTokens = 48 });
        Assert.NotEqual(argmax, sampled.Send(StoryPrompt).Text);
        Assert.Equal(argmax, greedy.Send(StoryPrompt).Text);
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

        Assert.True(chat.IsCancelled);
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.SendAsync(Count, cts.Token));

        var cancelled = Assert.Throws<LiteRtException>(() => chat.Send("hi"));
        Assert.Equal(LiteRtStatusCode.Cancelled, cancelled.StatusCode);
        Assert.True(chat.IsCancelled);
        Assert.Contains("before the send", Assert.Throws<InvalidOperationException>(() => chat.Clone()).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream cut with CancelProcess() fails with a LiteRtException, not OperationCanceledException (no token
    /// was cancelled), and consumes the conversation. The guide's checkpoint example depends on both.
    /// </summary>
    [SkippableFact]
    public async Task CancelProcess_DuringAStream_ShouldFailWithCancelled_AndConsumeTheConversation()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 256 });
        chat.Send(Fact);

        int chunks = 0;
        var failure = await Assert.ThrowsAsync<LiteRtException>(async () =>
        {
            await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync(Count))
                if (chunk.Kind == LiteRtStreamChunkKind.Answer && ++chunks == 12)
                    chat.CancelProcess();
        });

        Assert.Equal(LiteRtStatusCode.Cancelled, failure.StatusCode);
        Assert.True(chat.IsCancelled);
        Assert.Throws<InvalidOperationException>(() => chat.Clone());
    }

    /// <summary>
    /// A stream left before its reply ended consumes the conversation. One left after the native generation
    /// ended does not, even with chunks still unread: the send completed, so the conversation stays usable and
    /// its token count is refreshed.
    /// </summary>
    [SkippableFact]
    public async Task LeavingAStream_ShouldConsumeTheConversation_OnlyIfTheReplyHadNotEnded()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);

        using (var cut = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 256 }))
        {
            int chunks = 0;
            await foreach (LiteRtStreamChunk chunk in cut.SendStreamingAsync(Count))
                if (chunk.Kind == LiteRtStreamChunkKind.Answer && ++chunks == 12)
                    break;
            Assert.Equal(12, chunks);
            Assert.True(cut.IsCancelled);
        }

        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 48 });
        chat.Send(Fact);
        int before = chat.TokenCountCached;
        await foreach (LiteRtStreamChunk _ in chat.SendStreamingAsync("Write one short sentence about the sea."))
        {
            // Creating a conversation waits for the generation in flight, so the reply has ended after this. The
            // final chunk's callback runs just after that, so the pause lets it arrive before the break.
            using LiteRtConversation waited = engine.CreateConversation();
            await Task.Delay(200);
            break;
        }

        Assert.False(chat.IsCancelled);
        Assert.True(chat.TokenCountCached > before, "The completed turn must refresh the cache.");
        using var clone = chat.Clone();
        string cat = clone.Send("What is my cat called? One word.").Text ?? "";
        Assert.Contains("Miso", cat, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A conversation keeps the native engine alive: disposing the engine first is safe, the engine counts until
    /// its last conversation is disposed (a clone taken after that dispose included), and no new conversation can
    /// be created from it.
    /// </summary>
    [SkippableFact]
    public void Engine_DisposedBeforeItsConversations_ShouldBeReleasedByTheLastOne()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 16 });
        chat.Send(Fact);

        engine.Dispose();
        Assert.Equal(1, LiteRtEngine.LiveEngineCount);
        Assert.Throws<ObjectDisposedException>(() => engine.CreateConversation());

        using var clone = chat.Clone();
        chat.Dispose();
        Assert.Equal(1, LiteRtEngine.LiveEngineCount);
        clone.Dispose();
        Assert.Equal(0, LiteRtEngine.LiveEngineCount);
    }

    /// <summary>
    /// The overflow guard keeps working on a conversation that outlives its engine object: the native engine is
    /// still alive, so an oversized message is rejected up front and the conversation survives.
    /// </summary>
    [SkippableFact]
    public void OverflowGuard_ShouldKeepMeasuring_AfterTheEngineObjectWasDisposed()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 16 });
        chat.Send(Fact);
        engine.Dispose();

        string oversized = string.Join(" ", Enumerable.Repeat("many words that will not fit", 400));
        Assert.Throws<LiteRtContextOverflowException>(() => chat.Send(oversized));
        string cat = chat.Send("What is my cat called? One word.").Text ?? "";
        Assert.Contains("Miso", cat, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A consumer that disposes the conversation inside the read loop, then leaves the stream, must not make the
    /// stream's teardown call into the deleted native conversation.
    /// </summary>
    [SkippableFact]
    public async Task DisposingTheConversation_InsideTheReadLoop_ShouldNotCrashTheTeardown()
    {
        LiteRtEngineOptions? options = Options();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var chat = engine.CreateConversation(new LiteRtConversationOptions { Sampler = Argmax, MaxOutputTokens = 64 });
        await foreach (LiteRtStreamChunk _ in chat.SendStreamingAsync("Write one short sentence about the sea."))
        {
            chat.Dispose();
            break;
        }

        // Reaching this point is most of the test: a call into the deleted conversation would take the process down.
        using var fresh = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 16 });
        Assert.False(string.IsNullOrWhiteSpace(fresh.Send("Reply with one short word: hello").Text));
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

        Assert.False(chat.IsCancelled);
        using var clone = chat.Clone();
        string cat = clone.Send("What is my cat called? One word.").Text ?? "";
        Assert.Contains("Miso", cat, StringComparison.OrdinalIgnoreCase);
    }
}
