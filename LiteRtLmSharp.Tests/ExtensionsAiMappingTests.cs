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

        (IReadOnlyList<LiteRtMessage> history, string userText) = LiteRtChatMapping.Split(msgs);

        Assert.Equal("second", userText);
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
    public void ToConversationOptions_MapsSamplerAndMaxTokens()
    {
        var opts = new ChatOptions { Temperature = 0.3f, TopP = 0.8f, TopK = 20, MaxOutputTokens = 128 };

        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], opts);

        Assert.NotNull(conv);
        Assert.Equal(128, conv!.MaxOutputTokens);
        Assert.NotNull(conv.Sampler);
        Assert.Equal(SamplerType.TopP, conv.Sampler!.Type);
        Assert.Equal(0.3f, conv.Sampler.Temperature, 3);
        Assert.Equal(0.8f, conv.Sampler.TopP, 3);
        Assert.Equal(20, conv.Sampler.TopK);
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
}
