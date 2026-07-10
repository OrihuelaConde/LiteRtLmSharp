using System.Text.Json;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-free unit tests for the Microsoft.Extensions.AI (IChatClient) connector's pure mapping logic:
/// ChatMessage list → stateless conversation split, and ChatOptions → LiteRtConversationOptions. Run in CI
/// with no native binaries or model; model-backed behavior is in <see cref="ExtensionsAiModelTests"/>.
/// </summary>
public class ExtensionsAiMappingTests
{
    [Fact]
    public void Split_EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => LiteRtChatMapping.Split(new List<ChatMessage>()));
    }

    [Fact]
    public void Split_LastNotUser_Throws()
    {
        var msgs = new List<ChatMessage> { new(ChatRole.User, "hi"), new(ChatRole.Assistant, "hello") };
        Assert.Throws<ArgumentException>(() => LiteRtChatMapping.Split(msgs));
    }

    [Fact]
    public void Split_MapsRolesAndExtractsFinalUserText()
    {
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "first"),
            new(ChatRole.Assistant, "ans"),
            new(ChatRole.User, "second"),
        };

        (IReadOnlyList<LiteRtMessage> history, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split(msgs);

        Assert.False(trigger.IsToolResults);
        Assert.Equal("second", trigger.UserText);
        Assert.Equal(3, history.Count);
        Assert.Equal(LiteRtMessageRole.System, history[0].Role);
        Assert.Equal("sys", history[0].Text);
        Assert.Equal(LiteRtMessageRole.User, history[1].Role);
        Assert.Equal(LiteRtMessageRole.Model, history[2].Role);
        Assert.Equal("ans", history[2].Text);
    }

    [Fact]
    public void ToConversationOptions_NoHistoryNoOptions_ReturnsNull()
    {
        Assert.Null(LiteRtChatMapping.ToConversationOptions([], null));
    }

    [Fact]
    public void ToConversationOptions_MapsSampler_AndDoesNotCarryMaxTokens()
    {
        var opts = new ChatOptions { Temperature = 0.3f, TopP = 0.8f, TopK = 20, MaxOutputTokens = 128 };

        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], opts);

        Assert.NotNull(conv);
        // MaxOutputTokens is a MEAI per-request option and maps to the per-send cap (ToSendOptions), not the
        // conversation-level session config — so the conversation options never carry it.
        Assert.Equal(0, conv!.MaxOutputTokens);
        Assert.NotNull(conv.Sampler);
        Assert.Equal(LiteRtSamplerType.TopP, conv.Sampler!.Strategy);
        Assert.Equal(0.3f, conv.Sampler.Temperature, 3);
        Assert.Equal(0.8f, conv.Sampler.TopP, 3);
        Assert.Equal(20, conv.Sampler.TopK);
    }

    [Fact]
    public void ToConversationOptions_OnlyMaxTokens_ReturnsNull()
    {
        // With no history/sampler/thinking/tools/constrained set, an output cap alone no longer forces a
        // conversation config: the cap rides the per-send options instead, so a plain conversation is created.
        Assert.Null(LiteRtChatMapping.ToConversationOptions([], new ChatOptions { MaxOutputTokens = 256 }));
    }

    [Fact]
    public void ToSendOptions_MapsMaxOutputTokens()
    {
        LiteRtSendOptions? send = LiteRtChatMapping.ToSendOptions(new ChatOptions { MaxOutputTokens = 128 });
        Assert.NotNull(send);
        Assert.Equal(128, send!.MaxOutputTokens);
    }

    [Fact]
    public void ToSendOptions_NoMaxTokens_ReturnsNull()
    {
        Assert.Null(LiteRtChatMapping.ToSendOptions(null));
        Assert.Null(LiteRtChatMapping.ToSendOptions(new ChatOptions()));
        Assert.Null(LiteRtChatMapping.ToSendOptions(new ChatOptions { Temperature = 0.5f }));
    }

    [Fact]
    public void ToConversationOptions_ReadsEnableThinkingFromAdditionalProperties()
    {
        var opts = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { ["enable_thinking"] = true } };

        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], opts);

        Assert.NotNull(conv);
        Assert.True(conv!.EnableThinking);
    }

    // ─────────────────────── DI registration ───────────────────────
    // Inspects the registered descriptors only — never builds the provider or loads a model (the lazy engine
    // factory is not invoked), so it runs model-free in CI. The bogus path is only read if the engine loads.

    [Fact]
    public void AddLiteRtChatClient_FromOptions_RegistersOneEngineAndClient_Idempotently()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };

        var services = new ServiceCollection();
        services.AddLiteRtChatClient(options);
        services.AddLiteRtChatClient(options);   // second call must not add a second engine/client

        Assert.Single(services, d => d.ServiceType == typeof(LiteRtEngine));
        Assert.Single(services, d => d.ServiceType == typeof(IChatClient));
    }

    [Fact]
    public void LiteRtChatOptions_EnableThinking_BacksAdditionalProperty_AndFlowsThroughMapping()
    {
        var options = new LiteRtChatOptions { MaxOutputTokens = 10, EnableThinking = true };
        Assert.True(options.EnableThinking);
        Assert.Equal(true, options.AdditionalProperties?["enable_thinking"]);   // backed by the bag, like SK's ExtensionData

        // It IS a ChatOptions, so the connector reads the knob the same way as a plain ChatOptions.
        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], options);
        Assert.True(conv!.EnableThinking);

        Assert.False(new LiteRtChatOptions().EnableThinking);   // unset reads false
    }

    // ─────────────────────── Function calling (tools) ───────────────────────

    [Fact]
    public void ToConversationOptions_MapsFunctionToolsToNativeTools()
    {
        AIFunction fn = AIFunctionFactory.Create(
            (string city) => $"sunny in {city}", name: "get_weather", description: "Gets the weather.");
        var opts = new ChatOptions { Tools = [fn] };

        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], opts);

        Assert.NotNull(conv);
        LiteRtTool tool = Assert.Single(conv!.Tools!);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal("Gets the weather.", tool.Description);
        using var schema = JsonDocument.Parse(tool.ParametersJson);
        Assert.True(schema.RootElement.GetProperty("properties").TryGetProperty("city", out _));
    }

    [Fact]
    public void ToFunctionCall_ParsesArgumentsAndSynthesizesCallId()
    {
        FunctionCallContent fcc = LiteRtChatMapping.ToFunctionCall(
            new LiteRtToolCall("get_weather", """{"city":"Paris"}"""), index: 2);

        Assert.Equal("get_weather", fcc.Name);
        Assert.Equal("get_weather_2", fcc.CallId);   // synthesized, stable within a response
        Assert.Equal("Paris", fcc.Arguments!["city"]?.ToString());
    }

    [Fact]
    public void Split_ToolEndedList_ProducesToolResultsTriggerAndPreservesCallTurn()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "weather in Paris?"),
            new(ChatRole.Assistant, (IList<AIContent>)[
                new FunctionCallContent("get_weather_0", "get_weather", new Dictionary<string, object?> { ["city"] = "Paris" })]),
            new(ChatRole.Tool, (IList<AIContent>)[new FunctionResultContent("get_weather_0", "sunny")]),
        };

        (IReadOnlyList<LiteRtMessage> history, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split(messages);

        // Ends in a tool message → the trigger returns tool results, not user text.
        Assert.True(trigger.IsToolResults);
        LiteRtToolResult result = Assert.Single(trigger.ToolResults!);
        Assert.Equal("get_weather", result.Name);   // resolved from the call id, not the raw id
        Assert.Equal("\"sunny\"", result.ResultJson); // string result serialized to a JSON value

        // History keeps the user turn and the assistant tool-call turn (so the model sees the dialogue).
        Assert.Equal(2, history.Count);
        Assert.Equal(LiteRtMessageRole.Model, history[1].Role);
        LiteRtToolCall call = Assert.Single(history[1].ToolCalls);
        Assert.Equal("get_weather", call.Name);
        using var args = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("Paris", args.RootElement.GetProperty("city").GetString());
    }

    [Fact]
    public void ToConversationOptions_ReadsConstrainedDecoding()
    {
        var opts = new LiteRtChatOptions { EnableConstrainedDecoding = true };
        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], opts);
        Assert.NotNull(conv);
        Assert.True(conv!.EnableConstrainedDecoding);
    }

    [Fact]
    public void ToFunctionCall_DistinctIndices_ProduceDistinctCallIds()
    {
        FunctionCallContent a = LiteRtChatMapping.ToFunctionCall(new LiteRtToolCall("get_weather", "{}"), 0);
        FunctionCallContent b = LiteRtChatMapping.ToFunctionCall(new LiteRtToolCall("get_weather", "{}"), 1);
        Assert.Equal("get_weather_0", a.CallId);
        Assert.Equal("get_weather_1", b.CallId);
        Assert.NotEqual(a.CallId, b.CallId);
    }

    [Fact]
    public void ToFunctionCall_MalformedArguments_YieldNoArguments()
    {
        FunctionCallContent fcc = LiteRtChatMapping.ToFunctionCall(new LiteRtToolCall("f", "not json"), 0);
        Assert.True(fcc.Arguments is null || fcc.Arguments.Count == 0);
    }

    [Fact]
    public void Split_ToolResult_PlainTextWithBrackets_IsValidJsonAndSerializesWithoutThrowing()
    {
        // A plain-text tool return that happens to start with '{' or '[' must NOT be forwarded as raw JSON
        // (the native tool_response writer validates and would throw) — it becomes a JSON string instead.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "find user bob"),
            new(ChatRole.Assistant, (IList<AIContent>)[
                new FunctionCallContent("find_0", "find_user", new Dictionary<string, object?> { ["name"] = "bob" })]),
            new(ChatRole.Tool, (IList<AIContent>)[new FunctionResultContent("find_0", "{bob} not found")]),
        };

        (_, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split(messages);
        LiteRtToolResult result = Assert.Single(trigger.ToolResults!);

        Assert.Equal("\"{bob} not found\"", result.ResultJson);   // serialized to a JSON string
        using (JsonDocument.Parse(result.ResultJson)) { }          // valid JSON

        // The native tool-results writer uses WriteRawValue (which validates) — must not throw.
        string wire = LiteRtMessage.Serialize([LiteRtMessage.Tool(result)]);
        Assert.Contains("tool_response", wire);
    }

    // ─────────────────────── Tool choice (ChatOptions.ToolMode) ───────────────────────

    [Fact]
    public void ToTools_None_AdvertisesNoTools()
    {
        AIFunction fn = AIFunctionFactory.Create((string city) => "x", name: "get_weather", description: "d");
        // Temperature keeps the conversation options non-null (the output cap now rides the per-send options,
        // so it no longer forces a config); the point of the test is that None advertises no tools.
        var opts = new ChatOptions { Tools = [fn], ToolMode = ChatToolMode.None, Temperature = 0.5f };

        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], opts);
        Assert.NotNull(conv);
        Assert.Null(conv!.Tools);   // None → the model is given no tools, so it can't call any
    }

    [Fact]
    public void ToTools_RequireSpecific_AdvertisesOnlyThatTool()
    {
        AIFunction weather = AIFunctionFactory.Create((string city) => "x", name: "get_weather", description: "d");
        AIFunction time = AIFunctionFactory.Create(() => "x", name: "get_time", description: "d");
        var opts = new ChatOptions { Tools = [weather, time], ToolMode = ChatToolMode.RequireSpecific("get_weather") };

        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], opts);
        LiteRtTool tool = Assert.Single(conv!.Tools!);
        Assert.Equal("get_weather", tool.Name);   // only the required tool is offered
    }

    [Fact]
    public void ToTools_RequireAny_AdvertisesAllTools()
    {
        AIFunction weather = AIFunctionFactory.Create((string city) => "x", name: "get_weather", description: "d");
        AIFunction time = AIFunctionFactory.Create(() => "x", name: "get_time", description: "d");
        var opts = new ChatOptions { Tools = [weather, time], ToolMode = ChatToolMode.RequireAny };

        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], opts);
        Assert.Equal(2, conv!.Tools!.Count);
    }

    [Fact]
    public void WithRequiredToolInstruction_RequireAny_AddsSystemInstruction()
    {
        AIFunction fn = AIFunctionFactory.Create((string city) => "x", name: "get_weather", description: "d");
        var opts = new ChatOptions { Tools = [fn], ToolMode = ChatToolMode.RequireAny };

        IReadOnlyList<LiteRtMessage> history = LiteRtChatMapping.WithRequiredToolInstruction([], opts);
        LiteRtMessage sys = Assert.Single(history);
        Assert.Equal(LiteRtMessageRole.System, sys.Role);
        Assert.Contains("MUST use one of the available tools", sys.Text!);
    }

    [Fact]
    public void WithRequiredToolInstruction_RequireSpecific_NamesTool_AndMergesIntoExistingSystem()
    {
        AIFunction fn = AIFunctionFactory.Create((string city) => "x", name: "get_weather", description: "d");
        var opts = new ChatOptions { Tools = [fn], ToolMode = ChatToolMode.RequireSpecific("get_weather") };
        IReadOnlyList<LiteRtMessage> baseHistory = [LiteRtMessage.System("Be concise."), LiteRtMessage.User("hi")];

        IReadOnlyList<LiteRtMessage> history = LiteRtChatMapping.WithRequiredToolInstruction(baseHistory, opts);

        Assert.Equal(2, history.Count);   // merged into the existing system message, not added as a second one
        Assert.Equal(LiteRtMessageRole.System, history[0].Role);
        Assert.Contains("Be concise.", history[0].Text!);
        Assert.Contains("get_weather", history[0].Text!);
    }

    [Fact]
    public void WithRequiredToolInstruction_AutoAndNone_LeaveHistoryUnchanged()
    {
        AIFunction fn = AIFunctionFactory.Create((string city) => "x", name: "get_weather", description: "d");
        IReadOnlyList<LiteRtMessage> baseHistory = [LiteRtMessage.User("hi")];

        Assert.Same(baseHistory, LiteRtChatMapping.WithRequiredToolInstruction(
            baseHistory, new ChatOptions { Tools = [fn], ToolMode = ChatToolMode.Auto }));
        Assert.Same(baseHistory, LiteRtChatMapping.WithRequiredToolInstruction(
            baseHistory, new ChatOptions { Tools = [fn], ToolMode = ChatToolMode.None }));
    }

    // ─────────────────────── Bool-flag coercion parity ───────────────────────

    [Fact]
    public void AsBool_ToleratesBool_String_AndJsonElement()
    {
        Assert.True(LiteRtChatMapping.AsBool(true));
        Assert.True(LiteRtChatMapping.AsBool("true"));
        using var jbool = JsonDocument.Parse("true");
        Assert.True(LiteRtChatMapping.AsBool(jbool.RootElement));           // JsonElement true
        using var jstr = JsonDocument.Parse("\"true\"");
        Assert.True(LiteRtChatMapping.AsBool(jstr.RootElement));            // JsonElement "true" (string)
        Assert.Null(LiteRtChatMapping.AsBool(123));                        // unrelated → null
    }

    [Fact]
    public void LiteRtChatOptions_EnableThinking_ReadsBackStringAndJsonElement()
    {
        // The getter must agree with the runtime reader (ToConversationOptions) — both go through AsBool now.
        Assert.True(new LiteRtChatOptions { AdditionalProperties = new() { ["enable_thinking"] = "true" } }.EnableThinking);
        using var doc = JsonDocument.Parse("true");
        var opts = new LiteRtChatOptions { AdditionalProperties = new() { ["enable_thinking"] = doc.RootElement } };
        Assert.True(opts.EnableThinking);
        Assert.True(LiteRtChatMapping.ToConversationOptions([], opts)!.EnableThinking);   // runtime reader agrees
    }

    // ─────────────────────── Token usage (ChatResponse.Usage) ───────────────────────

    [Fact]
    public void BuildUsage_NoBenchmark_SetsTotalOnly()
    {
        UsageDetails u = LiteRtChatMapping.BuildUsage(120, benchmark: null);
        Assert.Equal(120L, u.TotalTokenCount!.Value);
        Assert.Null(u.InputTokenCount);    // no split without benchmark
        Assert.Null(u.OutputTokenCount);
    }

    [Fact]
    public void BuildUsage_WithBenchmark_SplitsInputOutput()
    {
        var bench = new LiteRtBenchmarkInfo { LastPrefillTokenCount = 30, LastDecodeTokenCount = 90 };
        UsageDetails u = LiteRtChatMapping.BuildUsage(120, bench);
        Assert.Equal(120L, u.TotalTokenCount!.Value);
        Assert.Equal(30L, u.InputTokenCount!.Value);
        Assert.Equal(90L, u.OutputTokenCount!.Value);
    }

    // ─────────────────────── Streaming chunk → MEAI content mapping ───────────────────────

    [Fact]
    public void ToStreamingTextContent_AnswerDelta_BecomesTextContent()
    {
        AIContent? content = LiteRtChatMapping.ToStreamingTextContent(LiteRtStreamChunk.Answer("hi"));
        TextContent text = Assert.IsType<TextContent>(content);
        Assert.Equal("hi", text.Text);
    }

    [Fact]
    public void ToStreamingTextContent_ThinkingDelta_BecomesReasoningContent()
    {
        AIContent? content = LiteRtChatMapping.ToStreamingTextContent(LiteRtStreamChunk.Thinking("pondering"));
        TextReasoningContent reasoning = Assert.IsType<TextReasoningContent>(content);
        Assert.Equal("pondering", reasoning.Text);
    }

    [Fact]
    public void ToStreamingTextContent_ToolCallChunk_IsIgnored()
    {
        // ToolCall chunks are surfaced separately by the client (FunctionCallContent + finish reason), so the
        // text-content mapper contributes nothing for them.
        var chunk = LiteRtStreamChunk.Tools([new LiteRtToolCall("get_weather", "{}")]);
        Assert.Null(LiteRtChatMapping.ToStreamingTextContent(chunk));
    }

    [Fact]
    public void ToStreamingTextContent_ToolCallDelta_IsIgnored()
    {
        // Defensive: the opt-in v0.14.0 ToolCallDelta progress fragment must never reach the text stream (the
        // connector never enables StreamToolCalls, but if a ToolCallDelta ever appeared it is dropped, not
        // mis-surfaced as answer/reasoning text — an unknown chunk kind can't break streaming).
        LiteRtStreamChunk delta = LiteRtStreamChunk.ToolCallDelta("""{"name":"get_""");
        Assert.Equal(LiteRtStreamChunkKind.ToolCallDelta, delta.Kind);
        Assert.Null(LiteRtChatMapping.ToStreamingTextContent(delta));
    }

    [Fact]
    public void ToStreamingTextContent_EmptyTextDelta_IsIgnored()
    {
        Assert.Null(LiteRtChatMapping.ToStreamingTextContent(default));   // default(chunk) = Answer with empty text
    }

    // ─────────────────────── Multimodal (image / audio) ───────────────────────

    [Fact]
    public void Split_UserMessageWithImageAndAudio_MapsToAttachmentsInOrder()
    {
        var msg = new ChatMessage(ChatRole.User, (IList<AIContent>)
        [
            new TextContent("describe these"),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
            new DataContent(new byte[] { 4, 5 }, "audio/wav"),
        ]);

        (_, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split([msg]);

        Assert.False(trigger.IsToolResults);
        Assert.True(trigger.HasAttachments);
        Assert.Equal("describe these", trigger.UserText);
        Assert.Equal(2, trigger.Attachments!.Count);
        // Content-part order preserved, inline-bytes carrier wired correctly (the source bytes, not a path).
        Assert.Equal(LiteRtAttachmentKind.Image, trigger.Attachments[0].Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, trigger.Attachments[0].Bytes);
        Assert.Null(trigger.Attachments[0].Path);
        Assert.Equal(LiteRtAttachmentKind.Audio, trigger.Attachments[1].Kind);
        Assert.Equal(new byte[] { 4, 5 }, trigger.Attachments[1].Bytes);
    }

    [Fact]
    public void Split_UserMessageWithFileUri_MapsToFilePathAttachment()
    {
        var uri = new Uri(OperatingSystem.IsWindows() ? "file:///C:/tmp/pic.jpg" : "file:///tmp/pic.jpg");
        var msg = new ChatMessage(ChatRole.User, (IList<AIContent>)
        [
            new TextContent("describe"),
            new UriContent(uri, "image/jpeg"),
        ]);

        (_, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split([msg]);

        LiteRtAttachment att = Assert.Single(trigger.Attachments!);
        Assert.Equal(LiteRtAttachmentKind.Image, att.Kind);
        // The file-carrier branch (ImageFile -> Path), distinct from the inline-bytes branch.
        Assert.Equal(uri.LocalPath, att.Path);
        Assert.Null(att.Bytes);
    }

    [Theory]
    [InlineData("https://example.com/pic.png")]                 // remote URI: unfetchable on-device, skipped
    [InlineData("pic.png", UriKind.Relative)]                   // relative URI: skipped, must not throw
    public void Split_UserMessageWithNonFileUri_SkipsIt(string uri, UriKind kind = UriKind.Absolute)
    {
        var msg = new ChatMessage(ChatRole.User, (IList<AIContent>)
        [
            new TextContent("describe"),
            new UriContent(new Uri(uri, kind), "image/png"),
        ]);

        (_, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split([msg]);
        Assert.False(trigger.HasAttachments);
    }

    [Fact]
    public void Split_UnsupportedDataContent_IsSkipped_ImageSurvives()
    {
        var msg = new ChatMessage(ChatRole.User, (IList<AIContent>)
        [
            new DataContent(new byte[] { 9 }, "application/pdf"),   // not image/audio -> ignored
            new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
        ]);

        (_, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split([msg]);
        LiteRtAttachment att = Assert.Single(trigger.Attachments!);
        Assert.Equal(LiteRtAttachmentKind.Image, att.Kind);
    }

    [Fact]
    public void Split_UserMessageWithoutMedia_HasNoAttachments()
    {
        var msg = new ChatMessage(ChatRole.User, "just text");
        (_, LiteRtChatMapping.SendTrigger trigger) = LiteRtChatMapping.Split([msg]);
        Assert.False(trigger.HasAttachments);
        Assert.Null(trigger.Attachments);
    }
}
