using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Model-backed tests of a per-send <see cref="LiteRtSendOptions.EnableThinking"/> <c>false</c> on a conversation
/// with thinking on, on the sends other than a plain text follow-up: tool results, a follow-up that calls a tool,
/// and constrained sends. The binding suppresses the token that opens the thinking channel on such a send. Each test
/// runs the same sends on two conversations under argmax: a control with that suppression turned off through an
/// internal switch, and one with it. When the control's reply does not think, the suppressed reply must be the same
/// reply; when it thinks, the suppressed reply must not. Measured on LiteRT-LM v0.20.0, gemma-4 E2B, win-x64 CPU and
/// GPU: the tool-result reply and the follow-up tool call did not think in the control either, and the regex and
/// JSON-schema constraints kept the control from thinking. Gated on <c>LITERTLM_TEST_MODEL</c>, and the tool cases
/// on <c>LITERTLM_TEST_TOOLS=1</c>; <c>LITERTLM_TEST_BACKEND</c> picks the backend.
/// </summary>
public sealed class ThinkingOffModelTests(EngineFixture fixture, ITestOutputHelper output) : IClassFixture<EngineFixture>
{
    private static readonly LiteRtSamplerParams Argmax = new() { Strategy = LiteRtSamplerType.TopP, TopK = 1 };

    private static readonly LiteRtTool Weather = new("get_current_weather", "Get the current weather for a city.",
        """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""");

    private static readonly LiteRtToolResult[] WeatherResult =
        [new("get_current_weather", """{"temperature":15,"unit":"celsius"}""")];

    private const string BridgesPrompt = "Think briefly about bridges, then reply with one short sentence about bridges.";

    private void SkipWithoutModel() => Skip.If(fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

    private void SkipUnlessToolTestsEnabled()
        => Skip.If(fixture.Engine is null || Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (and LITERTLM_TEST_MODEL with a version-matched binary) to run.");

    private static LiteRtConversationOptions ToolOptions(bool constrained) => new()
    {
        SystemMessage = "Use tools when needed.", EnableThinking = true, EnableConstrainedDecoding = constrained,
        MaxOutputTokens = 512, Sampler = Argmax, Tools = [Weather],
    };

    private LiteRtConversation Create(LiteRtConversationOptions options, bool suppress)
    {
        LiteRtConversation conv = fixture.Engine!.CreateConversation(options);
        conv.SuppressThinkingChannelWhenThinkingOff = suppress;
        return conv;
    }

    private void Log(string label, LiteRtResponse response)
        => output.WriteLine($"{label}: thinking {response.Thinking?.Length ?? 0} chars, channels " +
                            $"[{string.Join(",", response.Channels.Keys)}], raw {response.RawJson}");

    /// <summary>When the control's reply does not think, the suppression must leave the reply as it is; when it
    /// thinks, the suppressed reply must carry no thinking.</summary>
    private static void AssertSuppressionEffect(LiteRtResponse control, LiteRtResponse suppressed)
    {
        if (string.IsNullOrEmpty(control.Thinking))
            // Two argmax conversations gave byte-identical replies on win-x64, CPU and GPU.
            Assert.True(control.RawJson == suppressed.RawJson,
                "The suppressed reply differs from the control's, which did not think. Either the suppression changed " +
                "a reply that never opens the thinking channel, or this backend's argmax decode is not reproducible " +
                "across two conversations: compare two controls on this backend to tell them apart. Control: " +
                $"{control.RawJson} Suppressed: {suppressed.RawJson}");
        else
            Assert.True(string.IsNullOrEmpty(suppressed.Thinking),
                $"Per-send EnableThinking=false left a thinking trace ({suppressed.Thinking?.Length} chars).");
    }

    private static void AssertWeatherCall(LiteRtResponse response)
    {
        Assert.True(response.IsToolCall, $"Expected a tool call. Raw: {response.RawJson}");
        LiteRtToolCall call = Assert.Single(response.ToolCalls);
        Assert.Equal("get_current_weather", call.Name);
        using JsonDocument args = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal(JsonValueKind.String, args.RootElement.GetProperty("location").ValueKind);
    }

    /// <summary>Tool results sent with thinking off, after a first send that thought and called the tool: the
    /// reply is a text answer, and the send does not fail. Measured: the reply ("The current weather in Tokyo is
    /// 15 degrees Celsius." on CPU) did not think with or without the suppression, and was the same reply. Skips
    /// when the first send does not call the tool.</summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SendToolResults_WithThinkingOff_AnswersFromTheResults(bool constrained)
    {
        SkipUnlessToolTestsEnabled();

        LiteRtResponse? Run(bool suppress)
        {
            using LiteRtConversation conv = Create(ToolOptions(constrained), suppress);
            LiteRtResponse first = conv.Send("What's the weather in Tokyo?");
            Log(suppress ? "suppressed first" : "control first", first);
            if (!first.IsToolCall)
                return null;
            AssertWeatherCall(first);
            LiteRtResponse final = conv.SendToolResults(WeatherResult, new LiteRtSendOptions { EnableThinking = false });
            Log(suppress ? "suppressed results" : "control results", final);
            return final;
        }

        LiteRtResponse? control = Run(suppress: false);
        Skip.If(control is null, "The first send did not call the tool on this backend, so there are no results to send.");
        LiteRtResponse? suppressed = Run(suppress: true);

        // The first send has no per-send options, so both conversations make the same call.
        Assert.NotNull(suppressed);
        Assert.False(suppressed.IsToolCall, $"Expected a text answer. Raw: {suppressed.RawJson}");
        Assert.False(string.IsNullOrWhiteSpace(suppressed.Text));
        AssertSuppressionEffect(control!, suppressed);
    }

    /// <summary>A follow-up sent with thinking off, after a first reply that thought, still calls the tool with
    /// well-formed arguments. Measured: the call did not think with or without the suppression, and was the same
    /// call. Skips when the follow-up does not call the tool without the suppression either.</summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FollowUpToolCall_WithThinkingOff_IsWellFormed(bool constrained)
    {
        SkipUnlessToolTestsEnabled();

        LiteRtResponse Run(bool suppress)
        {
            using LiteRtConversation conv = Create(ToolOptions(constrained), suppress);
            Log(suppress ? "suppressed first" : "control first", conv.Send(BridgesPrompt));
            LiteRtResponse call = conv.Send("What's the weather in Tokyo?", attachments: null,
                new LiteRtSendOptions { EnableThinking = false });
            Log(suppress ? "suppressed follow-up" : "control follow-up", call);
            return call;
        }

        LiteRtResponse control = Run(suppress: false);
        Skip.If(!control.IsToolCall,
            "Without the suppression the follow-up did not call the tool on this backend, so it cannot show that the " +
            "suppression keeps the call.");
        LiteRtResponse suppressed = Run(suppress: true);

        AssertWeatherCall(suppressed);
        AssertSuppressionEffect(control, suppressed);
    }

    /// <summary>A constrained follow-up sent with thinking off, after a first reply that thought: tool-calling
    /// constrained decoding without tools (case 0), an LlGuidance regex (case 1) and an LlGuidance JSON schema
    /// (case 2). The reply follows the constraint and the send does not fail. Measured: in case 0 the control's
    /// reply thought (435 characters on CPU, 434 on GPU) and the suppressed one did not; in cases 1 and 2 neither
    /// thought, and the replies were the same.</summary>
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ConstrainedFollowUp_WithThinkingOff_FollowsTheConstraint(int kind)
    {
        SkipWithoutModel();
        const string schema = """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""";
        (string Prompt, LiteRtConstraint? Constraint) send = kind switch
        {
            0 => ("What is the capital of France? Answer in one short sentence.", null),
            1 => ("Pick a number between 1 and 50. Reply with just the number.", LiteRtConstraint.FromRegex("[0-9]{1,3}")),
            _ => ("What is the capital of France? Answer as JSON.", LiteRtConstraint.FromJsonSchema(schema)),
        };

        LiteRtResponse Run(bool suppress)
        {
            using LiteRtConversation conv = Create(new LiteRtConversationOptions
            {
                EnableThinking = true, MaxOutputTokens = 512, Sampler = Argmax,
                EnableConstrainedDecoding = kind == 0,
                ConstraintProvider = kind > 0 ? LiteRtConstraintProvider.LlGuidance : null,
            }, suppress);
            Log(suppress ? "suppressed first" : "control first", conv.Send(BridgesPrompt));
            LiteRtResponse reply = conv.Send(send.Prompt, attachments: null,
                new LiteRtSendOptions { EnableThinking = false, Constraint = send.Constraint });
            Log(suppress ? "suppressed follow-up" : "control follow-up", reply);
            return reply;
        }

        LiteRtResponse control = Run(suppress: false);
        LiteRtResponse suppressed = Run(suppress: true);

        string text = (suppressed.Text ?? "").Trim();
        switch (kind)
        {
            case 0:
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("<|channel>", text, StringComparison.Ordinal);
                break;
            case 1:
                Assert.Matches(new Regex("^[0-9]{1,3}$"), text);
                break;
            default:
                using (JsonDocument doc = JsonDocument.Parse(text))
                    Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("city").ValueKind);
                break;
        }
        AssertSuppressionEffect(control, suppressed);
    }

    /// <summary>With <see cref="LiteRtConversationOptions.StreamToolCalls"/> on, a blocking tool call carries no
    /// channel under the tool-call stream channel's name, with or without thinking (measured on LiteRT-LM v0.20.0,
    /// gemma-4 E2B, CPU and GPU). The truncation check behind
    /// <see cref="LiteRtConversation.LastReplyReachedContextLimit"/> counts every channel of a blocking reply as
    /// thinking; if the runtime starts to put the tool-call text in such a channel, the check has to leave it out,
    /// as the stream does.</summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockingToolCall_WithStreamToolCalls_CarriesNoToolCallChannel(bool thinking)
    {
        SkipUnlessToolTestsEnabled();

        using LiteRtConversation conv = fixture.Engine!.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = "Use tools when needed.", MaxOutputTokens = 512, Sampler = Argmax, Tools = [Weather],
            StreamToolCalls = true, EnableThinking = thinking,
        });
        LiteRtResponse response = conv.Send("What's the weather in Tokyo?");
        Log("blocking", response);
        AssertWeatherCall(response);
        Assert.DoesNotContain(LiteRtConversation.ToolCallStreamChannelName, response.Channels.Keys);
    }

    /// <summary>The truncation check skips the tokenizer for a reply with fewer UTF-8 bytes than its threshold,
    /// which assumes that the tokenizer gives at most one token per byte, with no token added around the text. On
    /// these strings, gemma-4 E2B's tokenizer gives at most one token per byte (measured on LiteRT-LM v0.20.0, CPU
    /// and GPU: "x " and the one-byte strings "a" and "\n" give one token per byte; every other string gives fewer
    /// tokens than bytes).</summary>
    [SkippableFact]
    public void CountTokens_IsAtMostTheUtf8ByteCount()
    {
        SkipWithoutModel();

        foreach (string text in new[] { "a", "日", "日本", "\U0001F600", " x", "x ", "\n", "  ", "é",
                     "<|channel>", "ab", "​", "\U0001F1E6\U0001F1F7", "\t\t" })
        {
            int bytes = Encoding.UTF8.GetByteCount(text);
            int tokens = fixture.Engine!.CountTokens(text);
            output.WriteLine($"{JsonSerializer.Serialize(text)}: {bytes} bytes, {tokens} tokens");
            Assert.True(tokens <= bytes, $"{JsonSerializer.Serialize(text)}: {tokens} tokens for {bytes} bytes");
        }
    }
}

/// <summary>
/// Model-backed tests of a per-send <see cref="LiteRtSendOptions.EnableThinking"/> <c>false</c> that need an engine
/// of their own: speculative decoding, and a conversation that outlives its disposed engine object. Each test loads
/// one engine at a time (the assembly disables parallelization). Gated on <c>LITERTLM_TEST_MODEL</c>;
/// <c>LITERTLM_TEST_BACKEND</c> picks the backend.
/// </summary>
public sealed class ThinkingOffOwnEngineModelTests(ITestOutputHelper output)
{
    private static readonly LiteRtSamplerParams Argmax = new() { Strategy = LiteRtSamplerType.TopP, TopK = 1 };
    private static string? Model => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
    private static LiteRtBackend Backend => LiteRtBackend.Parse(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu");

    private const string BridgesPrompt = "Think briefly about bridges, then reply with one short sentence about bridges.";
    private const string CapitalPrompt = "What is the capital of France? Answer in one short sentence.";

    private static LiteRtEngine LoadEngine()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        LiteRtEngine.SetMinLogLevel(3);
        return LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = Model!, Backend = Backend, MaxNumTokens = 2048, EnableBenchmark = true,
        });
    }

    /// <summary>With speculative decoding on, a follow-up sent with thinking off answers without thinking: the
    /// suppression also holds for the drafted tokens. The control, with the suppression turned off, thinks (435
    /// characters on CPU, 440 on GPU; LiteRT-LM v0.20.0, gemma-4 E2B, argmax). The decode rates are logged only:
    /// in the measurement they varied as much between two identical first sends as between the two follow-ups.
    /// The test skips when the control's follow-up does not think, after the checks on the reply itself.</summary>
    [SkippableFact]
    public void SpeculativeFollowUp_WithThinkingOff_AnswersWithoutThinking()
    {
        Skip.If(string.IsNullOrEmpty(Model) || !File.Exists(Model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");
        Skip.IfNot(LiteRtModelInfo.Read(Model!).SupportsSpeculativeDecoding, "The test model has no MTP drafter.");
        using LiteRtEngine engine = LoadEngine();

        LiteRtResponse Run(bool suppress)
        {
            using LiteRtConversation conv = engine.CreateConversation(new LiteRtConversationOptions
            {
                EnableThinking = true, MaxOutputTokens = 512, Sampler = Argmax, EnableSpeculativeDecoding = true,
            });
            conv.SuppressThinkingChannelWhenThinkingOff = suppress;
            string arm = suppress ? "suppressed" : "control";
            LiteRtResponse first = conv.Send(BridgesPrompt);
            LiteRtBenchmarkInfo b1 = conv.GetBenchmarkInfo()!;
            output.WriteLine($"{arm} first: thinking {first.Thinking?.Length ?? 0} chars, decode {b1.LastDecodeTokenCount} " +
                             $"at {b1.LastDecodeTokensPerSecond:F1} tok/s");
            LiteRtResponse off = conv.Send(CapitalPrompt, attachments: null, new LiteRtSendOptions { EnableThinking = false });
            LiteRtBenchmarkInfo b2 = conv.GetBenchmarkInfo()!;
            output.WriteLine($"{arm} off: thinking {off.Thinking?.Length ?? 0} chars, answer '{off.Text}', decode " +
                             $"{b2.LastDecodeTokenCount} at {b2.LastDecodeTokensPerSecond:F1} tok/s");
            return off;
        }

        LiteRtResponse control = Run(suppress: false);
        LiteRtResponse suppressed = Run(suppress: true);

        Assert.False(string.IsNullOrWhiteSpace(suppressed.Text));
        Assert.DoesNotContain("<|channel>", suppressed.Text, StringComparison.Ordinal);
        Skip.If(string.IsNullOrEmpty(control.Thinking),
            "Without the binding's suppression the speculative follow-up does not think on this backend, so it " +
            "cannot show the suppression.");
        Assert.True(string.IsNullOrEmpty(suppressed.Thinking),
            $"Per-send EnableThinking=false left a thinking trace under speculative decoding ({suppressed.Thinking?.Length} chars).");
    }

    /// <summary>A conversation that outlives its disposed engine object makes its first thinking-off send: the token
    /// lookup runs then, for the first time, against the native engine that the conversation keeps alive. The send
    /// does not throw and answers without thinking, and the lookup found the token.</summary>
    [SkippableFact]
    public void ConversationThatOutlivesItsEngine_ThinkingOffFollowUp_AnswersWithoutThinking()
    {
        LiteRtEngine engine = LoadEngine();
        LiteRtConversation conv;
        try
        {
            conv = engine.CreateConversation(new LiteRtConversationOptions
            {
                EnableThinking = true, MaxOutputTokens = 512, Sampler = Argmax,
            });
        }
        finally
        {
            // Disposed only after the conversation exists, which keeps the native engine alive.
            engine.Dispose();
        }
        using (conv)
        {
            Assert.Throws<ObjectDisposedException>(() => engine.Tokenize("x"));
            LiteRtResponse first = conv.Send(BridgesPrompt);
            LiteRtResponse off = conv.Send(CapitalPrompt, attachments: null, new LiteRtSendOptions { EnableThinking = false });
            output.WriteLine($"first thinking {first.Thinking?.Length ?? 0} chars; off thinking {off.Thinking?.Length ?? 0} " +
                             $"chars, answer '{off.Text}'");
            Assert.NotNull(engine.ThinkingChannelOpenToken);
            Assert.False(string.IsNullOrWhiteSpace(off.Text));
            // The suppressed token cannot be sampled, and a text follow-up's prompt does not open the channel.
            Assert.True(string.IsNullOrEmpty(off.Thinking),
                $"Per-send EnableThinking=false left a thinking trace ({off.Thinking?.Length} chars).");
        }
    }
}
