using System.Text;
using LiteRtLmSharp.Extensions.AI;
using LiteRtLmSharp.SemanticKernel;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-backed end-to-end tests for the Semantic Kernel connector. Gated on <c>LITERTLM_TEST_MODEL</c>
/// (and optionally <c>LITERTLM_TEST_BACKEND</c>, default "cpu"); skipped otherwise. Exercises the adapter
/// path the connector registers: a Semantic Kernel <see cref="IChatCompletionService"/> obtained from the
/// LiteRtLmSharp <c>IChatClient</c> via <c>AsChatCompletionService</c> (the DI/Kernel registration that
/// wires this up is covered model-free by <see cref="SemanticKernelMappingTests"/>). The engine is created
/// here and disposed via <c>using</c>; assembly parallelization is disabled, so tests run serially.
/// </summary>
public sealed class SemanticKernelModelTests
{
    private static string? Model => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
    private static string Backend => Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu";

    private static LiteRtEngineOptions Options() => new()
    {
        ModelPath = Model!,
        Backend = LiteRtBackend.Parse(Backend),
        MaxNumTokens = 2048,
    };

    [SkippableFact]
    public async Task ChatCompletion_BlockingAndStreaming_ProduceText()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, modelId: "litert-test");
        IChatCompletionService chat = client.AsChatCompletionService();
        var settings = new LiteRtPromptExecutionSettings { Temperature = 0.2f, MaxTokens = 64 };

        var history = new ChatHistory("You are concise. Answer in one short sentence.");
        history.AddUserMessage("What is the capital of France?");

        IReadOnlyList<ChatMessageContent> result = await chat.GetChatMessageContentsAsync(history, settings);
        Assert.NotEmpty(result);
        Assert.False(string.IsNullOrWhiteSpace(result[0].Content), "Expected a non-empty chat completion.");

        var sb = new StringBuilder();
        await foreach (StreamingChatMessageContent chunk in chat.GetStreamingChatMessageContentsAsync(history, settings))
            sb.Append(chunk.Content);
        Assert.False(string.IsNullOrWhiteSpace(sb.ToString()), "Expected a non-empty streaming completion.");
    }

    [SkippableFact]
    public async Task ChatCompletion_MultiTurn_CarriesContext()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var engine = LiteRtEngine.Load(Options());
        using var client = new LiteRtChatClient(engine, modelId: "litert-test");
        IChatCompletionService chat = client.AsChatCompletionService();
        var settings = new LiteRtPromptExecutionSettings { Temperature = 0.2f, MaxTokens = 64 };

        var history = new ChatHistory("You are concise. Answer in one short sentence.");
        history.AddUserMessage("Remember that my favorite color is teal. Acknowledge briefly.");

        string first = (await chat.GetChatMessageContentsAsync(history, settings))[0].Content ?? "";
        Assert.False(string.IsNullOrWhiteSpace(first), "Expected a non-empty first-turn reply.");
        history.AddAssistantMessage(first);

        history.AddUserMessage("What is my favorite color? Answer with just the color.");
        string second = (await chat.GetChatMessageContentsAsync(history, settings))[0].Content ?? "";

        // Stateless: turn two only knows "teal" if the prior turns flowed through the adapter into the
        // IChatClient's history mapping and were replayed.
        Assert.Contains("teal", second, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ChatCompletion_FunctionChoiceBehaviorAuto_InvokesKernelFunction()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (with a version-matched native binary) to run tool tests.");

        using var engine = LiteRtEngine.Load(Options());

        int invocations = 0;
        string? cityArg = null;

        // The real registration: AddLiteRtChatCompletion wraps the IChatClient with function-invocation so
        // FunctionChoiceBehavior.Auto actually invokes the kernel function.
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(engine, modelId: "litert-test");
        builder.Plugins.AddFromFunctions("weather", new[]
        {
            KernelFunctionFactory.CreateFromMethod(
                (string city) => { invocations++; cityArg = city; return $"22 degrees and sunny in {city}"; },
                functionName: "get_weather", description: "Gets the current weather for a given city."),
        });
        Kernel kernel = builder.Build();

        var settings = new LiteRtPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            MaxTokens = 256,
            EnableConstrainedDecoding = true,   // recommended when using tools
        };

        FunctionResult result = await kernel.InvokePromptAsync(
            "What is the weather in Paris? Use the get_weather tool.", new KernelArguments(settings));

        Assert.True(invocations >= 1, "Expected Semantic Kernel to auto-invoke the get_weather function.");
        Assert.True(invocations <= 2, "Expected no double-invocation (one auto-invoke loop, at most one re-call).");
        Assert.Contains("paris", (cityArg ?? string.Empty).ToLowerInvariant());
        Assert.False(string.IsNullOrWhiteSpace(result.ToString()), "Expected a final answer after the function ran.");
    }
}
