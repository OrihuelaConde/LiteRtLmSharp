using LiteRtLmSharp.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.TextGeneration;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-free unit tests for the Semantic Kernel connector's pure mapping logic: ChatHistory → stateless
/// conversation split, and PromptExecutionSettings → typed settings → SamplerParams. These run in CI with
/// no native binaries or model; model-backed behavior is exercised by the console sample.
/// </summary>
public class SemanticKernelMappingTests
{
    // ─────────────────────── ChatHistory mapping ───────────────────────

    [Fact]
    public void Split_EmptyHistory_Throws()
    {
        var history = new ChatHistory();
        Assert.Throws<ArgumentException>(() => LiteRtChatMapping.Split(history));
    }

    [Fact]
    public void Split_HistoryNotEndingWithUser_Throws()
    {
        var history = new ChatHistory();
        history.AddUserMessage("hi");
        history.AddAssistantMessage("hello");
        // Ends with an assistant message — unsupported (we respond to the final user turn).
        Assert.Throws<ArgumentException>(() => LiteRtChatMapping.Split(history));
    }

    [Fact]
    public void Split_MapsRolesAndExtractsFinalUserText()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("You are helpful.");
        history.AddUserMessage("first");
        history.AddAssistantMessage("answer");
        history.AddUserMessage("second");

        (IReadOnlyList<LiteRtMessage> prior, string userText) = LiteRtChatMapping.Split(history);

        Assert.Equal("second", userText);
        Assert.Equal(3, prior.Count);
        Assert.Equal(LiteRtMessageRole.System, prior[0].Role);
        Assert.Equal("You are helpful.", prior[0].Text);
        Assert.Equal(LiteRtMessageRole.User, prior[1].Role);
        Assert.Equal("first", prior[1].Text);
        Assert.Equal(LiteRtMessageRole.Model, prior[2].Role);
        Assert.Equal("answer", prior[2].Text);
    }

    [Fact]
    public void Split_SingleUserMessage_NoPriorHistory()
    {
        var history = new ChatHistory();
        history.AddUserMessage("just this");

        (IReadOnlyList<LiteRtMessage> prior, string userText) = LiteRtChatMapping.Split(history);

        Assert.Empty(prior);
        Assert.Equal("just this", userText);
    }

    [Fact]
    public void Split_UnsupportedRole_Throws()
    {
        var history = new ChatHistory();
        history.AddMessage(AuthorRole.Tool, "tool output");
        history.AddUserMessage("now answer");
        Assert.Throws<NotSupportedException>(() => LiteRtChatMapping.Split(history));
    }

    // ─────────────────────── Execution settings ───────────────────────

    [Fact]
    public void FromExecutionSettings_Null_ReturnsDefaults()
    {
        LiteRtPromptExecutionSettings settings = LiteRtPromptExecutionSettings.FromExecutionSettings(null);
        Assert.Null(settings.Temperature);
        Assert.Null(settings.TopP);
        Assert.Null(settings.MaxTokens);
        Assert.Null(settings.ToSamplerParams());
    }

    [Fact]
    public void FromExecutionSettings_AlreadyTyped_ReturnsSameInstance()
    {
        var typed = new LiteRtPromptExecutionSettings { Temperature = 0.5f };
        LiteRtPromptExecutionSettings result = LiteRtPromptExecutionSettings.FromExecutionSettings(typed);
        Assert.Same(typed, result);
    }

    [Fact]
    public void FromExecutionSettings_GenericWithExtensionData_RecoversTypedValues()
    {
        // Simulates settings declared in a prompt template (the values arrive as [JsonExtensionData]).
        var generic = new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = 0.3,
                ["top_p"] = 0.8,
                ["top_k"] = 20,
                ["max_tokens"] = 128,
                ["seed"] = 7,
            },
        };

        LiteRtPromptExecutionSettings settings = LiteRtPromptExecutionSettings.FromExecutionSettings(generic);

        Assert.Equal(0.3f, settings.Temperature!.Value, 3);
        Assert.Equal(0.8f, settings.TopP!.Value, 3);
        Assert.Equal(20, settings.TopK);
        Assert.Equal(128, settings.MaxTokens);
        Assert.Equal(7, settings.Seed);
    }

    [Fact]
    public void ToSamplerParams_NoKnobsSet_ReturnsNull()
    {
        var settings = new LiteRtPromptExecutionSettings { MaxTokens = 64 }; // MaxTokens alone is not a sampler knob
        Assert.Null(settings.ToSamplerParams());
    }

    [Fact]
    public void ToSamplerParams_SetsTopPSamplerAndFillsDefaults()
    {
        var settings = new LiteRtPromptExecutionSettings { Temperature = 0.2f };
        SamplerParams? sampler = settings.ToSamplerParams();

        Assert.NotNull(sampler);
        Assert.Equal(SamplerType.TopP, sampler!.Type);
        Assert.Equal(0.2f, sampler.Temperature, 3);
        Assert.Equal(40, sampler.TopK);       // default carried over
        Assert.Equal(0.95f, sampler.TopP, 3); // default carried over
    }

    // ─────────────────────── DI registration (options path) ───────────────────────
    // These inspect the registered descriptors only — they never build the provider or load a model
    // (the lazy engine factory is not invoked), so they run model-free in CI. The model path is bogus
    // on purpose: it is only read if the engine is actually loaded, which these tests never trigger.

    [Fact]
    public void AddChatAndTextFromOptions_RegisterOneSharedEngine()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(options);
        builder.AddLiteRtTextGeneration(options);

        // One engine per process: both services must share a single registered LiteRtEngine.
        Assert.Single(builder.Services, d => d.ServiceType == typeof(LiteRtEngine));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IChatCompletionService));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(ITextGenerationService));
    }

    [Fact]
    public void AddChatFromOptions_RegistersChatService()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(options, new LiteRtPromptExecutionSettings { Temperature = 0.5f }, modelId: "m");

        Assert.Single(builder.Services, d => d.ServiceType == typeof(IChatCompletionService));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(LiteRtEngine));
    }

    [Fact]
    public void AddLiteRtEngine_IsIdempotent_FirstRegistrationWins()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };

        var services = new ServiceCollection();
        services.AddLiteRtEngine(options);
        services.AddLiteRtEngine(options);   // second call must not add a second engine

        Assert.Single(services, d => d.ServiceType == typeof(LiteRtEngine));
    }

    [Fact]
    public void AddLiteRtEngine_ThenChatWithoutEngine_RegistersServiceOverSharedEngine()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtEngine(options);                  // register the shared engine (lazy: no load)
        builder.AddLiteRtChatCompletion(modelId: "m");     // service over the registered engine, no engine arg

        Assert.Single(builder.Services, d => d.ServiceType == typeof(LiteRtEngine));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IChatCompletionService));
    }

    // ─────────────────────── Conversation-options mapping ───────────────────────

    [Fact]
    public void TextGeneration_BuildOptions_MapsSystemPromptToSystemMessage()
    {
        var settings = new LiteRtPromptExecutionSettings { SystemPrompt = "Be terse.", Temperature = 0.5f, MaxTokens = 99 };
        LiteRtConversationOptions options = LiteRtTextGenerationService.BuildOptions(settings);

        Assert.Equal("Be terse.", options.SystemMessage);
        Assert.Equal(99, options.MaxOutputTokens);
        Assert.NotNull(options.Sampler);
        Assert.Equal(0.5f, options.Sampler!.Temperature, 3);
    }

    [Fact]
    public void ChatCompletion_BuildOptions_IgnoresSystemPrompt()
    {
        // Chat carries the system prompt as a System-role history message, so SystemPrompt is intentionally
        // not mapped to SystemMessage here (the contrast with text generation is deliberate).
        var settings = new LiteRtPromptExecutionSettings { SystemPrompt = "ignored here", MaxTokens = 50 };
        LiteRtConversationOptions options = LiteRtChatCompletionService.BuildOptions([], settings);

        Assert.Null(options.SystemMessage);
        Assert.Equal(50, options.MaxOutputTokens);
    }

    // ─────────────────────── Settings: thinking/system-prompt + Clone ───────────────────────

    [Fact]
    public void FromExecutionSettings_RecoversThinkingAndSystemPrompt()
    {
        var generic = new PromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["enable_thinking"] = true,
                ["system_prompt"] = "You are a pirate.",
            },
        };

        LiteRtPromptExecutionSettings settings = LiteRtPromptExecutionSettings.FromExecutionSettings(generic);

        Assert.True(settings.EnableThinking);
        Assert.Equal("You are a pirate.", settings.SystemPrompt);
    }

    [Fact]
    public void Clone_PreservesTypedKnobs()
    {
        var original = new LiteRtPromptExecutionSettings
        {
            Temperature = 0.3f, TopP = 0.7f, TopK = 10, MaxTokens = 123, Seed = 5,
            EnableThinking = true, SystemPrompt = "sys", ModelId = "m",
        };

        var clone = Assert.IsType<LiteRtPromptExecutionSettings>(original.Clone());

        Assert.Equal(0.3f, clone.Temperature!.Value, 3);
        Assert.Equal(0.7f, clone.TopP!.Value, 3);
        Assert.Equal(10, clone.TopK);
        Assert.Equal(123, clone.MaxTokens);
        Assert.Equal(5, clone.Seed);
        Assert.True(clone.EnableThinking);
        Assert.Equal("sys", clone.SystemPrompt);
        Assert.Equal("m", clone.ModelId);

        // The whole reason for overriding Clone: the sampler knobs survive (base Clone would drop them).
        SamplerParams? sampler = clone.ToSamplerParams();
        Assert.NotNull(sampler);
        Assert.Equal(0.3f, sampler!.Temperature, 3);
    }
}
