using System.Text.Json;
using LiteRtLmSharp.SemanticKernel;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-free unit tests for the Semantic Kernel connector after the re-base onto the Microsoft.Extensions.AI
/// <see cref="IChatClient"/> (exposed through SK's <c>AsChatCompletionService</c> adapter). Covers the
/// settings → ExtensionData mapping (which is how the knobs flow through the adapter) and the DI registration.
/// The chat behavior itself is exercised model-free by <see cref="ExtensionsAiMappingTests"/> (the shared
/// IChatClient) and end-to-end by <see cref="SemanticKernelModelTests"/>.
/// </summary>
public class SemanticKernelMappingTests
{
    // ─────────────────────── Settings ↔ ExtensionData ───────────────────────

    [Fact]
    public void Settings_StoreKnobsInExtensionData_UnderWellKnownKeys()
    {
        var s = new LiteRtPromptExecutionSettings
        {
            Temperature = 0.3f, TopP = 0.8f, TopK = 20, MaxTokens = 128, Seed = 7,
            EnableThinking = true, EnableConstrainedDecoding = true,
        };

        Assert.NotNull(s.ExtensionData);
        Assert.Equal(0.3f, Assert.IsType<float>(s.ExtensionData!["temperature"]), 3);
        Assert.Equal(0.8f, Assert.IsType<float>(s.ExtensionData["top_p"]), 3);
        Assert.Equal(20, Assert.IsType<int>(s.ExtensionData["top_k"]));
        Assert.Equal(128, Assert.IsType<int>(s.ExtensionData["max_tokens"]));
        Assert.Equal(7, Assert.IsType<int>(s.ExtensionData["seed"]));
        Assert.True(Assert.IsType<bool>(s.ExtensionData["enable_thinking"]));
        Assert.True(Assert.IsType<bool>(s.ExtensionData["enable_constrained_decoding"]));
    }

    [Fact]
    public void Settings_Getters_RoundTripValuesSet()
    {
        var s = new LiteRtPromptExecutionSettings { Temperature = 0.5f, MaxTokens = 64, EnableThinking = false };

        Assert.Equal(0.5f, s.Temperature!.Value, 3);
        Assert.Equal(64, s.MaxTokens);
        Assert.False(s.EnableThinking);
        Assert.Null(s.TopP); // unset → null
    }

    [Fact]
    public void Settings_Getters_TolerateJsonElement()
    {
        // Simulates settings deserialized from a prompt template's YAML/JSON, where ExtensionData holds JsonElement.
        using var doc = JsonDocument.Parse("""{ "temperature": 0.25, "max_tokens": 99, "enable_thinking": true }""");
        var s = new LiteRtPromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["temperature"] = doc.RootElement.GetProperty("temperature"),
                ["max_tokens"] = doc.RootElement.GetProperty("max_tokens"),
                ["enable_thinking"] = doc.RootElement.GetProperty("enable_thinking"),
            },
        };

        Assert.Equal(0.25f, s.Temperature!.Value, 3);
        Assert.Equal(99, s.MaxTokens);
        Assert.True(s.EnableThinking);
    }

    [Fact]
    public void Settings_SetNull_RemovesKey()
    {
        var s = new LiteRtPromptExecutionSettings { Temperature = 0.5f };
        Assert.True(s.ExtensionData!.ContainsKey("temperature"));

        s.Temperature = null;
        Assert.False(s.ExtensionData.ContainsKey("temperature"));
        Assert.Null(s.Temperature);
    }

    // ─────────────────────── DI registration ───────────────────────

    [Fact]
    public void AddChatCompletionFromOptions_RegistersClientServiceAndOneEngine()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(options);   // lazy: nothing is loaded

        Assert.Single(builder.Services, d => d.ServiceType == typeof(LiteRtEngine));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IChatClient));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IChatCompletionService));
    }

    [Fact]
    public void AddChatCompletionWithServiceId_RegistersKeyedService()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(options, serviceId: "local");

        // Keyed registration (e.g. an on-device service alongside a cloud one).
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IChatCompletionService) && Equals(d.ServiceKey, "local"));
    }
}
