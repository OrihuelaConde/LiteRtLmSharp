using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The C API 1.1 binary interface of the session functions (<c>c/engine.h</c>), called directly and without a
/// model: every session import is exported by the loaded library, every function rejects a null handle with
/// INVALID_ARGUMENT, pointer outputs are set to NULL, scalar outputs are left unwritten, and a decode that
/// cannot start never calls its callback.
/// </summary>
public sealed unsafe class SessionAbiTests(ITestOutputHelper output)
{
    static SessionAbiTests() => LiteRtEngine.SetMinLogLevel(3); // registers the resolver

    /// <summary>The session functions the binding imports, the blocking decode included.</summary>
    internal static readonly string[] SessionImports =
    [
        "litert_lm_engine_create_session",
        "litert_lm_session_delete",
        "litert_lm_session_cancel_process",
        "litert_lm_session_save_checkpoint",
        "litert_lm_session_rewind_to_checkpoint",
        "litert_lm_session_rewind_to_step",
        "litert_lm_session_run_prefill",
        "litert_lm_session_run_decode",
        "litert_lm_session_run_decode_async",
        "litert_lm_session_run_text_scoring",
        "litert_lm_session_config_set_apply_prompt_template",
        "litert_lm_responses_delete",
        "litert_lm_responses_get_num_candidates",
        "litert_lm_responses_get_response_text_at",
        "litert_lm_responses_get_score_at",
        "litert_lm_responses_get_token_length_at",
    ];

    private static string? LastMessage() => Marshal.PtrToStringUTF8(LiteRtLmNative.litert_lm_get_last_error_message());

    /// <summary>The library the binding's resolver loaded exports every session import.</summary>
    [Fact]
    public void SessionImports_AreExported()
    {
        NativeLibraryResolver.EnsureCompatible();
        nint library = NativeLibraryResolver.LoadedHandle;
        Assert.NotEqual(nint.Zero, library);
        string[] missing = SessionImports.Where(name => !NativeLibrary.TryGetExport(library, name, out _)).ToArray();
        Assert.Empty(missing);
    }

    /// <summary>Every session and responses function rejects a null handle with INVALID_ARGUMENT: pointer outputs
    /// are set to NULL, scalar outputs keep their value, and a decode that cannot start never calls its
    /// callback.</summary>
    [Fact]
    public void NullHandles_AreRejected_WithoutWritingScalarOutputs()
    {
        const nint Sentinel = 0x5A5A;
        nint ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_engine_create_session(nint.Zero, nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);
        output.WriteLine($"create without an engine: {LastMessage()}");
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_engine_create_session(nint.Zero, nint.Zero, null));

        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_cancel_process(nint.Zero));
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_rewind_to_step(nint.Zero, 0));
        byte* label = stackalloc byte[] { (byte)'a', 0 };
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_save_checkpoint(nint.Zero, label));
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_rewind_to_checkpoint(nint.Zero, label));
        output.WriteLine($"null session: {LastMessage()}");

        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_run_prefill(nint.Zero, null, 0));
        byte[] text = "hi"u8.ToArray();
        nint input;
        fixed (byte* p = text)
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_input_data_create(LiteRtLmInputDataType.Text, p, (nuint)text.Length, &input));
        try
        {
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_run_prefill(nint.Zero, &input, 1));
            output.WriteLine($"prefill without a session: {LastMessage()}");
        }
        finally
        {
            LiteRtLmNative.litert_lm_input_data_delete(input);
        }

        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_run_decode(nint.Zero, &ptr));
        Assert.Equal(nint.Zero, ptr);
        byte** targets = stackalloc byte*[1];
        targets[0] = label;
        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_run_text_scoring(nint.Zero, targets, 1, true, &ptr));
        Assert.Equal(nint.Zero, ptr);

        Callback.Reset();
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_run_decode_async(nint.Zero, &Callback.OnChunk, nint.Zero));
        Assert.Equal(0, Callback.Chunks);

        nuint count = 77;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_responses_get_num_candidates(nint.Zero, &count));
        Assert.Equal((nuint)77, count);
        ptr = Sentinel;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_responses_get_response_text_at(nint.Zero, 0, &ptr));
        Assert.Equal(nint.Zero, ptr);
        float score = 7.5f;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_responses_get_score_at(nint.Zero, 0, &score));
        Assert.Equal(7.5f, score);
        nuint length = 77;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_responses_get_token_length_at(nint.Zero, 0, &length));
        Assert.Equal((nuint)77, length);
        output.WriteLine($"null responses: {LastMessage()}");
        NativeError.Clear();
    }

    /// <summary>The prompt-template setter returns OK for both values on a real config and INVALID_ARGUMENT for a
    /// null config.</summary>
    [Fact]
    public void SessionConfig_ApplyPromptTemplate_ReturnsStatuses()
    {
        nint config;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_create(&config));
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_set_apply_prompt_template(config, false));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_set_apply_prompt_template(config, true));
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_config_set_apply_prompt_template(nint.Zero, false));
            output.WriteLine($"null config: {LastMessage()}");
        }
        finally
        {
            LiteRtLmNative.litert_lm_session_config_delete(config);
            NativeError.Clear();
        }
    }

    /// <summary>A stream callback that only counts. The tests run one at a time, so a static counter is
    /// enough.</summary>
    private static class Callback
    {
        private static int _chunks;
        public static int Chunks => Volatile.Read(ref _chunks);

        public static void Reset() => _chunks = 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void OnChunk(nint callbackData, nint chunk) => Interlocked.Increment(ref _chunks);
    }
}
