using LiteRtLmSharp;
using Xunit;

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
