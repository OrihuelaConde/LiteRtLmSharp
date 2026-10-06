using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The native error report (LiteRT-LM v0.18.0 <c>c/error_reporter.h</c>): a failed native call surfaces the
/// runtime's own status and reason in <see cref="LiteRtException"/>. The engine-load case needs no model, so
/// it runs on every CI leg; the conversation cases load the test model.
/// </summary>
public sealed class NativeErrorTests(ITestOutputHelper output)
{
    /// <summary>
    /// A file that is not a model fails engine creation, and the exception carries the runtime's reason
    /// and status instead of a bare "returned null". The failed load also releases the one-engine slot.
    /// </summary>
    [Fact]
    public void Load_InvalidModelFile_ReportsNativeReason()
    {
        LiteRtEngine.SetMinLogLevel(3);
        string bogus = Path.Combine(Path.GetTempPath(), $"not-a-model-{Guid.NewGuid():N}.litertlm");
        File.WriteAllBytes(bogus, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 31))]);
        try
        {
            var ex = Assert.Throws<LiteRtException>(() => LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = bogus }));
            output.WriteLine($"[{ex.StatusCode}] {ex.Message}");

            Assert.NotNull(ex.StatusCode);
            Assert.NotEqual(LiteRtStatusCode.Ok, ex.StatusCode);
            Assert.StartsWith("litert_lm_engine", ex.Message, StringComparison.Ordinal);
            string code = NativeError.CanonicalName(ex.StatusCode!.Value);
            Assert.Contains(code, ex.Message, StringComparison.Ordinal);
            // The runtime's text already starts with the code; the binding prints it once.
            Assert.DoesNotContain($"{code}: {code}", ex.Message, StringComparison.Ordinal);
            // The guess-the-cause fallback only appears when the runtime reports nothing.
            Assert.DoesNotContain("reported no reason", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    /// <summary>A report is consumed when read: the next read on the same thread sees nothing, so a later
    /// failure can never be described with an earlier one's reason.</summary>
    [Fact]
    public void Take_ConsumesTheThreadReport()
    {
        LiteRtEngine.SetMinLogLevel(3);
        string bogus = Path.Combine(Path.GetTempPath(), $"not-a-model-{Guid.NewGuid():N}.litertlm");
        File.WriteAllBytes(bogus, new byte[4096]);
        try
        {
            Assert.Throws<LiteRtException>(() => LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = bogus }));
            Assert.Null(NativeError.Take());
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    /// <summary>Every frozen native status code has its canonical spelling, and an unrecognized value would
    /// read as UNKNOWN.</summary>
    [Fact]
    public void CanonicalName_CoversEveryStatusCode()
    {
        foreach (LiteRtStatusCode code in Enum.GetValues<LiteRtStatusCode>())
        {
            string name = NativeError.CanonicalName(code);
            Assert.False(string.IsNullOrEmpty(name));
            Assert.True(code == LiteRtStatusCode.Unknown || name != "UNKNOWN", $"{code} has no canonical name.");
        }
        Assert.Equal("INVALID_ARGUMENT", NativeError.CanonicalName(LiteRtStatusCode.InvalidArgument));
        Assert.Equal("UNKNOWN", NativeError.CanonicalName((LiteRtStatusCode)999));
        Assert.Equal(17, Enum.GetValues<LiteRtStatusCode>().Length);
    }

    /// <summary>A LoRA adapter path that does not exist fails conversation creation with the runtime's
    /// reason next to the binding's guidance. Skipped unless LITERTLM_TEST_MODEL is set.</summary>
    [SkippableFact]
    public void CreateConversation_MissingLoraFile_ReportsNativeReason()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = model!, MaxNumTokens = 2048 });
        string bogus = Path.Combine(Path.GetTempPath(), $"no-such-lora-{Guid.NewGuid():N}.litertlm");
        var ex = Assert.Throws<LiteRtException>(() => engine.CreateConversation(new LiteRtConversationOptions { LoraPath = bogus }));
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");

        Assert.Contains(bogus, ex.Message, StringComparison.Ordinal);
        Assert.Contains("LoRA-enabled", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Bytes that are not an image fail with the runtime's decoding reason, without the binding's
    /// generic multimodal setup guidance (the specific reason is the actionable one). Skipped unless
    /// LITERTLM_TEST_MODEL is set.</summary>
    [SkippableFact]
    public void Send_InvalidImageBytes_ReportsTheDecodingReasonOnly()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = model!, MaxNumTokens = 2048 });
        using var conv = engine.CreateConversation();
        var ex = Assert.Throws<LiteRtException>(
            () => conv.Send("Describe this image.", [LiteRtAttachment.Image(new byte[] { 1, 2, 3, 4 })]));
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");

        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("decode image", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(LiteRtConversation.MultimodalSendHint, ex.Message, StringComparison.Ordinal);
    }
}
