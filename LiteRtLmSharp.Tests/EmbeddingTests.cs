using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>Model-free checks of the embedding and model-info surface: argument validation that must hold
/// before any native call, and how the metadata reader's paths reach the native side.</summary>
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

    /// <summary>EmbeddingGemma must not run in float16, which is the runtime's GPU fallback: the embedding
    /// options default to float32, and null still hands the choice to the runtime.</summary>
    [Fact]
    public void Options_DefaultToFloat32Activations()
    {
        Assert.Equal(LiteRtActivationDataType.Float32, new LiteRtEmbeddingEngineOptions().ActivationDataType);
        Assert.Null(new LiteRtEmbeddingEngineOptions { ActivationDataType = null }.ActivationDataType);
    }

    /// <summary>A minimum above the maximum is rejected before any native call, naming both settings.</summary>
    [Fact]
    public void Load_RejectsAMinimumAboveTheMaximum()
    {
        string file = Path.Combine(Path.GetTempPath(), $"not-a-model-{Guid.NewGuid():N}.litertlm");
        File.WriteAllBytes(file, [1, 2, 3]);
        try
        {
            var ex = Assert.Throws<ArgumentException>(() => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
            {
                ModelPath = file, MinInputLength = 2048, MaxInputLength = 1024,
            }));
            Assert.Contains("MinInputLength", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Off Windows the narrow native API takes UTF-8; plain ASCII passes through everywhere.</summary>
    [Fact]
    public void NarrowPath_EncodesAsciiAndUtf8Paths()
    {
        byte[] ascii = Native.NarrowPath.Encode("/models/model.litertlm", out bool asciiOpens);
        Assert.True(asciiOpens);
        Assert.Equal("/models/model.litertlm\0"u8.ToArray(), ascii);
        if (!OperatingSystem.IsWindows())
        {
            byte[] utf8 = Native.NarrowPath.Encode("/modelos-José/m.litertlm", out bool opens);
            Assert.True(opens);
            Assert.Equal("/modelos-José/m.litertlm\0"u8.ToArray(), utf8);
        }
    }

    /// <summary>On Windows a non-ASCII path reaches the metadata reader as bytes both of its opens agree on:
    /// UTF-8 under the UTF-8 code page, else ASCII (the 8.3 alias, or a DOS device name that exists only
    /// while the lease is held).</summary>
    [SkippableFact]
    public void NarrowPath_Acquire_GivesBothOpensOneName()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        string dir = Path.Combine(Path.GetTempPath(), $"modelos-José-日本語-😀-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "modelo-ñ.litertlm");
            File.WriteAllBytes(file, [1, 2, 3]);
            byte[] encoded = Native.NarrowPath.Encode(file, out bool encodes);
            string? alias;
            using (Native.NarrowPath.Lease lease = Native.NarrowPath.Acquire(file))
            {
                Assert.True(lease.Opens);
                Assert.Equal(!encodes, lease.IsDeviceAlias);
                if (!lease.IsDeviceAlias)
                {
                    Assert.Equal(encoded, lease.Bytes);
                    return;
                }
                Assert.All(lease.Bytes, b => Assert.True(b < 128));
                alias = System.Text.Encoding.ASCII.GetString(lease.Bytes, 0, lease.Bytes.Length - 1);
                Assert.StartsWith(@"\\?\LiteRtLmSharp-", alias, StringComparison.Ordinal);
                Assert.Equal(3, File.ReadAllBytes(alias).Length);
            }
            Assert.False(File.Exists(alias), "The DOS device alias outlived its lease.");
            using Native.NarrowPath.Lease without = Native.NarrowPath.Acquire(file, allowDeviceAlias: false);
            Assert.False(without.Opens);
            Assert.False(without.IsDeviceAlias);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The DOS device alias targets the extended-length form of a full path: <c>\\?\UNC\</c> for a
    /// UNC path, <c>\\?\</c> for a drive path, and a device path unchanged.</summary>
    [Fact]
    public void NarrowPath_ExtendedPath_PrefixesDriveAndUncPaths()
    {
        Assert.Equal(@"\\?\C:\modelos-José\m.litertlm", Native.NarrowPath.ExtendedPath(@"C:\modelos-José\m.litertlm"));
        Assert.Equal(@"\\?\UNC\server\share\m.litertlm", Native.NarrowPath.ExtendedPath(@"\\server\share\m.litertlm"));
        Assert.Equal(@"\\?\C:\m.litertlm", Native.NarrowPath.ExtendedPath(@"\\?\C:\m.litertlm"));
        Assert.Equal(@"\\?\UNC\server\share\m.litertlm", Native.NarrowPath.ExtendedPath(@"\\?\UNC\server\share\m.litertlm"));
        Assert.Equal(@"\\.\C:\m.litertlm", Native.NarrowPath.ExtendedPath(@"\\.\C:\m.litertlm"));
    }

    /// <summary>A non-ASCII path longer than <c>MAX_PATH</c> gets an alias too, which reads the file and
    /// goes with the lease.</summary>
    [SkippableFact]
    public void NarrowPath_Acquire_AliasesAPathLongerThanMaxPath()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        string root = Path.Combine(Path.GetTempPath(), $"modelos-José-{Guid.NewGuid():N}");
        string dir = root;
        while (dir.Length < 300)
            dir = Path.Combine(dir, "segment-0123456789");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "modelo-ñ.litertlm");
            File.WriteAllBytes(file, [1, 2, 3]);
            Native.NarrowPath.Encode(file, out bool encodes);
            string? alias;
            using (Native.NarrowPath.Lease lease = Native.NarrowPath.Acquire(file))
            {
                Assert.True(lease.Opens, "No ASCII alias was made for a path longer than MAX_PATH.");
                if (encodes)
                    return;
                Assert.True(lease.IsDeviceAlias);
                alias = System.Text.Encoding.ASCII.GetString(lease.Bytes, 0, lease.Bytes.Length - 1);
                Assert.Equal(3, File.ReadAllBytes(alias).Length);
            }
            Assert.False(File.Exists(alias), "The DOS device alias outlived its lease.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An existing file that the native reader cannot open (here, held by another handle that
    /// shares nothing) fails with the runtime's UNKNOWN status. The message names the caller's path and, when
    /// the runtime's reason names the temporary alias, says what that alias is.</summary>
    [SkippableFact]
    public void ModelInfo_FileThatCannotBeOpened_ReportsUnknownAndTheCallersPath()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        // The alias hint depends on the runtime's reason, which a silenced log would drop.
        LiteRtEngine.SetMinLogLevel(3);
        string dir = Path.Combine(Path.GetTempPath(), $"modelos-José-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "modelo-ñ.litertlm");
            File.WriteAllBytes(file, new byte[4096]);
            Native.NarrowPath.Encode(file, out bool encodes);
            LiteRtException ex;
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                ex = Assert.Throws<LiteRtException>(() => LiteRtModelInfo.Read(file));
            Assert.Equal(LiteRtStatusCode.Unknown, ex.StatusCode);
            Assert.Contains($"'{file}'", ex.Message, StringComparison.Ordinal);
            if (!encodes)
                Assert.Contains("a temporary ASCII alias of", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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

    // Default options on purpose: they must already be right for EmbeddingGemma (float32 activations).
    private static LiteRtEmbeddingEngine LoadEngine(LiteRtBackend? backend = null) => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
    {
        ModelPath = EmbeddingModel!,
        Backend = backend ?? Backend,
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
        // The runtime reports the sampler and the vision lengths of an embedding model as not found: they
        // read as undeclared, and the expected failures leave no message on the thread.
        Assert.Null(info.DefaultSampler);
        Assert.Empty(info.VisionTokenSizes);
        Assert.Null(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Native.LiteRtLmNative.litert_lm_get_last_error_message()));
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
        Assert.True(info.MaxContextTokens > 0);
        Assert.Null(System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Native.LiteRtLmNative.litert_lm_get_last_error_message()));
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
        // Cosine ignores scale, so check the scale itself: the raw vector is far from unit length.
        Assert.True(Norm(raw) > 2, $"Normalize = false still returned a unit vector (norm {Norm(raw):F4}).");
        // Without the begin/end tokens the model sees a different input.
        float[] bare = engine.Embed(text, new LiteRtEmbeddingOptions { InsertSpecialTokens = false });
        output.WriteLine($"insertSpecialTokens=false: cosine vs default {Cosine(bare, full):F6}");
        Assert.True(Cosine(bare, full) < 0.99999, "InsertSpecialTokens = false did not change the vector.");
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
            ModelPath = EmbeddingModel!, Backend = Backend,
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

        var options = new LiteRtEmbeddingEngineOptions { ModelPath = EmbeddingModel!, Backend = Backend };
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
        Skip.If(Backend != LiteRtBackend.Gpu, "Set LITERTLM_TEST_BACKEND=gpu to run.");
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
        output.WriteLine($"cpu vs gpu (default options, float32 activations) cosine: min {cosines.Min():F5}, mean {cosines.Average():F5}");

        // For the docs: the runtime's own GPU precision (float16), which EmbeddingGemma advises against.
        using (var gpuRuntimeDefault = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
        {
            ModelPath = EmbeddingModel!, Backend = LiteRtBackend.Gpu, ActivationDataType = null,
        }))
        {
            double[] f16 = cpu.Zip(gpuRuntimeDefault.EmbedBatch(texts), Cosine).ToArray();
            output.WriteLine($"cpu vs gpu (runtime default, float16) cosine: min {f16.Min():F5}, mean {f16.Average():F5}");
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
            Assert.NotEmpty(vectors);
            double worst = vectors.Min(v => Cosine(v, reference));
            output.WriteLine($"round {round}: {chunks} chunks streamed, {vectors.Count} embeddings in parallel, worst cosine {worst:F7}");
            Assert.True(chunks > 1);
            Assert.InRange(worst, 0.9999, 1.0001);
        }
    }

    /// <summary>One engine shared across threads: concurrent calls are serialized and each returns the vector
    /// a lone call would.</summary>
    [SkippableFact]
    public async Task ConcurrentCalls_OnOneEngine_MatchSingleCalls()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LoadEngine();
        string[] texts = Enumerable.Range(0, 8).Select(i => $"{Document}Parallel note {i} about rivers and bridges.").ToArray();
        float[][] expected = texts.Select(t => engine.Embed(t)).ToArray();
        float[][] actual = await Task.WhenAll(texts.Select(t => Task.Run(() => engine.EmbedAsync(t))));
        for (int i = 0; i < texts.Length; i++)
            Assert.InRange(Cosine(actual[i], expected[i]), 0.9999, 1.0001);
    }

    /// <summary>Dispose waits for the call in progress, which completes; later calls throw
    /// ObjectDisposedException.</summary>
    [SkippableFact]
    public async Task Dispose_WaitsForTheCallInProgress_ThenRejectsCalls()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        var engine = LoadEngine();
        string[] texts = Enumerable.Range(0, 64).Select(i => $"{Document}Batch note {i} about mountains.").ToArray();
        Task<float[][]> batch = engine.EmbedBatchAsync(texts);
        await Task.Delay(20);
        engine.Dispose();
        float[][] vectors = await batch;
        Assert.Equal(texts.Length, vectors.Length);
        Assert.All(vectors, v => Assert.Equal(768, v.Length));
        Assert.Throws<ObjectDisposedException>(() => engine.Embed(Document + "after dispose"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.EmbedAsync(Document + "after dispose"));
    }

    [SkippableFact]
    public void EmbedBatch_NullElement_IsRejected_WithItsIndex()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LoadEngine();
        var ex = Assert.Throws<ArgumentException>(() => engine.EmbedBatch([Document + "ok", null!]));
        Assert.Contains("texts[1]", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The metadata reader opens paths with non-ASCII characters, inside and outside the ANSI code
    /// page (on Windows the native side opens a path both as UTF-8 and in the ANSI code page), and reads the
    /// same metadata as from an ASCII path, also from parallel calls. Without an ASCII alias such a path
    /// fails with guidance. Uses the small LoRA test bundle next to LITERTLM_TEST_MODEL when present.</summary>
    [SkippableFact]
    public void ModelInfo_ReadsAPathWithNonAsciiCharacters()
    {
        Skip.If(string.IsNullOrEmpty(ChatModel) || !File.Exists(ChatModel), "Set LITERTLM_TEST_MODEL to run.");
        string small = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ChatModel!))!, "test_lm_lora.litertlm");
        Skip.If(!File.Exists(small), "Place test_lm_lora.litertlm next to LITERTLM_TEST_MODEL to run.");
        LiteRtModelInfo expected = LiteRtModelInfo.Read(small);
        foreach (string name in new[] { "modelos-José", "モデル-日本語", "models-😀" })
        {
            string dir = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                string copy = Path.Combine(dir, "modelo-ñ.litertlm");
                File.Copy(small, copy);
                LiteRtModelInfo[] infos = new LiteRtModelInfo[4];
                Parallel.For(0, infos.Length, i => infos[i] = LiteRtModelInfo.Read(copy));
                foreach (LiteRtModelInfo info in infos)
                {
                    Assert.Equal(LiteRtModelType.LanguageModel, info.ModelType);
                    Assert.Equal(expected.MaxContextTokens, info.MaxContextTokens);
                    Assert.Equal(expected.IsDynamicContext, info.IsDynamicContext);
                    Assert.Equal(expected.InputModalities, info.InputModalities);
                    Assert.Equal(expected.SupportedBackends[LiteRtModality.Text], info.SupportedBackends[LiteRtModality.Text]);
                }

                if (OperatingSystem.IsWindows())
                {
                    Native.NarrowPath.Encode(copy, out bool encodes);
                    if (!encodes)
                    {
                        var ex = Assert.Throws<LiteRtException>(() => LiteRtModelInfo.ReadCore(copy, allowDeviceAlias: false));
                        output.WriteLine($"{name}, no alias: [{ex.StatusCode}] {ex.Message}");
                        Assert.Contains(copy, ex.Message, StringComparison.Ordinal);
                        Assert.Contains("ASCII alias", ex.Message, StringComparison.Ordinal);
                    }
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>The runtime validates the enum values the binding passes through: an undeclared overflow
    /// strategy fails the call, and an undeclared activation type fails the load, both with the runtime's
    /// reason.</summary>
    [SkippableFact]
    public void UndeclaredEnumValues_FailWithTheRuntimeReason()
    {
        SkipWithoutEmbeddingModel();
        LiteRtEngine.SetMinLogLevel(3);
        using (var engine = LoadEngine())
        {
            var call = Assert.Throws<LiteRtException>(() => engine.Embed("text", new LiteRtEmbeddingOptions { OverflowStrategy = (LiteRtInputOverflowStrategy)7 }));
            output.WriteLine(call.Message);
            Assert.Equal(LiteRtStatusCode.InvalidArgument, call.StatusCode);
            Assert.Equal(
                "litert_lm_embedding_options_set_input_overflow_strategy failed: INVALID_ARGUMENT: Unknown LiteRtLmInputOverflowStrategy.",
                call.Message);
            // The engine keeps working after the rejected call.
            Assert.Equal(768, engine.Embed(Document + "after").Length);
        }
        var load = Assert.Throws<LiteRtException>(() => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
        {
            ModelPath = EmbeddingModel!, Backend = Backend, ActivationDataType = (LiteRtActivationDataType)7,
        }));
        output.WriteLine(load.Message);
        Assert.Equal(LiteRtStatusCode.InvalidArgument, load.StatusCode);
        Assert.Equal(
            "litert_lm_embedding_engine_settings_set_activation_data_type failed: INVALID_ARGUMENT: Unknown LiteRtLmActivationDataType.",
            load.Message);
    }
}
