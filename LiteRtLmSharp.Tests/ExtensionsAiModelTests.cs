using System.Text;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-backed end-to-end tests for the Microsoft.Extensions.AI <see cref="IChatClient"/> connector. Gated
/// on <c>LITERTLM_TEST_MODEL</c> (and optionally <c>LITERTLM_TEST_BACKEND</c>, default "cpu"); skipped
/// otherwise. Each test loads and disposes its own engine — assembly parallelization is disabled
/// (AssemblyInfo.cs), so they run serially and never have two engines alive at once.
/// </summary>
public sealed class ExtensionsAiModelTests
{
    private static string? Model => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
    private static string Backend => Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu";

    private static LiteRtEngineOptions Options() => new()
    {
        ModelPath = Model!,
        Backend = Backend,
        MaxNumTokens = 2048,
    };

    [SkippableFact]
    public async Task ChatClient_BlockingAndStreaming_ProduceText()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using IChatClient client = new LiteRtChatClient(engine, modelId: "litert-test");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are concise. Answer in one short sentence."),
            new(ChatRole.User, "What is the capital of France?"),
        };
        var options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64 };

        ChatResponse response = await client.GetResponseAsync(messages, options);
        Assert.False(string.IsNullOrWhiteSpace(response.Text), "Expected a non-empty chat response.");
        Assert.Equal("litert-test", response.ModelId);

        var sb = new StringBuilder();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options))
            sb.Append(update.Text);
        Assert.False(string.IsNullOrWhiteSpace(sb.ToString()), "Expected a non-empty streaming response.");
    }

    [SkippableFact]
    public async Task ChatClient_MultiTurn_CarriesContext()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using IChatClient client = new LiteRtChatClient(engine);
        var options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64 };

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are concise. Answer in one short sentence."),
            new(ChatRole.User, "Remember that my favorite color is teal. Acknowledge briefly."),
        };

        ChatResponse first = await client.GetResponseAsync(history, options);
        Assert.False(string.IsNullOrWhiteSpace(first.Text), "Expected a non-empty first-turn reply.");
        history.AddMessages(first);   // MEAI helper: append the response's messages to the history

        history.Add(new ChatMessage(ChatRole.User, "What is my favorite color? Answer with just the color."));
        ChatResponse second = await client.GetResponseAsync(history, options);

        // The connector is stateless, so turn two only knows "teal" if the prior turns were replayed as
        // restored history — this proves the history mapping works end-to-end.
        Assert.Contains("teal", second.Text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ChatClient_WithThinking_SurfacesReasoningSeparateFromAnswer()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine);
        // generous budget so the reasoning does not starve the answer
        var options = new LiteRtChatOptions { MaxOutputTokens = 512, EnableThinking = true };
        var messages = new List<ChatMessage> { new(ChatRole.User, "What is 2+2? Think briefly, then give the answer.") };

        // Blocking: the reasoning is surfaced as TextReasoningContent; the answer is the (clean) .Text.
        ChatResponse response = await client.GetResponseAsync(messages, options);
        Assert.Contains(response.Messages[0].Contents, c => c is TextReasoningContent { Text.Length: > 0 });
        Assert.False(string.IsNullOrWhiteSpace(response.Text), "Expected a non-empty answer with a generous budget.");

        // Streaming: at least one reasoning update (TextReasoningContent) and one answer update (text).
        bool sawReasoning = false, sawAnswer = false;
        await foreach (ChatResponseUpdate u in client.GetStreamingResponseAsync(messages, options))
        {
            if (u.Contents.Any(c => c is TextReasoningContent { Text.Length: > 0 })) sawReasoning = true;
            if (u.Text.Length > 0) sawAnswer = true;
        }
        Assert.True(sawReasoning, "Expected at least one reasoning (thinking) streaming update.");
        Assert.True(sawAnswer, "Expected at least one answer streaming update.");

        // Truncation: a tiny budget makes the reasoning consume it all → empty answer, signaled as Length
        // (so a silent empty result is diagnosable). The reasoning trace is still surfaced.
        var tiny = new LiteRtChatOptions { MaxOutputTokens = 48, EnableThinking = true };
        ChatResponse truncated = await client.GetResponseAsync(messages, tiny);
        // A tiny budget always yields a (partial) reasoning trace.
        Assert.Contains(truncated.Messages[0].Contents, c => c is TextReasoningContent { Text.Length: > 0 });
        // The Length signal and an empty answer are consistent: the connector flags Length exactly when the
        // reasoning produced no answer. A model may occasionally squeeze in a stray answer token, so don't
        // require empty — just assert the two stay consistent.
        if (string.IsNullOrEmpty(truncated.Text))
            Assert.Equal(ChatFinishReason.Length, truncated.FinishReason);
    }
}
