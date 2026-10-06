using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>Model-free checks of the embedding and model-info surface: argument validation that must hold
/// before any native call.</summary>
public class EmbeddingValidationTests
{
    [Fact]
    public void Load_WithMissingModel_Throws()
        => Assert.Throws<ArgumentException>(() => LiteRtEmbeddingEngine.Load(
            new LiteRtEmbeddingEngineOptions { ModelPath = "does-not-exist.litertlm" }));

    [Fact]
    public void Load_WithoutModelPath_Throws()
        => Assert.Throws<ArgumentException>(() => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions()));

    [Fact]
    public void ModelInfo_WithMissingFile_Throws()
        => Assert.Throws<ArgumentException>(() => LiteRtModelInfo.Read("does-not-exist.litertlm"));

    [Fact]
    public void Options_RejectNonPositiveValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtEmbeddingOptions { OutputDimensions = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtEmbeddingEngineOptions { NumThreads = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtEmbeddingEngineOptions { MaxInputLength = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtEmbeddingEngineOptions { MinInputLength = -1 });
        Assert.Equal(0, new LiteRtEmbeddingEngineOptions { MinInputLength = 0 }.MinInputLength);
    }
}

/// <summary>
/// Model-backed embedding tests (EmbeddingGemma 2). Set LITERTLM_TEST_EMBEDDING_MODEL to an embedding
/// <c>.litertlm</c> (e.g. embeddinggemma-2-text-270m.litertlm); LITERTLM_TEST_BACKEND picks the backend.
/// The coexistence tests also need LITERTLM_TEST_MODEL (a chat model).
/// </summary>
public sealed class EmbeddingModelTests(ITestOutputHelper output)
{
    private static string? EmbeddingModel => Environment.GetEnvironmentVariable("LITERTLM_TEST_EMBEDDING_MODEL");
    private static string? ChatModel => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
    private static LiteRtBackend Backend => LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu");

    // EmbeddingGemma 2's own retrieval prefixes (google/embeddinggemma-2 model card).
    private const string Query = "task: search result | query: ";
    private const string Document = "title: none | text: ";

    private static void SkipWithoutEmbeddingModel() => Skip.If(
        string.IsNullOrEmpty(EmbeddingModel) || !File.Exists(EmbeddingModel),
        "Set LITERTLM_TEST_EMBEDDING_MODEL to an embedding .litertlm (e.g. embeddinggemma-2-text-270m) to run.");

    private static LiteRtEmbeddingEngine LoadEngine(LiteRtBackend? backend = null) => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
    {
        ModelPath = EmbeddingModel!,
        Backend = backend ?? Backend,
        // EmbeddingGemma must not run in float16; F32 is a no-op on CPU and the safe choice on GPU.
        ActivationDataType = LiteRtActivationDataType.Float32,
    });

    private static double Norm(float[] v) => Math.Sqrt(v.Sum(x => (double)x * x));

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    [SkippableFact]
    public void ModelInfo_DescribesTheEmbeddingModel()
    {
        SkipWithoutEmbeddingModel();
        LiteRtModelInfo info = LiteRtModelInfo.Read(EmbeddingModel!);
        output.WriteLine($"type={info.ModelType} dim={info.EmbeddingDimension} lengths=[{string.Join(",", info.EmbeddingInputLengths)}] " +
                         $"modalities=[{string.Join(",", info.InputModalities)}] ctx={info.MaxContextTokens} min={info.MinRuntimeVersion}");
        Assert.Equal(LiteRtModelType.Embedding, info.ModelType);
        Assert.Equal(768, info.EmbeddingDimension);
        Assert.Contains(128, info.EmbeddingInputLengths);
        Assert.Contains(LiteRtModality.Text, info.InputModalities);
    }

    [SkippableFact]
    public void ModelInfo_DescribesTheChatModel()
    {
        Skip.If(string.IsNullOrEmpty(ChatModel) || !File.Exists(ChatModel), "Set LITERTLM_TEST_MODEL to run.");
        LiteRtModelInfo info = LiteRtModelInfo.Read(ChatModel!);
        output.WriteLine($"type={info.ModelType} ctx={info.MaxContextTokens} dynamic={info.IsDynamicContext} thinking={info.SupportsThinking} " +
                         $"tools={info.SupportsFunctionCalling} spec={info.SupportsSpeculativeDecoding} vision=[{string.Join(",", info.VisionTokenSizes)}] " +
                         $"budget={info.MaxVisionTokenBudget} modalities=[{string.Join(",", info.InputModalities)}] " +
                         $"backends={string.Join(" ", info.SupportedBackends.Select(kv => $"{kv.Key}:[{string.Join(",", kv.Value)}]"))} " +
                         $"sampler={info.DefaultSampler} min={info.MinRuntimeVersion}");
        Assert.Equal(LiteRtModelType.LanguageModel, info.ModelType);
        Assert.Null(info.EmbeddingDimension);
        Assert.Empty(info.EmbeddingInputLengths);
    }

    [SkippableFact]
    public void Embed_ReturnsANormalizedVectorOfTheModelDimension()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        var sw = Stopwatch.StartNew();
        using var engine = LoadEngine();
        output.WriteLine($"load {sw.ElapsedMilliseconds} ms, Dimension={engine.Dimension}");
        sw.Restart();
        float[] v = engine.Embed(Document + "The northern lights are caused by charged particles from the sun.");
        output.WriteLine($"embed {sw.ElapsedMilliseconds} ms, length {v.Length}, norm {Norm(v):F6}");
        Assert.Equal(768, engine.Dimension);
        Assert.Equal(768, v.Length);
        Assert.InRange(Norm(v), 0.999, 1.001);
    }

    [SkippableFact]
    public void Embed_OptionsControlDimensionsAndNormalization()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LoadEngine();
        string text = Document + "A lighthouse keeper writes letters to the sea.";
        float[] full = engine.Embed(text);
        foreach (int dims in new[] { 512, 256, 128 })
        {
            float[] cut = engine.Embed(text, new LiteRtEmbeddingOptions { OutputDimensions = dims });
            double prefixCosine = Cosine(cut, full[..dims]);
            output.WriteLine($"output {dims}: length {cut.Length}, norm {Norm(cut):F6}, cosine vs full prefix {prefixCosine:F6}");
            Assert.Equal(dims, cut.Length);
            // Matryoshka truncation: the prefix of the full vector, normalized again.
            Assert.InRange(Norm(cut), 0.999, 1.001);
            Assert.InRange(prefixCosine, 0.9999, 1.0001);
        }
        // Longer than the model's vectors: the runtime rejects it.
        var tooLong = Assert.Throws<LiteRtException>(() => engine.Embed(text, new LiteRtEmbeddingOptions { OutputDimensions = full.Length + 1 }));
        Assert.Equal(LiteRtStatusCode.InvalidArgument, tooLong.StatusCode);
        float[] raw = engine.Embed(text, new LiteRtEmbeddingOptions { Normalize = false });
        output.WriteLine($"normalize=false: norm {Norm(raw):F4}, cosine vs normalized {Cosine(raw, full):F6}");
        Assert.InRange(Cosine(raw, full), 0.9999, 1.0001);
    }

    [SkippableFact]
    public void EmbedBatch_MatchesSingleCalls_AndIsDeterministic()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LoadEngine();
        string[] texts =
        [
            Document + "Paris is the capital of France.",
            Document + "The mitochondria is the powerhouse of the cell.",
            Document + "Bake the bread at 220 degrees for thirty minutes.",
        ];
        var sw = Stopwatch.StartNew();
        float[][] batch = engine.EmbedBatch(texts);
        output.WriteLine($"batch of {texts.Length}: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(texts.Length, batch.Length);
        for (int i = 0; i < texts.Length; i++)
        {
            float[] single = engine.Embed(texts[i]);
            double c = Cosine(single, batch[i]);
            output.WriteLine($"item {i}: cosine single vs batch {c:F7}");
            Assert.InRange(c, 0.9999, 1.0001);
        }
        Assert.Equal(engine.Embed(texts[0]), engine.Embed(texts[0]));
        Assert.Empty(engine.EmbedBatch([]));
    }

    [SkippableFact]
    public void Embed_RanksTheRelatedDocumentFirst()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LoadEngine();
        string[] docs =
        [
            Document + "The northern lights are caused by charged particles from the sun hitting the atmosphere.",
            Document + "Sourdough bread needs a long, slow fermentation.",
            Document + "The stock market closed higher on Friday.",
        ];
        float[][] docVectors = engine.EmbedBatch(docs);
        float[] query = engine.Embed(Query + "What causes the aurora borealis?");
        double[] scores = docVectors.Select(d => Cosine(query, d)).ToArray();
        output.WriteLine($"scores: {string.Join(", ", scores.Select(s => s.ToString("F4")))}");
        Assert.Equal(0, Array.IndexOf(scores, scores.Max()));
    }

    [SkippableFact]
    public async Task EmbedAsync_MatchesEmbed_AndHonorsAPreCancelledToken()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LoadEngine();
        string text = Document + "Asynchronous embeddings run on the thread pool.";
        Assert.Equal(engine.Embed(text), await engine.EmbedAsync(text));
        float[][] batch = await engine.EmbedBatchAsync([text, text]);
        Assert.Equal(2, batch.Length);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.EmbedAsync(text, cancellationToken: cts.Token));
    }

    /// <summary>The runtime rejects a missing cache directory with a bare source trace, so the binding checks it
    /// first and names the problem; an existing directory loads.</summary>
    [SkippableFact]
    public void Load_ChecksTheCacheDirectory()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        string dir = Path.Combine(Path.GetTempPath(), $"embedding-cache-{Guid.NewGuid():N}");
        var ex = Assert.Throws<ArgumentException>(() => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
        {
            ModelPath = EmbeddingModel!, Backend = Backend, Cache = LiteRtCache.Directory(dir),
        }));
        Assert.Contains(dir, ex.Message, StringComparison.Ordinal);

        Directory.CreateDirectory(dir);
        try
        {
            using var engine = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
            {
                ModelPath = EmbeddingModel!, Backend = Backend, Cache = LiteRtCache.Directory(dir),
            });
            Assert.Equal(768, engine.Embed(Document + "cached").Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public void Load_WithAChatModel_ThrowsAClearError()
    {
        Skip.If(string.IsNullOrEmpty(ChatModel) || !File.Exists(ChatModel), "Set LITERTLM_TEST_MODEL to run.");
        var ex = Assert.Throws<ArgumentException>(() => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions { ModelPath = ChatModel! }));
        Assert.Contains("LiteRtEngine.Load", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void OverflowStrategy_GovernsTextsLongerThanTheLargestSignature()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
        {
            ModelPath = EmbeddingModel!, Backend = Backend, ActivationDataType = LiteRtActivationDataType.Float32,
            MaxInputLength = 128,
        });
        string longText = Document + string.Join(" ", Enumerable.Range(0, 120).Select(i => $"Sentence number {i} talks about lighthouses and the sea."));
        foreach (LiteRtInputOverflowStrategy? strategy in new LiteRtInputOverflowStrategy?[] { null, LiteRtInputOverflowStrategy.ChunkAndAverage, LiteRtInputOverflowStrategy.Truncate, LiteRtInputOverflowStrategy.Error })
        {
            try
            {
                float[] v = engine.Embed(longText, strategy is null ? null : new LiteRtEmbeddingOptions { OverflowStrategy = strategy });
                output.WriteLine($"{strategy?.ToString() ?? "default"}: length {v.Length}, norm {Norm(v):F4}");
            }
            catch (LiteRtException ex)
            {
                output.WriteLine($"{strategy?.ToString() ?? "default"}: [{ex.StatusCode}] {ex.Message}");
            }
        }
        // The runtime default is Error.
        Assert.Equal(LiteRtStatusCode.InvalidArgument, Assert.Throws<LiteRtException>(() => engine.Embed(longText)).StatusCode);
        Assert.Throws<LiteRtException>(() => engine.Embed(longText, new LiteRtEmbeddingOptions { OverflowStrategy = LiteRtInputOverflowStrategy.Error }));
        float[] truncated = engine.Embed(longText, new LiteRtEmbeddingOptions { OverflowStrategy = LiteRtInputOverflowStrategy.Truncate });
        float[] chunked = engine.Embed(longText, new LiteRtEmbeddingOptions { OverflowStrategy = LiteRtInputOverflowStrategy.ChunkAndAverage });
        Assert.Equal(768, truncated.Length);
        Assert.Equal(768, chunked.Length);
        Assert.InRange(Norm(chunked), 0.999, 1.001);
    }

    /// <summary>Without MaxInputLength the engine prepares for the limit the model declares (EmbeddingGemma 2:
    /// 1024 tokens), not for its longest signature; raising it accepts longer texts.</summary>
    [SkippableFact]
    public void MaxInputLength_DefaultsToTheModelsDeclaredLimit()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        var info = LiteRtModelInfo.Read(EmbeddingModel!);
        Assert.Contains(2048, info.EmbeddingInputLengths);
        // Each of these words is one token, so 1100 of them overflow 1024 tokens but fit in 2048.
        string[] pool = "alpha beta gamma delta river mountain cloud signal paper window garden engine".Split(' ');
        string text = string.Join(' ', Enumerable.Range(0, 1100).Select(i => pool[i % pool.Length]));

        var options = new LiteRtEmbeddingEngineOptions { ModelPath = EmbeddingModel!, Backend = Backend, ActivationDataType = LiteRtActivationDataType.Float32 };
        using (var byDefault = LiteRtEmbeddingEngine.Load(options))
            Assert.Equal(LiteRtStatusCode.InvalidArgument, Assert.Throws<LiteRtException>(() => byDefault.Embed(text)).StatusCode);
        using var raised = LiteRtEmbeddingEngine.Load(options with { MaxInputLength = 2048 });
        Assert.Equal(768, raised.Embed(text).Length);
    }

    /// <summary>The GPU backend (activations in float32, as EmbeddingGemma requires) produces vectors close to
    /// the CPU ones, but not identical: index and query on one backend.</summary>
    [SkippableFact]
    public void GpuVectors_AreCloseToCpuVectors()
    {
        SkipWithoutEmbeddingModel();
        Skip.If(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") != "gpu", "Set LITERTLM_TEST_BACKEND=gpu to run.");
        LiteRtEngine.SetMinLogLevel(3);
        string[] texts = Enumerable.Range(0, 16).Select(i => $"{Document}Fact number {i}: the ocean covers most of the planet.").ToArray();
        float[][] cpu, gpu;
        using (var cpuEngine = LoadEngine(LiteRtBackend.Cpu))
            cpu = cpuEngine.EmbedBatch(texts);
        var sw = Stopwatch.StartNew();
        using (var gpuEngine = LoadEngine(LiteRtBackend.Gpu))
        {
            output.WriteLine($"gpu load {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            gpu = gpuEngine.EmbedBatch(texts);
            output.WriteLine($"gpu batch of {texts.Length}: {sw.ElapsedMilliseconds} ms");
        }
        double[] cosines = cpu.Zip(gpu, Cosine).ToArray();
        output.WriteLine($"cpu vs gpu (float32 activations) cosine: min {cosines.Min():F5}, mean {cosines.Average():F5}");

        // For the docs: the engine's default GPU precision, which EmbeddingGemma advises against.
        using (var gpuDefault = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions { ModelPath = EmbeddingModel!, Backend = LiteRtBackend.Gpu }))
        {
            double[] defaults = cpu.Zip(gpuDefault.EmbedBatch(texts), Cosine).ToArray();
            output.WriteLine($"cpu vs gpu (default activations) cosine: min {defaults.Min():F5}, mean {defaults.Average():F5}");
        }
        Assert.True(cosines.Min() > 0.99, $"GPU vectors drifted from CPU: min cosine {cosines.Min():F5}");
    }

    /// <summary>A chat engine and an embedding engine live in one process: the one-engine gate does not count
    /// the embedding engine, an embedding computed between two turns matches one computed without the chat
    /// engine, and the conversation keeps its context.</summary>
    [SkippableFact]
    public void ChatAndEmbeddingEngines_Coexist()
    {
        SkipWithoutEmbeddingModel();
        Skip.If(string.IsNullOrEmpty(ChatModel) || !File.Exists(ChatModel), "Set LITERTLM_TEST_MODEL to run.");
        LiteRtEngine.SetMinLogLevel(3);
        string text = Document + "The northern lights are caused by charged particles from the sun.";
        float[] reference;
        using (var alone = LoadEngine())
            reference = alone.Embed(text);

        using var chat = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = ChatModel!, Backend = Backend, MaxNumTokens = 2048 });
        using var embeddings = LoadEngine();
        using var conv = chat.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 32 });
        conv.Send("Remember this number: 4721. Reply with OK.");
        float[] between = embeddings.Embed(text);
        string recall = conv.Send("What number did I ask you to remember? Reply with the number only.").Text ?? "";
        output.WriteLine($"cosine vs reference {Cosine(between, reference):F7}; recall: {recall}");
        Assert.InRange(Cosine(between, reference), 0.9999, 1.0001);
        Assert.Contains("4721", recall, StringComparison.Ordinal);
    }

    /// <summary>Embeddings computed on another thread while a chat engine streams a reply: both finish, and the
    /// vectors match the reference.</summary>
    [SkippableFact]
    public async Task EmbeddingDuringAStreamingChatReply_BothComplete()
    {
        SkipWithoutEmbeddingModel();
        Skip.If(string.IsNullOrEmpty(ChatModel) || !File.Exists(ChatModel), "Set LITERTLM_TEST_MODEL to run.");
        LiteRtEngine.SetMinLogLevel(3);
        string text = Document + "Concurrency between a chat engine and an embedding engine.";
        using var chat = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = ChatModel!, Backend = Backend, MaxNumTokens = 2048 });
        using var embeddings = LoadEngine();
        float[] reference = embeddings.Embed(text);

        for (int round = 0; round < 3; round++)
        {
            using var conv = chat.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 96 });
            int chunks = 0;
            Task stream = Task.Run(async () =>
            {
                await foreach (var _ in conv.SendStreamingAsync("Write a short paragraph about the ocean."))
                    Interlocked.Increment(ref chunks);
            });
            var vectors = new List<float[]>();
            while (!stream.IsCompleted)
                vectors.Add(await embeddings.EmbedAsync(text));
            await stream;
            double worst = vectors.Count == 0 ? 1 : vectors.Min(v => Cosine(v, reference));
            output.WriteLine($"round {round}: {chunks} chunks streamed, {vectors.Count} embeddings in parallel, worst cosine {worst:F7}");
            Assert.True(chunks > 1);
            Assert.InRange(worst, 0.9999, 1.0001);
        }
    }
}
