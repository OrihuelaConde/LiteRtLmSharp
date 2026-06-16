using LiteRtLmSharp;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

public class NativeInteropTests
{
    /// <summary>
    /// Proves the native LiteRtLm library resolves and a C ABI function is callable.
    /// This validates the whole P/Invoke + native-resolution path without needing a model.
    /// </summary>
    [Fact]
    public void NativeLibrary_Loads_And_LogLevelCallSucceeds()
    {
        // Will throw DllNotFoundException / EntryPointNotFoundException if interop is broken.
        var ex = Record.Exception(() => LiteRtEngine.SetMinLogLevel(3));
        Assert.Null(ex);
    }

    [Fact]
    public void Load_WithMissingModel_Throws()
    {
        var options = new LiteRtEngineOptions { ModelPath = "does-not-exist.litertlm" };
        Assert.Throws<ArgumentException>(() => LiteRtEngine.Load(options));
    }
}

/// <summary>
/// Loads the model engine ONCE for the whole test class. Only one engine may be ALIVE at a
/// time (and engine creation is the expensive step), so engine-backed tests share a single
/// <see cref="LiteRtEngine"/> and create per-test conversations from it.
/// Set LITERTLM_TEST_MODEL to a .litertlm file to enable these tests, and optionally
/// LITERTLM_TEST_BACKEND to run them on another backend (e.g. "gpu"; default "cpu").
/// </summary>
public sealed class EngineFixture : IDisposable
{
    public LiteRtEngine? Engine { get; }

    public EngineFixture()
    {
        string? modelPath = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
        {
            LiteRtEngine.SetMinLogLevel(3);
            Engine = LiteRtEngine.Load(new LiteRtEngineOptions
            {
                ModelPath = modelPath,
                Backend = Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu",
                MaxNumTokens = 2048,
            });
        }
    }

    public void Dispose() => Engine?.Dispose();
}

public sealed class ModelTests(EngineFixture fixture) : IClassFixture<EngineFixture>
{
    private readonly EngineFixture _fixture = fixture;

    /// <summary>Plain blocking chat. Skipped unless LITERTLM_TEST_MODEL is set.</summary>
    [SkippableFact]
    public void Chat_Blocking_ProducesText()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var conversation = _fixture.Engine!.CreateConversation();
        var response = conversation.Send("Reply with one short sentence: what is the capital of France?");

        Assert.False(string.IsNullOrWhiteSpace(response.Text),
            $"Expected a non-empty blocking response. Raw: {response.RawJson}");
    }

    /// <summary>End-to-end streaming generation. Skipped unless LITERTLM_TEST_MODEL is set.
    /// (Validated on v0.13.1; the async path crashed on the interim commit 032334d8.)</summary>
    [SkippableFact]
    public async Task Streaming_Generation_ProducesText()
    {
        Skip.If(_fixture.Engine is null, "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        using var conversation = _fixture.Engine!.CreateConversation();
        var sb = new System.Text.StringBuilder();
        await foreach (string chunk in conversation.SendMessageStreamingAsync("Count from 1 to 3."))
            sb.Append(chunk);

        Assert.True(sb.Length > 0, "Expected a non-empty streamed response.");
    }

    /// <summary>
    /// Function-calling loop WITHOUT constrained decoding — works on every platform (this is
    /// the documented workaround while the linux-x64 constrained-decoding guard is in place).
    /// </summary>
    [SkippableFact]
    public void ToolCalling_Unconstrained_Loop_ExecutesTool()
    {
        SkipUnlessToolTestsEnabled();
        RunToolCallingLoop(constrainedDecoding: false);
    }

    /// <summary>
    /// Function-calling loop WITH constrained decoding. On linux-x64 this currently asserts the
    /// temporary guard fires (upstream prebuilt constraint provider is broken — LiteRT-LM#2149,
    /// see the roadmap watchlist); everywhere else it runs the real end-to-end loop.
    /// </summary>
    [SkippableFact]
    public void ToolCalling_Constrained_Loop_ExecutesTool()
    {
        SkipUnlessToolTestsEnabled();

        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            // TEMPORARY branch: validates the guard in LiteRtConversation.Create. When Google
            // republishes a fixed linux prebuilt and the guard is removed, DELETE this branch so
            // the constrained loop runs for real on Linux too (removal steps: docs/roadmap.md).
            Assert.Throws<PlatformNotSupportedException>(() => RunToolCallingLoop(constrainedDecoding: true));
            return;
        }

        RunToolCallingLoop(constrainedDecoding: true);
    }

    /// <summary>
    /// Gated on LITERTLM_TEST_TOOLS=1 because the conversation-config path access-violates on
    /// version-skewed binaries (e.g. community native-v0.12.0-a) — only run with a
    /// version-matched build in runtimes/&lt;rid&gt;/native.
    /// </summary>
    private void SkipUnlessToolTestsEnabled()
        => Skip.If(_fixture.Engine is null || Environment.GetEnvironmentVariable("LITERTLM_TEST_TOOLS") != "1",
            "Set LITERTLM_TEST_TOOLS=1 (and LITERTLM_TEST_MODEL with a version-matched binary) to run.");

    private void RunToolCallingLoop(bool constrainedDecoding)
    {
        using var conv = _fixture.Engine!.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = "Use tools when needed.",
            EnableConstrainedDecoding = constrainedDecoding,
            MaxOutputTokens = 128,
            Tools =
            [
                new LiteRtTool("get_current_weather", "Get the current weather for a city.",
                    """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}"""),
            ],
        });

        var response = conv.Send("What's the weather in Tokyo?");
        Assert.True(response.IsToolCall, $"Expected a tool call. Raw: {response.RawJson}");
        Assert.Equal("get_current_weather", response.ToolCalls[0].Name);

        var final = conv.SendToolResults(
            [new LiteRtToolResult("get_current_weather", """{"temperature":15,"unit":"celsius"}""")]);
        Assert.False(string.IsNullOrWhiteSpace(final.Text), $"Expected a final text answer. Raw: {final.RawJson}");
    }
}

/// <summary>
/// A/B benchmark for speculative decoding: measures decode throughput (via the native benchmark
/// API) with the MTP drafter OFF vs ON, on the same fixed prompt, and confirms both runs still
/// produce coherent, non-empty text. The speedup ratio is logged for the docs/CI record.
/// <para>
/// Sampler note: the v0.13.1 native build only implements the TopP sampler (Greedy/TopK return
/// "not implemented yet"), so the runs are stochastic and the two outputs are NOT expected to be
/// byte-identical — we therefore assert coherence + measured throughput (the real deliverable) and
/// log output similarity rather than asserting equality. A fixed seed keeps each run reproducible.
/// </para>
/// <para>
/// This class loads its OWN engines (it does NOT take <see cref="EngineFixture"/>) and disposes each
/// before loading the next, so only one engine is ever alive — required because two live engines hang
/// the native layer. Safe to coexist with <see cref="ModelTests"/>: the assembly disables test
/// parallelization (see AssemblyInfo.cs), so classes run one at a time and the shared fixture engine
/// is never alive while this runs.
/// </para>
/// Gated on <c>LITERTLM_TEST_BENCH=1</c> (in addition to <c>LITERTLM_TEST_MODEL</c>) because it loads
/// the model twice — only worth it against a known MTP-capable model such as gemma-4-E2B-it.
/// </summary>
public sealed class SpeculativeDecodingBenchmarkTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // Reliably generates a sustained answer so decode throughput is measurable over many tokens.
    private const string Prompt = "Write a detailed paragraph about the history of the printing press.";
    private const int MaxOutputTokens = 128;

    [SkippableFact]
    public void SpeculativeDecoding_SpeedsUpDecode_AndProducesText()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model)
                || Environment.GetEnvironmentVariable("LITERTLM_TEST_BENCH") != "1",
            "Set LITERTLM_TEST_BENCH=1 and LITERTLM_TEST_MODEL to an MTP-capable .litertlm file to run.");

        string backend = Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND") ?? "cpu";

        Result baseline = Measure(model!, backend, speculative: false);
        Result spec = Measure(model!, backend, speculative: true);

        double ratio = baseline.DecodeTps > 0 ? spec.DecodeTps / baseline.DecodeTps : 0;

        // Emit a Markdown row so the weekly CI log (and a copy/paste into docs/speculative-decoding.md)
        // captures the measured speedup per backend.
        string table =
            "| backend | spec off decode tok/s | spec on decode tok/s | speedup | TTFT off | TTFT on |\n"
            + "|---|---:|---:|---:|---:|---:|\n"
            + $"| {backend} | {baseline.DecodeTps:F1} | {spec.DecodeTps:F1} | {ratio:F2}x | {baseline.Ttft:F2}s | {spec.Ttft:F2}s |";
        _output.WriteLine(table);
        _output.WriteLine($"spec OFF output: {baseline.Text}");
        _output.WriteLine($"spec ON  output: {spec.Text}");

        // On GitHub Actions, also append the row to the job's step summary: the CI runs at
        // verbosity=normal, which swallows ITestOutputHelper output for passing tests, so the numbers
        // would otherwise be lost. This lands them on the run's Summary page instead.
        string? stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(stepSummary))
            File.AppendAllText(stepSummary, $"### Speculative decoding A/B — {backend}\n\n{table}\n\n");

        // The benchmark API must actually report decode throughput on both runs (proves the binding
        // and that benchmark instrumentation is wired through engine creation).
        Assert.True(baseline.DecodeTps > 0, "No decode throughput recorded with speculative decoding OFF.");
        Assert.True(spec.DecodeTps > 0, "No decode throughput recorded with speculative decoding ON.");

        // Coherence: speculative decoding must still produce real text (not empty, not a degenerate
        // single-character repetition). We do NOT assert equality — TopP sampling is stochastic.
        AssertCoherent(baseline.Text, "speculative OFF");
        AssertCoherent(spec.Text, "speculative ON");

        // Effectiveness is informational: a regression below 1x on an MTP model is suspicious but can
        // be hardware noise, so warn rather than fail.
        if (ratio < 1.0)
            _output.WriteLine(
                $"WARNING: speculative decoding did not speed up decode (ratio {ratio:F2}x). " +
                "Expected >=1x on an MTP-capable model — verify the model ships a drafter.");
    }

    private static Result Measure(string model, string backend, bool speculative)
    {
        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = model,
            Backend = backend,
            MaxNumTokens = 2048,
            EnableBenchmark = true,
            EnableSpeculativeDecoding = speculative,
            // On the WebGPU GPU backend the MTP drafter's shared weight-cache file fails to open on
            // Windows ("Access denied") unless the disk cache is off; disable it for GPU so the A/B
            // can run there too (an upstream issue — Google's own CLI needs --cache no, see
            // docs/speculative-decoding.md). Both legs use the same setting for a fair comparison.
            CacheDir = backend == "gpu" ? LiteRtEngineOptions.CacheDisabled : null,
        });
        using var conv = engine.CreateConversation(new LiteRtConversationOptions
        {
            // TopP is the only sampler the v0.13.1 native build implements (Greedy/TopK return
            // "not implemented yet"). A fixed seed keeps each run reproducible; speculative decoding
            // preserves the output distribution, so coherence — not byte-equality — is what we check.
            Sampler = new SamplerParams { Type = SamplerType.TopP, TopK = 40, TopP = 0.95f, Temperature = 1.0f, Seed = 42 },
            MaxOutputTokens = MaxOutputTokens,
        });

        LiteRtResponse response = conv.Send(Prompt);
        LiteRtBenchmarkInfo? bench = conv.GetBenchmarkInfo();
        return new Result(
            response.Text ?? string.Empty,
            bench?.LastDecodeTokensPerSecond ?? 0,
            bench?.TimeToFirstTokenSeconds ?? 0);
    } // engine disposed here → the next Measure may load its own

    private static void AssertCoherent(string text, string label)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), $"Empty response ({label}).");
        Assert.True(text.Trim().Length >= 16, $"Response too short to be coherent ({label}): {text}");
        Assert.True(text.Distinct().Count() > 3, $"Degenerate response ({label}): {text}");
    }

    private readonly record struct Result(string Text, double DecodeTps, double Ttft);
}
