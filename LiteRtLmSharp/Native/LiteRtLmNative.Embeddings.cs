using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

// Embedding engine (c/embedding_engine.h, C API 1.1.0) and the generic input-data helpers it consumes
// (c/engine.h). The embedding engine is a separate native object from LiteRtLmEngine: it loads an
// embedding model (EmbeddingGemma 2) and is not subject to the one-live-engine rule.
internal static unsafe partial class LiteRtLmNative
{
    // --- Input data (engine.h) ------------------------------------------

    /// <summary>Creates an input item of the given type. For text, <paramref name="data"/> is UTF-8 and
    /// <paramref name="size"/> its byte length; for image/audio, the raw bytes. The data is copied, so the
    /// caller's buffer can be released right after the call. Null data is accepted only with a zero size,
    /// and an undeclared type fails with <c>INVALID_ARGUMENT</c>. Caller frees
    /// <paramref name="out_input_data"/> with <see cref="litert_lm_input_data_delete"/>; it is NULL on
    /// failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_input_data_create(
        LiteRtLmInputDataType type, void* data, nuint size, nint* out_input_data);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_input_data_delete(nint input_data);

    // --- Embedding engine settings ---------------------------------------

    /// <summary>Creates embedding-engine settings for a model file (opened as UTF-8) and backend ("cpu",
    /// "gpu", "npu"). Null vision/audio backends mean "the main backend for any encoder the bundle carries":
    /// a multimodal bundle still loads its vision and audio encoders. Caller frees
    /// <paramref name="out_settings"/> with <see cref="litert_lm_embedding_engine_settings_delete"/>; it is
    /// NULL on failure.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_engine_settings_create(
        string model_path, string backend_str, string? vision_backend_str, string? audio_backend_str, nint* out_settings);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_engine_settings_delete(nint settings);

    /// <summary>CPU backend thread count; a non-positive value is a successful no-op.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_engine_settings_set_num_threads(nint settings, int num_threads);

    /// <summary>Cache directory, with the same special values as the LLM engine (<c>:nocache</c>,
    /// <c>:memory</c>). The setter does not check the directory: one that does not exist, or that the
    /// runtime cannot spell (on Windows it checks the path through the C runtime's narrow API), makes engine
    /// creation fail.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_engine_settings_set_cache_dir(nint settings, string cache_dir);

    /// <summary>Largest text-encoder signature (tokens) to load; non-positive unsets.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_engine_settings_set_max_input_length(nint settings, int max_input_length);

    /// <summary>Smallest text-encoder signature (tokens) to load; negative unsets.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_engine_settings_set_min_input_length(nint settings, int min_input_length);

    /// <summary>Activation precision (0=F32, 1=F16, 2=I16, 3=I8, the <c>LiteRtLmActivationDataType</c>
    /// values). Fails with <c>INVALID_ARGUMENT</c> for a value the enum does not declare.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_engine_settings_set_activation_data_type(
        nint settings, int activation_data_type);

    // --- Embedding engine ------------------------------------------------

    /// <summary>Creates the engine. Caller frees <paramref name="out_engine"/> with
    /// <see cref="litert_lm_embedding_engine_delete"/>; it is NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_engine_create(nint settings, nint* out_engine);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_engine_delete(nint engine);

    /// <summary>Embeds one request made of <paramref name="num_inputs"/> input items (one text item for a
    /// text embedding). Null options = defaults. Caller frees <paramref name="out_response"/> with
    /// <see cref="litert_lm_embedding_response_delete"/>; it is NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_engine_compute_embedding(
        nint engine, nint* inputs, nuint num_inputs, nint options, nint* out_response);

    /// <summary>Embeds <paramref name="batch_size"/> requests: <paramref name="inputs_batch"/>[i] points at
    /// request i's <paramref name="num_inputs_per_batch"/>[i] input items. One failing request fails the
    /// call. Caller frees <paramref name="out_responses"/> with
    /// <see cref="litert_lm_embedding_responses_delete"/>; it is NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_engine_compute_embedding_batch(
        nint engine, nint** inputs_batch, nuint* num_inputs_per_batch, nuint batch_size, nint options, nint* out_responses);

    // --- Embedding options -----------------------------------------------

    /// <summary>Options with the native defaults: normalize = true, insert special tokens = true, default
    /// output size and overflow strategy. Caller frees <paramref name="out_options"/> with
    /// <see cref="litert_lm_embedding_options_delete"/>; it is NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_options_create(nint* out_options);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_options_delete(nint options);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_options_set_normalize(
        nint options, [MarshalAs(UnmanagedType.U1)] bool normalize);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_options_set_insert_special_tokens(
        nint options, [MarshalAs(UnmanagedType.U1)] bool insert_special_tokens);

    /// <summary>Fails with <c>INVALID_ARGUMENT</c> for a value <c>LiteRtLmInputOverflowStrategy</c> does not
    /// declare (it declares 0 to 2).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_options_set_input_overflow_strategy(nint options, int strategy);

    /// <summary>Truncates the output vector to this size (Matryoshka); 0 or negative restores the default.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_options_set_output_size(nint options, int output_size);

    // --- Embedding responses ---------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_response_delete(nint response);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_response_get_size(nint response, nuint* out_size);

    /// <summary>The vector's values, lent by the response, or by the collection that owns it, until that
    /// object is deleted; NULL (a success) for an empty vector.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_response_get_values(nint response, float** out_values);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_embedding_responses_delete(nint responses);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_responses_get_size(nint responses, nuint* out_size);

    /// <summary>The response at <paramref name="index"/>, lent by the collection (do not delete it);
    /// <c>OUT_OF_RANGE</c> for an index past the end.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_embedding_responses_get_at(nint responses, nuint index, nint* out_response);
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
