using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-backed tests of what <see cref="LiteRtChatClient"/> reports for a reply with thinking: the finish reason
/// when the KV overflow guard cuts the reply at the context limit, and the input token count with benchmarking on.
/// Gated on <c>LITERTLM_TEST_MODEL</c> (and optionally <c>LITERTLM_TEST_BACKEND</c>, default "cpu"); skipped
/// otherwise.
/// </summary>
public sealed class ExtensionsAiContextLimitModelTests(ITestOutputHelper output)
{
    private const int Limit = 1024;
    private const string EssayPrompt = "Think very carefully and at great length about the history of bridges, then " +
                                       "write an essay of at least 900 words about bridges.";
    private const string ShortThinkingPrompt = "Think about bridges first. Then write a 30-word sentence about bridges.";
    private static readonly LiteRtSamplerParams Argmax = new() { Strategy = LiteRtSamplerType.TopP, TopK = 1 };

    private static string? Model => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
    private static string Backend => Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu";

    private static LiteRtEngine LoadEngine(int maxNumTokens, bool benchmark)
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        return LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = LiteRtBackend.Parse(Backend), MaxNumTokens = maxNumTokens, EnableBenchmark = benchmark,
        });
    }

    private static LiteRtChatClient ThinkingClient(LiteRtEngine engine, bool stateful, bool thinking = true) => new(
        engine, "gemma", new LiteRtConversationOptions { Sampler = Argmax, EnableThinking = thinking },
        stateful ? new LiteRtStatefulConversationOptions() : null);

    /// <summary>The blocking response, or the streamed updates folded into one, with the finish reason of the
    /// last update that carried one.</summary>
    private static async Task<(ChatResponse Response, ChatFinishReason? LastFinish)> AskAsync(
        IChatClient client, string prompt, ChatOptions? options, bool stream)
    {
        List<ChatMessage> messages = [new(ChatRole.User, prompt)];
        if (!stream)
        {
            ChatResponse response = await client.GetResponseAsync(messages, options);
            return (response, response.FinishReason);
        }
        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options))
            updates.Add(update);
        ChatFinishReason? last = updates.LastOrDefault(u => u.FinishReason is not null)?.FinishReason;
        return (updates.ToChatResponse(), last);
    }

    private static string Reasoning(ChatResponse response)
        => string.Concat(response.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Select(r => r.Text));

    /// <summary>A reply with thinking that the guard cuts at the context limit finishes with
    /// <see cref="ChatFinishReason.Length"/>, in the blocking response and on the last streamed update, with and
    /// without benchmarking (the decoded count, or the tokenized reply). The runtime removes the thinking from the
    /// KV cache once the reply ends, so the context is not full and a stateful thread takes the next turn. Without
    /// the truncation check the finish reason is null: the token count after the reply no longer shows that the
    /// reply reached the limit.</summary>
    [SkippableTheory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task ClampedReplyWithThinking_FinishesWithLength_AndTheContextHasRoom(bool stateful, bool stream, bool benchmark)
    {
        using LiteRtEngine engine = LoadEngine(Limit, benchmark);
        using LiteRtChatClient client = ThinkingClient(engine, stateful);
        int promptTokens;
        using (LiteRtConversation measure = engine.CreateConversation(new LiteRtConversationOptions { EnableThinking = true }))
            promptTokens = engine.CountTokens(measure.RenderMessage(EssayPrompt));

        (ChatResponse response, ChatFinishReason? lastFinish) = await AskAsync(client, EssayPrompt, options: null, stream);

        int answerTokens = engine.CountTokens(response.Text), thinkingTokens = engine.CountTokens(Reasoning(response));
        long total = response.Usage?.TotalTokenCount ?? -1;
        output.WriteLine($"prompt {promptTokens}, answer {answerTokens}, thinking {thinkingTokens}, total {total}, " +
                         $"finish {response.FinishReason}, last streamed finish {lastFinish}, output {response.Usage?.OutputTokenCount}");
        // The reply ran into the clamp: answer and thinking together nearly fill the context.
        Assert.True(answerTokens + thinkingTokens > Limit - 128, $"answer {answerTokens} + thinking {thinkingTokens}");
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
        Assert.Equal(ChatFinishReason.Length, lastFinish);
        // How the model splits the budget between answer and thinking is its choice (answer/thinking 294/668 on CPU,
        // 144/818 on GPU here). Without thinking, or with an answer long enough to fill the context on its own, the
        // full context gives Length by itself and the room left after the reply cannot be checked.
        Skip.If(thinkingTokens <= 32,
            $"The reply carried {thinkingTokens} tokens of thinking; the check needs a reply that thought.");
        Skip.If(LiteRtContextGuard.IsContextFull(promptTokens + answerTokens + LiteRtContextGuard.SafetyMargin, Limit),
            $"The answer alone filled the context (prompt {promptTokens} + answer {answerTokens}), so it has no room " +
            "left to check.");
        // The prompt and the answer leave room, so a full context here means the thinking stayed in the KV cache.
        Assert.False(LiteRtContextGuard.IsContextFull(checked((int)total), Limit),
            $"total {total} (prompt {promptTokens} + answer {answerTokens}): the runtime kept the thinking in the KV cache");

        if (stateful)
        {
            ChatResponse next = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with one word: OK")],
                new ChatOptions { ConversationId = response.ConversationId });
            output.WriteLine($"next '{next.Text}', finish {next.FinishReason}, total {next.Usage?.TotalTokenCount}");
            Assert.False(string.IsNullOrWhiteSpace(next.Text));
            Assert.Null(next.FinishReason);
        }
    }

    /// <summary>A reply with thinking that ends on its own, on a send whose output the guard capped at the room
    /// left in the context, keeps a null finish reason: the truncation check fires only on a reply that used the
    /// whole budget.</summary>
    [SkippableTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NaturalReplyWithThinking_UnderTheGuard_HasNoFinishReason(bool stream, bool benchmark)
    {
        using LiteRtEngine engine = LoadEngine(Limit, benchmark);
        using LiteRtChatClient client = ThinkingClient(engine, stateful: true);

        (ChatResponse response, ChatFinishReason? lastFinish) = await AskAsync(client, ShortThinkingPrompt, options: null, stream);

        output.WriteLine($"answer '{response.Text}', thinking {Reasoning(response).Length} chars, finish {response.FinishReason}, " +
                         $"last streamed finish {lastFinish}, output {response.Usage?.OutputTokenCount}");
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.Null(response.FinishReason);
        Assert.Null(lastFinish);
    }

    /// <summary>With benchmarking on, the input token count is the prefill of the message itself, with or without
    /// thinking, on a stateful thread's first and second turns. After a reply with thinking the runtime records the
    /// KV-cache refill (about the answer's size) as a second prefill turn of the same send; reporting the last
    /// prefill turn would give that refill instead of the message.</summary>
    [SkippableTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task InputTokenCount_IsTheMessagesPrefill(bool thinking, bool stream)
    {
        using LiteRtEngine engine = LoadEngine(2048, benchmark: true);
        using LiteRtChatClient client = ThinkingClient(engine, stateful: true, thinking);

        // Short messages with long answers, so the refill (about the answer's size) cannot pass for the message.
        string[] prompts =
        [
            "Think first. Then write a 100-word paragraph about bridges.",
            "Think first. Then write a 100-word paragraph about rivers.",
        ];

        // Renders each message as the thread would prefill it: the first one with the conversation's preface,
        // later ones alone (a conversation that already sent renders the turn alone).
        using LiteRtConversation measure = engine.CreateConversation(new LiteRtConversationOptions { EnableThinking = thinking });
        string? id = null;
        int turnsThatThought = 0;
        foreach (string prompt in prompts)
        {
            int rendered = engine.CountTokens(measure.RenderMessage(prompt));
            if (id is null)
                measure.Send("Reply with one word: OK", null, new LiteRtSendOptions { EnableThinking = false, MaxOutputTokens = 8 });

            (ChatResponse response, _) = await AskAsync(client, prompt, new ChatOptions { MaxOutputTokens = 1024, ConversationId = id }, stream);
            id = response.ConversationId;
            long input = response.Usage!.InputTokenCount!.Value;
            int answerTokens = engine.CountTokens(response.Text);
            string reasoning = Reasoning(response);
            output.WriteLine($"rendered {rendered}, input {input}, output {response.Usage.OutputTokenCount}, " +
                             $"answer {answerTokens}, thinking {reasoning.Length} chars");
            if (!thinking)
                Assert.Empty(reasoning);
            else if (reasoning.Length > 0)
                turnsThatThought++;
            Assert.True(answerTokens > rendered + 32, $"answer {answerTokens} must be far longer than the message {rendered}");
            // Measured: the first send prefills the rendered message plus one token, later sends the rendered
            // message alone.
            Assert.InRange(input, rendered - 2, rendered + 2);
        }
        // Whether a turn thinks is the model's choice; the refill this test is about follows only a turn that did.
        Skip.If(thinking && turnsThatThought == 0, "No turn thought, so no turn was followed by a KV-cache refill.");
    }
}
