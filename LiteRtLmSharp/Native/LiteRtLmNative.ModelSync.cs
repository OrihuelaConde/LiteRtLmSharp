using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

// Differential download and update of .litertlm files (c/model_sync.h, C API 1.1.0). Options, plans and results
// are caller-owned, independent handles. Paths and messages are NUL-terminated byte strings the binding encodes
// itself: paths with NarrowPath.EncodeForModelSync, messages as UTF-8. A C bool input is one byte
// (UnmanagedType.U1) and a bool* output is byte*. The remote fetcher callback is
//   LiteRtLmStatusCode (*)(uint64_t offset, uint64_t length, LiteRtLmModelSyncRangeWriter* writer, void* user_data)
// declared as delegate* unmanaged[Cdecl]<ulong, ulong, nint, nint, LiteRtStatusCode>. It runs synchronously on the
// thread that called plan_create_from_file or update, and never after that call returns (measured on LiteRT-LM
// v0.20.0, win-x64). The writer is valid only during the callback.
internal static unsafe partial class LiteRtLmNative
{
    /// <summary>Streams fetched bytes into the writer from inside the fetcher callback. <paramref name="data"/>
    /// may be NULL only when <paramref name="size"/> is 0. INVALID_ARGUMENT for a NULL writer, or NULL data with
    /// a nonzero size; DATA_LOSS when the sink fails.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_range_writer_write(nint writer, void* data, nuint size);

    /// <summary>Records a UTF-8 message for a failing fetch, or clears it with NULL. It does not fail the fetch
    /// by itself: the callback still returns a non-OK status. The runtime puts the text after its status and the
    /// fetch phase in the call's last error.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_range_writer_set_error(nint writer, byte* message);

    /// <summary>Creates options with the native defaults. Caller frees <paramref name="out_options"/> with
    /// <see cref="litert_lm_model_sync_options_delete"/>; it is NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_options_create(nint* out_options);

    /// <summary>NULL-safe.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_model_sync_options_delete(nint options);

    /// <summary>The change ratio above which an update downloads the whole file (default 0.80). INVALID_ARGUMENT
    /// outside <c>[0, 1]</c> or for NaN.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_options_set_full_download_threshold(nint options, double threshold);

    /// <summary>Whether to delete the local model before the download (default false). The binding always sends
    /// false: the delete comes before a download that can fail.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_options_set_clean(
        nint options, [MarshalAs(UnmanagedType.U1)] bool clean);

    /// <summary>Whether to check free disk space before writing (default true). The binding always sends true.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_options_set_check_disk_space(
        nint options, [MarshalAs(UnmanagedType.U1)] bool check_disk_space);

    /// <summary>Read only by <see cref="litert_lm_model_sync_cache_sync"/>, which the binding does not wrap.
    /// Declared so the C API tests cover it.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_options_set_allow_offline_fallback(
        nint options, [MarshalAs(UnmanagedType.U1)] bool allow_offline_fallback);

    /// <summary>Fetches the remote header through <paramref name="remote_fetcher"/>, reads the local file's
    /// header and computes a plan. A NULL, missing, unreadable or invalid local file is ignored, and every
    /// section is then planned for download. <paramref name="options"/> may be NULL. Caller frees
    /// <paramref name="out_plan"/> with <see cref="litert_lm_model_sync_plan_delete"/>; it is NULL on
    /// failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_plan_create_from_file(
        byte* local_model_path, delegate* unmanaged[Cdecl]<ulong, ulong, nint, nint, LiteRtStatusCode> remote_fetcher,
        nint user_data, nint options, nint* out_plan);

    /// <summary>NULL-safe.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_model_sync_plan_delete(nint plan);

    /// <summary>Not written on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_plan_is_up_to_date(nint plan, byte* out_is_up_to_date);

    /// <summary>Not written on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_plan_get_bytes_to_download(nint plan, ulong* out_bytes);

    /// <summary>Not written on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_plan_get_bytes_to_reuse(nint plan, ulong* out_bytes);

    /// <summary>Not written on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_plan_get_total_remote_size(nint plan, ulong* out_bytes);

    /// <summary>A summary the plan owns until it is deleted: copy it first. NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_plan_format(nint plan, nint* out_formatted_string);

    /// <summary>Writes the remote model to <paramref name="output_model_path"/> through a temporary file next to
    /// it (<c>OUTPUT.tmp_updating</c>), reusing unchanged sections of <paramref name="local_model_path"/>, which
    /// may be NULL. The runtime compares the two path strings to detect an update in place. INVALID_ARGUMENT for
    /// a NULL or empty output or a NULL fetcher.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_update(
        byte* local_model_path, byte* output_model_path,
        delegate* unmanaged[Cdecl]<ulong, ulong, nint, nint, LiteRtStatusCode> remote_fetcher, nint user_data, nint options);

    /// <summary>The high-level cache manager, which the binding does not wrap. Declared so the C API tests cover
    /// it. Caller frees <paramref name="out_result"/> with <see cref="litert_lm_model_sync_result_delete"/>; it
    /// is NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_cache_sync(
        byte* cache_dir, byte* model_id,
        delegate* unmanaged[Cdecl]<ulong, ulong, nint, nint, LiteRtStatusCode> remote_fetcher, nint user_data, nint options,
        nint* out_result);

    /// <summary>NULL-safe.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_model_sync_result_delete(nint result);

    /// <summary>A path the result owns until it is deleted. NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_model_sync_result_get_cached_path(nint result, nint* out_cached_path);
}
