using System.Text.Json;
using LiteRtLmSharp.SemanticKernel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;
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

    [Fact]
    public void AddChatCompletionWithTwoServiceIds_RegistersAClientAndEnginePerServiceId()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e2b.litertlm", AllowMultipleEngines = true }, serviceId: "fast");
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e4b.litertlm", AllowMultipleEngines = true }, serviceId: "smart");

        foreach (string id in new[] { "fast", "smart" })
        {
            Assert.Contains(builder.Services, d => d.ServiceType == typeof(IChatCompletionService) && Equals(d.ServiceKey, id));
            Assert.Contains(builder.Services, d => d.ServiceType == typeof(IChatClient) && Equals(d.ServiceKey, id));
            Assert.Contains(builder.Services, d => d.ServiceType == typeof(LiteRtEngine) && Equals(d.ServiceKey, id));
        }
        // The first keyed client is also the unkeyed IChatClient, as before keyed clients existed.
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IChatClient) && d.ServiceKey is null);
    }

    [Fact]
    public async Task AddChatCompletionWithTwoServiceIds_RoutesEachServiceToItsOwnEngine()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e2b-missing.litertlm", AllowMultipleEngines = true }, serviceId: "fast");
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e4b-missing.litertlm", AllowMultipleEngines = true }, serviceId: "smart");
        using ServiceProvider provider = builder.Services.BuildServiceProvider();

        // Resolving a service loads nothing, because Semantic Kernel's default selection resolves every registered
        // one. Its first call does, and a missing path names itself before any native work.
        var fast = provider.GetRequiredKeyedService<IChatCompletionService>("fast");
        var smart = provider.GetRequiredKeyedService<IChatCompletionService>("smart");
        var history = new ChatHistory();
        history.AddUserMessage("hi");

        Assert.Contains("e4b-missing.litertlm",
            (await Assert.ThrowsAsync<ArgumentException>(() => smart.GetChatMessageContentAsync(history))).Message);
        Assert.Contains("e2b-missing.litertlm",
            (await Assert.ThrowsAsync<ArgumentException>(() => fast.GetChatMessageContentAsync(history))).Message);
        // Without a key, the first serviceId's client and engine.
        Assert.Contains("e2b-missing.litertlm",
            Assert.Throws<ArgumentException>(() => provider.GetRequiredService<IChatClient>()).Message);
    }

    [Fact]
    public async Task APromptWithoutAServiceId_LoadsOnlyTheServiceItUses()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "fast-missing.litertlm", AllowMultipleEngines = true }, serviceId: "fast");
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "smart-missing.litertlm", AllowMultipleEngines = true }, serviceId: "smart");
        Kernel kernel = builder.Build();

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => kernel.InvokePromptAsync("hi"));
        Assert.Contains("smart-missing.litertlm", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fast-missing.litertlm", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEngineYouRegisteredWithoutAKey_ServesAServiceId()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<LiteRtEngine>(_ => throw new NotSupportedException("the app's own engine"));
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "local-missing.litertlm" }, serviceId: "local");
        using ServiceProvider provider = builder.Services.BuildServiceProvider();

        Assert.Equal("the app's own engine",
            Assert.Throws<NotSupportedException>(() => provider.GetRequiredKeyedService<IChatClient>("local")).Message);
    }

    // A missing path names itself before any native work. The deadline is for the alias cycles these guard against:
    // the container does not detect them, and the first resolve hangs.
    private static async Task AssertLoadsAsync(Func<object> resolve, string path)
    {
        ArgumentException missing = await Task.Run(() => Assert.Throws<ArgumentException>(resolve)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(path, missing.Message, StringComparison.Ordinal);
    }

    private static readonly LiteRtEngineOptions Shared = new() { ModelPath = "shared-missing.litertlm" };

    [Fact]
    public async Task ReplacingTheUnkeyedRegistration_ThroughAServiceId_DoesNotBuildACycle()
    {
        var services = new ServiceCollection();
        services.AddLiteRtChatClient(Shared);
        services.AddLiteRtChatCompletion(Shared, serviceId: "k");
        services.RemoveAll<IChatClient>();
        services.RemoveAll<LiteRtEngine>();
        services.AddLiteRtChatCompletion(Shared, serviceId: "k");
        using ServiceProvider provider = services.BuildServiceProvider();

        await AssertLoadsAsync(() => provider.GetRequiredKeyedService<LiteRtEngine>("k"), "shared-missing.litertlm");
    }

    [Fact]
    public async Task ReplacingAKey_WhoseEngineTheUnkeyedRegistrationShares_DoesNotBuildACycle()
    {
        var services = new ServiceCollection();
        services.AddKeyedLiteRtChatClient("a", Shared);
        services.AddLiteRtChatClient(Shared);
        services.RemoveAllKeyed<IChatClient>("a");
        services.RemoveAllKeyed<LiteRtEngine>("a");
        services.AddLiteRtChatCompletion(Shared, serviceId: "a");
        using ServiceProvider provider = services.BuildServiceProvider();

        await AssertLoadsAsync(() => provider.GetRequiredKeyedService<IChatClient>("a"), "shared-missing.litertlm");
    }

    [Fact]
    public async Task ReplacingAKey_NextToAnAppsClient_DoesNotBuildACycle()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new StubChatClient());
        services.AddLiteRtChatCompletion(Shared, serviceId: "a");
        services.AddLiteRtChatClient(Shared);
        services.RemoveAllKeyed<IChatClient>("a");
        services.RemoveAllKeyed<LiteRtEngine>("a");
        services.AddLiteRtChatCompletion(Shared, serviceId: "a");
        using ServiceProvider provider = services.BuildServiceProvider();

        await AssertLoadsAsync(() => provider.GetRequiredKeyedService<IChatClient>("a"), "shared-missing.litertlm");
    }

    private static async Task<string> PromptFailureAsync(Kernel kernel, string serviceId)
        => (await Assert.ThrowsAnyAsync<Exception>(() => kernel.InvokePromptAsync(
            "hi", new KernelArguments(new PromptExecutionSettings { ServiceId = serviceId })))).ToString();

    [Fact]
    public async Task AServiceId_AfterAnUnkeyedRegistrationThatSharesAKeysEngine_LoadsItsOwnModel()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddKeyedLiteRtChatClient("a", new LiteRtEngineOptions { ModelPath = "e2b-missing.litertlm" });
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e2b-missing.litertlm" });
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e4b-missing.litertlm", AllowMultipleEngines = true }, modelId: "e4b", serviceId: "smart");

        string failure = await PromptFailureAsync(builder.Build(), "smart");

        Assert.Contains("e4b-missing.litertlm", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("e2b-missing.litertlm", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServiceId_AfterAnUnkeyedServiceOnAnAppsClient_LoadsItsOwnModel()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatClient>(new StubChatClient());
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e2b-missing.litertlm", AllowMultipleEngines = true }, serviceId: "fast");
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e4b-missing.litertlm", AllowMultipleEngines = true });
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e4b-missing.litertlm", AllowMultipleEngines = true }, serviceId: "smart");

        string failure = await PromptFailureAsync(builder.Build(), "smart");

        Assert.Contains("e4b-missing.litertlm", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("e2b-missing.litertlm", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EagerOnASharedEngine_HasNoEffect_OnAPromptThatRunsElsewhere()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(Shared, serviceId: "a");
        builder.AddLiteRtChatCompletion(Shared, serviceId: "b", eager: true);   // shares a's engine: nothing loads
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "last-missing.litertlm", AllowMultipleEngines = true }, serviceId: "last");

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => builder.Build().InvokePromptAsync("hi"));

        Assert.Contains("last-missing.litertlm", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("shared-missing.litertlm", failure.ToString(), StringComparison.Ordinal);
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public void TwoServiceIds_NextToAnAppsOwnClient_EachLoadTheirOwnEngine()
    {
        // The unkeyed alias of the first serviceId's engine must not pass for an engine the app registered.
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatClient>(_ => throw new NotSupportedException("the app's own client"));
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "fast-missing.litertlm", AllowMultipleEngines = true }, serviceId: "fast");
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "smart-missing.litertlm", AllowMultipleEngines = true }, serviceId: "smart");
        using ServiceProvider provider = builder.Services.BuildServiceProvider();

        Assert.Contains("smart-missing.litertlm",
            Assert.Throws<ArgumentException>(() => provider.GetRequiredKeyedService<IChatClient>("smart")).Message);
    }

    [Fact]
    public void AnEngineYouRegisteredWithoutAKey_ServesEveryLaterServiceId()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<LiteRtEngine>(_ => throw new NotSupportedException("the app's own engine"));
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "a-missing.litertlm" }, serviceId: "a");
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "b-missing.litertlm" }, serviceId: "b");
        using ServiceProvider provider = builder.Services.BuildServiceProvider();

        foreach (string id in new[] { "a", "b" })
            Assert.Equal("the app's own engine",
                Assert.Throws<NotSupportedException>(() => provider.GetRequiredKeyedService<IChatClient>(id)).Message);
    }

    [Fact]
    public async Task AUnkeyedService_DoesNotLoadItsEngine_OnAPromptThatRunsOnAnother()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "unkeyed-missing.litertlm", AllowMultipleEngines = true });
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "smart-missing.litertlm", AllowMultipleEngines = true }, serviceId: "smart");
        Kernel kernel = builder.Build();

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => kernel.InvokePromptAsync("hi"));
        Assert.DoesNotContain("unkeyed-missing.litertlm", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyedService_ReportsTheModelIdOfTheClientItRunsOn()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddKeyedLiteRtChatClient("a", new LiteRtEngineOptions { ModelPath = "a-missing.litertlm" }, modelId: "first");
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "a-missing.litertlm" }, modelId: "second", serviceId: "a");
        using ServiceProvider provider = builder.Services.BuildServiceProvider();

        Assert.Equal("first", provider.GetRequiredKeyedService<IChatCompletionService>("a").GetModelId());
    }

    [Fact]
    public void AnAppsOwnUnkeyedClient_StillGetsTheUnkeyedEngine_OfAServiceId()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatClient>(_ => throw new NotSupportedException("the app's own client"));
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "local-missing.litertlm" }, serviceId: "local");
        using ServiceProvider provider = builder.Services.BuildServiceProvider();

        Assert.Contains("local-missing.litertlm",
            Assert.Throws<ArgumentException>(() => provider.GetRequiredService<LiteRtEngine>()).Message);
        Assert.Equal("the app's own client",
            Assert.Throws<NotSupportedException>(() => provider.GetRequiredService<IChatClient>()).Message);
    }

    [Fact]
    public void AnUnkeyedRegistrationAfterAServiceId_ThrowsForOtherOptions_AndIsANoOpForEqualOnes()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e4b.litertlm" }, serviceId: "smart");

        var conflict = Assert.Throws<InvalidOperationException>(
            () => builder.Services.AddLiteRtChatClient(new LiteRtEngineOptions { ModelPath = "e2b.litertlm" }));
        Assert.Contains("'smart'", conflict.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(
            () => builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "e2b.litertlm" }));
        builder.Services.AddLiteRtChatClient(new LiteRtEngineOptions { ModelPath = "e4b.litertlm" });

        Assert.Single(builder.Services, d => d.ServiceType == typeof(IChatClient) && d.ServiceKey is null);
        Assert.Single(builder.Services, d => d.ServiceType == typeof(LiteRtEngine) && d.ServiceKey is null);
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(IChatCompletionService) && d.ServiceKey is null);
    }

    [Fact]
    public void AddChatCompletionWithTwoServiceIdsFromEqualOptions_ShareTheFirstEngine()
    {
        // The first key's engine is the app's, so a load through the second key would name the missing path instead.
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddKeyedSingleton<LiteRtEngine>("chat", (_, _) => throw new NotSupportedException("the first key's engine"));
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "shared-missing.litertlm" }, serviceId: "chat");
        builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "shared-missing.litertlm" }, serviceId: "summary");
        using ServiceProvider provider = builder.Services.BuildServiceProvider();

        Assert.Equal("the first key's engine",
            Assert.Throws<NotSupportedException>(() => provider.GetRequiredKeyedService<IChatClient>("summary")).Message);
    }
}
