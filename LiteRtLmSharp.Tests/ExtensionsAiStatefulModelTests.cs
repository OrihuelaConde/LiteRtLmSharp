using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-backed end-to-end tests for the opt-in stateful-conversations mode of the Microsoft.Extensions.AI
/// <see cref="IChatClient"/> connector (<see cref="LiteRtStatefulConversationOptions"/>). Gated on
/// <c>LITERTLM_TEST_MODEL</c> (and optionally <c>LITERTLM_TEST_BACKEND</c>, default "cpu"); skipped otherwise.
/// Each test loads and disposes its own engine — assembly parallelization is disabled (AssemblyInfo.cs), so
/// they run serially and never have two engines alive at once.
/// </summary>
public sealed class ExtensionsAiStatefulModelTests
{
    private static string? Model => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
    private static string Backend => Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu";

    private static LiteRtEngineOptions Options(bool benchmark = false) => new()
    {
        ModelPath = Model!,
        Backend = LiteRtBackend.Parse(Backend),
        MaxNumTokens = 2048,
        EnableBenchmark = benchmark,
    };

    /// <summary>
    /// The canonical MEAI stateful loop: turn 1 (no <c>ConversationId</c>) returns an id; turn 2 sets that id
    /// and sends ONLY the new question. The reply recalls a fact from turn 1 without it being resent, and the
    /// continuation's prefill is far below a stateless full-history re-prefill (proving it is incremental).
    /// </summary>
    [SkippableFact]
    public async Task Stateful_MultiTurn_IsIncremental_AndRecallsTurnOneContext()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        // EnableBenchmark exposes the per-turn prefill (InputTokenCount) so we can compare re-prefill sizes.
        long statefulTurn2Prefill;
        string colorAnswer;
        string? firstId, secondId;
        using (var engine = LiteRtEngine.Load(Options(benchmark: true)))
        using (var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions()))
        {
            var turn1 = new List<ChatMessage>
            {
                new(ChatRole.System, "You are concise. Answer in one short sentence."),
                new(ChatRole.User, "Remember that my favorite color is teal. Acknowledge briefly."),
            };
            ChatResponse r1 = await client.GetResponseAsync(turn1, new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64 });
            firstId = r1.ConversationId;
            Assert.False(string.IsNullOrEmpty(firstId), "A stateful provider must return a ConversationId on the first turn.");

            // Reuse the id and send only the new user message (per the MEAI stateful contract).
            var turn2 = new List<ChatMessage> { new(ChatRole.User, "What is my favorite color? Answer with just the color.") };
            var turn2Options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64, ConversationId = firstId };
            ChatResponse r2 = await client.GetResponseAsync(turn2, turn2Options);

            secondId = r2.ConversationId;
            colorAnswer = r2.Text;
            statefulTurn2Prefill = r2.Usage!.InputTokenCount!.Value;
        }

        Assert.Equal(firstId, secondId);   // the id is fixed for the lifetime of the conversation
        // Recall WITHOUT resending turn 1: the fact lives in the live native conversation's KV cache.
        Assert.Contains("teal", colorAnswer, StringComparison.OrdinalIgnoreCase);

        // Stateless A/B: the equivalent turn 2 re-prefills the whole thread, so its prefill is far larger.
        long statelessTurn2Prefill;
        using (var engine = LiteRtEngine.Load(Options(benchmark: true)))
        using (var client = new LiteRtChatClient(engine))   // stateless (default)
        {
            var full = new List<ChatMessage>
            {
                new(ChatRole.System, "You are concise. Answer in one short sentence."),
                new(ChatRole.User, "Remember that my favorite color is teal. Acknowledge briefly."),
                new(ChatRole.Assistant, "Got it — teal."),
                new(ChatRole.User, "What is my favorite color? Answer with just the color."),
            };
            ChatResponse rFull = await client.GetResponseAsync(full, new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64 });
            statelessTurn2Prefill = rFull.Usage!.InputTokenCount!.Value;
        }

        Assert.True(statefulTurn2Prefill < statelessTurn2Prefill,
            $"Expected the stateful continuation to prefill only the new question ({statefulTurn2Prefill} tokens), " +
            $"far below the stateless full-history re-prefill ({statelessTurn2Prefill} tokens).");
    }

    /// <summary>
    /// FunctionInvokingChatClient over a stateful client: the tool loop completes (the model calls the tool,
    /// its result is returned, the model answers) and the final response carries the ConversationId — the
    /// continuation after the tool round resumes the same live conversation incrementally.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_FunctionInvocation_CompletesToolLoop_AndCarriesConversationId()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(OperatingSystem.IsLinux(),
            "EnableConstrainedDecoding (recommended with tools) is blocked on linux-x64 — see docs.");
        Skip.If(Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (with a version-matched native binary) to run tool tests.");

        using var engine = LiteRtEngine.Load(Options());

        int invocations = 0;
        AIFunction weather = AIFunctionFactory.Create(
            (string city) => { invocations++; return $"22 degrees and sunny in {city}"; },
            name: "get_weather", description: "Gets the current weather for a given city.");

        // Stateful + UseFunctionInvocation: FICC detects the ConversationId and sends only the tool result on
        // the continuation, so the whole loop runs incrementally against one live conversation.
        using IChatClient client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions())
            .AsBuilder().UseFunctionInvocation().Build();
        var options = new LiteRtChatOptions
        {
            Tools = [weather],
            MaxOutputTokens = 256,
            EnableConstrainedDecoding = true,   // recommended when using tools
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What is the weather in Paris? Use the get_weather tool."),
        };

        ChatResponse response = await client.GetResponseAsync(messages, options);

        Assert.True(invocations >= 1, "Expected the model to call the get_weather tool.");
        Assert.False(string.IsNullOrWhiteSpace(response.Text), "Expected a final answer after the tool ran.");
        Assert.False(string.IsNullOrEmpty(response.ConversationId),
            "Expected the stateful response to carry a ConversationId.");
    }

    /// <summary>A request naming a ConversationId the client never issued throws (nothing to resume).</summary>
    [SkippableFact]
    public async Task Stateful_UnknownConversationId_Throws()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());

        var options = new ChatOptions { ConversationId = "does-not-exist", MaxOutputTokens = 16 };
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options));
    }

    /// <summary>With <c>MaxLiveConversations = 1</c>, creating a second conversation evicts (and disposes) the
    /// first; resuming the first id then throws, while the second remains resumable.</summary>
    [SkippableFact]
    public async Task Stateful_Eviction_BeyondCap_InvalidatesOldestConversationId()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(
            engine, statefulConversations: new LiteRtStatefulConversationOptions { MaxLiveConversations = 1 });
        var options = new ChatOptions { MaxOutputTokens = 16 };

        ChatResponse first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Remember A.")], options);
        ChatResponse second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Remember B.")], options);
        Assert.False(string.IsNullOrEmpty(first.ConversationId));
        Assert.NotEqual(first.ConversationId, second.ConversationId);

        // The cap is 1, so creating the second conversation evicted the first — resuming it now throws.
        var resumeFirst = new ChatOptions { MaxOutputTokens = 16, ConversationId = first.ConversationId };
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "what did I say?")], resumeFirst));

        // The most-recently-used conversation is still live and resumable.
        var resumeSecond = new ChatOptions { MaxOutputTokens = 16, ConversationId = second.ConversationId };
        ChatResponse ok = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "ok?")], resumeSecond);
        Assert.Equal(second.ConversationId, ok.ConversationId);
    }
}
