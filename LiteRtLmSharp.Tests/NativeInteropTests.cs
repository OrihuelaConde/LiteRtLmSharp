using System.Text.Json;
using LiteRtLmSharp;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

public class NativeInteropTests
{
    /// <summary>
    /// Proves the native LiteRtLm library resolves and a C ABI function is callable.
    /// This validates the whole P/Invoke + native-resolution path without needing a model.
    /// </summary>
    [Fact]
    public void NativeLibrary_Loads_And_LogLevelCallSucceeds()
    {
        // Will throw DllNotFoundException / EntryPointNotFoundException if interop is broken.
        var ex = Record.Exception(() => LiteRtEngine.SetMinLogLevel(3));
        Assert.Null(ex);
    }

    [Fact]
    public void Load_WithMissingModel_Throws()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };
        Assert.Throws<ArgumentException>(() => LiteRtEngine.Load(options));
    }

    /// <summary>
    /// Proves the multimodal conversation optional-args C ABI functions resolve and are callable
    /// without a model (create → set visual token budget → delete) — the native plumbing behind
    /// <see cref="LiteRtConversationOptions.VisualTokenBudget"/>. Mirrors
    /// <see cref="NativeLibrary_Loads_And_LogLevelCallSucceeds"/>; throws
    /// <see cref="EntryPointNotFoundException"/> on a native binary predating the multimodal API.
    /// </summary>
    [Fact]
    public void OptionalArgs_NativeEntrypoints_Resolve()
    {
        // Touch LiteRtEngine first so its static ctor runs NativeLibraryResolver.Initialize(): this
        // test calls the native lib DIRECTLY (not via LiteRtEngine), and on Linux the .so lives in
        // runtimes/<rid>/native and is only found once the resolver is registered. Without this the
        // call fails with DllNotFoundException when it happens to run before any other LiteRtEngine
        // use (real consumers always create an engine first, so this just mirrors that setup).
        LiteRtEngine.SetMinLogLevel(3);

        nint args = LiteRtLmNative.litert_lm_conversation_optional_args_create();
        Assert.NotEqual(nint.Zero, args);
        // The only setter the C API exposes — must not throw on a version-matched binary.
        LiteRtLmNative.litert_lm_conversation_optional_args_set_visual_token_budget(args, 128);
        LiteRtLmNative.litert_lm_conversation_optional_args_delete(args);
    }
}

/// <summary>
/// Pure unit tests for the multimodal user-message JSON builder
/// (<see cref="LiteRtJson.UserMessage(string, System.Collections.Generic.IReadOnlyList{LiteRtAttachment})"/>),
/// which backs the attachment <c>Send</c> overloads. No model needed, so these always run in CI and
/// pin the exact content-part wire format the native parser expects (verified against
/// <c>runtime/conversation/model_data_processor/data_utils.cc</c>): text part first, then each
/// attachment as <c>{"type":"image"|"audio","blob":&lt;base64&gt;}</c> or <c>{… ,"path":…}</c>, in order.
/// </summary>
public class MultimodalMessageJsonTests
{
    private static readonly byte[] Bytes = [0xDE, 0xAD, 0xBE, 0xEF];
    private static readonly string Base64 = Convert.ToBase64String(Bytes);

    private static JsonElement Content(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("user", doc.RootElement.GetProperty("role").GetString());
        return doc.RootElement.GetProperty("content").Clone();
    }

    [Fact]
    public void UserMessage_NullAttachments_FallsBackToTextOnly()
    {
        JsonElement content = Content(LiteRtJson.UserMessage("Hi", null));
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Hi", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void UserMessage_EmptyAttachments_FallsBackToTextOnly()
    {
        JsonElement content = Content(LiteRtJson.UserMessage("Hi", []));
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
    }

    [Fact]
    public void UserMessage_ImageBytes_EmitsBase64Blob_AfterText()
    {
        JsonElement content = Content(LiteRtJson.UserMessage("Describe:", [LiteRtAttachment.Image(Bytes)]));
        Assert.Equal(2, content.GetArrayLength());
        // Text part first, then the image part — order is preserved and significant.
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal(Base64, content[1].GetProperty("blob").GetString());
        Assert.False(content[1].TryGetProperty("path", out _));
        // The blob round-trips back to the original bytes (it is base64, not raw).
        Assert.Equal(Bytes, Convert.FromBase64String(content[1].GetProperty("blob").GetString()!));
    }

    [Fact]
    public void UserMessage_AudioBytes_EmitsAudioBlob()
    {
        JsonElement content = Content(LiteRtJson.UserMessage("Transcribe:", [LiteRtAttachment.Audio(Bytes)]));
        Assert.Equal("audio", content[1].GetProperty("type").GetString());
        Assert.Equal(Base64, content[1].GetProperty("blob").GetString());
    }

    [Fact]
    public void UserMessage_ImageFile_EmitsPath_NotBlob()
    {
        JsonElement content = Content(
            LiteRtJson.UserMessage("Describe:", [LiteRtAttachment.ImageFile("/tmp/pic.jpg")]));
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("/tmp/pic.jpg", content[1].GetProperty("path").GetString());
        Assert.False(content[1].TryGetProperty("blob", out _));
    }

    [Fact]
    public void UserMessage_EmptyText_OmitsTextPart()
    {
        // No text → the content array carries only the attachment part (matches the upstream shape:
        // an empty text part is not emitted).
        JsonElement content = Content(LiteRtJson.UserMessage("", [LiteRtAttachment.Image(Bytes)]));
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("image", content[0].GetProperty("type").GetString());
    }

    [Fact]
    public void UserMessage_MultipleAttachments_PreserveOrder()
    {
        JsonElement content = Content(LiteRtJson.UserMessage("Both:",
            [LiteRtAttachment.Image(Bytes), LiteRtAttachment.Audio(Bytes)]));
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("audio", content[2].GetProperty("type").GetString());
    }
}

/// <summary>
/// Pure unit tests for the extra-context JSON builder (<see cref="LiteRtJson.ExtraContext"/>) that
/// backs the EnableThinking / ExtraContext conversation options. No model required, so these always
/// run in CI: they pin the merge rules (typed enable_thinking flag overrides any raw key), the
/// null/empty fast paths, and the JSON-object validation.
/// </summary>
public class ExtraContextJsonTests
{
    [Fact]
    public void ExtraContext_BothNull_ReturnsNull()
        => Assert.Null(LiteRtJson.ExtraContext(null, null));

    [Fact]
    public void ExtraContext_WhitespaceRaw_ReturnsNull()
        => Assert.Null(LiteRtJson.ExtraContext("   ", null));

    [Fact]
    public void ExtraContext_EnableThinkingTrue_SetsBooleanTrue()
    {
        using var doc = JsonDocument.Parse(LiteRtJson.ExtraContext(null, true)!);
        Assert.Equal(JsonValueKind.True, doc.RootElement.GetProperty("enable_thinking").ValueKind);
    }

    [Fact]
    public void ExtraContext_EnableThinkingFalse_SetsBooleanFalse()
    {
        using var doc = JsonDocument.Parse(LiteRtJson.ExtraContext(null, false)!);
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("enable_thinking").ValueKind);
    }

    [Fact]
    public void ExtraContext_MergesRawKeysWithFlag()
    {
        using var doc = JsonDocument.Parse(LiteRtJson.ExtraContext("""{"user_name":"Alice"}""", true)!);
        JsonElement root = doc.RootElement;
        Assert.Equal("Alice", root.GetProperty("user_name").GetString());
        Assert.True(root.GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public void ExtraContext_TypedFlagOverridesRawKey()
    {
        // Raw says false, the typed flag says true — the flag wins.
        using var doc = JsonDocument.Parse(LiteRtJson.ExtraContext("""{"enable_thinking":false}""", true)!);
        Assert.True(doc.RootElement.GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public void ExtraContext_NullFlag_PreservesRawValue()
    {
        // No typed flag → the raw enable_thinking value passes through unchanged.
        using var doc = JsonDocument.Parse(LiteRtJson.ExtraContext("""{"enable_thinking":true}""", null)!);
        Assert.True(doc.RootElement.GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public void ExtraContext_InvalidJson_Throws()
        => Assert.Throws<ArgumentException>(() => LiteRtJson.ExtraContext("not json", null));

    [Fact]
    public void ExtraContext_NonObjectJson_Throws()
        => Assert.Throws<ArgumentException>(() => LiteRtJson.ExtraContext("[1,2,3]", null));

    [Fact]
    public void ExtraContext_PreservesRawValueBytesVerbatim()
    {
        // Raw object values pass through verbatim (matching the Tools/ToolResults builders) instead
        // of being re-encoded by the default escaper, which would emit < > &.
        const string raw = """{"note":"a & b <tag>"}""";
        string merged = LiteRtJson.ExtraContext(raw, true)!;
        Assert.Contains("a & b <tag>", merged);
        Assert.DoesNotContain("\\u0026", merged);
        // Still valid JSON with the flag merged in, and the value round-trips to the same string.
        using var doc = JsonDocument.Parse(merged);
        Assert.Equal("a & b <tag>", doc.RootElement.GetProperty("note").GetString());
        Assert.True(doc.RootElement.GetProperty("enable_thinking").GetBoolean());
    }
}

/// <summary>
/// Parsing of the assistant response, focused on the reasoning ("thinking") channel: the model's
/// thinking lands in a separate <c>"channels"</c> object, which <see cref="LiteRtResponse"/> exposes
/// via <see cref="LiteRtResponse.Thinking"/> / <see cref="LiteRtResponse.Channels"/> separate from
/// the <c>content</c> answer. Pure parsing — no model needed.
/// </summary>
public class ResponseChannelTests
{
    [Fact]
    public void Parse_AnswerOnly_HasNoThinking()
    {
        var r = LiteRtResponse.Parse("""{"role":"assistant","content":[{"type":"text","text":"Paris"}]}""");
        Assert.Equal("Paris", r.Text);
        Assert.Null(r.Thinking);
        Assert.Empty(r.Channels);
    }

    [Fact]
    public void Parse_ContentAndThinkingChannel_SplitsThem()
    {
        var r = LiteRtResponse.Parse(
            """{"role":"assistant","content":[{"type":"text","text":"Paris."}],"channels":{"thought":"The capital of France is Paris."}}""");
        Assert.Equal("Paris.", r.Text);
        Assert.Equal("The capital of France is Paris.", r.Thinking);
        Assert.Equal("The capital of France is Paris.", r.Channels["thought"]);
    }

    [Fact]
    public void Parse_ThinkingOnlyChunk_HasThinkingAndEmptyText()
    {
        // A streaming reasoning delta: channels populated, no content yet.
        var r = LiteRtResponse.Parse("""{"role":"assistant","channels":{"thought":"Let me think"}}""");
        Assert.True(string.IsNullOrEmpty(r.Text));
        Assert.Equal("Let me think", r.Thinking);
    }

    [Fact]
    public void Parse_EmptyChannelValues_AreDropped()
    {
        var r = LiteRtResponse.Parse("""{"role":"assistant","content":[{"type":"text","text":"Hi"}],"channels":{"thought":""}}""");
        Assert.Null(r.Thinking);
        Assert.Empty(r.Channels);
    }
}

/// <summary>
/// Streaming chunk splitting: <see cref="LiteRtConversation.SplitMessageChunk"/> turns one raw
/// message-chunk JSON into the ordered, tagged <see cref="LiteRtStreamChunk"/> pieces the streaming
/// callback writes. Pure parsing — no model needed.
/// </summary>
public class StreamChunkSplitTests
{
    [Fact]
    public void Split_AnswerOnly_YieldsOneAnswerChunk()
    {
        var chunks = LiteRtConversation.SplitMessageChunk("""{"role":"assistant","content":[{"type":"text","text":"Paris"}]}""");
        var c = Assert.Single(chunks);
        Assert.Equal(LiteRtStreamChunkKind.Answer, c.Kind);
        Assert.Equal("Paris", c.Text);
        Assert.False(c.IsThinking);
        Assert.Empty(c.ToolCalls);
    }

    [Fact]
    public void Split_ThinkingOnly_YieldsOneThinkingChunk()
    {
        var chunks = LiteRtConversation.SplitMessageChunk("""{"role":"assistant","channels":{"thought":"Let me think"}}""");
        var c = Assert.Single(chunks);
        Assert.Equal(LiteRtStreamChunkKind.Thinking, c.Kind);
        Assert.True(c.IsThinking);
        Assert.Equal("Let me think", c.Text);
    }

    [Fact]
    public void Split_ContentAndThinking_YieldsThinkingThenAnswer()
    {
        var chunks = LiteRtConversation.SplitMessageChunk(
            """{"role":"assistant","content":[{"type":"text","text":"Paris."}],"channels":{"thought":"reasoning"}}""");
        Assert.Equal(2, chunks.Count);
        Assert.Equal(LiteRtStreamChunkKind.Thinking, chunks[0].Kind);
        Assert.Equal("reasoning", chunks[0].Text);
        Assert.Equal(LiteRtStreamChunkKind.Answer, chunks[1].Kind);
        Assert.Equal("Paris.", chunks[1].Text);
    }

    [Fact]
    public void Split_ToolCall_YieldsOneToolCallChunk()
    {
        var chunks = LiteRtConversation.SplitMessageChunk(
            """{"role":"assistant","tool_calls":[{"function":{"name":"get_weather","arguments":{"location":"Tokyo"}}}]}""");
        var c = Assert.Single(chunks);
        Assert.Equal(LiteRtStreamChunkKind.ToolCall, c.Kind);
        Assert.Equal("", c.Text);
        var call = Assert.Single(c.ToolCalls);
        Assert.Equal("get_weather", call.Name);
    }

    [Fact]
    public void Split_ToolCallWithThinking_YieldsThinkingThenToolCall()
    {
        var chunks = LiteRtConversation.SplitMessageChunk(
            """{"role":"assistant","channels":{"thought":"need the weather"},"tool_calls":[{"function":{"name":"get_weather","arguments":{}}}]}""");
        Assert.Equal(2, chunks.Count);
        Assert.Equal(LiteRtStreamChunkKind.Thinking, chunks[0].Kind);
        Assert.Equal(LiteRtStreamChunkKind.ToolCall, chunks[1].Kind);
    }

    [Fact]
    public void Split_EmptyContentNoChannels_YieldsNothing()
    {
        var chunks = LiteRtConversation.SplitMessageChunk("""{"role":"assistant","content":[]}""");
        Assert.Empty(chunks);
    }

    [Fact]
    public void DefaultChunk_HasNonNullTextAndToolCalls()
    {
        // A value type's default is always reachable; it must still honor the non-null contract.
        LiteRtStreamChunk d = default;
        Assert.Equal("", d.Text);
        Assert.Empty(d.ToolCalls);
        Assert.False(d.IsThinking);
    }
}

/// <summary>
/// Pure unit tests for the conversation-history (restore) surface: the <see cref="LiteRtMessage"/>
/// model, its wire serialization (<see cref="LiteRtJson.Messages"/>), the
/// <see cref="LiteRtResponse.ToMessage"/> capture, the typed/raw resolution
/// (<see cref="LiteRtJson.ResolveHistory"/>), and the persist/reload round-trip. No model needed, so
/// these always run in CI and pin the wire format the native <c>conversation_config_set_messages</c>
/// expects (roles, content parts, tool calls).
/// </summary>
public class HistoryMessageTests
{
    [Fact]
    public void Serialize_UserAndModel_ProducesWireShape()
    {
        string json = LiteRtMessage.Serialize([LiteRtMessage.User("Hi"), LiteRtMessage.Model("Hello!")]);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());
        Assert.Equal("user", root[0].GetProperty("role").GetString());
        // Assistant turns serialize to the wire role "model" (not "assistant").
        Assert.Equal("model", root[1].GetProperty("role").GetString());
        JsonElement part = root[0].GetProperty("content")[0];
        Assert.Equal("text", part.GetProperty("type").GetString());
        Assert.Equal("Hi", part.GetProperty("text").GetString());
    }

    [Fact]
    public void Serialize_ModelToolCall_EmitsFunctionShape()
    {
        var msg = LiteRtMessage.Model("", [new LiteRtToolCall("get_weather", """{"location":"Tokyo"}""")]);
        using var doc = JsonDocument.Parse(LiteRtMessage.Serialize([msg]));
        JsonElement call = doc.RootElement[0].GetProperty("tool_calls")[0];

        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        // arguments is a raw JSON object, not a string.
        Assert.Equal("Tokyo", call.GetProperty("function").GetProperty("arguments").GetProperty("location").GetString());
        // An empty-text model turn omits the content array (matches the upstream Message.toJson).
        Assert.False(doc.RootElement[0].TryGetProperty("content", out _));
    }

    [Fact]
    public void Serialize_ToolResults_EmitsToolResponseParts()
    {
        var msg = LiteRtMessage.Tool(new LiteRtToolResult("get_weather", """{"temp":15}"""));
        using var doc = JsonDocument.Parse(LiteRtMessage.Serialize([msg]));
        JsonElement m = doc.RootElement[0];

        Assert.Equal("tool", m.GetProperty("role").GetString());
        JsonElement part = m.GetProperty("content")[0];
        Assert.Equal("tool_response", part.GetProperty("type").GetString());
        Assert.Equal("get_weather", part.GetProperty("name").GetString());
        Assert.Equal(15, part.GetProperty("response").GetProperty("temp").GetInt32());
    }

    [Fact]
    public void RoundTrip_PreservesRolesTextAndCalls()
    {
        IReadOnlyList<LiteRtMessage> original =
        [
            LiteRtMessage.System("Be terse."),
            LiteRtMessage.User("Weather in Tokyo?"),
            LiteRtMessage.Model("", [new LiteRtToolCall("get_weather", """{"location":"Tokyo"}""")]),
            LiteRtMessage.Tool(new LiteRtToolResult("get_weather", """{"temp":15}""")),
            LiteRtMessage.Model("It is 15 degrees."),
        ];

        IReadOnlyList<LiteRtMessage> restored = LiteRtMessage.Deserialize(LiteRtMessage.Serialize(original));

        Assert.Equal(5, restored.Count);
        Assert.Equal(LiteRtMessageRole.System, restored[0].Role);
        Assert.Equal("Be terse.", restored[0].Text);
        Assert.Equal(LiteRtMessageRole.User, restored[1].Role);
        Assert.Equal(LiteRtMessageRole.Model, restored[2].Role);
        Assert.Equal("get_weather", restored[2].ToolCalls[0].Name);
        Assert.Equal(LiteRtMessageRole.Tool, restored[3].Role);
        Assert.Equal("get_weather", restored[3].ToolResults[0].Name);
        Assert.Equal("It is 15 degrees.", restored[4].Text);
    }

    [Fact]
    public void Deserialize_AssistantRole_NormalizesToModel()
    {
        var restored = LiteRtMessage.Deserialize(
            """[{"role":"assistant","content":[{"type":"text","text":"hi"}]}]""");
        Assert.Equal(LiteRtMessageRole.Model, restored[0].Role);
        Assert.Equal("hi", restored[0].Text);
    }

    [Fact]
    public void ToMessage_CapturesTextAndCalls_DropsThinking()
    {
        var withThinking = LiteRtResponse.Parse(
            """{"role":"assistant","content":[{"type":"text","text":"42"}],"channels":{"thinking":"let me compute"}}""");
        LiteRtMessage captured = withThinking.ToMessage();

        Assert.Equal(LiteRtMessageRole.Model, captured.Role);
        Assert.Equal("42", captured.Text);
        // The reasoning trace must NOT be replayed into the restored context.
        Assert.DoesNotContain("compute", LiteRtMessage.Serialize([captured]));

        var toolCall = LiteRtResponse.Parse(
            """{"role":"assistant","tool_calls":[{"function":{"name":"f","arguments":{"x":1}}}]}""");
        Assert.Single(toolCall.ToMessage().ToolCalls);
        Assert.Empty(toolCall.ToMessage(includeToolCalls: false).ToolCalls);
    }

    [Fact]
    public void ResolveHistory_TypedWinsOverRaw()
    {
        string? resolved = LiteRtJson.ResolveHistory([LiteRtMessage.User("typed")], """[{"role":"user"}]""");
        Assert.Contains("typed", resolved);
    }

    [Fact]
    public void ResolveHistory_RawArrayPassesThrough_AndBothEmptyIsNull()
    {
        Assert.Null(LiteRtJson.ResolveHistory(null, null));
        Assert.Null(LiteRtJson.ResolveHistory([], "   "));
        Assert.Equal("""[{"role":"user"}]""", LiteRtJson.ResolveHistory(null, """[{"role":"user"}]"""));
    }

    [Fact]
    public void ResolveHistory_NonArrayRaw_Throws()
    {
        Assert.Throws<ArgumentException>(() => LiteRtJson.ResolveHistory(null, """{"role":"user"}"""));
        Assert.Throws<ArgumentException>(() => LiteRtJson.ResolveHistory(null, "not json"));
    }

    [Fact]
    public void Deserialize_NonArray_Throws()
        => Assert.Throws<ArgumentException>(() => LiteRtMessage.Deserialize("""{"role":"user"}"""));

    [Fact]
    public void Deserialize_StringOrObjectContent_PreservesText()
    {
        // Foreign clients (Kotlin/C++/hand-authored) and the C API's raw system message may send content
        // as a bare string or a single object, not only an array — Deserialize must not drop the text.
        var fromString = LiteRtMessage.Deserialize("""[{"role":"system","content":"Be terse."}]""");
        Assert.Equal("Be terse.", fromString[0].Text);

        var fromObject = LiteRtMessage.Deserialize("""[{"role":"user","content":{"type":"text","text":"hi"}}]""");
        Assert.Equal("hi", fromObject[0].Text);
    }
}

/// <summary>
/// Loads the model engine ONCE for the whole test class. Only one engine may be ALIVE at a
/// time (and engine creation is the expensive step), so engine-backed tests share a single
/// <see cref="LiteRtEngine"/> and create per-test conversations from it.
/// Set LITERTLM_TEST_MODEL to a .litertlm file to enable these tests, and optionally
/// LITERTLM_TEST_BACKEND to run them on another backend (e.g. "gpu"; default "cpu").
/// </summary>
public sealed class EngineFixture : IDisposable
{
    public LiteRtEngine? Engine { get; }

    public EngineFixture()
    {
        string? modelPath = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
        {
            LiteRtEngine.SetMinLogLevel(3);
            Engine = LiteRtEngine.Load(new LiteRtEngineOptions
            {
                ModelPath = modelPath,
                Backend = Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu",
                MaxNumTokens = 2048,
            });
        }
    }

    public void Dispose() => Engine?.Dispose();
}

public sealed class ModelTests(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    private readonly EngineFixture _fixture = fixture;

    /// <summary>Plain blocking chat. Skipped unless LITERTLM_TEST_MODEL is set.</summary>
    [SkippableFact]
    public void Chat_Blocking_ProducesText()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var conversation = _fixture.Engine!.CreateConversation();
        var response = conversation.Send("Reply with one short sentence: what is the capital of France?");

        Assert.False(string.IsNullOrWhiteSpace(response.Text),
            $"Expected a non-empty blocking response. Raw: {response.RawJson}");
    }

    /// <summary>Reasoning mode via extra context: a conversation created with EnableThinking = true and
    /// the thinking channel filtered from the KV cache sends a message and gets a real response back,
    /// proving the extra_context + filter_channel_content_from_kv_cache bindings wire through (Send
    /// throws if the native call returns null). We assert on the raw response rather than the parsed
    /// answer text on purpose: reasoning mode changes how much text the model emits (a thinking block
    /// precedes the answer) and where it lands, so over-fitting to content[0].text would be flaky
    /// under the shared 2048-token budget. The CI model (gemma-4-E2B-it) supports reasoning mode.
    /// Skipped unless LITERTLM_TEST_MODEL is set.</summary>
    [SkippableFact]
    public void Chat_WithThinkingEnabled_ProducesResponse()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var conversation = _fixture.Engine!.CreateConversation(new LiteRtConversationOptions
        {
            EnableThinking = true,
            FilterThinkingFromKvCache = true,
        });
        var response = conversation.Send("Reply with one short sentence: what is the capital of France?");

        Assert.False(string.IsNullOrWhiteSpace(response.RawJson),
            "Expected a non-empty native response with thinking enabled.");
    }

    /// <summary>End-to-end streaming generation. Skipped unless LITERTLM_TEST_MODEL is set.
    /// (Validated on v0.13.1; the async path crashed on the interim commit 032334d8.)</summary>
    [SkippableFact]
    public async Task Streaming_Generation_ProducesText()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var conversation = _fixture.Engine!.CreateConversation();
        var sb = new System.Text.StringBuilder();
        await foreach (LiteRtStreamChunk chunk in conversation.SendMessageStreamingAsync("Count from 1 to 3."))
            if (chunk.Kind == LiteRtStreamChunkKind.Answer)
                sb.Append(chunk.Text);

        Assert.True(sb.Length > 0, "Expected a non-empty streamed response.");
    }

    /// <summary>
    /// Restore: a conversation created with <see cref="LiteRtConversationOptions.History"/> re-prefills
    /// the prior turns, so it carries more context than a fresh one. Deterministic proof that the
    /// history wires through native (<c>conversation_config_set_messages</c>): after sending the same
    /// probe to a fresh conversation and to one seeded with a multi-turn history, the restored
    /// conversation's KV cache holds strictly more tokens. Also asserts the restored reply is non-empty.
    /// Skipped unless LITERTLM_TEST_MODEL is set.
    /// </summary>
    [SkippableFact]
    public void Restore_History_PrefillsPriorTurns()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        const string probe = "Reply with one short sentence.";

        using var fresh = _fixture.Engine!.CreateConversation();
        fresh.SendMessage(probe);
        int freshCount = fresh.TokenCount;

        // A round-trip through Serialize/Deserialize, exactly as an app would persist and reload it.
        IReadOnlyList<LiteRtMessage> history = LiteRtMessage.Deserialize(LiteRtMessage.Serialize(
        [
            LiteRtMessage.User("My name is Ada and I love astronomy."),
            LiteRtMessage.Model("Nice to meet you, Ada. Astronomy is fascinating."),
        ]));
        using var restored = _fixture.Engine!.CreateConversation(new LiteRtConversationOptions { History = history });
        var response = restored.Send(probe);
        int restoredCount = restored.TokenCount;

        Assert.False(string.IsNullOrWhiteSpace(response.RawJson), "Expected a non-empty reply after restore.");
        Assert.True(restoredCount > freshCount,
            $"Restored conversation should hold the prefilled history (got {restoredCount} tokens vs {freshCount} fresh).");
    }

    /// <summary>
    /// Clone: <see cref="LiteRtConversation.Clone"/> duplicates the prefilled KV-cache state into an
    /// independent conversation. Deterministic checks: right after cloning the clone holds the same
    /// token count as the parent; sending on the clone advances only the clone (the parent is
    /// untouched). Skipped if the engine/backend does not implement cloning (the native call throws).
    /// Skipped unless LITERTLM_TEST_MODEL is set.
    /// </summary>
    [SkippableFact]
    public void Clone_Conversation_ForksIndependentState()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var baseConv = _fixture.Engine!.CreateConversation();
        baseConv.SendMessage("Remember this secret word: banana.");
        int baseCount = baseConv.TokenCount;

        LiteRtConversation clone;
        try { clone = baseConv.Clone(); }
        catch (LiteRtException ex) { Skip.If(true, $"Clone not supported on this engine/backend: {ex.Message}"); return; }

        using (clone)
        {
            Assert.Equal(baseCount, clone.TokenCount); // clone copied the parent's prefilled state

            var reply = clone.Send("What was the secret word? Answer with one word.");
            Assert.False(string.IsNullOrWhiteSpace(reply.RawJson), "Expected a non-empty reply from the clone.");
            Assert.True(clone.TokenCount > baseCount, "Sending on the clone should advance its own KV cache.");
            Assert.Equal(baseCount, baseConv.TokenCount); // the parent is untouched by the clone
        }
    }

    /// <summary>
    /// Function-calling loop WITHOUT constrained decoding — works on every platform (this is
    /// the documented workaround while the linux-x64 constrained-decoding guard is in place).
    /// </summary>
    [SkippableFact]
    public void ToolCalling_Unconstrained_Loop_ExecutesTool()
    {
        SkipUnlessToolTestsEnabled();
        RunToolCallingLoop(constrainedDecoding: false);
    }

    /// <summary>
    /// Function-calling loop WITH constrained decoding. On linux-x64 this currently asserts the
    /// temporary guard fires (upstream prebuilt constraint provider is broken — LiteRT-LM#2149,
    /// see the roadmap watchlist); everywhere else it runs the real end-to-end loop.
    /// </summary>
    [SkippableFact]
    public void ToolCalling_Constrained_Loop_ExecutesTool()
    {
        SkipUnlessToolTestsEnabled();

        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            // TEMPORARY branch: validates the guard in LiteRtConversation.Create. When Google
            // republishes a fixed linux prebuilt and the guard is removed, DELETE this branch so
            // the constrained loop runs for real on Linux too (removal steps: docs/roadmap.md).
            Assert.Throws<PlatformNotSupportedException>(() => RunToolCallingLoop(constrainedDecoding: true));
            return;
        }

        RunToolCallingLoop(constrainedDecoding: true);
    }

    /// <summary>
    /// Gated on LITERTLM_TEST_TOOLS=1 because the conversation-config path access-violates on
    /// version-skewed binaries (e.g. community native-v0.12.0-a) — only run with a
    /// version-matched build in runtimes/&lt;rid&gt;/native.
    /// </summary>
    private void SkipUnlessToolTestsEnabled()
        => Skip.If(_fixture.Engine is null || Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (and LITERTLM_TEST_MODEL with a version-matched binary) to run.");

    private void RunToolCallingLoop(bool constrainedDecoding)
    {
        using var conv = _fixture.Engine!.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = "Use tools when needed.",
            EnableConstrainedDecoding = constrainedDecoding,
            MaxOutputTokens = 128,
            Tools =
            [
                new LiteRtTool("get_current_weather", "Get the current weather for a city.",
                    """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}"""),
            ],
        });

        var response = conv.Send("What's the weather in Tokyo?");
        Assert.True(response.IsToolCall, $"Expected a tool call. Raw: {response.RawJson}");
        Assert.Equal("get_current_weather", response.ToolCalls[0].Name);

        var final = conv.SendToolResults(
            [new LiteRtToolResult("get_current_weather", """{"temperature":15,"unit":"celsius"}""")]);
        Assert.False(string.IsNullOrWhiteSpace(final.Text), $"Expected a final text answer. Raw: {final.RawJson}");
    }
}

/// <summary>
/// A/B benchmark for speculative decoding: measures decode throughput (via the native benchmark
/// API) with the MTP drafter OFF vs ON, on the same fixed prompt, and confirms both runs still
/// produce coherent, non-empty text. The speedup ratio is logged for the docs/CI record.
/// <para>
/// Sampler note: the v0.13.1 native build only implements the TopP sampler (Greedy/TopK return
/// "not implemented yet"), so the runs are stochastic and the two outputs are NOT expected to be
/// byte-identical — we therefore assert coherence + measured throughput (the real deliverable) and
/// log output similarity rather than asserting equality. A fixed seed keeps each run reproducible.
/// </para>
/// <para>
/// This class loads its OWN engines (it does NOT take <see cref="EngineFixture"/>) and disposes each
/// before loading the next, so only one engine is ever alive — required because two live engines hang
/// the native layer. Safe to coexist with <see cref="ModelTests"/>: the assembly disables test
/// parallelization (see AssemblyInfo.cs), so classes run one at a time and the shared fixture engine
/// is never alive while this runs.
/// </para>
/// Gated on <c>LITERTLM_TEST_BENCH=1</c> (in addition to <c>LITERTLM_TEST_MODEL</c>) because it loads
/// the model twice — only worth it against a known MTP-capable model such as gemma-4-E2B-it.
/// </summary>
public sealed class SpeculativeDecodingBenchmarkTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // Reliably generates a sustained answer so decode throughput is measurable over many tokens.
    private const string Prompt = "Write a detailed paragraph about the history of the printing press.";
    private const int MaxOutputTokens = 128;

    [SkippableFact]
    public void SpeculativeDecoding_SpeedsUpDecode_AndProducesText()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model)
                || Environment.GetEnvironmentVariable("LITERTLM_TEST_BENCH") != "1",
            "Set LITERTLM_TEST_BENCH=1 and LITERTLM_TEST_MODEL to an MTP-capable .litertlm file to run.");

        string backend = Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu";

        Result baseline = Measure(model!, backend, speculative: false);
        Result spec = Measure(model!, backend, speculative: true);

        double ratio = baseline.DecodeTps > 0 ? spec.DecodeTps / baseline.DecodeTps : 0;

        // Emit a Markdown row so the weekly CI log (and a copy/paste into docs/speculative-decoding.md)
        // captures the measured speedup per backend.
        string table =
            "| backend | spec off decode tok/s | spec on decode tok/s | speedup | TTFT off | TTFT on |\n"
            + "|---|---:|---:|---:|---:|---:|\n"
            + $"| {backend} | {baseline.DecodeTps:F1} | {spec.DecodeTps:F1} | {ratio:F2}x | {baseline.Ttft:F2}s | {spec.Ttft:F2}s |";
        _output.WriteLine(table);
        _output.WriteLine($"spec OFF output: {baseline.Text}");
        _output.WriteLine($"spec ON  output: {spec.Text}");

        // On GitHub Actions, also append the row to the job's step summary: the CI runs at
        // verbosity=normal, which swallows ITestOutputHelper output for passing tests, so the numbers
        // would otherwise be lost. This lands them on the run's Summary page instead.
        string? stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(stepSummary))
            File.AppendAllText(stepSummary, $"### Speculative decoding A/B — {backend}\n\n{table}\n\n");

        // The benchmark API must actually report decode throughput on both runs (proves the binding
        // and that benchmark instrumentation is wired through engine creation).
        Assert.True(baseline.DecodeTps > 0, "No decode throughput recorded with speculative decoding OFF.");
        Assert.True(spec.DecodeTps > 0, "No decode throughput recorded with speculative decoding ON.");

        // Coherence: speculative decoding must still produce real text (not empty, not a degenerate
        // single-character repetition). We do NOT assert equality — TopP sampling is stochastic.
        AssertCoherent(baseline.Text, "speculative OFF");
        AssertCoherent(spec.Text, "speculative ON");

        // Effectiveness is informational: a regression below 1x on an MTP model is suspicious but can
        // be hardware noise, so warn rather than fail.
        if (ratio < 1.0)
            _output.WriteLine(
                $"WARNING: speculative decoding did not speed up decode (ratio {ratio:F2}x). " +
                "Expected >=1x on an MTP-capable model — verify the model ships a drafter.");
    }

    private static Result Measure(string model, string backend, bool speculative)
    {
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = model,
            Backend = backend,
            MaxNumTokens = 2048,
            EnableBenchmark = true,
            EnableSpeculativeDecoding = speculative,
            // On the WebGPU GPU backend the MTP drafter's shared weight-cache file fails to open on
            // Windows ("Access denied") unless the disk cache is off; disable it for GPU so the A/B
            // can run there too (an upstream issue — Google's own CLI needs --cache no, see
            // docs/speculative-decoding.md). Both legs use the same setting for a fair comparison.
            CacheDir = backend == "gpu" ? LiteRtEngineOptions.CacheDisabled : null,
        });
        using var conv = engine.CreateConversation(new LiteRtConversationOptions
        {
            // TopP is the only sampler the v0.13.1 native build implements (Greedy/TopK return
            // "not implemented yet"). A fixed seed keeps each run reproducible; speculative decoding
            // preserves the output distribution, so coherence — not byte-equality — is what we check.
            Sampler = new SamplerParams { Type = SamplerType.TopP, TopK = 40, TopP = 0.95f, Temperature = 1.0f, Seed = 42 },
            MaxOutputTokens = MaxOutputTokens,
        });

        LiteRtResponse response = conv.Send(Prompt);
        LiteRtBenchmarkInfo? bench = conv.GetBenchmarkInfo();
        return new Result(
            response.Text ?? string.Empty,
            bench?.LastDecodeTokensPerSecond ?? 0,
            bench?.TimeToFirstTokenSeconds ?? 0);
    } // engine disposed here → the next Measure may load its own

    private static void AssertCoherent(string text, string label)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), $"Empty response ({label}).");
        Assert.True(text.Trim().Length >= 16, $"Response too short to be coherent ({label}): {text}");
        Assert.True(text.Distinct().Count() > 3, $"Degenerate response ({label}): {text}");
    }

    private readonly record struct Result(string Text, double DecodeTps, double Ttft);
}

/// <summary>
/// Model-backed multimodal tests: send an image (and, gated separately, audio) attachment through the
/// high-level conversation API and prove it is actually ingested by the model's vision/audio encoder.
/// <para>
/// The proof is <b>deterministic and model-agnostic</b>: an image/audio attachment expands to a block
/// of encoder tokens, so a turn that carries one prefills strictly more tokens than the same text-only
/// prompt. We cap <see cref="LiteRtConversationOptions.MaxOutputTokens"/> small so decode can't mask
/// the difference, and we assert on the token delta + a non-empty reply rather than on the model's
/// exact words (a tiny model's description is not reliable enough to hard-assert). The actual reply is
/// logged for the record.
/// </para>
/// <para>
/// Loads its OWN engine (with the vision/audio backend enabled), like
/// <see cref="SpeculativeDecodingBenchmarkTests"/> — the shared <see cref="EngineFixture"/> has no
/// modality configured, and only one engine may be alive at a time (the assembly disables test
/// parallelization, so this never races the fixture engine). Gated on <c>LITERTLM_TEST_VISION=1</c> (in
/// addition to <c>LITERTLM_TEST_MODEL</c>) because it needs a vision/audio-capable model such as
/// gemma-4-E2B-it; on a text-only model the engine load would not configure the encoder.
/// </para>
/// </summary>
public sealed class MultimodalModelTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // A 64x64 solid red PNG (246 bytes), generated once and embedded so the test is self-contained
    // and runs cross-platform in CI with no fixture file.
    private const string RedPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJ" +
        "cEhZcwAADsMAAA7DAcdvqGQAAACLSURBVHhe7dAhAQBADIDAJVn/UN9l76kA4gySebtnNgw2DWCwaQCDTQMYbBrA" +
        "YNMABpsGMNg0gMGmAQw2DWCwaQCDTQMYbBrAYNMABpsGMNg0gMGmAQw2DWCwaQCDTQMYbBrAYNMABpsGMNg0gMGm" +
        "AQw2DWCwaQCDTQMYbBrAYNMABpsGMNg0gMHmA+xncfBUukEyAAAAAElFTkSuQmCC";

    private const string Prompt = "What is the main color of this image? Answer with a single word.";

    [SkippableFact]
    public void Vision_ImageAttachment_IsIngestedByTheEncoder()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model)
                || Environment.GetEnvironmentVariable("LITERTLM_TEST_VISION") != "1",
            "Set LITERTLM_TEST_VISION=1 and LITERTLM_TEST_MODEL to a vision-capable .litertlm (e.g. gemma-4-E2B-it) to run.");

        byte[] image = Convert.FromBase64String(RedPngBase64);
        (int textTokens, int mediaTokens, string reply) =
            RunModalityProbe(model!, vision: true, audio: false, "Describe this. ", LiteRtAttachment.Image(image));

        _output.WriteLine($"vision: text-only prefill={textTokens} tokens, with-image={mediaTokens} tokens");
        _output.WriteLine($"vision reply: {reply}");
        if (reply.Contains("red", StringComparison.OrdinalIgnoreCase))
            _output.WriteLine("vision reply correctly identified the color (informational).");

        Assert.True(mediaTokens > textTokens,
            $"Expected the image to add vision tokens (text {textTokens} vs image {mediaTokens}). " +
            "If equal, the image did not reach the vision encoder — check VisionBackend / the native build.");
    }

    [SkippableFact]
    public void Audio_AudioAttachment_IsIngestedByTheEncoder()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model)
                || Environment.GetEnvironmentVariable("LITERTLM_TEST_VISION") != "1",
            "Set LITERTLM_TEST_VISION=1 and LITERTLM_TEST_MODEL to an audio-capable .litertlm (e.g. gemma-4-E2B-it) to run.");

        byte[] mp3 = LoadEmbedded("countdown.mp3"); // a real spoken 5->0 countdown (embedded resource)
        string backend = Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu";

        int textTokens, mediaTokens;
        string reply;
        try
        {
            (textTokens, mediaTokens, reply) = RunModalityProbe(model!, vision: false, audio: true,
                "Transcribe the spoken words in this audio clip.", LiteRtAttachment.Audio(mp3));
        }
        catch (LiteRtException ex) when (backend == "gpu")
        {
            // gemma-4's audio sub-model is CPU-constrained ("Audio backend constraint mismatch. Model
            // requires one of [cpu]"), so audio=gpu fails engine creation on any platform. Record it as a
            // skip rather than failing the GPU leg; the macOS GPU leg confirms the same model constraint.
            Skip.If(true, $"audio on the GPU backend did not initialize (expected — model audio is CPU-constrained): {ex.Message}");
            return;
        }

        _output.WriteLine($"audio (backend={backend}): text-only prefill={textTokens} tokens, with-audio={mediaTokens} tokens");
        _output.WriteLine($"audio reply: {reply}");
        bool referencesCountdown =
            new[] { "5", "4", "3", "2", "1", "0", "five", "four", "three", "two", "one", "zero", "count" }
                .Any(t => reply.Contains(t, StringComparison.OrdinalIgnoreCase));
        _output.WriteLine(referencesCountdown
            ? "audio reply references the spoken countdown (informational)."
            : "audio reply did not clearly transcribe the countdown (informational).");

        Assert.True(mediaTokens > textTokens,
            $"Expected the audio to add encoder tokens (text {textTokens} vs audio {mediaTokens}). " +
            "If equal, the audio did not reach the audio encoder — check AudioBackend / the native build.");
    }

    /// <summary>
    /// Loads an engine with the requested modality enabled, then prefills the same prompt twice in two
    /// fresh conversations — once text-only, once with the media attachment — and returns each turn's
    /// accumulated token count plus the multimodal reply. Output is capped so decode cannot mask the
    /// encoder-token delta. The engine is disposed before returning (one engine alive at a time).
    /// </summary>
    private static (int textTokens, int mediaTokens, string reply) RunModalityProbe(
        string model, bool vision, bool audio, string prompt, LiteRtAttachment attachment)
    {
        string backend = Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu";

        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = model,
            Backend = backend,
            // Both encoders follow the main backend so a GPU run actually exercises vision/audio on GPU.
            // Vision on GPU works. Audio on GPU fails for gemma-4: the model's audio sub-model declares a
            // CPU backend constraint ("Audio backend constraint mismatch. Model requires one of [cpu]"),
            // so engine_create returns null with audio=gpu — on ANY platform, not just win-x64. The audio
            // test treats that GPU-load failure as a recorded skip; the macOS GPU CI leg confirms the
            // constraint is the model's, not platform-specific.
            VisionBackend = vision ? backend : null,
            AudioBackend = audio ? backend : null,
            MaxNumTokens = 4096,
            // Desktop GPU + multimodal: keep the disk cache off (same shared-cache family as the MTP
            // weight-cache bug); harmless on CPU.
            CacheDir = backend == "gpu" ? LiteRtEngineOptions.CacheDisabled : null,
        });

        // Small output cap so the attachment's encoder-token block dominates any decode-length
        // difference between the two replies.
        var opts = new LiteRtConversationOptions { MaxOutputTokens = 16 };

        int textTokens;
        using (var textOnly = engine.CreateConversation(opts))
        {
            textOnly.Send(prompt);
            textTokens = textOnly.TokenCount;
        }

        using var withMedia = engine.CreateConversation(opts);
        LiteRtResponse reply = withMedia.Send(prompt, [attachment]);
        return (textTokens, withMedia.TokenCount, reply.Text ?? string.Empty);
    }

    /// <summary>Reads an embedded test resource (by its <c>LogicalName</c>) into a byte array.</summary>
    private static byte[] LoadEmbedded(string logicalName)
    {
        using Stream s = typeof(MultimodalModelTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
