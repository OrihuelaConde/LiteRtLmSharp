using LiteRtLmSharp.Extensions.AI;
using LiteRtLmSharp.SemanticKernel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;
// The Semantic Kernel namespace also defines these content types; disambiguate to the MEAI ones.
using TextContent = Microsoft.Extensions.AI.TextContent;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-free unit tests for the opt-in stateful-conversations mode of the Microsoft.Extensions.AI connector:
/// the <see cref="LiteRtStatefulConversationOptions"/> validation, the continuation-message mapping
/// (<see cref="LiteRtChatMapping.SplitContinuation"/>), and DI registration of a stateful client. The live
/// LRU store and the end-to-end MEAI stateful contract are exercised by the gated model-backed tests in
/// <see cref="ExtensionsAiStatefulModelTests"/> (they need a real engine to hold a native conversation).
/// </summary>
public class StatefulConversationMappingTests
{
    private static readonly IReadOnlyDictionary<string, string> NoKnownCalls = new Dictionary<string, string>(0);

    // ─────────────────────── Options validation ───────────────────────

    [Fact]
    public void Options_Default_MaxLiveConversationsIsEight()
    {
        Assert.Equal(8, new LiteRtStatefulConversationOptions().MaxLiveConversations);
    }

    [Fact]
    public void Options_PositiveMaxLiveConversations_IsAccepted()
    {
        Assert.Equal(3, new LiteRtStatefulConversationOptions { MaxLiveConversations = 3 }.MaxLiveConversations);
        Assert.Equal(1, new LiteRtStatefulConversationOptions { MaxLiveConversations = 1 }.MaxLiveConversations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Options_NonPositiveMaxLiveConversations_Throws(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtStatefulConversationOptions { MaxLiveConversations = value });
    }

    // ─────────────────────── Continuation mapping: user turn ───────────────────────

    [Fact]
    public void SplitContinuation_TrailingUserMessage_ProducesUserTrigger_NoHistory()
    {
        // A continuation sends only the new user turn; the native conversation already holds the prior turns.
        LiteRtChatMapping.SendTrigger trigger =
            LiteRtChatMapping.SplitContinuation([new ChatMessage(ChatRole.User, "what is my favorite color?")], NoKnownCalls);

        Assert.False(trigger.IsToolResults);
        Assert.Equal("what is my favorite color?", trigger.UserText);
        Assert.False(trigger.HasAttachments);
    }

    [Fact]
    public void SplitContinuation_UserMessageWithImage_MapsAttachment()
    {
        var msg = new ChatMessage(ChatRole.User, (IList<AIContent>)
        [
            new TextContent("describe"),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
        ]);

        LiteRtChatMapping.SendTrigger trigger = LiteRtChatMapping.SplitContinuation([msg], NoKnownCalls);

        Assert.True(trigger.HasAttachments);
        LiteRtAttachment att = Assert.Single(trigger.Attachments!);
        Assert.Equal(LiteRtAttachmentKind.Image, att.Kind);
    }

    // ─────────────────────── Continuation mapping: tool results ───────────────────────

    [Fact]
    public void SplitContinuation_ToolResults_ResolvesNameFromKnownCallMap()
    {
        // The FICC continuation sends ONLY the tool-result message; the assistant tool-call turn that named
        // the call is NOT resent, so the tool name must come from the stored conversation's accumulated map.
        var known = new Dictionary<string, string> { ["get_weather_0"] = "get_weather" };
        var toolMsg = new ChatMessage(ChatRole.Tool, (IList<AIContent>)
            [new FunctionResultContent("get_weather_0", "22 and sunny")]);

        LiteRtChatMapping.SendTrigger trigger = LiteRtChatMapping.SplitContinuation([toolMsg], known);

        Assert.True(trigger.IsToolResults);
        LiteRtToolResult result = Assert.Single(trigger.ToolResults!);
        Assert.Equal("get_weather", result.Name);                 // resolved from the known map, not the raw id
        Assert.Equal("\"22 and sunny\"", result.ResultJson);
    }

    [Fact]
    public void SplitContinuation_ToolResults_ResolvesNameFromInlineFunctionCall()
    {
        // If the incoming list happens to carry the assistant tool-call turn too, its FunctionCallContent
        // supplements (and overrides) the known map for name resolution.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, (IList<AIContent>)[
                new FunctionCallContent("find_0", "find_user", new Dictionary<string, object?> { ["name"] = "bob" })]),
            new(ChatRole.Tool, (IList<AIContent>)[new FunctionResultContent("find_0", "found")]),
        };

        LiteRtChatMapping.SendTrigger trigger = LiteRtChatMapping.SplitContinuation(messages, NoKnownCalls);

        Assert.True(trigger.IsToolResults);
        Assert.Equal("find_user", Assert.Single(trigger.ToolResults!).Name);
    }

    // ─────────────────────── Continuation mapping: rejections ───────────────────────

    [Fact]
    public void SplitContinuation_SystemMessage_ThrowsInvalidOperation()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a pirate now."),
            new(ChatRole.User, "hi"),
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => LiteRtChatMapping.SplitContinuation(messages, NoKnownCalls));
        Assert.Contains("system", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitContinuation_EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => LiteRtChatMapping.SplitContinuation([], NoKnownCalls));
    }

    [Fact]
    public void SplitContinuation_LastMessageAssistant_Throws()
    {
        Assert.Throws<ArgumentException>(() => LiteRtChatMapping.SplitContinuation(
            [new ChatMessage(ChatRole.Assistant, "not a valid trigger")], NoKnownCalls));
    }

    [Fact]
    public void SplitContinuation_EmptyToolMessage_Throws()
    {
        var toolMsg = new ChatMessage(ChatRole.Tool, (IList<AIContent>)[new TextContent("no results here")]);
        Assert.Throws<ArgumentException>(() => LiteRtChatMapping.SplitContinuation([toolMsg], NoKnownCalls));
    }

    // ─────────────────────── DI registration (Extensions.AI) ───────────────────────

    [Fact]
    public void AddLiteRtChatClient_FromOptions_WithStateful_RegistersOneEngineAndClient()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };
        var stateful = new LiteRtStatefulConversationOptions { MaxLiveConversations = 4 };

        var services = new ServiceCollection();
        services.AddLiteRtChatClient(options, statefulConversations: stateful);   // lazy engine; never loaded here

        Assert.Single(services, d => d.ServiceType == typeof(LiteRtEngine));
        Assert.Single(services, d => d.ServiceType == typeof(IChatClient));
    }

    [Fact]
    public void AddLiteRtChatClient_FromOptions_WithTemplateAndStateful_RegistersClient()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };
        var template = new LiteRtConversationOptions { SystemMessage = "You are terse." };
        var stateful = new LiteRtStatefulConversationOptions();

        var services = new ServiceCollection();
        services.AddLiteRtChatClient(options, optionsTemplate: template, statefulConversations: stateful);

        Assert.Single(services, d => d.ServiceType == typeof(IChatClient));
    }

    // ─────────────────────── DI registration (Semantic Kernel) ───────────────────────

    [Fact]
    public void AddLiteRtChatCompletion_WithStateful_RegistersClientAndCompletionService()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(options, statefulConversations: new LiteRtStatefulConversationOptions());

        Assert.Single(builder.Services, d => d.ServiceType == typeof(LiteRtEngine));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IChatClient));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IChatCompletionService));
    }
}
