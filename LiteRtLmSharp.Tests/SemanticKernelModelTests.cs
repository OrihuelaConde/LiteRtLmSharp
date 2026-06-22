using System.Text;
using LiteRtLmSharp.SemanticKernel;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-backed end-to-end tests for the Semantic Kernel connector. Gated on <c>LITERTLM_TEST_MODEL</c>
/// (and optionally <c>LITERTLM_TEST_BACKEND</c>, default "cpu"); skipped otherwise, like the other model
/// tests. Each test loads and disposes its own engine — assembly parallelization is disabled
/// (see AssemblyInfo.cs), so they run serially and never have two engines alive at once (the one-engine
/// rule). The model-free mapping/settings/DI-registration logic lives in
/// <see cref="SemanticKernelMappingTests"/>.
/// </summary>
public sealed class SemanticKernelModelTests
{
    private static string? Model => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
    private static string Backend => Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu";

    private static LiteRtEngineOptions Options() => new()
    {
        ModelPath = Model!,
        Backend = Backend,
        MaxNumTokens = 2048,
    };

    /// <summary>
    /// Blocking chat through the connector: a system prompt plus a user turn yields a non-empty answer, and
    /// the configured <c>modelId</c> is surfaced on the result. Exercises the stateless system+user mapping
    /// and the blocking Send. (The options/DI registration that loads and shares the engine is covered
    /// model-free in <see cref="SemanticKernelMappingTests"/>; the end-to-end inference path is identical
    /// whether the engine is passed in or resolved from a container, so it is exercised here via the engine
    /// overload.)
    /// </summary>
    [SkippableFact]
    public void ChatCompletion_Blocking_ProducesText()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var chat = new LiteRtChatCompletionService(engine, modelId: "litert-test");

        var history = new ChatHistory("You are concise. Answer in one short sentence.");
        history.AddUserMessage("What is the capital of France?");

        IReadOnlyList<ChatMessageContent> result =
            chat.GetChatMessageContentsAsync(history).GetAwaiter().GetResult();

        Assert.NotEmpty(result);
        Assert.False(string.IsNullOrWhiteSpace(result[0].Content),
            "Expected a non-empty chat completion.");
        Assert.Equal("litert-test", result[0].ModelId);
    }

    /// <summary>
    /// The engine-overload path, streaming, multi-turn: state a fact on turn one, then ask for it back on
    /// turn two. The connector is stateless, so turn two only knows the fact if the prior turns were
    /// replayed as restored history — asserting the answer recalls it proves the history mapping works
    /// end-to-end. The engine is created here and disposed by the <c>using</c> (caller-owned).
    /// </summary>
    [SkippableFact]
    public async Task ChatCompletion_Streaming_MultiTurn_CarriesContext()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var chat = new LiteRtChatCompletionService(engine, modelId: "litert-test");
        var settings = new LiteRtPromptExecutionSettings { Temperature = 0.2f, MaxTokens = 64 };

        var history = new ChatHistory("You are concise. Answer in one short sentence.");

        history.AddUserMessage("Remember that my favorite color is teal. Acknowledge briefly.");
        string first = await StreamToStringAsync(chat, history, settings);
        Assert.False(string.IsNullOrWhiteSpace(first), "Expected a non-empty first-turn reply.");
        history.AddAssistantMessage(first);

        history.AddUserMessage("What is my favorite color? Answer with just the color.");
        string second = await StreamToStringAsync(chat, history, settings);

        Assert.Contains("teal", second, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Single-shot text generation through <see cref="LiteRtTextGenerationService"/>, both blocking and
    /// streaming, over one engine. Covers the text-gen path end-to-end including the
    /// <see cref="LiteRtPromptExecutionSettings.SystemPrompt"/> mapping that is unique to this service.
    /// </summary>
    [SkippableFact]
    public async Task TextGeneration_BlockingAndStreaming_ProduceText()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var textGen = new LiteRtTextGenerationService(engine, modelId: "litert-test");
        var settings = new LiteRtPromptExecutionSettings { SystemPrompt = "Answer in one short sentence.", MaxTokens = 64 };

        IReadOnlyList<TextContent> blocking =
            await textGen.GetTextContentsAsync("What is the capital of France?", settings);
        Assert.NotEmpty(blocking);
        Assert.False(string.IsNullOrWhiteSpace(blocking[0].Text), "Expected non-empty blocking text generation.");
        Assert.Equal("litert-test", blocking[0].ModelId);

        var sb = new StringBuilder();
        await foreach (StreamingTextContent chunk in textGen.GetStreamingTextContentsAsync("Name two primary colors.", settings))
            sb.Append(chunk.Text);
        Assert.False(string.IsNullOrWhiteSpace(sb.ToString()), "Expected non-empty streaming text generation.");
    }

    private static async Task<string> StreamToStringAsync(
        IChatCompletionService chat, ChatHistory history, PromptExecutionSettings settings)
    {
        var sb = new StringBuilder();
        await foreach (StreamingChatMessageContent chunk in chat.GetStreamingChatMessageContentsAsync(history, settings))
            sb.Append(chunk.Content);
        return sb.ToString();
    }
}
