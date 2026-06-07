using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteLMSharp.Native;

/// <summary>
/// Raw P/Invoke declarations for the LiteRT-LM C API (see <c>c/engine.h</c> upstream).
/// Source-generated marshalling via <see cref="LibraryImportAttribute"/> (AOT-friendly).
/// String parameters are UTF-8; <c>const char*</c> returns are owned by native objects
/// and are returned as <see cref="nint"/> to avoid the runtime freeing them.
/// </summary>
internal static unsafe partial class LiteRtLmNative
{
    internal const string Library = "LiteRtLm";

    // --- Logging ---------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void litert_lm_set_min_log_level(int level);

    // --- Engine settings -------------------------------------------------

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_engine_settings_create(
        string model_path, string backend_str, string? vision_backend_str, string? audio_backend_str);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_max_num_tokens(nint settings, int max_num_tokens);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_delete(nint settings);

    // --- Engine ----------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_engine_create(nint settings);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_delete(nint engine);

    // --- Session config (sampler) ---------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_session_config_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_session_config_set_max_output_tokens(nint config, int max_output_tokens);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_session_config_set_sampler_params(nint config, LiteRtLmSamplerParams* sampler_params);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_session_config_delete(nint config);

    // --- Conversation config --------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_config_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_session_config(nint config, nint session_config);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_system_message(nint config, string system_message_json);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_delete(nint config);

    // --- Conversation ----------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_create(nint engine, nint config);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_delete(nint conversation);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_cancel_process(nint conversation);

    /// <summary>Tokens currently in the conversation KV cache (prefill + decode). Negative on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_conversation_get_token_count(nint conversation);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_send_message(
        nint conversation, string message_json, string? extra_context, nint optional_args);

    /// <summary>Streaming send. <paramref name="callback"/> is an unmanaged function pointer.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_conversation_send_message_stream(
        nint conversation, string message_json, string? extra_context, nint optional_args,
        delegate* unmanaged[Cdecl]<nint, nint, byte, nint, void> callback, nint callback_data);

    // --- JSON response ---------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_json_response_get_string(nint response);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_json_response_delete(nint response);
}

/// <summary>Mirrors <c>LiteRtLmSamplerType</c> in engine.h.</summary>
internal enum LiteRtLmSamplerType
{
    Unspecified = 0,
    TopK = 1,
    TopP = 2,
    Greedy = 3,
}

/// <summary>Mirrors the <c>LiteRtLmSamplerParams</c> struct in engine.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LiteRtLmSamplerParams
{
    public LiteRtLmSamplerType Type;
    public int TopK;
    public float TopP;
    public float Temperature;
    public int Seed;
}
