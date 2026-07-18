using LiteRtLmSharp;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Pure unit tests for the KV overflow guard's arithmetic (<see cref="LiteRtContextGuard"/>) — the
/// thresholds behind <see cref="LiteRtContextOverflowException"/>. No model needed, so these always
/// run in CI. The live end-to-end behavior (a conversation grown to the limit throws instead of
/// crashing the native runtime) is covered by <see cref="ContextOverflowModelTests"/>.
/// </summary>
public class ContextOverflowGuardTests
{
    // The full/not-full boundary is maxNumTokens - SafetyMargin, shared by the pre-send hard stop and
    // the public IsContextFull signal so the two can never disagree.
    private const int FullAt = 4096 - LiteRtContextGuard.SafetyMargin;

    [Theory]
    [InlineData(0, 4096)]           // fresh conversation
    [InlineData(FullAt - 1, 4096)]  // one below the shared ceiling — the budget stage decides, not the hard stop
    public void ThrowIfContextFull_UnderTheCeiling_DoesNotThrow(int used, int limit)
        => LiteRtContextGuard.ThrowIfContextFull(used, limit);

    [Theory]
    [InlineData(FullAt, 4096)]      // exactly at the ceiling — where a clamped send lands by construction
    [InlineData(4096, 4096)]        // at the hard limit
    [InlineData(4440, 4096)]        // past the limit — the exact field-reported state (crashed 0xC0000005 unguarded)
    public void ThrowIfContextFull_AtOrPastTheCeiling_Throws(int used, int limit)
    {
        var ex = Assert.Throws<LiteRtContextOverflowException>(
            () => LiteRtContextGuard.ThrowIfContextFull(used, limit));
        Assert.Equal(used, ex.TokenCount);
        Assert.Equal(limit, ex.MaxNumTokens);
    }

    /// <summary>An unknown limit (engine default, 0) keeps the guard off no matter the count.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ThrowIfContextFull_UnknownLimit_GuardIsOff(int limit)
        => LiteRtContextGuard.ThrowIfContextFull(tokenCount: 999_999, limit);

    /// <summary>The signal and the hard stop share one predicate: IsContextFull true ⇔ the next send throws.</summary>
    [Theory]
    [InlineData(0, 4096, false)]
    [InlineData(FullAt - 1, 4096, false)]
    [InlineData(FullAt, 4096, true)]
    [InlineData(4096, 4096, true)]
    [InlineData(4440, 4096, true)]
    [InlineData(999_999, 0, false)]   // unknown limit → guard off → never "full"
    public void IsContextFull_MatchesTheHardStopExactly(int used, int limit, bool expected)
    {
        Assert.Equal(expected, LiteRtContextGuard.IsContextFull(used, limit));
        bool threw = Record.Exception(() => LiteRtContextGuard.ThrowIfContextFull(used, limit)) is not null;
        Assert.Equal(expected, threw);
    }

    [Fact]
    public void DecodeBudget_IsRemainingContextMinusInputAndMargin()
        => Assert.Equal(
            4096 - 3000 - 500 - LiteRtContextGuard.SafetyMargin,
            LiteRtContextGuard.DecodeBudget(tokenCount: 3000, maxNumTokens: 4096, inputTokens: 500));

    [Fact]
    public void DecodeBudget_OfExactlyOneToken_IsAllowed()
        => Assert.Equal(1, LiteRtContextGuard.DecodeBudget(
            tokenCount: 0, maxNumTokens: 100 + LiteRtContextGuard.SafetyMargin + 1, inputTokens: 100));

    /// <summary>The message fits the cache but leaves no room to decode → reject before native code.
    /// This is the original overflow shape: a fat tool result landing on a nearly-full tool-loop conversation.</summary>
    [Fact]
    public void DecodeBudget_MessageLeavesNoRoomToReply_Throws()
    {
        var ex = Assert.Throws<LiteRtContextOverflowException>(
            () => LiteRtContextGuard.DecodeBudget(tokenCount: 3900, maxNumTokens: 4096, inputTokens: 300));
        Assert.Equal(3900, ex.TokenCount);
        Assert.Equal(4096, ex.MaxNumTokens);
    }

    /// <summary>Boundary: a budget of exactly 0 (input fills the remaining space to the margin) rejects.</summary>
    [Fact]
    public void DecodeBudget_OfZero_Throws()
        => Assert.Throws<LiteRtContextOverflowException>(() => LiteRtContextGuard.DecodeBudget(
            tokenCount: 0, maxNumTokens: 100 + LiteRtContextGuard.SafetyMargin, inputTokens: 100));

    [Theory]
    [InlineData(80, 0, 80)]     // no caller cap → the budget becomes the per-send cap
    [InlineData(80, 64, 64)]    // caller cap fits → honored unchanged
    [InlineData(80, 80, 80)]    // caller cap == budget → honored
    [InlineData(80, 512, 80)]   // caller cap exceeds the budget → clamped
    public void EffectiveOutputCap_TakesTheSmallerOfCallerCapAndBudget(int budget, int callerCap, int expected)
        => Assert.Equal(expected, LiteRtContextGuard.EffectiveOutputCap(budget, callerCap));

    /// <summary>The guard probes raw SendRaw JSON for media on every guarded send, so MessageHasMedia must
    /// tolerate every shape a raw caller can produce — valid JSON with a non-object root included
    /// (JsonElement.TryGetProperty throws InvalidOperationException on those, so the shape needs its own
    /// check, not just the JsonException catch).</summary>
    [Theory]
    [InlineData("[{\"role\":\"user\"}]")]   // array root — a raw caller batching messages
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void MessageHasMedia_ToleratesAnyRawShape(string messageJson)
        => Assert.False(LiteRtConversation.MessageHasMedia(messageJson));
}

/// <summary>
/// End-to-end regression tests for the KV-overflow crash (a stateful tool loop grew the live conversation past
/// MaxNumTokens = 4096 and the next call killed the process with 0xC0000005 / 0xC0000374 heap
/// corruption). With the guard, a conversation driven into its context limit throws
/// <see cref="LiteRtContextOverflowException"/> and the engine stays healthy. Uses its OWN small
/// engine (tiny context) rather than the shared fixture, so it loads/disposes per test.
/// Set LITERTLM_TEST_MODEL to a .litertlm file to run.
/// </summary>
public sealed class ContextOverflowModelTests
{
    // Small enough to fill within a few capped turns, but not below the model's compiled prefill
    // chunk: gemma-4-E2B rejects a 256- or 512-token KV outright (DYNAMIC_UPDATE_SLICE
    // "update > operand" at tensor allocation — the KV cache must fit at least one prefill chunk,
    // 1024 for this model).
    private const int SmallContext = 1024;


    private static LiteRtEngineOptions? SmallContextOptions()
    {
        string? modelPath = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            return null;
        LiteRtEngine.SetMinLogLevel(3);
        return new LiteRtEngineOptions
        {
            ModelPath = modelPath,
            Backend = LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu"),
            MaxNumTokens = SmallContext,
        };
    }

    /// <summary>
    /// Drives a conversation into its context limit with repeated sends. Every send must either
    /// complete with the KV cache still inside the limit (the decode clamp at work) or throw the
    /// overflow exception — never crash the process (the unguarded outcome). Afterwards the engine
    /// must still serve a fresh conversation.
    /// </summary>
    [SkippableFact]
    public void Send_GrowsToTheContextLimit_ThrowsInsteadOfCrashing()
    {
        LiteRtEngineOptions? options = SmallContextOptions();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        LiteRtContextOverflowException? overflow = null;
        bool fullSignaled = false;
        // A per-turn output cap keeps each send fast; the guard still clamps the LAST turns below it
        // as the remaining context shrinks under 64.
        using (var conv = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 64 }))
        {
            // 20 capped turns are far more than the small context can hold; the guard must fire first.
            for (int i = 0; i < 20 && overflow is null; i++)
            {
                // Raw stdout, not ITestOutputHelper: xunit's buffered output dies with the host if the
                // native layer crashes — this line is the last-known-state marker in the CI log (an
                // intermittent testhost crash was seen here on CI win-x64, 2026-07-17).
                Console.WriteLine($"[overflow-guard] turn {i}: TokenCount={conv.TokenCount} IsContextFull={fullSignaled}");
                try
                {
                    conv.Send("Tell me more about the sea and its many storied voyages.");
                    Assert.True(conv.TokenCount <= SmallContext,
                        $"KV cache grew past the limit ({conv.TokenCount} > {SmallContext}) — the decode clamp failed.");
                    // IsContextFull is a same-turn promise: once observed true, the NEXT send must throw.
                    Assert.False(fullSignaled,
                        "IsContextFull was true after the previous send, but this send did not throw.");
                    fullSignaled = conv.IsContextFull;
                }
                catch (LiteRtContextOverflowException ex)
                {
                    overflow = ex;
                }
            }
        }

        Assert.NotNull(overflow);
        Assert.Equal(SmallContext, overflow.MaxNumTokens);
        Assert.InRange(overflow.TokenCount, 0, SmallContext);
        // Coherence of the same-turn signal: when the throw was the "context full" path (the ceiling was
        // reached by a previous send), IsContextFull must have said so right after that send — the caller
        // never needs the next-turn exception to learn the conversation is over. (A throw below the
        // ceiling is the "message doesn't fit" path, which is same-call by nature.)
        if (overflow.TokenCount >= SmallContext - LiteRtContextGuard.SafetyMargin)
            Assert.True(fullSignaled, "The context filled up without IsContextFull signaling it in the same turn.");

        // The whole point of the guard: the engine (and the process) survive the full conversation.
        using var fresh = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 16 });
        LiteRtResponse after = fresh.Send("Reply with one short word: hello");
        Assert.False(string.IsNullOrWhiteSpace(after.Text), "Engine unusable after a guarded overflow.");
    }

    /// <summary>A message whose prefill alone cannot fit the remaining context is rejected before any
    /// native call — the failure mode where even a clamped decode could not have saved the send.</summary>
    [SkippableFact]
    public void Send_MessageBiggerThanTheContext_ThrowsUpFront()
    {
        LiteRtEngineOptions? options = SmallContextOptions();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var conv = engine.CreateConversation();

        string oversized = string.Join(" ", Enumerable.Repeat("many words that will not fit", 400));
        var ex = Assert.Throws<LiteRtContextOverflowException>(() => conv.Send(oversized));
        Assert.Equal(SmallContext, ex.MaxNumTokens);
        // Nothing was prefilled: the rejection happened before the native send.
        Assert.Equal(0, conv.TokenCount);
    }

    /// <summary>
    /// The same-turn signal through the Extensions.AI connector: a reply clamped by the KV overflow guard
    /// (the context filled up during the turn) must carry <see cref="ChatFinishReason.Length"/> on the
    /// response that was clamped — the caller learns the conversation is over without waiting for the next
    /// call's <see cref="LiteRtContextOverflowException"/>.
    /// </summary>
    [SkippableFact]
    public async Task ChatClient_ClampedTurn_SignalsLengthInTheSameTurn()
    {
        LiteRtEngineOptions? options = SmallContextOptions();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var client = new LiteRtLmSharp.Extensions.AI.LiteRtChatClient(engine);

        // A prompt the model will not finish on its own, with a caller cap far above the context: the
        // guard's clamp — not the model, not the caller — is what ends the reply.
        Microsoft.Extensions.AI.ChatResponse response = await client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User,
                "Count upward from 1, one number per line, and do not stop counting.")],
            new Microsoft.Extensions.AI.ChatOptions { MaxOutputTokens = 4096 });

        long total = response.Usage?.TotalTokenCount ?? 0;
        Skip.If(total < SmallContext - LiteRtContextGuard.SafetyMargin,
            $"The model stopped on its own at {total} tokens — no clamp happened, nothing to observe.");
        Assert.Equal(Microsoft.Extensions.AI.ChatFinishReason.Length, response.FinishReason);
    }

    /// <summary>
    /// A "message doesn't fit" rejection happens BEFORE any native work, so in the stateful mode it must
    /// NOT evict the live conversation: the thread stays resumable and a shorter message on the same
    /// ConversationId succeeds. (Only the terminal "context is full" rejection evicts.)
    /// </summary>
    [SkippableFact]
    public async Task StatefulThread_SurvivesAMessageThatDoesNotFit()
    {
        LiteRtEngineOptions? options = SmallContextOptions();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var client = new LiteRtLmSharp.Extensions.AI.LiteRtChatClient(
            engine, statefulConversations: new LiteRtLmSharp.Extensions.AI.LiteRtStatefulConversationOptions());

        var chatOptions = new Microsoft.Extensions.AI.ChatOptions { MaxOutputTokens = 16 };
        Microsoft.Extensions.AI.ChatResponse first = await client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Reply with one short word: hello")],
            chatOptions);
        Assert.NotNull(first.ConversationId);

        // Oversized continuation: rejected up front, conversation untouched.
        string oversized = string.Join(" ", Enumerable.Repeat("many words that will not fit", 400));
        chatOptions.ConversationId = first.ConversationId;
        await Assert.ThrowsAsync<LiteRtContextOverflowException>(() => client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, oversized)], chatOptions));

        // The SAME id must still be live: a shorter message continues the thread.
        Microsoft.Extensions.AI.ChatResponse retry = await client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Reply with one short word: bye")],
            chatOptions);
        Assert.Equal(first.ConversationId, retry.ConversationId);
        Assert.False(string.IsNullOrWhiteSpace(retry.Text), "The preserved thread did not answer.");
    }

    /// <summary>
    /// The guard must measure tool-results sends too (the exact path that motivated the guard): a fat tool
    /// result that cannot fit is rejected up front — proving the native render handles the tool role —
    /// and the conversation survives to take the real (small) result afterwards.
    /// </summary>
    [SkippableFact]
    public void SendToolResults_TooFatToFit_ThrowsUpFrontAndConversationSurvives()
    {
        LiteRtEngineOptions? options = SmallContextOptions();
        Skip.If(options is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(options);
        using var conv = engine.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = "Use tools when needed.",
            MaxOutputTokens = 64,
            Tools =
            [
                new LiteRtTool("get_current_weather", "Get the current weather for a city.",
                    """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}"""),
            ],
        });

        LiteRtResponse response = conv.Send("What's the weather in Tokyo?");
        Skip.If(!response.IsToolCall, "The model did not call the tool — nothing to continue.");
        int afterCall = conv.TokenCount;

        string fat = string.Join(" ", Enumerable.Repeat("many detailed weather observations follow here", 300));
        var ex = Assert.Throws<LiteRtContextOverflowException>(() => conv.SendToolResults(
            [new LiteRtToolResult("get_current_weather", $"{{\"report\":\"{fat}\"}}")]));
        // Rejected before native: nothing was prefilled and the tool-results render was measurable.
        Assert.Equal(afterCall, conv.TokenCount);

        LiteRtResponse after = conv.SendToolResults(
            [new LiteRtToolResult("get_current_weather", """{"temp":15}""")]);
        Assert.False(string.IsNullOrWhiteSpace(after.Text), "The conversation did not survive the rejection.");
    }
}
