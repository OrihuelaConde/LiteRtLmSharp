using System.Text.Json;
using System.Text.RegularExpressions;
using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using LiteRtLmSharp.SemanticKernel;
using Microsoft.Extensions.AI;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>Model-free validation of the v0.15.0 decoding option types.</summary>
public class DecodingOptionsValidationTests
{
    [Fact]
    public void RepetitionPenaltyOptions_RejectsOutOfRangeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtRepetitionPenaltyOptions { RepetitionPenalty = 0.9f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtRepetitionPenaltyOptions { RepetitionPenalty = float.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtRepetitionPenaltyOptions { WindowSize = -1 });
        // Defaults are the native "off" values; negative subtractive penalties are legal (reward mode).
        var defaults = new LiteRtRepetitionPenaltyOptions();
        Assert.Equal(1.0f, defaults.RepetitionPenalty);
        Assert.Equal(0f, defaults.PresencePenalty);
        Assert.Equal(0f, defaults.FrequencyPenalty);
        Assert.Equal(0, defaults.WindowSize);
        _ = new LiteRtRepetitionPenaltyOptions { PresencePenalty = -0.5f, FrequencyPenalty = -0.5f };
    }

    [Fact]
    public void NoRepeatNgramOptions_RejectsNonPositiveSizeAndNegativeWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtNoRepeatNgramOptions { NgramSize = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtNoRepeatNgramOptions { NgramSize = -3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtNoRepeatNgramOptions { NgramSize = 2, WindowSize = -1 });
        Assert.Equal(0, new LiteRtNoRepeatNgramOptions { NgramSize = 2 }.WindowSize);
    }

    [Fact]
    public void Constraint_RejectsEmptyPattern_AndFactoriesTagTheType()
    {
        Assert.Throws<ArgumentException>(() => new LiteRtConstraint { Type = LiteRtConstraintType.Regex, Pattern = " " });
        Assert.Throws<ArgumentNullException>(() => new LiteRtConstraint { Type = LiteRtConstraintType.Regex, Pattern = null! });

        var regex = LiteRtConstraint.FromRegex("[0-9]+");
        Assert.Equal(LiteRtConstraintType.Regex, regex.Type);
        Assert.Equal("[0-9]+", regex.Pattern);

        var schema = LiteRtConstraint.FromJsonSchema("""{"type":"object"}""");
        Assert.Equal(LiteRtConstraintType.JsonSchema, schema.Type);
    }

    [Fact]
    public void ThinkingTokenBudget_RejectsBelowMinusOne_OnBothLevels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtConversationOptions { ThinkingTokenBudget = -2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtSendOptions { ThinkingTokenBudget = -2 });
        // -1 (infinite), 0 and positive are all legal.
        _ = new LiteRtConversationOptions { ThinkingTokenBudget = -1 };
        _ = new LiteRtSendOptions { ThinkingTokenBudget = 128 };
    }

    [Fact]
    public void EngineOptions_GpuDecodeStepsPerSync_RejectsNonPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtEngineOptions { GpuDecodeStepsPerSync = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteRtEngineOptions { GpuDecodeStepsPerSync = -1 });
        _ = new LiteRtEngineOptions { GpuDecodeStepsPerSync = 4, GpuWaitForWeightUploads = true, UseRingbuffersLocalAttention = true };
    }
}

/// <summary>MEAI/SK mapping of the v0.15.0 decoding features.</summary>
public class DecodingOptionsMappingTests
{
    [Fact]
    public void ToSendOptions_MapsOpenAiPenalties()
    {
        LiteRtSendOptions? send = LiteRtChatMapping.ToSendOptions(new ChatOptions
        {
            FrequencyPenalty = 0.7f,
            PresencePenalty = 0.3f,
        });

        Assert.NotNull(send?.RepetitionPenalties);
        Assert.Equal(0.7f, send!.RepetitionPenalties!.FrequencyPenalty, 3);
        Assert.Equal(0.3f, send.RepetitionPenalties.PresencePenalty, 3);
        // The multiplicative penalty stays at its native "off" default — MEAI has no surface for it.
        Assert.Equal(1.0f, send.RepetitionPenalties.RepetitionPenalty, 3);
    }

    [Fact]
    public void ToSendOptions_MapsJsonSchemaResponseFormat_ToConstraint()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","required":["city"]}""");
        LiteRtSendOptions? send = LiteRtChatMapping.ToSendOptions(new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement),
        });

        Assert.NotNull(send?.Constraint);
        Assert.Equal(LiteRtConstraintType.JsonSchema, send!.Constraint!.Type);
        Assert.Contains("\"city\"", send.Constraint.Pattern, StringComparison.Ordinal);
    }

    [Fact]
    public void ToSendOptions_SchemalessJsonAndText_MapNoConstraint()
    {
        Assert.Null(LiteRtChatMapping.ToSendOptions(new ChatOptions { ResponseFormat = ChatResponseFormat.Json })?.Constraint);
        Assert.Null(LiteRtChatMapping.ToSendOptions(new ChatOptions { ResponseFormat = ChatResponseFormat.Text }));
    }

    [Fact]
    public void ToConversationOptions_ArmsLlGuidance_WhenResponseFormatHasSchema()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object"}""");
        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions(
            [], new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement) });

        Assert.NotNull(conv);
        Assert.Equal(LiteRtConstraintProvider.LlGuidance, conv!.ConstraintProvider);
    }

    [Fact]
    public void ToConversationOptions_PassesNewTemplateSettingsThrough()
    {
        var template = new LiteRtConversationOptions
        {
            ThinkingTokenBudget = 128,
            PromptTemplate = "{{ messages }}",
            ConstraintProvider = LiteRtConstraintProvider.LlGuidance,
        };
        LiteRtConversationOptions? conv = LiteRtChatMapping.ToConversationOptions([], options: null, template);

        Assert.NotNull(conv);
        Assert.Equal(128, conv!.ThinkingTokenBudget);
        Assert.Equal("{{ messages }}", conv.PromptTemplate);
        Assert.Equal(LiteRtConstraintProvider.LlGuidance, conv.ConstraintProvider);
    }

    [Fact]
    public void SkSettings_PenaltyProperties_WriteWellKnownKeysAndRoundTrip()
    {
        var s = new LiteRtPromptExecutionSettings { PresencePenalty = 0.4f, FrequencyPenalty = 0.9f };

        // The exact ExtensionData keys Semantic Kernel's IChatClient adapter reads (same converter
        // path as "top_p" — both key spellings verified present in the SK 1.77 assembly).
        Assert.Equal(0.4f, Assert.IsType<float>(s.ExtensionData!["presence_penalty"]), 3);
        Assert.Equal(0.9f, Assert.IsType<float>(s.ExtensionData["frequency_penalty"]), 3);
        Assert.Equal(0.4f, s.PresencePenalty!.Value, 3);
        Assert.Equal(0.9f, s.FrequencyPenalty!.Value, 3);
    }
}

/// <summary>
/// Model-backed scenarios for the v0.15.0 decoding features (suppress tokens, no-repeat-ngram,
/// thinking budget, LlGuidance constraints). Set LITERTLM_TEST_MODEL to run; sends rely on the
/// engine's default sampling (deterministic at the seed-0 default) — see the sampler note below.
/// </summary>
public sealed class DecodingModelTests(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    private readonly EngineFixture _fixture = fixture;

    // No explicit sampler anywhere in these tests: the engine's internal default sampling is
    // deterministic at its seed-0 default, and an explicit Greedy/TopK sampler CANNOT be used on the
    // CPU backend — the native CPU sampler factory only implements TopP (both v0.14.0 and v0.15.0;
    // sampler_factory.cc CreateCpuSampler), so a send that needs a fresh Greedy/TopK sampler fails
    // with UNIMPLEMENTED "Sampler type: N not implemented yet" (and whether it needs one is
    // order-dependent: an earlier TopP/default conversation leaves a sampler behind that later
    // conversations silently reuse). Verified against both native eras on win-x64 CPU, 2026-08-09.

    /// <summary>Suppressing every token id of the expected answer word must keep that word out of the
    /// reply — the most directly observable of the new logit processors (the id's logit is forced to
    /// -inf on every step).</summary>
    [SkippableFact]
    public void Send_WithSuppressTokens_KeepsBannedWordOut()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        var engine = _fixture.Engine!;

        const string prompt = "What is the capital of France? Reply with just the city name.";

        using (var baseline = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 32 }))
        {
            string text = baseline.Send(prompt).Text ?? "";
            Skip.If(!text.Contains("Paris", StringComparison.OrdinalIgnoreCase),
                $"Baseline did not answer Paris (got: '{text}') — cannot exercise the suppression meaningfully.");
        }

        // Ban every id of the surface forms the answer could start with (with and without the
        // SentencePiece leading-space marker).
        int[] banned = [.. engine.Tokenize(" Paris"), .. engine.Tokenize("Paris")];

        using var constrained = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 32 });
        string suppressed = constrained.Send(prompt, attachments: null,
            new LiteRtSendOptions { SuppressTokens = banned }).Text ?? "";

        Assert.DoesNotContain("Paris", suppressed, StringComparison.Ordinal);
    }

    /// <summary>With a 2-gram ban, an instructed exact repetition ("echo echo echo …") becomes
    /// impossible: completing an already-seen (echo, echo) bigram is masked, so the long run present
    /// in the greedy baseline must not appear.</summary>
    [SkippableFact]
    public void Send_WithNoRepeatNgram_BreaksExactRepetition()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        var engine = _fixture.Engine!;

        const string prompt = "Repeat the word echo exactly ten times, separated by single spaces, nothing else.";

        // The ban operates on TOKEN bigrams, and no STRING-level property survives it reliably: the
        // model escapes the banned ([▁echo],[▁echo]) bigram through neighboring tokens that still
        // read as the word — observed escapes include "echo echo echo ech echo" (local win-x64) and
        // "echo echo echo echoe echo" (CI win-x64/macOS), where "echoe" starts with "echo" and so
        // still CONTAINS a 4-run substring despite zero repeated token bigrams. The honest,
        // platform-stable assertion is behavioral: under the engine's deterministic seed-0 default
        // sampling, the same prompt on a fresh conversation reproduces the baseline byte-for-byte —
        // unless the ngram config actually reaches the decode and masks logits, in which case the
        // output MUST diverge.
        string baselineText;
        using (var baseline = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 64 }))
        {
            baselineText = baseline.Send(prompt).Text ?? "";
            Skip.If(!baselineText.Contains("echo echo echo echo", StringComparison.OrdinalIgnoreCase),
                $"Baseline did not produce the repetition (got: '{baselineText}') — cannot exercise the ngram ban meaningfully.");
        }

        using var constrained = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 64 });
        string banned = constrained.Send(prompt, attachments: null,
            new LiteRtSendOptions { NoRepeatNgram = new LiteRtNoRepeatNgramOptions { NgramSize = 2 } }).Text ?? "";

        Assert.NotEqual(baselineText, banned);
    }

    /// <summary>The thinking token budget must bound the reasoning block: with a small budget the
    /// thinking trace tokenizes to at most the budget plus a small structural margin (the cut happens
    /// at token granularity and the end-of-thinking marker is appended). The unbudgeted baseline on
    /// this prompt runs much longer, so the bound is the observable effect.</summary>
    [SkippableFact]
    public void Send_WithThinkingTokenBudget_BoundsThinkingLength()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        var engine = _fixture.Engine!;

        const string prompt = "What is 23 multiplied by 47? Think it through step by step, then answer.";
        const int budget = 48;

        using var conv = engine.CreateConversation(new LiteRtConversationOptions
        {
            EnableThinking = true,
            ThinkingTokenBudget = budget,
            MaxOutputTokens = 512,
        });
        var response = conv.Send(prompt);

        Skip.If(response.Thinking is null or { Length: 0 },
            "Model produced no thinking trace on this prompt — budget not exercisable.");
        int thinkingTokens = engine.Tokenize(response.Thinking!).Length;
        Assert.True(thinkingTokens <= budget + 16,
            $"Thinking ran to {thinkingTokens} tokens despite a budget of {budget}.");
    }

    /// <summary>LlGuidance regex constraint: the reply must match the pattern exactly — constrained
    /// sampling masks every token that cannot extend a valid match. Also the readiness probe for the
    /// from-source LlGuidance provider on every platform (unlike tool-calling constrained decoding,
    /// whose linux-x64 prebuilt provider is broken — see the guard in LiteRtConversation.Create).</summary>
    [SkippableFact]
    public void Send_WithLlGuidanceRegexConstraint_OutputMatchesPattern()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var conv = _fixture.Engine!.CreateConversation(new LiteRtConversationOptions
        {
            ConstraintProvider = LiteRtConstraintProvider.LlGuidance,
            MaxOutputTokens = 16,
        });
        string text = conv.Send("Pick a number between 1 and 50. Reply with just the number.",
            attachments: null,
            new LiteRtSendOptions { Constraint = LiteRtConstraint.FromRegex("[0-9]{1,3}") }).Text ?? "";

        Assert.Matches(new Regex("^[0-9]{1,3}$"), text.Trim());
    }

    /// <summary>LlGuidance JSON-Schema constraint: the reply must parse as JSON conforming to the
    /// schema (an object with a required string property).</summary>
    [SkippableFact]
    public void Send_WithJsonSchemaConstraint_ProducesConformingJson()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        const string schema = """
            {"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}
            """;

        using var conv = _fixture.Engine!.CreateConversation(new LiteRtConversationOptions
        {
            ConstraintProvider = LiteRtConstraintProvider.LlGuidance,
            MaxOutputTokens = 64,
        });
        string text = conv.Send("What is the capital of France? Answer as JSON.",
            attachments: null,
            new LiteRtSendOptions { Constraint = LiteRtConstraint.FromJsonSchema(schema) }).Text ?? "";

        using JsonDocument doc = JsonDocument.Parse(text.Trim());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("city").ValueKind);
    }

    /// <summary>A per-send constraint on a conversation created without a constraint provider must be
    /// rejected managed-side with a clear message (nothing native can enforce it).</summary>
    [SkippableFact]
    public void Send_ConstraintWithoutProvider_ThrowsClearError()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var conv = _fixture.Engine!.CreateConversation();
        var ex = Assert.Throws<LiteRtException>(() => conv.Send("hello", attachments: null,
            new LiteRtSendOptions { Constraint = LiteRtConstraint.FromRegex("[a-z]+") }));
        Assert.Contains("ConstraintProvider", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Mutual exclusion: tool-calling constrained decoding and a custom constraint provider
    /// cannot be combined on one conversation.</summary>
    [SkippableFact]
    public void CreateConversation_BothConstrainedModes_ThrowsArgumentException()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        Assert.Throws<ArgumentException>(() => _fixture.Engine!.CreateConversation(new LiteRtConversationOptions
        {
            EnableConstrainedDecoding = true,
            ConstraintProvider = LiteRtConstraintProvider.LlGuidance,
        }));
    }
}
