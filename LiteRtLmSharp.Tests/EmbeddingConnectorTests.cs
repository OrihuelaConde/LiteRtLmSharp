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
        services.AddLiteRtEmbeddingGenerator(new LiteRtEmbeddingEngineOptions { ModelPath = "other.litertlm" });   // idempotent
        Assert.Single(services, d => d.ServiceType == typeof(LiteRtEmbeddingEngine));
        Assert.Single(services, d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
        Assert.Single(services, d => d.ServiceType == typeof(IChatClient));
        Assert.Single(services, d => d.ServiceType == typeof(LiteRtEngine));
    }

    [Fact]
    public void KernelBuilder_RegistersAKeyedGenerator()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddLiteRtEmbeddingGenerator(new LiteRtEmbeddingEngineOptions { ModelPath = "embed.litertlm" }, serviceId: "local");
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>) && Equals(d.ServiceKey, "local"));
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
        ModelPath = EmbeddingModel!, Backend = Backend, ActivationDataType = LiteRtActivationDataType.Float32,
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
