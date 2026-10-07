using System.Text.Json;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>Model-free checks of the MEAI embedding options mapping and the DI registrations.</summary>
public class EmbeddingConnectorMappingTests
{
    [Fact]
    public void NoOptions_UsesTheDefaults()
    {
        var defaults = new LiteRtEmbeddingOptions { OutputDimensions = 256 };
        Assert.Same(defaults, LiteRtEmbeddingMapping.ToEmbeddingOptions(null, defaults));
        Assert.Null(LiteRtEmbeddingMapping.ToEmbeddingOptions(null, null));
        Assert.Null(LiteRtEmbeddingMapping.ToEmbeddingOptions(new EmbeddingGenerationOptions(), null));
    }

    [Fact]
    public void TypedOptions_MapOntoTheNativeOptions_AndOverrideDefaults()
    {
        var defaults = new LiteRtEmbeddingOptions { OutputDimensions = 768, Normalize = true };
        var options = new LiteRtEmbeddingGenerationOptions
        {
            Dimensions = 128,
            Normalize = false,
            InsertSpecialTokens = false,
            OverflowStrategy = LiteRtInputOverflowStrategy.ChunkAndAverage,
        };
        LiteRtEmbeddingOptions? mapped = LiteRtEmbeddingMapping.ToEmbeddingOptions(options, defaults);
        Assert.Equal(new LiteRtEmbeddingOptions
        {
            OutputDimensions = 128,
            Normalize = false,
            InsertSpecialTokens = false,
            OverflowStrategy = LiteRtInputOverflowStrategy.ChunkAndAverage,
        }, mapped);
    }

    [Fact]
    public void PlainAdditionalProperties_Work_IncludingNamesAndNumbers()
    {
        var byName = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["overflow_strategy"] = "truncate", ["normalize"] = "false" } };
        Assert.Equal(LiteRtInputOverflowStrategy.Truncate, LiteRtEmbeddingMapping.ToEmbeddingOptions(byName, null)!.OverflowStrategy);
        Assert.False(LiteRtEmbeddingMapping.ToEmbeddingOptions(byName, null)!.Normalize);

        var byNumber = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["overflow_strategy"] = 2L } };
        Assert.Equal(LiteRtInputOverflowStrategy.Error, LiteRtEmbeddingMapping.ToEmbeddingOptions(byNumber, null)!.OverflowStrategy);
    }

    [Fact]
    public void InvalidValues_AreRejected()
    {
        var bad = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["overflow_strategy"] = "shrink" } };
        Assert.Throws<ArgumentException>(() => LiteRtEmbeddingMapping.ToEmbeddingOptions(bad, null));
        var outOfRange = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["overflow_strategy"] = 7 } };
        Assert.Throws<ArgumentException>(() => LiteRtEmbeddingMapping.ToEmbeddingOptions(outOfRange, null));

        // Values that only look like a strategy: a boolean, a fractional number, a list of names.
        foreach (object value in new object[] { true, 1.6, "ChunkAndAverage, Truncate" })
        {
            var options = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["overflow_strategy"] = value } };
            Assert.Throws<ArgumentException>(() => LiteRtEmbeddingMapping.ToEmbeddingOptions(options, null));
        }
        // A boolean knob that is not a boolean fails instead of silently keeping the default.
        var notBool = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["normalize"] = "no" } };
        Assert.Throws<ArgumentException>(() => LiteRtEmbeddingMapping.ToEmbeddingOptions(notBool, null));
        // The typed getter never throws: a malformed value reads as null, and the call reports it.
        var typed = new LiteRtEmbeddingGenerationOptions { AdditionalProperties = new() { ["overflow_strategy"] = "shrink" } };
        Assert.Null(typed.OverflowStrategy);
        Assert.Equal(LiteRtInputOverflowStrategy.Error, LiteRtEmbeddingMapping.ParseOverflowStrategy("2"));
    }

    /// <summary>Options deserialized from JSON hold JsonElement values; every key still maps.</summary>
    [Fact]
    public void JsonValues_Map_IncludingNamesAndNumbers()
    {
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
        var byName = new EmbeddingGenerationOptions
        {
            AdditionalProperties = new() { ["overflow_strategy"] = Json("\"Truncate\""), ["normalize"] = Json("false"), ["insert_special_tokens"] = Json("true") },
        };
        LiteRtEmbeddingOptions mapped = LiteRtEmbeddingMapping.ToEmbeddingOptions(byName, null)!;
        Assert.Equal(LiteRtInputOverflowStrategy.Truncate, mapped.OverflowStrategy);
        Assert.False(mapped.Normalize);
        Assert.True(mapped.InsertSpecialTokens);

        var byNumber = new EmbeddingGenerationOptions { AdditionalProperties = new() { ["overflow_strategy"] = Json("0") } };
        Assert.Equal(LiteRtInputOverflowStrategy.ChunkAndAverage, LiteRtEmbeddingMapping.ToEmbeddingOptions(byNumber, null)!.OverflowStrategy);

        // A round trip through MEAI's own serializer settings.
        var typed = new LiteRtEmbeddingGenerationOptions { Dimensions = 128, OverflowStrategy = LiteRtInputOverflowStrategy.ChunkAndAverage, Normalize = false };
        string json = JsonSerializer.Serialize<EmbeddingGenerationOptions>(typed, AIJsonUtilities.DefaultOptions);
        var back = JsonSerializer.Deserialize<EmbeddingGenerationOptions>(json, AIJsonUtilities.DefaultOptions)!;
        Assert.Equal(new LiteRtEmbeddingOptions
        {
            OutputDimensions = 128, OverflowStrategy = LiteRtInputOverflowStrategy.ChunkAndAverage, Normalize = false,
        }, LiteRtEmbeddingMapping.ToEmbeddingOptions(back, null));
    }

    [Fact]
    public void TypedOptions_SetToNull_RemoveTheirKeys()
    {
        var options = new LiteRtEmbeddingGenerationOptions { Normalize = true, OverflowStrategy = LiteRtInputOverflowStrategy.Truncate };
        options.Normalize = null;
        options.OverflowStrategy = null;
        Assert.False(options.AdditionalProperties?.ContainsKey("normalize") ?? false);
        Assert.False(options.AdditionalProperties?.ContainsKey("overflow_strategy") ?? false);
    }

    [Fact]
    public void Registrations_CoexistWithTheChatClient_AndLoadLazily()
    {
        var services = new ServiceCollection();
        services.AddLiteRtChatClient(new LiteRtEngineOptions { ModelPath = "chat.litertlm" });
        services.AddLiteRtEmbeddingGenerator(new LiteRtEmbeddingEngineOptions { ModelPath = "embed.litertlm" });
        services.AddLiteRtEmbeddingGenerator(new LiteRtEmbeddingEngineOptions { ModelPath = "embed.litertlm" });   // same options: no-op
        Assert.Single(services, d => d.ServiceType == typeof(LiteRtEmbeddingEngine));
        Assert.Single(services, d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
        Assert.Single(services, d => d.ServiceType == typeof(IChatClient));
        Assert.Single(services, d => d.ServiceType == typeof(LiteRtEngine));

        // Different options would be silently ignored otherwise: they throw.
        Assert.Throws<InvalidOperationException>(() =>
            services.AddLiteRtEmbeddingGenerator(new LiteRtEmbeddingEngineOptions { ModelPath = "other.litertlm" }));
    }

    [Fact]
    public void KernelBuilder_RegistersAKeyedGenerator()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtEmbeddingGenerator(new LiteRtEmbeddingEngineOptions { ModelPath = "embed.litertlm" }, serviceId: "local");
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>) && Equals(d.ServiceKey, "local"));
    }

    /// <summary>Each serviceId gets its own engine: resolving a keyed generator loads that key's model (here a
    /// missing file, so the load names the path it was asked to open).</summary>
    [Fact]
    public void KernelBuilder_KeyedGenerators_UseTheirOwnOptions()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtEmbeddingGenerator(new LiteRtEmbeddingEngineOptions { ModelPath = "model-a.litertlm" }, serviceId: "a");
        builder.AddLiteRtEmbeddingGenerator(new LiteRtEmbeddingEngineOptions { ModelPath = "model-b.litertlm", Backend = LiteRtBackend.Gpu }, serviceId: "b");
        Kernel kernel = builder.Build();
        var ex = Assert.Throws<ArgumentException>(() => kernel.Services.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>("b"));
        Assert.Contains("model-b.litertlm", ex.Message, StringComparison.Ordinal);
        ex = Assert.Throws<ArgumentException>(() => kernel.Services.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>("a"));
        Assert.Contains("model-a.litertlm", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Model-backed MEAI and Semantic Kernel embedding tests. Set LITERTLM_TEST_EMBEDDING_MODEL to an embedding
/// <c>.litertlm</c> (e.g. embeddinggemma-2-text-270m.litertlm).
/// </summary>
public sealed class EmbeddingConnectorModelTests
{
    private static string? EmbeddingModel => Environment.GetEnvironmentVariable("LITERTLM_TEST_EMBEDDING_MODEL");
    private static LiteRtBackend Backend => LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu");

    private static LiteRtEmbeddingEngineOptions Options => new()
    {
        ModelPath = EmbeddingModel!, Backend = Backend,
    };

    private static void SkipWithoutEmbeddingModel() => Skip.If(
        string.IsNullOrEmpty(EmbeddingModel) || !File.Exists(EmbeddingModel),
        "Set LITERTLM_TEST_EMBEDDING_MODEL to an embedding .litertlm (e.g. embeddinggemma-2-text-270m) to run.");

    [SkippableFact]
    public async Task Generator_EmbedsInOrder_AcrossBatches_AndHonorsDimensions()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEmbeddingEngine.Load(Options);
        using IEmbeddingGenerator<string, Embedding<float>> generator = new LiteRtEmbeddingGenerator(engine, "embeddinggemma-2-text-270m");

        // More values than one internal batch (32), so ordering across batches is exercised.
        string[] texts = Enumerable.Range(0, LiteRtEmbeddingGenerator.BatchSize + 8)
            .Select(i => $"title: none | text: Note number {i} about the weather in city {i % 7}.").ToArray();
        GeneratedEmbeddings<Embedding<float>> embeddings = await generator.GenerateAsync(texts);
        Assert.Equal(texts.Length, embeddings.Count);
        foreach (int i in new[] { 0, 31, 32, texts.Length - 1 })
            Assert.Equal(engine.Embed(texts[i]), embeddings[i].Vector.ToArray());
        Assert.Equal("embeddinggemma-2-text-270m", embeddings[0].ModelId);

        Embedding<float> short256 = await generator.GenerateAsync("title: none | text: short", new EmbeddingGenerationOptions { Dimensions = 256 });
        Assert.Equal(256, short256.Vector.Length);

        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        Assert.Equal(768, metadata?.DefaultModelDimensions);
        Assert.Same(engine, generator.GetService<LiteRtEmbeddingEngine>());

        // The engine runs one model: a request's ModelId does not relabel the vectors.
        Embedding<float> relabeled = await generator.GenerateAsync("title: none | text: short", new EmbeddingGenerationOptions { ModelId = "some-other-model" });
        Assert.Equal("embeddinggemma-2-text-270m", relabeled.ModelId);

        // A null anywhere is reported with its position before anything is embedded.
        string?[] withLateNull = [.. texts, null];
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => generator.GenerateAsync(withLateNull!));
        Assert.Contains($"values[{texts.Length}]", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Kernel_ResolvesTheGenerator_AndEmbeds()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtEmbeddingGenerator(Options, modelId: "embeddinggemma-2-text-270m");
        Kernel kernel = builder.Build();
        try
        {
            var generator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            Embedding<float> embedding = await generator.GenerateAsync("task: search result | query: northern lights");
            Assert.Equal(768, embedding.Vector.Length);
        }
        finally
        {
            (kernel.Services as IDisposable)?.Dispose();
        }
    }
}
