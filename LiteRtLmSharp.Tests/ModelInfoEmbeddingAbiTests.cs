using System.Runtime.InteropServices;
using System.Text;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Direct calls to the model-info and embedding C API (C API 1.1.0) for the outcomes the public API cannot
/// reach: a NULL out pointer, an undeclared modality, an index past the end, a value the model does not
/// declare (NOT_FOUND, with the out value left unwritten), and the shapes of the lent results. The model-free
/// cases run on every CI leg; the others need LITERTLM_TEST_EMBEDDING_MODEL and LITERTLM_TEST_MODEL.
/// </summary>
public sealed unsafe class ModelInfoEmbeddingAbiTests(ITestOutputHelper output)
{
    static ModelInfoEmbeddingAbiTests() => LiteRtEngine.SetMinLogLevel(3); // registers the resolver

    private static string? EmbeddingModel => Environment.GetEnvironmentVariable("LITERTLM_TEST_EMBEDDING_MODEL");
    private static string? ChatModel => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");

    private static string? LastMessage() => Marshal.PtrToStringUTF8(LiteRtLmNative.litert_lm_get_last_error_message());

    // Reads and clears the thread's message.
    private static string? TakeMessage()
    {
        string? message = LastMessage();
        NativeError.Clear();
        return message;
    }

    private static byte[] Utf8(string path) => Encoding.UTF8.GetBytes(path + "\0");

    private static nint Open(string path)
    {
        nint file;
        using NarrowPath.Lease lease = NarrowPath.Acquire(path);
        fixed (byte* p = lease.Bytes)
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_create(p, &file));
        Assert.NotEqual(nint.Zero, file);
        return file;
    }

    /// <summary>A file that does not exist, a file that is not a model and a NULL out pointer each fail with
    /// their status and reason, and leave the out pointer NULL.</summary>
    [Fact]
    public void LoadedFileCreate_Failures_ReturnTheStatusAndANullFile()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"no-such-model-{Guid.NewGuid():N}.litertlm");
        nint file = 1;
        LiteRtStatusCode status;
        fixed (byte* p = Utf8(missing))
            status = LiteRtLmNative.litert_lm_loaded_file_create(p, &file);
        Assert.NotEqual(LiteRtStatusCode.Ok, status);
        Assert.Equal(nint.Zero, file);
        string? missingReason = TakeMessage();
        output.WriteLine($"missing: {status} {missingReason}");
        // The text comes from LiteRT's Windows file implementation (measured on win-x64).
        if (OperatingSystem.IsWindows() && NarrowPath.IsAscii(missing))
        {
            Assert.Equal(LiteRtStatusCode.Unknown, status);
            Assert.Equal($"UNKNOWN: Failed to open: {missing}", missingReason);
        }

        string bogus = Path.Combine(Path.GetTempPath(), $"not-a-model-{Guid.NewGuid():N}.litertlm");
        File.WriteAllBytes(bogus, new byte[4096]);
        try
        {
            // Encoded like LiteRtModelInfo.Read, so a non-ASCII temp folder still reaches the parser.
            using NarrowPath.Lease path = NarrowPath.Acquire(bogus);
            file = 1;
            fixed (byte* p = path.Bytes)
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_loaded_file_create(p, &file));
            Assert.Equal(nint.Zero, file);
            string? reason = TakeMessage();
            output.WriteLine(reason);
            Assert.StartsWith("INVALID_ARGUMENT: Invalid magic number or failed to read", reason, StringComparison.Ordinal);

            fixed (byte* p = path.Bytes)
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_loaded_file_create(p, null));
            Assert.Equal("out_loaded_file must not be NULL.", TakeMessage());
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    /// <summary>The embedding builders: options and their setters return statuses, the runtime rejects an
    /// undeclared overflow strategy and activation type and a NULL cache directory, and an unknown backend
    /// fails settings creation with the runtime's list of backends.</summary>
    [Fact]
    public void EmbeddingBuilders_ReturnStatuses()
    {
        nint options;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_options_create(&options));
        Assert.NotEqual(nint.Zero, options);
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_options_set_normalize(options, false));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_options_set_insert_special_tokens(options, false));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_options_set_output_size(options, 128));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_options_set_output_size(options, -1));
            for (int strategy = 0; strategy <= 2; strategy++)
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_options_set_input_overflow_strategy(options, strategy));
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_embedding_options_set_input_overflow_strategy(options, 3));
            Assert.Equal("Unknown LiteRtLmInputOverflowStrategy.", TakeMessage());
        }
        finally
        {
            LiteRtLmNative.litert_lm_embedding_options_delete(options);
        }
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_embedding_options_create(null));
        Assert.Equal("out_options must not be NULL.", TakeMessage());

        // Settings creation only parses the backend; a bad one fails before the model is opened.
        nint settings = 1;
        Assert.Equal(LiteRtStatusCode.InvalidArgument,
            LiteRtLmNative.litert_lm_embedding_engine_settings_create("unused.litertlm", "tpu", null, null, &settings));
        Assert.Equal(nint.Zero, settings);
        string? reason = TakeMessage();
        output.WriteLine(reason);
        Assert.StartsWith("INVALID_ARGUMENT: Unsupported backend: tpu.", reason, StringComparison.Ordinal);
    }

    /// <summary>Every embedding settings setter returns a status; the runtime rejects an undeclared
    /// activation type and a NULL cache directory.</summary>
    [SkippableFact]
    public void EmbeddingSettings_EverySetterReturnsAStatus()
    {
        Skip.If(string.IsNullOrEmpty(EmbeddingModel) || !File.Exists(EmbeddingModel), "Set LITERTLM_TEST_EMBEDDING_MODEL to run.");
        nint settings;
        Assert.Equal(LiteRtStatusCode.Ok,
            LiteRtLmNative.litert_lm_embedding_engine_settings_create(EmbeddingModel!, "cpu", null, null, &settings));
        Assert.NotEqual(nint.Zero, settings);
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_engine_settings_set_num_threads(settings, 2));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_engine_settings_set_num_threads(settings, 0));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_engine_settings_set_max_input_length(settings, 256));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_engine_settings_set_min_input_length(settings, 128));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_engine_settings_set_activation_data_type(settings, 0));
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_embedding_engine_settings_set_activation_data_type(settings, 7));
            Assert.Equal("Unknown LiteRtLmActivationDataType.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_engine_settings_set_cache_dir(settings, ":nocache"));
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_embedding_engine_settings_set_cache_dir(settings, null!));
            Assert.Equal("cache_dir must not be NULL.", TakeMessage());
        }
        finally
        {
            LiteRtLmNative.litert_lm_embedding_engine_settings_delete(settings);
        }
    }

    /// <summary>On an embedding model the LLM getters return NOT_FOUND and leave the out value unwritten; the
    /// embedding getters return the values; a count query with a short buffer still reports the total; an
    /// undeclared modality and a NULL out pointer return INVALID_ARGUMENT.</summary>
    [SkippableFact]
    public void ModelInfoGetters_OnTheEmbeddingModel()
    {
        Skip.If(string.IsNullOrEmpty(EmbeddingModel) || !File.Exists(EmbeddingModel), "Set LITERTLM_TEST_EMBEDDING_MODEL to run.");
        nint file = Open(EmbeddingModel!);
        try
        {
            LiteRtLmSamplerType sampler = (LiteRtLmSamplerType)77;
            Assert.Equal(LiteRtStatusCode.NotFound, LiteRtLmNative.litert_lm_loaded_file_sampler_type(file, &sampler));
            Assert.Equal((LiteRtLmSamplerType)77, sampler);
            Assert.Equal("NOT_FOUND: The model is not an LLM and has no default sampler parameters.", TakeMessage());
            float temperature = -5;
            Assert.Equal(LiteRtStatusCode.NotFound, LiteRtLmNative.litert_lm_loaded_file_sampler_temperature(file, &temperature));
            Assert.Equal(-5, temperature);
            NativeError.Clear();

            nuint count = 99;
            Assert.Equal(LiteRtStatusCode.NotFound, LiteRtLmNative.litert_lm_loaded_file_vision_signature_selection(file, null, 0, &count));
            Assert.Equal((nuint)99, count);
            Assert.Equal("The model does not define vision signature selection lengths.", TakeMessage());

            int dimension;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_embedding_dimension(file, &dimension));
            Assert.Equal(768, dimension);
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_embedding_signature_selection(file, null, 0, &count));
            Assert.Equal((nuint)6, count);
            int first = 0;
            nuint total;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_embedding_signature_selection(file, &first, 1, &total));
            Assert.Equal(128, first);
            Assert.Equal((nuint)6, total);

            uint context;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_max_context_tokens(file, &context));
            Assert.Equal(8192u, context);
            byte flag = 0xCC;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_is_dynamic_context(file, &flag));
            Assert.Equal(1, flag);
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_supports_thinking(file, &flag));
            Assert.Equal(0, flag);
            LiteRtLmModelType type;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_model_type(file, &type));
            Assert.Equal(LiteRtLmModelType.Embedding, type);
            nint version = 1;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_min_runtime_version(file, &version));
            Assert.Equal(nint.Zero, version);

            flag = 0xCC;
            Assert.Equal(LiteRtStatusCode.InvalidArgument,
                LiteRtLmNative.litert_lm_loaded_file_supports_input_modality(file, (LiteRtLmModality)9, &flag));
            Assert.Equal(0xCC, flag);
            Assert.Equal("INVALID_ARGUMENT: Unknown LiteRtLmModality: 9.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument,
                LiteRtLmNative.litert_lm_loaded_file_modality_supported_backends(file, (LiteRtLmModality)9, null, 0, &count));
            NativeError.Clear();
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_loaded_file_model_type(file, null));
            Assert.Equal("out_model_type must not be NULL.", TakeMessage());
        }
        finally
        {
            LiteRtLmNative.litert_lm_loaded_file_delete(file);
        }
    }

    /// <summary>On a language model the embedding getters return NOT_FOUND. The vision getters return either
    /// NOT_FOUND, which the header allows for a model without vision or without a declared budget, or a budget
    /// that is one of the returned lengths; the test checks that rule, not the values (it prints them).</summary>
    [SkippableFact]
    public void ModelInfoGetters_OnTheChatModel()
    {
        Skip.If(string.IsNullOrEmpty(ChatModel) || !File.Exists(ChatModel), "Set LITERTLM_TEST_MODEL to run.");
        nint file = Open(ChatModel!);
        try
        {
            int dimension = -3;
            Assert.Equal(LiteRtStatusCode.NotFound, LiteRtLmNative.litert_lm_loaded_file_embedding_dimension(file, &dimension));
            Assert.Equal(-3, dimension);
            Assert.Equal("The model does not define an embedding dimension.", TakeMessage());
            nuint count = 99;
            Assert.Equal(LiteRtStatusCode.NotFound, LiteRtLmNative.litert_lm_loaded_file_embedding_signature_selection(file, null, 0, &count));
            Assert.Equal((nuint)99, count);
            Assert.Equal("The model does not define embedding signature lengths.", TakeMessage());

            LiteRtLmSamplerType sampler;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_sampler_type(file, &sampler));
            output.WriteLine($"sampler {sampler}");
            uint context;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_max_context_tokens(file, &context));
            output.WriteLine($"context {context}");
            Assert.True(context > 0);

            int budget;
            LiteRtStatusCode status = LiteRtLmNative.litert_lm_loaded_file_max_vision_token_budget(file, &budget);
            output.WriteLine($"vision budget: {status}");
            if (status == LiteRtStatusCode.Ok)
            {
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_vision_signature_selection(file, null, 0, &count));
                int[] lengths = new int[(int)count];
                nuint again;
                fixed (int* p = lengths)
                    Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_loaded_file_vision_signature_selection(file, p, count, &again));
                Assert.Equal(count, again);
                output.WriteLine($"budget {budget}, lengths [{string.Join(",", lengths)}]");
                Assert.Contains(budget, lengths);
            }
            else
            {
                Assert.Equal(LiteRtStatusCode.NotFound, status);
                NativeError.Clear();
            }
        }
        finally
        {
            LiteRtLmNative.litert_lm_loaded_file_delete(file);
        }
    }

    /// <summary>The embedding results: a single response and a batch collection report their sizes, the
    /// values are lent by the response, a collection element is lent by the collection, and an index past
    /// the end returns OUT_OF_RANGE with a NULL response.</summary>
    [SkippableFact]
    public void EmbeddingResponses_LentResultsAndBounds()
    {
        Skip.If(string.IsNullOrEmpty(EmbeddingModel) || !File.Exists(EmbeddingModel), "Set LITERTLM_TEST_EMBEDDING_MODEL to run.");
        nint settings, engine;
        Assert.Equal(LiteRtStatusCode.Ok,
            LiteRtLmNative.litert_lm_embedding_engine_settings_create(EmbeddingModel!, "cpu", null, null, &settings));
        try
        {
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_engine_settings_set_activation_data_type(settings, 0));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_engine_create(settings, &engine));
        }
        finally
        {
            LiteRtLmNative.litert_lm_embedding_engine_settings_delete(settings);
        }
        nint input = nint.Zero;
        try
        {
            byte[] text = "title: none | text: a lighthouse"u8.ToArray();
            fixed (byte* p = text)
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_input_data_create(LiteRtLmInputDataType.Text, p, (nuint)text.Length, &input));

            nint response;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_engine_compute_embedding(engine, &input, 1, nint.Zero, &response));
            try
            {
                nuint size;
                float* values;
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_response_get_size(response, &size));
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_response_get_values(response, &values));
                Assert.Equal((nuint)768, size);
                Assert.True(values is not null);
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_embedding_response_get_values(response, null));
                Assert.Equal("out_values must not be NULL.", TakeMessage());
            }
            finally
            {
                LiteRtLmNative.litert_lm_embedding_response_delete(response);
            }

            nint* request = &input;
            nuint one = 1;
            nint responses;
            Assert.Equal(LiteRtStatusCode.Ok,
                LiteRtLmNative.litert_lm_embedding_engine_compute_embedding_batch(engine, &request, &one, 1, nint.Zero, &responses));
            try
            {
                nuint count;
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_responses_get_size(responses, &count));
                Assert.Equal((nuint)1, count);
                nint element;
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_responses_get_at(responses, 0, &element));
                nuint size;
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_embedding_response_get_size(element, &size));
                Assert.Equal((nuint)768, size);
                element = 1;
                Assert.Equal(LiteRtStatusCode.OutOfRange, LiteRtLmNative.litert_lm_embedding_responses_get_at(responses, 1, &element));
                Assert.Equal(nint.Zero, element);
                string? reason = TakeMessage();
                output.WriteLine(reason);
                Assert.NotNull(reason);
            }
            finally
            {
                // The element was lent by the collection: deleting the collection frees it.
                LiteRtLmNative.litert_lm_embedding_responses_delete(responses);
            }

            // A NULL input array with a non-zero count is rejected.
            nint ignored = 1;
            Assert.Equal(LiteRtStatusCode.InvalidArgument,
                LiteRtLmNative.litert_lm_embedding_engine_compute_embedding(engine, null, 1, nint.Zero, &ignored));
            Assert.Equal(nint.Zero, ignored);
            output.WriteLine(TakeMessage());
        }
        finally
        {
            LiteRtLmNative.litert_lm_input_data_delete(input);
            LiteRtLmNative.litert_lm_embedding_engine_delete(engine);
        }
    }
}
