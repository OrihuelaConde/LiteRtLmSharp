using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

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

    /// <summary>Sets the maximum number of images the engine accepts. Per the header this only
    /// affects the <i>legacy</i> engine implementation; the current path ignores it. Bound for
    /// completeness — the per-conversation knob is <c>optional_args_set_visual_token_budget</c>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_max_num_images(nint settings, int max_num_images);

    /// <summary>Enables speculative decoding (MTP drafter); requires a model that ships a drafter.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_enable_speculative_decoding(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool enable);

    /// <summary>Turns on benchmark instrumentation so the conversation exposes timings.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_enable_benchmark(nint settings);

    /// <summary>Directory for the compiled-artifact cache. Special values <c>:nocache</c> (disable)
    /// and <c>:memory</c> (in-RAM); empty = next to the model file.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_cache_dir(nint settings, string cache_dir);

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

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_tools(nint config, string tools_json);

    /// <summary>Sets the initial messages (conversation history / few-shot preface) as a JSON
    /// <b>array</b>. Consumed at <c>conversation_create</c>: parsed and appended to the preface after
    /// the system message, then prefilled into the KV cache.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_messages(nint config, string messages_json);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_enable_constrained_decoding(
        nint config, [MarshalAs(UnmanagedType.U1)] bool enable);

    /// <summary>Sets the conversation-preface extra context (a JSON object string) passed to the
    /// prompt-template renderer, e.g. <c>{"enable_thinking":true}</c> for Gemma reasoning mode.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_extra_context(
        nint config, string extra_context_json);

    /// <summary>Whether to drop channel content (in practice the thinking channel) from the KV cache,
    /// so a long reasoning block does not consume the context window on later turns.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_filter_channel_content_from_kv_cache(
        nint config, [MarshalAs(UnmanagedType.U1)] bool filter);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_delete(nint config);

    // --- Conversation optional args (multimodal) -------------------------
    // Per-send overrides passed as the last argument to send_message / send_message_stream.
    // The only setter the C API exposes is the visual token budget (image prefill budget).

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_optional_args_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_optional_args_delete(nint optional_args);

    /// <summary>Sets the visual token budget (number of tokens images may consume during prefill).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_optional_args_set_visual_token_budget(
        nint optional_args, int visual_token_budget);

    // --- Conversation ----------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_create(nint engine, nint config);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_delete(nint conversation);

    /// <summary>Clones a conversation, duplicating its prefilled (KV-cache) state into a new,
    /// independent conversation. Returns null on failure, including when the engine/backend does
    /// not implement cloning (the native layer returns <c>Unimplemented</c>). The caller owns the
    /// returned pointer and frees it with <see cref="litert_lm_conversation_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_clone(nint conversation);

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

    /// <summary>Renders a message JSON to its templated prompt string (does not send). The returned
    /// pointer is owned by the conversation and valid only until the next render call or the
    /// conversation's deletion — copy it out immediately. Null on failure.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_render_message_to_string(nint conversation, string message_json);

    // --- JSON response ---------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_json_response_get_string(nint response);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_json_response_delete(nint response);

    // --- Benchmark info --------------------------------------------------
    // Populated only when the engine was created with benchmark enabled
    // (litert_lm_engine_settings_enable_benchmark). Prefill/decode metrics
    // accumulate per turn; the latest turn is index (num_*_turns - 1).

    /// <summary>Returns the benchmark info for the conversation, or null when unavailable.
    /// Caller owns it and must free with <see cref="litert_lm_benchmark_info_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_get_benchmark_info(nint conversation);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_benchmark_info_delete(nint info);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double litert_lm_benchmark_info_get_time_to_first_token(nint info);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double litert_lm_benchmark_info_get_total_init_time_in_second(nint info);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_benchmark_info_get_num_prefill_turns(nint info);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_benchmark_info_get_num_decode_turns(nint info);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_benchmark_info_get_prefill_token_count_at(nint info, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_benchmark_info_get_decode_token_count_at(nint info, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double litert_lm_benchmark_info_get_prefill_tokens_per_sec_at(nint info, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial double litert_lm_benchmark_info_get_decode_tokens_per_sec_at(nint info, int index);

    // --- Tokenizer -------------------------------------------------------
    // Exact tokenization / detokenization without running inference, plus the model's configured
    // start (BOS) and stop (EOS) tokens. Each tokenize/detokenize/token-union object is caller-owned
    // and freed by its matching *_delete (see the handles in Handles.cs); the const int*/char* getters
    // point INTO the owning object, so callers copy the data out before disposing it.

    /// <summary>Tokenizes UTF-8 <paramref name="text"/> with the engine's tokenizer. Returns a result
    /// object (caller frees with <see cref="litert_lm_tokenize_result_delete"/>), or null on failure.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_engine_tokenize(nint engine, string text);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_tokenize_result_delete(nint result);

    /// <summary>Pointer to the result's internal token-id array (valid only while the result lives).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_tokenize_result_get_tokens(nint result);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint litert_lm_tokenize_result_get_num_tokens(nint result);

    /// <summary>Detokenizes a token-id array back to UTF-8. Returns a result object (caller frees with
    /// <see cref="litert_lm_detokenize_result_delete"/>), or null on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_engine_detokenize(nint engine, int* tokens, nuint num_tokens);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_detokenize_result_delete(nint result);

    /// <summary>The detokenized UTF-8 string (owned by the result; valid only while it lives).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_detokenize_result_get_string(nint result);

    // A TokenUnion is one start/stop token: either a literal string or a sequence of ids (see the type).

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_token_union_delete(nint token_union);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtLmTokenUnionType litert_lm_token_union_get_type(nint token_union);

    /// <summary>The string value, or null when the type is not <see cref="LiteRtLmTokenUnionType.String"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_token_union_get_string(nint token_union);

    /// <summary>Receives the internal id array + count. Returns 0 on success, non-zero when the type is
    /// not <see cref="LiteRtLmTokenUnionType.Ids"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_token_union_get_ids(nint token_union, int** out_tokens, nuint* out_num_tokens);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_token_unions_delete(nint tokens);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint litert_lm_token_unions_get_num_tokens(nint tokens);

    /// <summary>The token union at <paramref name="index"/> — a NEW caller-owned object freed with
    /// <see cref="litert_lm_token_union_delete"/>; null when out of bounds.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_token_unions_get_token_at(nint tokens, nuint index);

    /// <summary>The model's configured start (BOS) token, or null if none — caller frees with
    /// <see cref="litert_lm_token_union_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_engine_get_start_token(nint engine);

    /// <summary>The model's configured stop (EOS) tokens, or null if none — caller frees with
    /// <see cref="litert_lm_token_unions_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_engine_get_stop_tokens(nint engine);
}

/// <summary>Mirrors <c>LiteRtLmSamplerType</c> in engine.h.</summary>
internal enum LiteRtLmSamplerType
{
    Unspecified = 0,
    TopK = 1,
    TopP = 2,
    Greedy = 3,
}

/// <summary>Mirrors <c>LiteRtLmTokenUnionType</c> in engine.h: a start/stop token is either a
/// literal string or a sequence of token ids.</summary>
internal enum LiteRtLmTokenUnionType
{
    String = 0,
    Ids = 1,
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
