using System.Runtime.InteropServices;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The C API version rule the native library resolver enforces (<see cref="NativeApiVersion"/>). Pure: no
/// native library is loaded.
/// </summary>
public class NativeApiVersionRuleTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 9, false)]
    [InlineData(2, 0, false)]
    [InlineData(2, 5, false)]
    public void IsCompatible_ShouldRequireTheSameMajorAndAtLeastTheBuiltMinor(int major, int minor, bool expected)
        => Assert.Equal(expected, NativeApiVersion.IsCompatible(major, minor));

    [Fact]
    public void Problem_ShouldBeNullForACompatibleLibrary()
    {
        Assert.Null(NativeApiVersion.Problem(exported: true, LiteRtStatusCode.Ok, 1, 1, 0, "lib"));
        Assert.Null(NativeApiVersion.Problem(exported: true, LiteRtStatusCode.Ok, 1, 4, 2, "lib"));
    }

    [Fact]
    public void Problem_ShouldNameTheNeededAndTheFoundVersionAndTheFix()
    {
        string? message = NativeApiVersion.Problem(exported: true, LiteRtStatusCode.Ok, 1, 0, 3, "C:/app/LiteRtLm.dll");

        Assert.NotNull(message);
        Assert.Contains("C API 1.1 or a later 1.x version", message, StringComparison.Ordinal);
        Assert.Contains("implements C API 1.0.3", message, StringComparison.Ordinal);
        Assert.Contains("'C:/app/LiteRtLm.dll'", message, StringComparison.Ordinal);
        Assert.Contains("must have the same version", message, StringComparison.Ordinal);
        Assert.Contains($"LiteRtLmSharp.runtime.{RuntimeInformation.RuntimeIdentifier}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Problem_ShouldExplainALibraryWithoutTheVersionFunction()
    {
        string? message = NativeApiVersion.Problem(exported: false, LiteRtStatusCode.Ok, 0, 0, 0, "lib");

        Assert.NotNull(message);
        Assert.Contains("does not export litert_lm_get_api_version", message, StringComparison.Ordinal);
        Assert.Contains("older than 1.0.0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Problem_ShouldReportAFailedVersionQuery_WithoutReadingTheVersion()
    {
        // The out values are not written on failure, so whatever they hold must not be judged compatible.
        string? message = NativeApiVersion.Problem(exported: true, LiteRtStatusCode.InvalidArgument, 1, 1, 0, "lib");

        Assert.NotNull(message);
        Assert.Contains("returned INVALID_ARGUMENT", message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeLogLevel_ShouldSendUndeclaredLevelsAsSilent()
    {
        foreach (int declared in new[] { 0, 1, 2, 3, 4, 5, 1000 })
            Assert.Equal(declared, LiteRtEngine.NativeLogLevel(declared));
        foreach (int undeclared in new[] { -1, 6, 7, 999, 1001, int.MinValue, int.MaxValue })
            Assert.Equal(LiteRtEngine.SilentLogLevel, LiteRtEngine.NativeLogLevel(undeclared));
    }
}

/// <summary>
/// The C API 1.1 binary interface of the engine-core functions, called directly: status returns, out
/// pointers written on success, and the thread-local message on failure. The native library is loaded,
/// but no model, except in the engine-settings test, which needs a model path and is skipped without
/// LITERTLM_TEST_MODEL.
/// </summary>
public sealed unsafe class EngineCoreAbiTests(ITestOutputHelper output)
{
    static EngineCoreAbiTests() => LiteRtEngine.SetMinLogLevel(3); // registers the resolver

    private static string? LastMessage() => Marshal.PtrToStringUTF8(LiteRtLmNative.litert_lm_get_last_error_message());

    [Fact]
    public void ApiVersion_OfTheLoadedLibrary_IsCompatible()
    {
        int major, minor, patch;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_get_api_version(&major, &minor, &patch));
        output.WriteLine($"C API {major}.{minor}.{patch}");
        Assert.True(NativeApiVersion.IsCompatible(major, minor), $"C API {major}.{minor}.{patch}");
    }

    [Fact]
    public void SetMinLogLevel_UndeclaredLevels_SilenceLikeTheEarlierRuntime()
    {
        try
        {
            foreach (int level in new[] { 6, -1, 999, 1001 })
                LiteRtEngine.SetMinLogLevel(level);
        }
        finally
        {
            LiteRtEngine.SetMinLogLevel(3);
        }
    }

    /// <summary>A rejected value returns the status and leaves a bare reason (no code prefix) on the thread;
    /// the exception carries both, and reading the message clears it.</summary>
    [Fact]
    public void RejectedLogLevel_ReportsTheStatusAndTheReason()
    {
        LiteRtStatusCode status = LiteRtLmNative.litert_lm_set_min_log_level(7);
        Assert.Equal(LiteRtStatusCode.InvalidArgument, status);

        LiteRtException ex = NativeError.Exception(status, "litert_lm_set_min_log_level failed.");
        output.WriteLine(ex.Message);
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal("litert_lm_set_min_log_level failed: INVALID_ARGUMENT: Unknown LiteRtLmLogSeverity.", ex.Message);
        Assert.Null(LastMessage());
    }

    [Fact]
    public void ThrowIfFailed_ThrowsOnlyForAFailure()
    {
        NativeError.ThrowIfFailed(LiteRtStatusCode.Ok, "unused");

        var ex = Assert.Throws<LiteRtException>(
            () => NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_set_min_log_level(-5), "litert_lm_set_min_log_level", hint: "A hint."));
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.StartsWith("litert_lm_set_min_log_level failed: INVALID_ARGUMENT: ", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(" A hint.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Take_ReadsAnUnrecognizedStatusAsUnknown()
    {
        // No message on the thread: the status alone still makes a report, with an empty reason.
        NativeError.Clear();
        var report = NativeError.Take((LiteRtStatusCode)99);
        Assert.Equal(LiteRtStatusCode.Unknown, report.Code);
        Assert.Equal("", report.Message);
    }

    /// <summary>The sampler, session and decoding builders: every create writes a handle, every setter returns
    /// OK for a valid value, and the values the runtime validates are rejected with INVALID_ARGUMENT.</summary>
    [Fact]
    public void ConfigBuilders_ReturnStatusesAndHandles()
    {
        nint sampler;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_sampler_params_create(LiteRtLmSamplerType.TopP, &sampler));
        Assert.NotEqual(nint.Zero, sampler);
        nint session;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_create(&session));
        Assert.NotEqual(nint.Zero, session);
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_sampler_params_set_top_k(sampler, 1));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_sampler_params_set_top_p(sampler, 0.9f));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_sampler_params_set_temperature(sampler, 0.7f));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_sampler_params_set_seed(sampler, 5));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_set_sampler_params(session, sampler));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_set_max_output_tokens(session, 16));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_set_enable_speculative_decoding(session, false));
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_config_set_sampler_params(session, nint.Zero));
            output.WriteLine($"null sampler params: {LastMessage()}");
        }
        finally
        {
            LiteRtLmNative.litert_lm_sampler_params_delete(sampler);
            LiteRtLmNative.litert_lm_session_config_delete(session);
        }

        nint rejected = 1;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_sampler_params_create((LiteRtLmSamplerType)7, &rejected));
        Assert.Equal(nint.Zero, rejected); // pointer outputs are set to NULL on failure
        output.WriteLine($"undeclared sampler type: {LastMessage()}");

        nint repetition, ngram, suppress, thinking;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_repetition_penalty_config_create(&repetition));
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_no_repeat_ngram_config_create(&ngram));
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_suppress_tokens_config_create(&suppress));
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_thinking_config_create(&thinking));
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_repetition_penalty_config_set_repetition_penalty(repetition, 1.1f));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_repetition_penalty_config_set_presence_penalty(repetition, 0.1f));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_repetition_penalty_config_set_frequency_penalty(repetition, 0.2f));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_repetition_penalty_config_set_window_size(repetition, 64));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_no_repeat_ngram_config_set_no_repeat_ngram_size(ngram, 3));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_no_repeat_ngram_config_set_window_size(ngram, 64));
            int[] ids = [1, 2, 3];
            fixed (int* p = ids)
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_suppress_tokens_config_set_suppress_tokens(suppress, p, (nuint)ids.Length));
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_suppress_tokens_config_set_suppress_tokens(suppress, null, 2));
            output.WriteLine($"null suppress ids: {LastMessage()}");
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_thinking_config_set_enable_thinking(thinking, true));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_thinking_config_set_thinking_token_budget(thinking, 128));
        }
        finally
        {
            LiteRtLmNative.litert_lm_repetition_penalty_config_delete(repetition);
            LiteRtLmNative.litert_lm_no_repeat_ngram_config_delete(ngram);
            LiteRtLmNative.litert_lm_suppress_tokens_config_delete(suppress);
            LiteRtLmNative.litert_lm_thinking_config_delete(thinking);
        }
        NativeError.Clear();
    }

    /// <summary>The session config opens a LoRA file when it is set, so a missing file fails there with the
    /// status of the open and a reason that names the file.</summary>
    [Fact]
    public void SessionConfig_MissingLoraFile_FailsWithTheRuntimeReason()
    {
        string bogus = Path.Combine(Path.GetTempPath(), $"no-such-lora-{Guid.NewGuid():N}.litertlm");
        nint session;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_session_config_create(&session));
        try
        {
            LiteRtStatusCode status = LiteRtLmNative.litert_lm_session_config_set_lora_path(session, bogus);
            Assert.NotEqual(LiteRtStatusCode.Ok, status);
            LiteRtException ex = NativeError.Exception(status, $"litert_lm_session_config_set_lora_path failed for '{bogus}'.");
            output.WriteLine($"[{ex.StatusCode}] {ex.Message}");
            Assert.Equal(status, ex.StatusCode);
            Assert.DoesNotContain("gave no reason", ex.Message, StringComparison.Ordinal);

            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_session_config_set_audio_lora_path(session, ""));
            output.WriteLine($"empty audio LoRA path: {LastMessage()}");
        }
        finally
        {
            LiteRtLmNative.litert_lm_session_config_delete(session);
            NativeError.Clear();
        }
    }

    [Fact]
    public void InputData_CopiesBytes_AndRejectsNullDataWithASize()
    {
        byte[] text = "hello"u8.ToArray();
        nint input;
        fixed (byte* p = text)
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_input_data_create(LiteRtLmInputDataType.Text, p, (nuint)text.Length, &input));
        Assert.NotEqual(nint.Zero, input);
        LiteRtLmNative.litert_lm_input_data_delete(input);

        nint empty;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_input_data_create(LiteRtLmInputDataType.Text, null, 0, &empty));
        LiteRtLmNative.litert_lm_input_data_delete(empty);

        nint rejected = 1;
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_input_data_create(LiteRtLmInputDataType.Text, null, 4, &rejected));
        Assert.Equal(nint.Zero, rejected);
        output.WriteLine($"null data with a size: {LastMessage()}");
        NativeError.Clear();
    }

    /// <summary>Every engine-settings setter the binding calls returns OK on CPU, and the activation type is
    /// validated. Creating the settings reads the model file but builds no engine. Skipped unless
    /// LITERTLM_TEST_MODEL is set.</summary>
    [SkippableFact]
    public void EngineSettings_EverySetterReturnsAStatus()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        nint s;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_create(model!, "cpu", null, null, &s));
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_max_num_tokens(s, 2048));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_num_threads(s, 4));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_audio_num_threads(s, 2));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_lora_rank(s, 0));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_audio_lora_rank(s, 0));
            int[] ranks = [4, 8];
            fixed (int* p = ranks)
            {
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_supported_lora_ranks(s, p, (nuint)ranks.Length));
                // No audio executor is configured.
                LiteRtStatusCode audio = LiteRtLmNative.litert_lm_engine_settings_set_supported_audio_lora_ranks(s, p, (nuint)ranks.Length);
                output.WriteLine($"audio LoRA ranks without an audio executor: {audio}: {LastMessage()}");
                Assert.Equal(LiteRtStatusCode.FailedPrecondition, audio);
            }
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_max_num_images(s, 1));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_max_vision_tokens_per_image(s, 256));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_gpu_enable_metal_residency_set(s, false));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_enable_speculative_decoding(s, false));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_enable_benchmark(s));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_cache_dir(s, ":nocache"));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_parallel_file_section_loading(s, true));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_enable_ynnpack(s, false));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_activation_data_type(s, 0));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_prefill_chunk_size(s, 128));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_num_prefill_tokens(s, 32));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_num_decode_tokens(s, 32));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_gpu_decode_steps_per_sync(s, 1));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_gpu_wait_for_weight_uploads(s, true));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_engine_settings_set_use_ringbuffers_local_attention(s, false));

            LiteRtStatusCode activation = LiteRtLmNative.litert_lm_engine_settings_set_activation_data_type(s, 7);
            output.WriteLine($"undeclared activation type: {activation}: {LastMessage()}");
            Assert.Equal(LiteRtStatusCode.InvalidArgument, activation);
        }
        finally
        {
            LiteRtLmNative.litert_lm_engine_settings_delete(s);
            NativeError.Clear();
        }

        nint missing = 1;
        LiteRtStatusCode status = LiteRtLmNative.litert_lm_engine_settings_create(model!, "tpu", null, null, &missing);
        output.WriteLine($"unknown backend: {status}: {LastMessage()}");
        Assert.Equal(LiteRtStatusCode.InvalidArgument, status);
        Assert.Equal(nint.Zero, missing);
        NativeError.Clear();
    }

    /// <summary>The managed load maps an undeclared activation type to the runtime's rejection instead of
    /// passing it silently. Skipped unless LITERTLM_TEST_MODEL is set.</summary>
    [SkippableFact]
    public void Load_UndeclaredActivationType_FailsWithTheRuntimeReason()
    {
        string? model = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), "Set LITERTLM_TEST_MODEL to a .litertlm file to run.");

        var ex = Assert.Throws<LiteRtException>(() => LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = model!,
            ActivationDataType = (LiteRtActivationDataType)7,
        }));
        output.WriteLine($"[{ex.StatusCode}] {ex.Message}");
        Assert.Equal(LiteRtStatusCode.InvalidArgument, ex.StatusCode);
        Assert.StartsWith("litert_lm_engine_settings_set_activation_data_type failed: INVALID_ARGUMENT: ", ex.Message, StringComparison.Ordinal);
    }
}
