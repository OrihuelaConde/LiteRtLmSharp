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
            // The failed load released the one-engine slot: a second attempt fails the same way, not with
            // "another engine is still alive".
            Assert.Throws<LiteRtException>(() => LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = bogus }));
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    /// <summary>The model metadata reader and the embedding engine turn down a file that is not a model
    /// with the runtime's reason, like the chat engine does.</summary>
    [Fact]
    public void ModelInfoAndEmbeddingEngine_InvalidFile_ReportNativeReason()
    {
        LiteRtEngine.SetMinLogLevel(3);
        string bogus = Path.Combine(Path.GetTempPath(), $"not-a-model-{Guid.NewGuid():N}.litertlm");
        File.WriteAllBytes(bogus, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 31))]);
        try
        {
            var info = Assert.Throws<LiteRtException>(() => LiteRtModelInfo.Read(bogus));
            output.WriteLine($"model info: [{info.StatusCode}] {info.Message}");
            Assert.StartsWith("litert_lm_loaded_file_create", info.Message, StringComparison.Ordinal);
            Assert.NotNull(info.StatusCode);

            var load = Assert.Throws<LiteRtException>(() => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions { ModelPath = bogus }));
            output.WriteLine($"embedding engine: [{load.StatusCode}] {load.Message}");
            Assert.StartsWith("litert_lm_embedding_engine", load.Message, StringComparison.Ordinal);
            Assert.NotNull(load.StatusCode);
            // The runtime's call trace reads as one line: the reason, then where it was raised.
            Assert.DoesNotContain("ERROR: [", load.Message, StringComparison.Ordinal);
            Assert.Contains("(at ", load.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    /// <summary>LiteRT's multi-line status traces (real messages from v0.18.0) read as one line: the reason,
    /// then the innermost source location; a trace without a reason keeps the location.</summary>
    [Fact]
    public void Untrace_PutsTheReasonFirst()
    {
        const string cacheDir =
            "ERROR: [third_party/odml/litert_lm/runtime/core/embedding_engine_impl.cc:402]\n" +
            "\u2514 ERROR: [third_party/odml/litert_lm/runtime/engine/embedding_engine_settings.cc:316]\n" +
            "\u2514 Cache directory does not exist or is not writable: C:\\missing";
        Assert.Equal(
            "Cache directory does not exist or is not writable: C:\\missing (at embedding_engine_settings.cc:316)",
            NativeError.Untrace(cacheDir));

        const string bareTrace =
            "ERROR: [third_party/odml/litert_lm/runtime/executor/llm_litert_compiled_model_executor_factory.cc:236]\n" +
            "\u2514 ERROR: [third_party/odml/litert/litert/runtime/compiled_model.cc:1036]";
        Assert.Equal("no details, failed at compiled_model.cc:1036", NativeError.Untrace(bareTrace));

        const string plain = "Unsupported backend: tpu. Supported backends are: [CPU, GPU, NPU]";
        Assert.Same(plain, NativeError.Untrace(plain));

        // CRLF line ends, a reason on the location line, and a reason that ends its own sentence.
        Assert.Equal("reason one (at b.cc:2)", NativeError.Untrace("ERROR: [a/a.cc:1]\r\n\u2514 ERROR: [b/b.cc:2]\r\n\u2514 reason one"));
        Assert.Equal("inline reason (at x.cc:7)", NativeError.Untrace("ERROR: [x.cc:7] inline reason"));
        Assert.Equal("length (402) exceeds (128) (at e.cc:1598)", NativeError.Untrace("ERROR: [e.cc:1598]\n\u2514 length (402) exceeds (128)."));
    }

    /// <summary>The detail drops the code the runtime repeats; a silenced native log leaves a status with no
    /// detail at all, which must read as "no reason" rather than as the code printed twice.</summary>
    [Fact]
    public void Detail_And_StatusText_AreNormalized()
    {
        Assert.Equal("Invalid magic number", NativeError.Detail(LiteRtStatusCode.InvalidArgument, "INVALID_ARGUMENT: Invalid magic number"));
        Assert.Equal("", NativeError.Detail(LiteRtStatusCode.InvalidArgument, "INVALID_ARGUMENT: "));
        Assert.Equal("", NativeError.Detail(LiteRtStatusCode.InvalidArgument, "INVALID_ARGUMENT"));
        Assert.Equal("raw text", NativeError.Detail(LiteRtStatusCode.Internal, "raw text"));

        Assert.Equal((LiteRtStatusCode.Cancelled, "Task cancelled"), NativeError.ParseStatusText("CANCELLED: Task cancelled"));
        Assert.Equal((LiteRtStatusCode.InvalidArgument, ""), NativeError.ParseStatusText("INVALID_ARGUMENT: "));
        Assert.Null(NativeError.ParseStatusText("something went wrong"));

        var silent = NativeError.FromStatusText("send failed.", "INVALID_ARGUMENT: ", _ => null);
        Assert.Equal(LiteRtStatusCode.InvalidArgument, silent.StatusCode);
        Assert.DoesNotContain("INVALID_ARGUMENT: INVALID_ARGUMENT", silent.Message, StringComparison.Ordinal);
        Assert.Contains("SetMinLogLevel", silent.Message, StringComparison.Ordinal);

        var unparsed = NativeError.FromStatusText("send failed.", "something went wrong", _ => null);
        Assert.Null(unparsed.StatusCode);
        Assert.Contains("something went wrong", unparsed.Message, StringComparison.Ordinal);
    }

    /// <summary>The send guidance fits the failure: the small-context hint only for its own failure (or no
    /// reason), the multimodal hint only with media and the missing-encoder failure (or no reason).</summary>
    [Fact]
    public void SendFailureHint_FitsTheFailure()
    {
        Assert.Null(LiteRtConversation.SendFailureHint("Task cancelled", mediaHintApplies: true, smallContext: true));
        Assert.Equal(LiteRtConversation.SmallContextSendHint,
            LiteRtConversation.SendFailureHint("Failed to invoke the compiled model Failed to allocate tensors", false, true));
        Assert.Equal(LiteRtConversation.MultimodalSendHint,
            LiteRtConversation.SendFailureHint("Vision executor should not be null, please TryLoadingVisionExecutor() first", true, true));
        Assert.Null(LiteRtConversation.SendFailureHint("Vision executor should not be null", mediaHintApplies: false, smallContext: false));
        Assert.Equal(LiteRtConversation.MultimodalSendHint + " " + LiteRtConversation.SmallContextSendHint,
            LiteRtConversation.SendFailureHint(null, mediaHintApplies: true, smallContext: true));
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
        // The native setter only opens the file, so the guidance is about the file, not the model.
        Assert.Contains("readable LoRA weights file", ex.Message, StringComparison.Ordinal);
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

    /// <summary>A failure the runtime delivers through a stream chunk (an image on an engine without a vision
    /// backend fails inside prefill) carries the same status, reason and guidance as the blocking path.</summary>
    [SkippableFact]
    public async Task Stream_FailureInTheChunk_CarriesTheStatusAndTheMatchingHint()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = model!, MaxNumTokens = 2048 });
        using var conv = engine.CreateConversation();
        byte[] png = Convert.FromBase64String(MultimodalModelTests.RedPngBase64);
        var ex = await Assert.ThrowsAsync<LiteRtException>(async () =>
        {
            await foreach (var _ in conv.SendStreamingAsync("What color is this image?", [LiteRtAttachment.Image(png)])) { }
        });
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");

        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.StartsWith("litert_lm_conversation_send_message_stream failed: INVALID_ARGUMENT: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(LiteRtConversation.MultimodalSendHint, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LiteRtConversation.SmallContextSendHint, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>With the native log silenced, LiteRT records the status of a macro-raised failure without
    /// its reason; the exception still carries the status and says why the reason is missing.</summary>
    [SkippableFact]
    public void SilencedLog_KeepsTheStatus_AndExplainsTheMissingReason()
    {
        string? embeddingModel = Environment.GetEnvironmentVariable("LITERTLM_TEST_EMBEDDING_MODEL");
        Skip.If(string.IsNullOrEmpty(embeddingModel) || !File.Exists(embeddingModel),
            "Set LITERTLM_TEST_EMBEDDING_MODEL to an embedding .litertlm to run.");

        LiteRtEngine.SetMinLogLevel(3);
        using var engine = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions { ModelPath = embeddingModel!, MaxInputLength = 128 });
        string tooLong = string.Join(' ', Enumerable.Repeat("word", 400));
        try
        {
            LiteRtEngine.SetMinLogLevel(1000);
            var silenced = Assert.Throws<LiteRtException>(() => engine.Embed(tooLong));
            output.WriteLine($"silenced: [{silenced.StatusCode}] {silenced.Message}");
            Assert.Equal(LiteRtStatusCode.InvalidArgument, silenced.StatusCode);
            Assert.DoesNotContain("INVALID_ARGUMENT: INVALID_ARGUMENT", silenced.Message, StringComparison.Ordinal);
            Assert.Contains("SetMinLogLevel", silenced.Message, StringComparison.Ordinal);
        }
        finally
        {
            LiteRtEngine.SetMinLogLevel(3);
        }
        var logged = Assert.Throws<LiteRtException>(() => engine.Embed(tooLong));
        output.WriteLine($"logged: [{logged.StatusCode}] {logged.Message}");
        Assert.Contains("exceeds maximum supported signature length", logged.Message, StringComparison.Ordinal);
    }
}
