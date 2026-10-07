using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Engine and conversation options added by native LiteRT-LM v0.18.0: the Metal residency set, the per-image
/// vision token cap and per-conversation speculative decoding. Each
/// test loads its own engine (one engine alive at a time; the assembly disables parallelization). Skipped
/// unless LITERTLM_TEST_MODEL is set; the vision cases also need LITERTLM_TEST_VISION=1.
/// </summary>
public sealed class ExecutionOptionsModelTests(ITestOutputHelper output)
{
    private static string? Model => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");

    private static LiteRtBackend Backend => LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu");

    private static void SkipWithoutModel() => Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model),
        "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

    private static void SkipWithoutVision() => Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model)
            || Environment.GetEnvironmentVariable("LITERTLM_TEST_VISION") != "1",
        "Set LITERTLM_TEST_VISION=1 and LITERTLM_TEST_MODEL to a vision-capable .litertlm (e.g. gemma-4-E2B-it) to run.");

    /// <summary><see cref="LiteRtEngineOptions.EnableMetalResidencySet"/> is accepted on every platform (Apple
    /// GPU applies it, everything else ignores it) and the engine still answers.</summary>
    [SkippableFact]
    public void EnableMetalResidencySet_IsAcceptedAndTheEngineAnswers()
    {
        SkipWithoutModel();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = Backend, MaxNumTokens = 2048, EnableMetalResidencySet = true,
        });
        using var conv = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 32 });
        Assert.Contains("Paris", conv.Send("What is the capital of France? One word.").Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Per-conversation speculative decoding on an engine loaded without it. <c>true</c> loads the MTP
    /// drafter for that conversation: on CPU the engine gets a cache directory of its own, and the drafter's
    /// compiled cache must appear there only then. The drafter then stays on the engine, so the binding
    /// passes the engine setting to later conversations that leave the option unset (the runtime would
    /// otherwise keep drafting for them: 0.80× decode on CPU, measured). A conversation that opts out on a
    /// speculative engine also answers. Timings are informational: speculative decoding does not speed up
    /// desktop CPU or GPU (see docs/speculative-decoding.md).
    /// </summary>
    [SkippableFact]
    public void ConversationSpeculativeDecoding_OverridesTheEngineSetting()
    {
        SkipWithoutModel();
        Skip.IfNot(LiteRtModelInfo.Read(Model!).SupportsSpeculativeDecoding, "The test model has no MTP drafter.");
        LiteRtEngine.SetMinLogLevel(3);

        // On GPU the compiled caches run to several GB, so only the CPU leg watches a cache of its own.
        string? cacheDir = Backend == LiteRtBackend.Cpu ? Directory.CreateTempSubdirectory("litertlm-spec-").FullName : null;
        LiteRtCache cache = cacheDir is null ? LiteRtCache.Default : LiteRtCache.Directory(cacheDir);
        bool DrafterCached() => cacheDir is not null && Directory.EnumerateFiles(cacheDir, "*mtp_drafter*").Any();
        try
        {
            using (var engine = LiteRtEngine.Load(new LiteRtEngineOptions
            {
                ModelPath = Model!, Backend = Backend, MaxNumTokens = 2048, EnableBenchmark = true, Cache = cache,
            }))
            {
                AskForPlanets(engine, null, "engine off, conversation inherits");
                Assert.False(DrafterCached(), "The drafter loaded although no conversation asked for it.");
                Assert.False(engine.DrafterLoadedByAConversation);

                AskForPlanets(engine, true, "engine off, conversation on");
                if (cacheDir is not null)
                    Assert.True(DrafterCached(), "EnableSpeculativeDecoding = true did not load the drafter.");
                Assert.True(engine.DrafterLoadedByAConversation);

                AskForPlanets(engine, null, "engine off, conversation inherits after the drafter loaded");
            }

            using (var engine = LiteRtEngine.Load(new LiteRtEngineOptions
            {
                ModelPath = Model!, Backend = Backend, MaxNumTokens = 2048, EnableBenchmark = true, Cache = cache,
                EnableSpeculativeDecoding = true,
            }))
            {
                AskForPlanets(engine, null, "engine on, conversation inherits");
                AskForPlanets(engine, false, "engine on, conversation off");
            }
        }
        finally
        {
            if (cacheDir is not null)
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    private void AskForPlanets(LiteRtEngine engine, bool? speculative, string label)
    {
        using var conv = engine.CreateConversation(new LiteRtConversationOptions
        {
            MaxOutputTokens = 64, EnableSpeculativeDecoding = speculative,
        });
        string text = conv.Send("List the first five planets of the solar system, comma separated.").Text ?? "";
        output.WriteLine($"{label}: {conv.GetBenchmarkInfo()?.LastDecodeTokensPerSecond:F1} tok/s: {text}");
        Assert.Contains("Mercury", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Speculative decoding on a model without a drafter fails instead of being ignored: at engine creation
    /// with the engine setting (the exception carries the binding's hint), at the first send with the
    /// per-conversation one. The engine keeps answering plain conversations afterwards. Uses upstream's small
    /// LoRA test bundle, which has no drafter, next to LITERTLM_TEST_MODEL.
    /// </summary>
    [SkippableFact]
    public void SpeculativeDecoding_OnAModelWithoutADrafter_Fails()
    {
        SkipWithoutModel();
        string toy = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Model!))!, "test_lm_lora.litertlm");
        Skip.If(!File.Exists(toy), "Place test_lm_lora.litertlm (upstream runtime/testdata) next to LITERTLM_TEST_MODEL to run.");
        LiteRtEngine.SetMinLogLevel(3);
        Assert.False(LiteRtModelInfo.Read(toy).SupportsSpeculativeDecoding);

        var load = Assert.Throws<LiteRtException>(() => LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = toy, Backend = LiteRtBackend.Cpu, EnableSpeculativeDecoding = true,
        }));
        output.WriteLine($"engine: [{load.StatusCode}] {load.Message}");
        Assert.Contains(LiteRtEngine.MissingDrafterHint, load.Message, StringComparison.Ordinal);

        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = toy, Backend = LiteRtBackend.Cpu });
        using (var speculative = engine.CreateConversation(new LiteRtConversationOptions
        {
            MaxOutputTokens = 8, EnableSpeculativeDecoding = true,
        }))
        {
            var send = Assert.Throws<LiteRtException>(() => speculative.Send("Say hello."));
            output.WriteLine($"conversation: [{send.StatusCode}] {send.Message}");
            Assert.NotNull(send.StatusCode);
        }
        using var plain = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 8 });
        Assert.False(string.IsNullOrEmpty(plain.Send("Say hello.").Text));
    }

    /// <summary>
    /// A per-image <see cref="LiteRtConversationOptions.VisualTokenBudget"/> downscales images to a smaller
    /// vision signature, alone or under a matching <see cref="LiteRtEngineOptions.MaxVisionTokensPerImage"/>
    /// cap: the same image costs about the budget instead of the model's default size.
    /// </summary>
    [SkippableFact]
    public void VisualTokenBudget_ShrinksTheImageTokenCost_WithOrWithoutACap()
    {
        SkipWithoutVision();
        int defaultCost = ImageTokenCost(maxVisionTokensPerImage: null, visualTokenBudget: 0);
        int budgetOnly = ImageTokenCost(maxVisionTokensPerImage: null, visualTokenBudget: 70);
        int cappedCost = ImageTokenCost(maxVisionTokensPerImage: 70, visualTokenBudget: 70);
        output.WriteLine($"image tokens: default {defaultCost}, budget 70 {budgetOnly}, cap 70 + budget 70 {cappedCost}");
        Assert.True(cappedCost < defaultCost, $"Expected the capped image ({cappedCost}) to cost less than the default ({defaultCost}).");
        Assert.InRange(cappedCost, 1, 70 + 16);
        Assert.InRange(budgetOnly, 1, 70 + 16);
    }

    /// <summary>
    /// A cap below the model's default image size with no visual token budget leaves no vision signature for a
    /// default-sized image: the runtime rejects the send and says why (InvalidArgument), and the binding does
    /// not bury that specific reason under its generic multimodal setup guidance.
    /// </summary>
    [SkippableFact]
    public void MaxVisionTokensPerImage_BelowTheDefaultImageSize_RequiresAVisualTokenBudget()
    {
        SkipWithoutVision();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = Backend, VisionBackend = Backend, MaxNumTokens = 4096,
            MaxVisionTokensPerImage = 70,
        });
        using var conv = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 8 });
        byte[] png = Convert.FromBase64String(MultimodalModelTests.RedPngBase64);
        var ex = Assert.Throws<LiteRtException>(() => conv.Send("Describe this.", [LiteRtAttachment.Image(png)]));
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.DoesNotContain(LiteRtConversation.MultimodalSendHint, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A budget above the model's own per-image maximum (280 on the Gemma 4 E-series) with no cap set: the
    /// runtime checks every send that carries a budget against that maximum and rejects it
    /// (InvalidArgument). The binding attaches the budget only to sends with an image, so text-only turns,
    /// blocking or streamed, still work and only the image send fails.
    /// </summary>
    [SkippableFact]
    public async Task VisualTokenBudget_AboveTheModelMaximum_FailsImageSends_NotTextTurns()
    {
        SkipWithoutVision();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = Backend, VisionBackend = Backend, MaxNumTokens = 4096,
        });
        using var conv = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 8, VisualTokenBudget = 512 });
        Assert.Contains("Paris", conv.Send("What is the capital of France? One word.").Text, StringComparison.OrdinalIgnoreCase);
        var streamed = new StringBuilder();
        await foreach (var chunk in conv.SendStreamingAsync("And the capital of Italy? One word."))
            streamed.Append(chunk.Text);
        Assert.Contains("Rome", streamed.ToString(), StringComparison.OrdinalIgnoreCase);
        string? perSend = conv.Send("And of Spain? One word.", attachments: null, new LiteRtSendOptions { VisualTokenBudget = 512 }).Text;
        Assert.Contains("Madrid", perSend, StringComparison.OrdinalIgnoreCase);

        byte[] png = Convert.FromBase64String(MultimodalModelTests.RedPngBase64);
        var ex = Assert.Throws<LiteRtException>(() => conv.Send("Describe this.", [LiteRtAttachment.Image(png)]));
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("max vision tokens per image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>With a per-image cap set, a per-send visual token budget above it is rejected by the runtime
    /// (InvalidArgument, with its reason in the message).</summary>
    [SkippableFact]
    public void MaxVisionTokensPerImage_RejectsALargerVisualTokenBudget()
    {
        SkipWithoutVision();
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = Backend, VisionBackend = Backend, MaxNumTokens = 4096,
            MaxVisionTokensPerImage = 70,
        });
        using var conv = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 8 });
        byte[] png = Convert.FromBase64String(MultimodalModelTests.RedPngBase64);
        var ex = Assert.Throws<LiteRtException>(() => conv.Send("Describe this.", [LiteRtAttachment.Image(png)],
            new LiteRtSendOptions { VisualTokenBudget = 280 }));
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("max vision tokens per image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tokens one image adds to a fresh conversation: (text + image) minus (text alone).</summary>
    private static int ImageTokenCost(int? maxVisionTokensPerImage, int visualTokenBudget)
    {
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = Backend, VisionBackend = Backend, MaxNumTokens = 4096,
            MaxVisionTokensPerImage = maxVisionTokensPerImage,
        });
        byte[] png = Convert.FromBase64String(MultimodalModelTests.RedPngBase64);
        int textOnly, withImage;
        using (var conv = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 1 }))
        {
            conv.Send("Describe this.");
            textOnly = conv.TokenCount;
        }
        using (var conv = engine.CreateConversation(new LiteRtConversationOptions
        {
            MaxOutputTokens = 1, VisualTokenBudget = visualTokenBudget,
        }))
        {
            conv.Send("Describe this.", [LiteRtAttachment.Image(png)]);
            withImage = conv.TokenCount;
        }
        return withImage - textOnly;
    }
}
