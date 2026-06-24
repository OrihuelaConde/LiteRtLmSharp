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

        // Streaming surfaces the same truncation signal: a Length finish reason in the updates when the
        // reasoning consumed the budget before any answer (mirrors GetResponseAsync).
        ChatFinishReason? streamedFinish = null;
        bool streamedAnswer = false;
        await foreach (ChatResponseUpdate u in client.GetStreamingResponseAsync(messages, tiny))
        {
            if (u.FinishReason is { } fr) streamedFinish = fr;
            if (u.Contents.Any(c => c is TextContent { Text.Length: > 0 })) streamedAnswer = true;
        }
        if (!streamedAnswer)
            Assert.Equal(ChatFinishReason.Length, streamedFinish);
    }

    [SkippableFact]
    public async Task ChatClient_FunctionInvocation_CallsToolAndAnswersFromResult()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(OperatingSystem.IsLinux(),
            "EnableConstrainedDecoding (recommended with tools) is blocked on linux-x64 — see docs.");

        using var engine = LiteRtEngine.Load(Options());

        int invocations = 0;
        string? cityArg = null;
        AIFunction weather = AIFunctionFactory.Create(
            (string city) => { invocations++; cityArg = city; return $"22 degrees and sunny in {city}"; },
            name: "get_weather", description: "Gets the current weather for a given city.");

        // UseFunctionInvocation runs the tool loop: model emits a call -> the AIFunction is invoked -> the
        // result is sent back -> the model answers.
        using IChatClient client = new LiteRtChatClient(engine).AsBuilder().UseFunctionInvocation().Build();
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
        Assert.Contains("paris", (cityArg ?? string.Empty).ToLowerInvariant());
        Assert.False(string.IsNullOrWhiteSpace(response.Text), "Expected a final answer after the tool ran.");
    }

    [SkippableFact]
    public async Task ChatClient_FunctionInvocation_Streaming_CallsToolAndAnswers()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.If(OperatingSystem.IsLinux(),
            "EnableConstrainedDecoding (recommended with tools) is blocked on linux-x64 — see docs.");

        using var engine = LiteRtEngine.Load(Options());

        int invocations = 0;
        AIFunction weather = AIFunctionFactory.Create(
            (string city) => { invocations++; return $"22 degrees and sunny in {city}"; },
            name: "get_weather", description: "Gets the current weather for a given city.");

        // Streaming exercises a distinct path: the tool call arrives as a streamed FunctionCallContent update,
        // and the post-tool answer comes back through the blocking SendToolResults fallback (there is no native
        // streaming tool-results call) surfaced as updates.
        using IChatClient client = new LiteRtChatClient(engine).AsBuilder().UseFunctionInvocation().Build();
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

        var sb = new StringBuilder();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options))
            sb.Append(update.Text);

        Assert.True(invocations >= 1, "Expected the model to call the get_weather tool while streaming.");
        Assert.False(string.IsNullOrWhiteSpace(sb.ToString()), "Expected a streamed final answer after the tool ran.");
    }

    [SkippableFact]
    public async Task ChatClient_Usage_TotalAlways_SplitWhenBenchmarkEnabled()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        var messages = new List<ChatMessage> { new(ChatRole.User, "Say hello in one short sentence.") };

        // Default engine (EnableBenchmark off): TotalTokenCount is set; the input/output split is absent and a
        // discoverability note is left on the response.
        using (var engine = LiteRtEngine.Load(Options()))
        using (IChatClient client = new LiteRtChatClient(engine))
        {
            ChatResponse r = await client.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 24 });
            Assert.NotNull(r.Usage);
            Assert.True(r.Usage!.TotalTokenCount > 0);
            Assert.Null(r.Usage.OutputTokenCount);
            Assert.True(r.AdditionalProperties?.ContainsKey(LiteRtChatMapping.UsageBenchmarkNoteKey) == true);
        }

        // EnableBenchmark on: the input/output split is populated.
        using (var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = Backend, MaxNumTokens = 2048, EnableBenchmark = true,
        }))
        using (IChatClient client = new LiteRtChatClient(engine))
        {
            ChatResponse r = await client.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 24 });
            Assert.True(r.Usage!.InputTokenCount > 0, "Expected prefill tokens with EnableBenchmark on.");
            Assert.True(r.Usage.OutputTokenCount > 0, "Expected decode tokens with EnableBenchmark on.");
        }
    }

    // ─────────────────────── Multimodal (image / audio) ───────────────────────
    // Gated additionally on LITERTLM_TEST_VISION=1 because they need a vision/audio-capable model and the
    // engine loaded with the matching backend (the connector just maps DataContent -> LiteRtAttachment).

    // A 64x64 solid red PNG (246 bytes), embedded so the test is self-contained and runs cross-platform.
    private const string RedPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJ" +
        "cEhZcwAADsMAAA7DAcdvqGQAAACLSURBVHhe7dAhAQBADIDAJVn/UN9l76kA4gySebtnNgw2DWCwaQCDTQMYbBrA" +
        "YNMABpsGMNg0gMGmAQw2DWCwaQCDTQMYbBrAYNMABpsGMNg0gMGmAQw2DWCwaQCDTQMYbBrAYNMABpsGMNg0gMGm" +
        "AQw2DWCwaQCDTQMYbBrAYNMABpsGMNg0gMHmA+xncfBUukEyAAAAAElFTkSuQmCC";

    private static byte[] LoadEmbedded(string name)
    {
        using Stream s = typeof(ExtensionsAiModelTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [SkippableFact]
    public async Task ChatClient_ImageAttachment_IsSentToTheModel()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model)
                || Environment.GetEnvironmentVariable("LITERTLM_TEST_VISION") != "1",
            "Set LITERTLM_TEST_VISION=1 and LITERTLM_TEST_MODEL to a vision-capable .litertlm (e.g. gemma-4-E2B-it) to run.");

        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!,
            Backend = Backend,
            VisionBackend = Backend,
            MaxNumTokens = 4096,
            CacheDir = Backend == "gpu" ? LiteRtEngineOptions.CacheDisabled : null,
        });
        using IChatClient client = new LiteRtChatClient(engine);

        byte[] redPng = Convert.FromBase64String(RedPngBase64);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, (IList<AIContent>)
            [
                new TextContent("What is the main color of this image? Answer with one word."),
                new DataContent(redPng, "image/png"),
            ]),
        };

        // Without VisionBackend + the image reaching the encoder this would throw; a non-empty reply confirms
        // the DataContent flowed through as an attachment.
        ChatResponse response = await client.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 32 });
        Assert.False(string.IsNullOrWhiteSpace(response.Text), $"Expected a description of the image. Raw: {response.RawRepresentation}");
    }

    [SkippableFact]
    public async Task ChatClient_AudioAttachment_IsSentToTheModel()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model)
                || Environment.GetEnvironmentVariable("LITERTLM_TEST_VISION") != "1",
            "Set LITERTLM_TEST_VISION=1 and LITERTLM_TEST_MODEL to an audio-capable .litertlm (e.g. gemma-4-E2B-it) to run.");
        // gemma-4's audio sub-model is CPU-constrained, so audio engine creation fails on a gpu backend.
        Skip.If(Backend == "gpu", "Audio is CPU-constrained on gemma-4; the GPU leg is skipped (see the native audio test).");

        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!,
            Backend = Backend,
            AudioBackend = Backend,
            MaxNumTokens = 4096,
        });
        using IChatClient client = new LiteRtChatClient(engine);

        byte[] mp3 = LoadEmbedded("countdown.mp3"); // a real spoken 5->0 countdown
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, (IList<AIContent>)
            [
                new TextContent("Transcribe the spoken words in this audio clip."),
                new DataContent(mp3, "audio/mpeg"),
            ]),
        };

        ChatResponse response = await client.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 64 });
        Assert.False(string.IsNullOrWhiteSpace(response.Text), $"Expected a transcription. Raw: {response.RawRepresentation}");
    }
}
