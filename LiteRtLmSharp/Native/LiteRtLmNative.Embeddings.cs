using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

// Embedding engine (c/embedding_engine.h, native v0.18.0) and the generic input-data helpers it consumes
// (c/engine.h). The embedding engine is a separate native object from LiteRtLmEngine: it loads an
// embedding model (EmbeddingGemma 2) and is not subject to the one-live-engine rule.
internal static unsafe partial class LiteRtLmNative
{
    // --- Input data (engine.h) ------------------------------------------

    /// <summary>Creates an input item of the given type. For text, <paramref name="data"/> is UTF-8 and
    /// <paramref name="size"/> its byte length; for image/audio, the raw bytes. The data is copied, so the
    /// caller's buffer can be released right after the call. Caller frees with
    /// <see cref="litert_lm_input_data_delete"/>. Null on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_input_data_create(LiteRtLmInputDataType type, void* data, nuint size);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_input_data_delete(nint input_data);

    // --- Embedding engine settings ---------------------------------------

    /// <summary>Creates embedding-engine settings for a model file and backend ("cpu", "gpu", "npu"); the
    /// vision/audio backends may be null (text only). Null on failure.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_embedding_engine_settings_create(
        string model_path, string backend_str, string? vision_backend_str, string? audio_backend_str);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_engine_settings_delete(nint settings);

    /// <summary>CPU backend thread count.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_engine_settings_set_num_threads(nint settings, int num_threads);

    /// <summary>Cache directory, with the same special values as the LLM engine (<c>:nocache</c>,
    /// <c>:memory</c>). A directory that does not exist makes engine creation fail.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_engine_settings_set_cache_dir(nint settings, string cache_dir);

    /// <summary>Largest text-encoder signature (tokens) to load; non-positive unsets.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_engine_settings_set_max_input_length(nint settings, int max_input_length);

    /// <summary>Smallest text-encoder signature (tokens) to load; negative unsets.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_engine_settings_set_min_input_length(nint settings, int min_input_length);

    /// <summary>Activation precision (0=F32, 1=F16, 2=I16, 3=I8, the <c>LiteRtLmActivationDataType</c> values).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_engine_settings_set_activation_data_type(nint settings, int activation_data_type);

    // --- Embedding engine ------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_embedding_engine_create(nint settings);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_engine_delete(nint engine);

    /// <summary>Embeds one request made of <paramref name="num_inputs"/> input items (one text item for a
    /// text embedding). Null options = defaults. Returns a caller-owned response, null on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_embedding_engine_compute_embedding(
        nint engine, nint* inputs, nuint num_inputs, nint options);

    /// <summary>Embeds <paramref name="batch_size"/> requests: <paramref name="inputs_batch"/>[i] points at
    /// request i's <paramref name="num_inputs_per_batch"/>[i] input items. Returns a caller-owned response
    /// collection, null on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_embedding_engine_compute_embedding_batch(
        nint engine, nint** inputs_batch, nuint* num_inputs_per_batch, nuint batch_size, nint options);

    // --- Embedding options -----------------------------------------------

    /// <summary>Options with the native defaults: normalize = true, insert special tokens = true, default
    /// output size and overflow strategy.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_embedding_options_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_options_delete(nint options);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_options_set_normalize(nint options, [MarshalAs(UnmanagedType.U1)] bool normalize);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_options_set_insert_special_tokens(
        nint options, [MarshalAs(UnmanagedType.U1)] bool insert_special_tokens);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_options_set_input_overflow_strategy(nint options, int strategy);

    /// <summary>Truncates the output vector to this size (Matryoshka); 0 or negative restores the default.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_options_set_output_size(nint options, int output_size);

    // --- Embedding responses ---------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_response_delete(nint response);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint litert_lm_embedding_response_get_size(nint response);

    /// <summary>The vector's values, owned by the response (valid while it lives); null when empty.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial float* litert_lm_embedding_response_get_values(nint response);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_responses_delete(nint responses);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint litert_lm_embedding_responses_get_size(nint responses);

    /// <summary>The response at <paramref name="index"/>, owned by the collection (do not delete it); null
    /// when out of bounds.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_embedding_responses_get_at(nint responses, nuint index);
}

/// <summary>Mirrors <c>LiteRtLmInputDataType</c> in engine.h.</summary>
internal enum LiteRtLmInputDataType
{
    Text = 0,
    Image = 1,
    ImageEnd = 2,
    Audio = 3,
    AudioEnd = 4,
}
