using LiteLMSharp;
using Xunit;

namespace LiteLMSharp.Tests;

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

    /// <summary>
    /// Full inference smoke test. Skipped unless LITERTLM_TEST_MODEL points to a model file.
    /// </summary>
    [SkippableFact]
    public async Task EndToEnd_Generation_ProducesText()
    {
        string? modelPath = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath),
            "Set LITERTLM_TEST_MODEL to a .litertlm file to run the end-to-end test.");

        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = modelPath!,
            Backend = "cpu",
            MaxNumTokens = 1024,
        });
        // NOTE: conversation-config (system message / sampler) is skipped here because the
        // current prebuilt binary (flutter_gemma native-v0.12.0-a) access-violates in
        // litert_lm_conversation_config_create — an ABI skew vs the `main` header. Fase 2
        // (own build pinned to a matching tag) will re-enable LiteRtConversationOptions.
        using var conversation = engine.CreateConversation();

        // Streaming path (the verified-robust path on this prebuilt binary).
        var sb = new System.Text.StringBuilder();
        await foreach (string chunk in conversation.SendMessageStreamingAsync("Count from 1 to 3."))
            sb.Append(chunk);
        Assert.True(sb.Length > 0, "Expected non-empty streamed response.");
    }
}
