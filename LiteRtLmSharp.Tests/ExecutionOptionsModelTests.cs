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
    /// Per-conversation speculative decoding on an engine loaded without it: the runtime loads the MTP
    /// drafter lazily for that conversation and the answer stays correct; a conversation that opts out on a
    /// speculative engine also answers. Timings are informational (speculative decoding does not speed up
    /// desktop CPU, see docs/speculative-decoding.md).
    /// </summary>
    [SkippableFact]
    public void ConversationSpeculativeDecoding_OverridesTheEngineSetting()
    {
        SkipWithoutModel();
        LiteRtEngine.SetMinLogLevel(3);
        const string Prompt = "List the first five planets of the solar system, comma separated.";

        using (var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = Backend, MaxNumTokens = 2048, EnableBenchmark = true,
        }))
        {
            foreach (bool? speculative in new bool?[] { null, true })
            {
                using var conv = engine.CreateConversation(new LiteRtConversationOptions
                {
                    MaxOutputTokens = 64, EnableSpeculativeDecoding = speculative,
                });
                string text = conv.Send(Prompt).Text ?? "";
                var bench = conv.GetBenchmarkInfo();
                output.WriteLine($"engine off, conversation {speculative?.ToString() ?? "inherit"}: " +
                                 $"{bench?.LastDecodeTokensPerSecond:F1} tok/s: {text}");
                Assert.Contains("Mercury", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        using (var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = Backend, MaxNumTokens = 2048, EnableBenchmark = true,
            EnableSpeculativeDecoding = true,
        }))
        {
            foreach (bool? speculative in new bool?[] { null, false })
            {
                using var conv = engine.CreateConversation(new LiteRtConversationOptions
                {
                    MaxOutputTokens = 64, EnableSpeculativeDecoding = speculative,
                });
                string text = conv.Send(Prompt).Text ?? "";
                var bench = conv.GetBenchmarkInfo();
                output.WriteLine($"engine on, conversation {speculative?.ToString() ?? "inherit"}: " +
                                 $"{bench?.LastDecodeTokensPerSecond:F1} tok/s: {text}");
                Assert.Contains("Mercury", text, StringComparison.OrdinalIgnoreCase);
            }
        }
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
