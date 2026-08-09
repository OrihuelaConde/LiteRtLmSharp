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
        Skip.If(OperatingSystem.IsLinux(),
            "EnableConstrainedDecoding (recommended with tools) is blocked on linux-x64 — see docs.");
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
