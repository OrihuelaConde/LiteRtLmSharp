using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

// Model metadata read from a .litertlm file without loading an engine (c/model_info.h, C API 1.1.0). Every
// getter returns a status and writes its value through a trailing out pointer, only on success. A value the
// model does not declare is NOT_FOUND for the getters whose header says so, and a success with a NULL out
// value for the string getter. A C bool out value is one byte: the declarations take byte*.
internal static unsafe partial class LiteRtLmNative
{
    /// <summary>Opens a model file for capability queries and parses its metadata. The path is a
    /// null-terminated byte string that the native side opens twice, once as UTF-8 and once through the C
    /// runtime's narrow <c>std::ifstream</c>: build it with <see cref="NarrowPath.Acquire"/>, not as plain
    /// UTF-8. Caller frees <paramref name="out_loaded_file"/> with <see cref="litert_lm_loaded_file_delete"/>;
    /// it is NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_create(byte* litertlm_path, nint* out_loaded_file);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_loaded_file_delete(nint loaded_file);

    /// <summary>Unknown (a success) when the file has neither LLM nor embedding metadata.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_model_type(nint loaded_file, LiteRtLmModelType* out_model_type);

    /// <summary>False (a success) for a model without a drafter, including non-LLM models.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_has_speculative_decoding_support(nint loaded_file, byte* out_supported);

    /// <summary>False (a success) when the metadata does not declare it.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_supports_thinking(nint loaded_file, byte* out_supported);

    /// <summary>False (a success) when the metadata does not declare it.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_supports_function_calling(nint loaded_file, byte* out_supported);

    /// <summary>Unspecified (a success) when an LLM declares no sampler; <c>NOT_FOUND</c> when the file is not
    /// an LLM.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_sampler_type(nint loaded_file, LiteRtLmSamplerType* out_sampler_type);

    /// <summary>0 (a success) when an LLM does not declare it; <c>NOT_FOUND</c> when the file is not an LLM.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_sampler_temperature(nint loaded_file, float* out_temperature);

    /// <summary>0 (a success) when an LLM does not declare it; <c>NOT_FOUND</c> when the file is not an LLM.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_sampler_top_k(nint loaded_file, int* out_top_k);

    /// <summary>0 (a success) when an LLM does not declare it; <c>NOT_FOUND</c> when the file is not an LLM.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_sampler_top_p(nint loaded_file, float* out_top_p);

    /// <summary>Fails with <c>INVALID_ARGUMENT</c> for a value <c>LiteRtLmModality</c> does not declare.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_supports_input_modality(
        nint loaded_file, LiteRtLmModality modality, byte* out_supported);

    /// <summary><c>NOT_FOUND</c> when the model has no vision or declares no budget; a declared budget can be
    /// 0.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_max_vision_token_budget(nint loaded_file, int* out_budget);

    /// <summary>The context size, always positive on success: for an LLM the default context size the
    /// metadata declares, else 4096 capped by a static graph's size; for an embedding model the fixed input
    /// length, or the largest one when it has several. <c>NOT_FOUND</c> when the model defines none.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_max_context_tokens(nint loaded_file, uint* out_max_context_tokens);

    /// <summary>Whether the context size is configurable, read from the model graph; false (a success) when
    /// the file has neither LLM nor embedding metadata.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_is_dynamic_context(nint loaded_file, byte* out_is_dynamic);

    /// <summary>Writes up to <paramref name="max_size"/> vision token lengths (null
    /// <paramref name="lengths"/> with a zero size queries the count only); <paramref name="out_count"/>
    /// receives the total, which can exceed <paramref name="max_size"/>. <c>NOT_FOUND</c> when the model has
    /// no vision lengths.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_vision_signature_selection(
        nint loaded_file, int* lengths, nuint max_size, nuint* out_count);

    /// <summary>Writes up to <paramref name="max_size"/> backends for a modality in priority order (null
    /// <paramref name="backends"/> with a zero size queries the count only); <paramref name="out_count"/>
    /// receives the total, 0 (a success) for an unsupported modality.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_modality_supported_backends(
        nint loaded_file, LiteRtLmModality modality, LiteRtLmBackendType* backends, nuint max_size, nuint* out_count);

    /// <summary>The minimum runtime version, a string lent by the loaded file (valid until it is deleted);
    /// NULL (a success) when the model declares none.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_min_runtime_version(nint loaded_file, nint* out_version);

    /// <summary>Always positive on success; <c>NOT_FOUND</c> when the model is not an embedding model or
    /// declares no dimension.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_embedding_dimension(nint loaded_file, int* out_dimension);

    /// <summary>Writes up to <paramref name="max_size"/> embedding input lengths, like
    /// <see cref="litert_lm_loaded_file_vision_signature_selection"/>. <c>NOT_FOUND</c> when the model is not
    /// an embedding model or declares no lengths.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_loaded_file_embedding_signature_selection(
        nint loaded_file, int* lengths, nuint max_size, nuint* out_count);
}

/// <summary>Mirrors <c>LiteRtLmModelType</c> in model_info.h.</summary>
internal enum LiteRtLmModelType
{
    Unknown = 0,
    Llm = 1,
    Embedding = 2,
}

/// <summary>Mirrors <c>LiteRtLmModality</c> in model_info.h.</summary>
internal enum LiteRtLmModality
{
    Text = 0,
    Vision = 1,
    Audio = 2,
    Video = 3,
}

/// <summary>Mirrors <c>LiteRtLmBackendType</c> in model_info.h.</summary>
internal enum LiteRtLmBackendType
{
    Cpu = 1,
    Gpu = 2,
    Npu = 3,
}
