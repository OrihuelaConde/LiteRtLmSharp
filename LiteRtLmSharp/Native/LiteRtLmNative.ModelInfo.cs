using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

// Model metadata read from a .litertlm file without loading an engine (c/model_info.h, native v0.18.0).
internal static unsafe partial class LiteRtLmNative
{
    /// <summary>Opens a model file for capability queries; null when the file cannot be opened. The path is
    /// a null-terminated narrow string that the native side passes to <c>std::ifstream</c>: build it with
    /// <see cref="NarrowPath.Encode"/>, not as plain UTF-8.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_loaded_file_create(byte* litertlm_path);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_loaded_file_delete(nint loaded_file);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtLmModelType litert_lm_loaded_file_model_type(nint loaded_file);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool litert_lm_loaded_file_has_speculative_decoding_support(nint loaded_file);

    /// <summary>False when the metadata does not declare it.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool litert_lm_loaded_file_supports_thinking(nint loaded_file);

    /// <summary>False when the metadata does not declare it.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool litert_lm_loaded_file_supports_function_calling(nint loaded_file);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtLmSamplerType litert_lm_loaded_file_sampler_type(nint loaded_file);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial float litert_lm_loaded_file_sampler_temperature(nint loaded_file);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_loaded_file_sampler_top_k(nint loaded_file);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial float litert_lm_loaded_file_sampler_top_p(nint loaded_file);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool litert_lm_loaded_file_supports_input_modality(nint loaded_file, LiteRtLmModality modality);

    /// <summary>-1 when the model has no vision or declares no budget.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_loaded_file_max_vision_token_budget(nint loaded_file);

    /// <summary>Fixed context size of a static model, or the largest settable size of a dynamic one; 0 when
    /// not found.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint litert_lm_loaded_file_max_context_tokens(nint loaded_file);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool litert_lm_loaded_file_is_dynamic_context(nint loaded_file);

    /// <summary>Writes up to <paramref name="max_size"/> vision token lengths and returns their count (null
    /// <paramref name="lengths"/> returns the count only); -1 when the model has no vision.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_loaded_file_vision_signature_selection(nint loaded_file, int* lengths, int max_size);

    /// <summary>Writes up to <paramref name="max_size"/> backends for a modality in priority order and returns
    /// their count (null <paramref name="backends"/> returns the count only); 0 when unsupported.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_loaded_file_modality_supported_backends(
        nint loaded_file, LiteRtLmModality modality, LiteRtLmBackendType* backends, int max_size);

    /// <summary>Minimum runtime version string owned by the loaded file; null when undefined.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_loaded_file_min_runtime_version(nint loaded_file);

    /// <summary>-1 when the model is not an embedding model or declares no dimension.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_loaded_file_embedding_dimension(nint loaded_file);

    /// <summary>Writes up to <paramref name="max_size"/> embedding input lengths and returns their count
    /// (null <paramref name="lengths"/> returns the count only); -1 when not an embedding model.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_loaded_file_embedding_signature_selection(nint loaded_file, int* lengths, int max_size);
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
