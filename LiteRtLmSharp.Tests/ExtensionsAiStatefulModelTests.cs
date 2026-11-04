using System.Text.Json;
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

    private const string CountPrompt = "Count upward starting from 400, one number per line, and never stop.";

    /// <summary>
    /// A cancelled continuation keeps its <c>ConversationId</c>: the client rolls the conversation back to the
    /// checkpoint it took before the turn, so the next request on the same id continues from the turn before the
    /// cut. This is what Agent Framework relies on, since it keeps the session's id after a failed run.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_CancelledContinuation_KeepsTheIdValid()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());
        var greedy = new ChatOptions { TopK = 1, MaxOutputTokens = 32 };

        ChatResponse first = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is TANGERINE. Reply OK.")], greedy);
        string id = first.ConversationId!;

        // A blocking continuation cancelled mid-generation.
        int beforeTurn = client.Store!.Get(id)!.Conversation.TokenCount;
        await CutTurnAsync(client, id);
        Assert.Equal(beforeTurn, client.Store!.Get(id)!.Conversation.TokenCount);

        ChatResponse recall = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is my code word? One word.")], new ChatOptions { TopK = 1, MaxOutputTokens = 32, ConversationId = id });
        Assert.Equal(id, recall.ConversationId);
        Assert.Contains("TANGERINE", recall.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CutTurnAsync(LiteRtChatClient client, string id, string prompt = CountPrompt)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions { TopK = 1, MaxOutputTokens = 900, ConversationId = id }, cts.Token));
    }

    /// <summary>
    /// The sequence the connector runs in a real session: a rollback disposes the consumed original at once, so
    /// the checkpoint must keep its state while another conversation generates, through a second cut on the same
    /// id, and through a fork.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_RolledBackConversation_KeepsItsState_AcrossOtherConversationsASecondCutAndAFork()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());
        var branching = (LiteRtConversationBranching)client.GetService(typeof(LiteRtConversationBranching))!;
        var greedy = new ChatOptions { TopK = 1, MaxOutputTokens = 32 };

        string a = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is TANGERINE. Reply OK.")], greedy)).ConversationId!;
        string b = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is COBALT. Reply OK.")], greedy)).ConversationId!;

        async Task<string> Ask(string id, string question) => (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, question)],
            new ChatOptions { TopK = 1, MaxOutputTokens = 32, ConversationId = id })).Text;

        // Each send on a checkpoint words its message differently from what the conversations it shares state with
        // already sent: on the GPU backend (LiteRT-LM v0.18.0), a first send on a clone that repeats such a message
        // word for word, after another conversation generated, gets a wrong reply.
        await CutTurnAsync(client, a);
        Assert.Contains("COBALT", await Ask(b, "What is my code word? One word."), StringComparison.OrdinalIgnoreCase);
        await CutTurnAsync(client, a, "Count upward starting from 700, one number per line, and never stop.");
        string fork = await branching.ForkAsync(a);
        Assert.Contains("COBALT", await Ask(b, "Repeat my code word. One word."), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TANGERINE", await Ask(a, "What is my code word? One word."), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TANGERINE", await Ask(fork, "Which code word did I give you? One word."), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-sending the cut message on the checkpoint, after another conversation generated, gets a real reply. On
    /// the GPU backend the runtime answers a checkpoint's first send that repeats the message word for word with
    /// a wrong or empty reply, so the client changes the repeated message, also when the retry is cut again.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_ResendingACutMessage_GetsARealReply_AfterAnotherConversationGenerated()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());
        var greedy = new ChatOptions { TopK = 1, MaxOutputTokens = 32 };

        string a = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is TANGERINE. Reply OK.")], greedy)).ConversationId!;
        string b = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is COBALT. Reply OK.")], greedy)).ConversationId!;

        await CutTurnAsync(client, a);
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is my code word? One word.")],
            new ChatOptions { TopK = 1, MaxOutputTokens = 32, ConversationId = b });
        await CutTurnAsync(client, a);
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Repeat my code word. One word.")],
            new ChatOptions { TopK = 1, MaxOutputTokens = 32, ConversationId = b });

        ChatResponse retry = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, CountPrompt)],
            new ChatOptions { TopK = 1, MaxOutputTokens = 32, ConversationId = a });
        Assert.Contains("400", retry.Text, StringComparison.Ordinal);
        Assert.Contains("401", retry.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A first call has nothing to roll back to: an abandoned first stream evicts the id its updates already
    /// carried, and resuming that id says so.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_AbandonedFirstStream_EvictsTheIdItsUpdatesCarried()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());

        string? id = null;
        int updates = 0;
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, CountPrompt)], new ChatOptions { TopK = 1, MaxOutputTokens = 900 }))
        {
            id = update.ConversationId;
            if (++updates == 10)
                break;
        }

        Assert.Equal(10, updates);
        Assert.NotNull(id);
        var gone = await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { MaxOutputTokens = 8, ConversationId = id }));
        Assert.Contains("started the conversation", gone.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream the consumer stops reading after the native generation ended is a completed turn, not a cut: the
    /// conversation is kept as it is, with the turn in it, instead of being rolled back.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_StreamLeftAfterTheReplyEnded_KeepsTheTurn()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());

        string id = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is OBSIDIAN. Reply OK.")],
            new ChatOptions { TopK = 1, MaxOutputTokens = 32 })).ConversationId!;
        LiteRtConversation live = client.Store!.Get(id)!.Conversation;
        int beforeTurn = live.TokenCount;

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Write one short sentence about the sea.")],
            new ChatOptions { TopK = 1, MaxOutputTokens = 48, ConversationId = id }))
        {
            // Creating a conversation waits for the generation in flight, so the reply has ended after this.
            using LiteRtConversation waited = engine.CreateConversation();
            break;
        }

        LiteRtConversationStore.Entry entry = client.Store!.Get(id)!;
        Assert.Same(live, entry.Conversation);
        Assert.False(live.IsCancelled);
        Assert.Null(entry.PendingTurn);
        Assert.True(live.TokenCount > beforeTurn, "The completed turn must stay in the conversation.");
    }

    /// <summary>
    /// A conversation with constrained decoding takes no checkpoint (cloning it rebuilds its constraints, about a
    /// second each), so a cut on a continuation evicts its id, as a backend that cannot clone does.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_ConstrainedConversation_IsEvictedByACut()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(
            engine,
            optionsTemplate: new LiteRtConversationOptions { EnableConstrainedDecoding = true },
            statefulConversations: new LiteRtStatefulConversationOptions());

        string id = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Reply with one short word: hello")],
            new ChatOptions { TopK = 1, MaxOutputTokens = 16 })).ConversationId!;
        Assert.True(client.Store!.Get(id)!.CloneIsSlow);

        await CutTurnAsync(client, id);

        var gone = await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { MaxOutputTokens = 8, ConversationId = id }));
        Assert.Contains("CheckpointConstrainedConversations", gone.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With <see cref="LiteRtStatefulConversationOptions.CheckpointConstrainedConversations"/>, a cut on a
    /// constrained conversation rolls back like any other, and the conversation still knows the earlier turn.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_ConstrainedConversation_KeepsItsId_WhenCheckpointsAreOn()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(
            engine,
            optionsTemplate: new LiteRtConversationOptions { EnableConstrainedDecoding = true },
            statefulConversations: new LiteRtStatefulConversationOptions { CheckpointConstrainedConversations = true });

        string id = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is OBSIDIAN. Reply OK.")],
            new ChatOptions { TopK = 1, MaxOutputTokens = 16 })).ConversationId!;
        int beforeTurn = client.Store!.Get(id)!.Conversation.TokenCount;

        await CutTurnAsync(client, id);

        Assert.Equal(beforeTurn, client.Store!.Get(id)!.Conversation.TokenCount);
        ChatResponse recall = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is my code word? One word.")],
            new ChatOptions { TopK = 1, MaxOutputTokens = 32, ConversationId = id });
        Assert.Contains("OBSIDIAN", recall.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static AIFunction WeatherTool() => AIFunctionFactory.Create(
        (string city) => $"22 degrees and sunny in {city}",
        name: "get_weather", description: "Gets the current weather for a given city.");

    private static ChatMessage ToolResult(FunctionCallContent call) => new(ChatRole.Tool, (IList<AIContent>)
        [new FunctionResultContent(call.CallId!, "22 degrees and sunny in Paris")]);

    private const string WeatherThenEssay =
        "What is the weather in Paris? Use the get_weather tool, and with its result write a long essay about the weather in Paris.";

    /// <summary>
    /// The rollback is per turn: a turn that ended in a tool call stays pending until the reply without tool
    /// calls, and a cancelled tool round discards the whole turn, its question and tool call included, which is
    /// what Agent Framework keeps after a failed run.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_CancelledToolRound_RollsBackTheWholeTurn()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (with a version-matched native binary) to run tool tests.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());

        // Tools are fixed when the conversation is created. No constrained decoding, so a checkpoint is taken.
        string id = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is JUNIPER. Reply OK.")],
            new ChatOptions { Tools = [WeatherTool()], TopK = 1, MaxOutputTokens = 32 })).ConversationId!;
        LiteRtConversationStore.Entry entry = client.Store!.Get(id)!;
        int beforeTurn = entry.Conversation.TokenCount;

        var round = new ChatOptions { TopK = 1, MaxOutputTokens = 512, ConversationId = id };
        ChatResponse call = await client.GetResponseAsync([new ChatMessage(ChatRole.User, WeatherThenEssay)], round);
        FunctionCallContent? fcc = call.Messages[^1].Contents.OfType<FunctionCallContent>().FirstOrDefault();
        Skip.If(fcc is null, "The model answered without calling the tool on this platform.");
        Assert.NotNull(entry.PendingTurn?.Checkpoint);

        await CutToolRoundAsync(client, fcc, round);

        Assert.Null(entry.PendingTurn);
        Assert.DoesNotContain(fcc.CallId!, entry.CallIdToName.Keys);
        Assert.Equal(beforeTurn, entry.Conversation.TokenCount);
        Assert.False(entry.Conversation.IsCancelled);

        // Results for the discarded call would reach a conversation that holds neither the question nor the call.
        var discarded = await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync([ToolResult(fcc)], round));
        Assert.Contains("discarded", discarded.Message, StringComparison.Ordinal);

        ChatResponse recall = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is my code word? One word.")],
            new ChatOptions { TopK = 1, MaxOutputTokens = 32, ConversationId = id });
        Assert.Contains("JUNIPER", recall.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A tool round that completes ends the turn and releases its checkpoint; a tool round of the call that
    /// started the conversation has nothing to roll back to, so cutting it evicts the id, which the caller of a
    /// failed run never received.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_ToolRounds_ReleaseTheCheckpointWhenTheTurnEnds_AndEvictAFirstCallsTurnWhenCut()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (with a version-matched native binary) to run tool tests.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());
        var first = new ChatOptions { Tools = [WeatherTool()], TopK = 1, MaxOutputTokens = 512 };

        ChatResponse call = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the weather in Paris? Use the get_weather tool.")], first);
        string completed = call.ConversationId!;
        FunctionCallContent? fcc = call.Messages[^1].Contents.OfType<FunctionCallContent>().FirstOrDefault();
        Skip.If(fcc is null, "The model answered without calling the tool on this platform.");
        LiteRtConversationStore.Entry entry = client.Store!.Get(completed)!;
        Assert.NotNull(entry.PendingTurn);
        Assert.Null(entry.PendingTurn!.Checkpoint);   // the turn created the conversation

        ChatResponse answer = await client.GetResponseAsync([ToolResult(fcc)], new ChatOptions { TopK = 1, MaxOutputTokens = 64, ConversationId = completed });
        Skip.If(answer.Messages[^1].Contents.OfType<FunctionCallContent>().Any(), "The model called the tool again on this platform.");
        Assert.Null(entry.PendingTurn);

        call = await client.GetResponseAsync([new ChatMessage(ChatRole.User, WeatherThenEssay)], first);
        string cut = call.ConversationId!;
        fcc = call.Messages[^1].Contents.OfType<FunctionCallContent>().FirstOrDefault();
        Skip.If(fcc is null, "The model answered without calling the tool on this platform.");
        await CutToolRoundAsync(client, fcc, new ChatOptions { TopK = 1, MaxOutputTokens = 512, ConversationId = cut });

        Assert.Null(client.Store!.Get(cut));
    }

    private static async Task CutToolRoundAsync(LiteRtChatClient client, FunctionCallContent call, ChatOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await client.GetResponseAsync([ToolResult(call)], options, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Skip.If(true, "The tool round ended before the cut on this platform.");
    }

    /// <summary>
    /// A stream stopped at its tool call keeps the turn, because the update arrives after the native generation
    /// ended: the conversation is unchanged, the turn stays pending, and the results can still be sent.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_StreamStoppedAtItsToolCall_KeepsTheTurn()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (with a version-matched native binary) to run tool tests.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());

        string id = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is JUNIPER. Reply OK.")],
            new ChatOptions { Tools = [WeatherTool()], TopK = 1, MaxOutputTokens = 32 })).ConversationId!;
        LiteRtConversationStore.Entry entry = client.Store!.Get(id)!;
        LiteRtConversation live = entry.Conversation;

        var round = new ChatOptions { TopK = 1, MaxOutputTokens = 256, ConversationId = id };
        FunctionCallContent? fcc = null;
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the weather in Paris? Use the get_weather tool.")], round))
        {
            fcc = update.Contents.OfType<FunctionCallContent>().FirstOrDefault();
            if (fcc is not null)
                break;
        }
        Skip.If(fcc is null, "The model answered without calling the tool on this platform.");

        Assert.Same(live, entry.Conversation);
        Assert.False(live.IsCancelled);
        Assert.NotNull(entry.PendingTurn);
        ChatResponse answer = await client.GetResponseAsync([ToolResult(fcc)], round);
        Assert.Equal(id, answer.ConversationId);
    }

    /// <summary>
    /// A run that ended between sends, for example cancelled while a tool ran, leaves its turn pending. Sending the
    /// same message again discards that turn, so the question is not repeated after an unanswered tool call.
    /// </summary>
    [SkippableFact]
    public async Task Stateful_RetryingAMessage_WhoseToolCallWentUnanswered_RollsBackItsTurn()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (with a version-matched native binary) to run tool tests.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());

        string id = (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is JUNIPER. Reply OK.")],
            new ChatOptions { Tools = [WeatherTool()], TopK = 1, MaxOutputTokens = 32 })).ConversationId!;
        LiteRtConversationStore.Entry entry = client.Store!.Get(id)!;
        int beforeTurn = entry.Conversation.TokenCount;

        const string question = "What is the weather in Paris? Use the get_weather tool.";
        var round = new ChatOptions { TopK = 1, MaxOutputTokens = 256, ConversationId = id };
        ChatResponse call = await client.GetResponseAsync([new ChatMessage(ChatRole.User, question)], round);
        Skip.If(!call.Messages[^1].Contents.OfType<FunctionCallContent>().Any(), "The model answered without calling the tool on this platform.");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, question)], new ChatOptions { TopK = 1, MaxOutputTokens = 1, ConversationId = id });

        // The retry started from the state before the first attempt, so with a one-token reply the conversation
        // holds less than the first attempt did, its tool call included. Without the rollback it would hold more.
        int afterRetry = entry.Conversation.TokenCount;
        Assert.True(afterRetry < call.Usage!.TotalTokenCount,
            $"Expected the first attempt to be gone: {beforeTurn} before the turn, {call.Usage.TotalTokenCount} after it, {afterRetry} after the retry.");
    }

    /// <summary>The streaming counterpart: a continuation stream the consumer abandons rolls back too.</summary>
    [SkippableFact]
    public async Task Stateful_AbandonedContinuationStream_KeepsTheIdValid()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());
        var greedy = new ChatOptions { TopK = 1, MaxOutputTokens = 32 };

        ChatResponse first = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember: my code word is OBSIDIAN. Reply OK.")], greedy);
        string id = first.ConversationId!;

        int updates = 0;
        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, CountPrompt)],
            new ChatOptions { TopK = 1, MaxOutputTokens = 900, ConversationId = id }))
        {
            if (++updates == 10)
                break;   // the consumer gives up: the client cancels the native stream
        }

        ChatResponse recall = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is my code word? One word.")], new ChatOptions { TopK = 1, MaxOutputTokens = 32, ConversationId = id });
        Assert.Equal(10, updates);
        Assert.Equal(id, recall.ConversationId);
        Assert.Contains("OBSIDIAN", recall.Text, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>LRU eviction at the cap: with <c>MaxLiveConversations = 1</c>, starting a second conversation
    /// (a call without a <c>ConversationId</c>) evicts and disposes the first, so resuming the first id throws
    /// while the second remains resumable. (Also the former hard-wired contract of the parked single-conversation
    /// era, now reachable only by choosing capacity 1.)</summary>
    [SkippableFact]
    public async Task Stateful_SecondConversationBeyondCap_EvictsFirst_OldIdThrows()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine,
            statefulConversations: new LiteRtStatefulConversationOptions { MaxLiveConversations = 1 });
        var options = new ChatOptions { MaxOutputTokens = 16 };

        ChatResponse first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Remember A.")], options);
        ChatResponse second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Remember B.")], options);
        Assert.False(string.IsNullOrEmpty(first.ConversationId));
        Assert.NotEqual(first.ConversationId, second.ConversationId);

        // Capacity 1: starting the second conversation evicted the first — resuming it now throws.
        var resumeFirst = new ChatOptions { MaxOutputTokens = 16, ConversationId = first.ConversationId };
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "what did I say?")], resumeFirst));

        // The most-recently-started conversation is still live and resumable.
        var resumeSecond = new ChatOptions { MaxOutputTokens = 16, ConversationId = second.ConversationId };
        ChatResponse ok = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "ok?")], resumeSecond);
        Assert.Equal(second.ConversationId, ok.ConversationId);

        // And at the DEFAULT capacity (8), a second conversation does NOT evict the first — both stay live.
        using var roomy = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());
        ChatResponse a = await roomy.GetResponseAsync([new ChatMessage(ChatRole.User, "Remember A.")], options);
        ChatResponse b = await roomy.GetResponseAsync([new ChatMessage(ChatRole.User, "Remember B.")], options);
        var resumeA = new ChatOptions { MaxOutputTokens = 16, ConversationId = a.ConversationId };
        ChatResponse aStillLive = await roomy.GetResponseAsync([new ChatMessage(ChatRole.User, "ok?")], resumeA);
        Assert.Equal(a.ConversationId, aStillLive.ConversationId);
    }

    /// <summary>A JSON-schema ResponseFormat arriving only on a CONTINUATION of a conversation created
    /// without one must be rejected in MEAI vocabulary (ArgumentException naming ResponseFormat) and —
    /// critically — must NOT destroy the live conversation: nothing native ran, so the id stays
    /// resumable. (The regression this pins: the client's eviction catch-all used to dispose the
    /// healthy conversation on this purely-managed validation failure.)</summary>
    [SkippableFact]
    public async Task Stateful_SchemaOnProviderlessContinuation_ThrowsAndConversationSurvives()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());
        var options = new ChatOptions { MaxOutputTokens = 16 };

        ChatResponse first = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember that my favorite color is teal. Acknowledge briefly.")], options);
        string id = first.ConversationId!;

        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","required":["color"]}""");
        var withSchema = new ChatOptions
        {
            MaxOutputTokens = 32,
            ConversationId = id,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement),
        };
        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "What is my color? As JSON.")], withSchema));
        Assert.Contains("ResponseFormat", ex.Message, StringComparison.Ordinal);

        // The conversation must still be live and hold its state.
        var resume = new ChatOptions { MaxOutputTokens = 32, ConversationId = id };
        ChatResponse after = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is my favorite color? Answer with just the color.")], resume);
        Assert.Contains("teal", after.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The documented two-phase pattern for tools + structured output actually works: a
    /// tools+schema request throws (pinned elsewhere), and the RECOMMENDED alternative — run the tool
    /// phase without the ResponseFormat, then ask for the schema-formatted answer — succeeds. In
    /// stateful mode the schema continuation needs the provider armed from creation, which is what
    /// the client's options template is for: with ConstraintProvider on the template, the tools
    /// conversation is schema-capable, the tool loop runs unconstrained, and the follow-up
    /// schema-constrained continuation returns conforming JSON informed by the tool result.</summary>
    [SkippableFact]
    public async Task Stateful_ToolPhaseThenSchemaContinuation_TwoPhasePatternWorks()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (with a version-matched native binary) to run tool tests.");

        using var engine = LiteRtEngine.Load(Options());

        int invocations = 0;
        AIFunction weather = AIFunctionFactory.Create(
            (string city) => { invocations++; return $"22 degrees and sunny in {city}"; },
            name: "get_weather", description: "Gets the current weather for a given city.");

        // The template arms the provider on every conversation, so the schema continuation below can
        // be enforced. NOTE: no EnableConstrainedDecoding here — provider and the tool-calling
        // constrained mode are mutually exclusive; tools still work without it.
        using IChatClient client = new LiteRtChatClient(engine,
                optionsTemplate: new LiteRtConversationOptions { ConstraintProvider = LiteRtConstraintProvider.LlGuidance },
                statefulConversations: new LiteRtStatefulConversationOptions())
            .AsBuilder().UseFunctionInvocation().Build();

        // Phase 1: the tool loop, unconstrained.
        ChatResponse toolPhase = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the weather in Paris? Use the get_weather tool.")],
            new ChatOptions { Tools = [weather], MaxOutputTokens = 256 });
        Assert.True(invocations >= 1, "Expected the model to call the get_weather tool.");
        Assert.False(string.IsNullOrEmpty(toolPhase.ConversationId));

        // Phase 2: schema-formatted answer on the SAME conversation (no tools on this request).
        using JsonDocument schema = JsonDocument.Parse(
            """{"type":"object","properties":{"city":{"type":"string"},"conditions":{"type":"string"}},"required":["city","conditions"],"additionalProperties":false}""");
        ChatResponse formatted = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Summarize that weather report as JSON.")],
            new ChatOptions
            {
                MaxOutputTokens = 64,
                ConversationId = toolPhase.ConversationId,
                ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement),
            });

        using JsonDocument parsed = JsonDocument.Parse(formatted.Text.Trim());
        Assert.Equal(JsonValueKind.String, parsed.RootElement.GetProperty("city").ValueKind);
        Assert.Equal(JsonValueKind.String, parsed.RootElement.GetProperty("conditions").ValueKind);
    }

    /// <summary>A fork of a schema-armed conversation must keep enforcing schemas on the branch: the
    /// native clone recreates the constraint provider, and the managed layer must carry the flag
    /// (both the core Clone() and the store entry the client tracks). Pins the review finding where
    /// the flag was dropped and every constrained send on a branch threw.</summary>
    [SkippableFact]
    public async Task Fork_OfSchemaArmedConversation_EnforcesSchemaOnBranch()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());
        using JsonDocument schema = JsonDocument.Parse(
            """{"type":"object","properties":{"color":{"type":"string"}},"required":["color"],"additionalProperties":false}""");

        // First call carries the schema → the provider is armed on the conversation.
        var schemaOptions = new ChatOptions
        {
            MaxOutputTokens = 48,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement),
        };
        ChatResponse seeded = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "My favorite color is teal. Reply as JSON with the color.")], schemaOptions);
        using (JsonDocument parsed = JsonDocument.Parse(seeded.Text.Trim()))
            Assert.True(parsed.RootElement.TryGetProperty("color", out _));

        var branching = (LiteRtConversationBranching)client.GetService(typeof(LiteRtConversationBranching))!;
        string branchId = await branching.ForkAsync(seeded.ConversationId!);

        // The branch must accept and enforce a schema-constrained continuation.
        var branchOptions = new ChatOptions
        {
            MaxOutputTokens = 48,
            ConversationId = branchId,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement),
        };
        ChatResponse branchReply = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Repeat my color as JSON.")], branchOptions);
        using JsonDocument branchParsed = JsonDocument.Parse(branchReply.Text.Trim());
        Assert.True(branchParsed.RootElement.TryGetProperty("color", out _));
    }

    // ─────────────────────── Forking + multi-conversation ───────────────────────
    //
    // These tests exercise the multi-live-conversation / forking surface, unlocked with the v0.15.0
    // repin: the native runtime now preserves a suspended conversation's state when another advances
    // (verified by the Upstream_..._Sentinel canary below and the interleave recall tests here). They
    // were parked behind LITERTLM_TEST_MULTICONV=1 from 2026-07-10 until that fix shipped.

    /// <summary><c>GetService(typeof(LiteRtConversationBranching))</c> returns a branching instance in the
    /// stateful mode and <c>null</c> in the stateless mode; the standard <see cref="ChatClientMetadata"/> and
    /// self lookups are unchanged in both.</summary>
    [SkippableFact]
    public async Task Fork_GetService_ReturnsBranchingOnlyInStatefulMode()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        await Task.CompletedTask;

        using var engine = LiteRtEngine.Load(Options());

        // Stateless (default): no live conversations, so no branching service.
        using (var stateless = new LiteRtChatClient(engine))
        {
            Assert.Null(stateless.GetService(typeof(LiteRtConversationBranching)));
            // Existing GetService behaviors are intact.
            Assert.NotNull(stateless.GetService(typeof(ChatClientMetadata)));
            Assert.Same(stateless, stateless.GetService(typeof(IChatClient)));
        }

        // Stateful: the branching hatch is available.
        using (var stateful = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions()))
        {
            object? svc = stateful.GetService(typeof(LiteRtConversationBranching));
            Assert.IsType<LiteRtConversationBranching>(svc);
            // A keyed lookup still returns null, and the metadata/self lookups are unchanged.
            Assert.Null(stateful.GetService(typeof(LiteRtConversationBranching), serviceKey: "k"));
            Assert.NotNull(stateful.GetService(typeof(ChatClientMetadata)));
            Assert.Same(stateful, stateful.GetService(typeof(IChatClient)));
        }
    }

    /// <summary>Forking a <c>ConversationId</c> the client never issued throws <see cref="ArgumentException"/>
    /// with the "no live conversation" wording (the same failure a continuation of an unknown id gives).</summary>
    [SkippableFact]
    public async Task Fork_UnknownId_Throws()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions());
        var branching = (LiteRtConversationBranching)client.GetService(typeof(LiteRtConversationBranching))!;

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(
            () => branching.ForkAsync("does-not-exist"));
        Assert.Contains("No live conversation", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does-not-exist", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// DIVERGENCE: seed a fact on the base, fork it, then tell the base ("branch A") a second fact and query the
    /// fork ("branch B"). The fork recalls the pre-fork fact but not branch A's post-fork fact, and the base is
    /// unaffected (it holds both facts). Proves the branch is independent native state sharing only the prefix.
    /// </summary>
    [SkippableFact]
    public async Task Fork_Divergence_BranchKnowsSeededFact_NotPostForkFact_ParentUnaffected()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        // Base + branch must be live at once, above the production single-conversation cap — raise the
        // live-conversation cap via the public knob.
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions { MaxLiveConversations = 4 });
        var branching = (LiteRtConversationBranching)client.GetService(typeof(LiteRtConversationBranching))!;

        // Seed the shared context on the base conversation.
        var seed = new List<ChatMessage>
        {
            new(ChatRole.System, "You are concise. Answer in one short sentence."),
            new(ChatRole.User, "Remember two facts as I give them. Fact one: my favorite color is teal. Acknowledge briefly."),
        };
        ChatResponse baseSeed = await client.GetResponseAsync(seed, new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64 });
        string baseId = baseSeed.ConversationId!;
        Assert.False(string.IsNullOrEmpty(baseId));

        // Fork the base BEFORE the second fact — the branch shares only the teal fact.
        string branchId = await branching.ForkAsync(baseId);
        Assert.NotEqual(baseId, branchId);

        // Add a second fact to the BASE only (branch A).
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Fact two: my lucky number is 42. Acknowledge briefly.")],
            new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64, ConversationId = baseId });

        // Branch B knows the seeded fact...
        ChatResponse branchColor = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is my favorite color? Answer with just the color.")],
            new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64, ConversationId = branchId });
        Assert.Contains("teal", branchColor.Text, StringComparison.OrdinalIgnoreCase);

        // ...but NOT the post-fork fact that was only told to the base.
        ChatResponse branchNumber = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is my lucky number? If I never told you, say you don't know.")],
            new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64, ConversationId = branchId });
        Assert.DoesNotContain("42", branchNumber.Text, StringComparison.Ordinal);

        // The parent (base) is unaffected — it holds BOTH facts.
        ChatResponse baseNumber = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is my lucky number? Answer with just the number.")],
            new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64, ConversationId = baseId });
        Assert.Contains("42", baseNumber.Text, StringComparison.Ordinal);
    }

    /// <summary>A fork counts toward the live-conversation cache capacity: with a capacity of 2 (via the
    /// internal test seam), holding a base + one fork and then forking again evicts the least-recently-used
    /// conversation (the first fork), whose id then no longer resumes, while the base and the newest fork remain
    /// live.</summary>
    [SkippableFact]
    public async Task Fork_CountsTowardEviction()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        // Capacity exactly 2 (via the internal seam) so the third live conversation evicts the LRU — the
        // parked multi-conversation machinery under test.
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions { MaxLiveConversations = 2 });
        var branching = (LiteRtConversationBranching)client.GetService(typeof(LiteRtConversationBranching))!;

        // Base conversation (1 live).
        ChatResponse baseResp = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Remember the base.")], new ChatOptions { MaxOutputTokens = 16 });
        string baseId = baseResp.ConversationId!;

        // First fork (2 live: base + fork1). Looking up base to fork it promotes it to most-recently-used, so
        // the just-added fork1 is MRU and base is next; fork1 is now the least-recently-used.
        string fork1 = await branching.ForkAsync(baseId);

        // Second fork of base (would be 3 live): forking looks up base (promoting it), then adds fork2, which
        // pushes the store over the cap and evicts the LRU — fork1.
        string fork2 = await branching.ForkAsync(baseId);

        // fork1 was evicted: resuming it throws.
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "still there?")],
            new ChatOptions { MaxOutputTokens = 16, ConversationId = fork1 }));

        // The base and the newest fork are still live and resumable.
        ChatResponse okBase = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "ok?")], new ChatOptions { MaxOutputTokens = 16, ConversationId = baseId });
        Assert.Equal(baseId, okBase.ConversationId);
        ChatResponse okFork2 = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "ok?")], new ChatOptions { MaxOutputTokens = 16, ConversationId = fork2 });
        Assert.Equal(fork2, okFork2.ConversationId);
    }

    /// <summary>
    /// A function-calling continuation works on a FORK: fork after the model has emitted a tool call, then send
    /// the tool result on the fork's id. The branch resolves the tool name from the parent's copied
    /// call-id → name map (the assistant tool-call turn is not resent on a continuation), so the model answers.
    /// </summary>
    [SkippableFact]
    public async Task Fork_ToolContinuation_WorksOnBranch()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (with a version-matched native binary) to run tool tests.");

        using var engine = LiteRtEngine.Load(Options());
        // Base + fork live at once, above the production single-conversation cap — internal capacity seam.
        using var client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions { MaxLiveConversations = 4 });
        var branching = (LiteRtConversationBranching)client.GetService(typeof(LiteRtConversationBranching))!;

        AIFunction weather = AIFunctionFactory.Create(
            (string city) => $"22 degrees and sunny in {city}",
            name: "get_weather", description: "Gets the current weather for a given city.");
        var toolOptions = new LiteRtChatOptions
        {
            Tools = [weather],
            MaxOutputTokens = 256,
            EnableConstrainedDecoding = true,
        };

        // Turn 1 on the base: drive the tool call MANUALLY (no UseFunctionInvocation) so we can fork between the
        // tool call and its result. The client records the synthesized call-id → name on the base's entry.
        ChatResponse call = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What is the weather in Paris? Use the get_weather tool.")], toolOptions);
        string baseId = call.ConversationId!;
        FunctionCallContent fcc = Assert.Single(call.Messages[^1].Contents.OfType<FunctionCallContent>());

        // Fork after the tool-call turn — the parent's call-id → name map is copied onto the branch.
        string branchId = await branching.ForkAsync(baseId);

        // Send the tool RESULT on the FORK's id. SplitContinuation must resolve "get_weather" from the copied
        // map (the assistant tool-call turn is not resent), and the model produces a final answer.
        var toolResult = new ChatMessage(ChatRole.Tool, (IList<AIContent>)
            [new FunctionResultContent(fcc.CallId!, "22 degrees and sunny in Paris")]);
        ChatResponse answer = await client.GetResponseAsync(
            [toolResult], new ChatOptions { MaxOutputTokens = 256, ConversationId = branchId });

        Assert.Equal(branchId, answer.ConversationId);
        Assert.False(string.IsNullOrWhiteSpace(answer.Text), "Expected a final answer after the tool result on the fork.");
    }
}
