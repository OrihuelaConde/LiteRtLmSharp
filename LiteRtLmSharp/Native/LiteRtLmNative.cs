using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

/// <summary>
/// Raw P/Invoke declarations for the LiteRT-LM C API (see <c>c/engine.h</c> upstream).
/// Source-generated marshalling via <see cref="LibraryImportAttribute"/> (AOT-friendly).
/// String parameters are UTF-8; <c>const char*</c> results are owned by native objects
/// and are read as <see cref="nint"/> to avoid the runtime freeing them.
/// </summary>
/// <remarks>
/// Since C API 1.0.0 (LiteRT-LM v0.19.0) every function that can fail returns a <c>LiteRtLmStatusCode</c>,
/// declared here as <see cref="LiteRtStatusCode"/> (same values, both <c>int</c>-sized), and delivers its
/// result through trailing <c>out_*</c> pointers. Pointer outputs are set to NULL on failure; scalar outputs
/// are written only on success, so callers read them only after checking the status (see
/// <see cref="NativeError.ThrowIfFailed"/>). <c>size_t</c> crosses as <see cref="nuint"/> and C <c>bool</c>
/// as one byte (<see cref="UnmanagedType.U1"/>). Only the <c>*_delete</c> destructors and the error-reporter
/// functions return no status.
/// </remarks>
internal static unsafe partial class LiteRtLmNative
{
    internal const string Library = "LiteRtLm";

    // --- Version (C API 1.0.0+) ----------------------------------------

    /// <summary>The C API version of the loaded library (1.1.0 for LiteRT-LM v0.20.0). Libraries older than
    /// C API 1.0.0 do not export it. <see cref="NativeLibraryResolver"/> calls it through a function pointer
    /// when it loads the library (a P/Invoke from inside the resolver would re-enter it); this declaration
    /// serves the other callers.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_get_api_version(int* out_major, int* out_minor, int* out_patch);

    // --- Logging ---------------------------------------------------------

    /// <summary>Sets the process-wide minimum native log severity. Fails with <c>INVALID_ARGUMENT</c>, and
    /// leaves the level unchanged, for a value <c>LiteRtLmLogSeverity</c> does not declare (0 to 5 and
    /// 1000).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_set_min_log_level(int level);

    // --- Error reporting (c/error_reporter.h) ----------------------------
    // errno-style and thread-local: every non-OK return stores a message on the calling thread; successful
    // calls never clear it. The status itself is the function's return value. NativeError owns the
    // read/clear discipline.

    /// <summary>The calling thread's last failure message, or null. Owned by the library (thread-local)
    /// and valid until the next failing call or clear on that thread — copy it out immediately.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_get_last_error_message();

    /// <summary>Resets the calling thread's message to null and frees it.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_clear_last_error();

    // --- Engine settings -------------------------------------------------

    /// <summary>Creates engine settings for a model file. Null vision or audio backends leave that modality
    /// unconfigured. Caller frees <paramref name="out_settings"/> with
    /// <see cref="litert_lm_engine_settings_delete"/>; it is NULL on failure.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_create(
        string model_path, string backend_str, string? vision_backend_str, string? audio_backend_str,
        nint* out_settings);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_max_num_tokens(nint settings, int max_num_tokens);

    /// <summary>Number of threads for the CPU text executor. A no-op on non-CPU backends: the setter
    /// fetches the executor's <c>CpuConfig</c> and only applies when the backend is CPU (see engine.cc
    /// <c>set_num_threads</c>).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_num_threads(nint settings, int num_threads);

    /// <summary>Number of threads for the CPU audio executor. Applies only when an audio executor is
    /// configured, and only reaches the audio backend's CPU compilation options (audio_executor path).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_audio_num_threads(nint settings, int num_threads);

    /// <summary>Sets the LoRA rank for the text executor. 0 disables LoRA. Requires a LoRA-enabled model.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_lora_rank(nint settings, int lora_rank);

    /// <summary>Sets the supported LoRA ranks for the text executor (copied). Fails with
    /// <c>INVALID_ARGUMENT</c> on null or empty input. The ranks are only honored on the GPU (Artisan)
    /// backend; on other backends the native layer accepts and ignores them (still returns OK) — see
    /// engine.cc + llm_executor_settings.h.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_supported_lora_ranks(nint settings, int* lora_ranks, nuint num_ranks);

    /// <summary>Sets the LoRA rank for the audio executor. Applies only when an audio executor is configured.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_audio_lora_rank(nint settings, int lora_rank);

    /// <summary>Sets the supported LoRA ranks for the audio executor (copied). Fails with
    /// <c>INVALID_ARGUMENT</c> on null or empty input and with <c>FAILED_PRECONDITION</c> when no audio
    /// executor is configured.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_supported_audio_lora_ranks(nint settings, int* lora_ranks, nuint num_ranks);

    /// <summary>Sets the maximum number of images the engine accepts. Per the header this only
    /// affects the <i>legacy</i> engine implementation; the current path ignores it. Bound for
    /// completeness — the per-conversation knob is <c>optional_args_set_visual_token_budget</c>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_max_num_images(nint settings, int max_num_images);

    /// <summary>Caps the vision tokens generated per image: the engine picks the smallest vision
    /// encoder/adapter signature that fits (engine_advanced_impl.cc). Must be positive; only applies when
    /// a vision executor is configured and the model declares vision tokens per image. Per-send visual
    /// token budgets above it are then rejected (conversation.cc ValidateVisualTokenBudget).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_max_vision_tokens_per_image(nint settings, int max_vision_tokens_per_image);

    /// <summary>Keeps model weights and allocations resident in GPU memory through Apple's MTLResidencySet
    /// API. Apple platforms with the Metal GPU backend only; ignored elsewhere.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_gpu_enable_metal_residency_set(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool enable_metal_residency_set);

    /// <summary>Enables speculative decoding (MTP drafter); requires a model that ships a drafter. Deprecated
    /// in C API 1.0.0 in favor of the speculative-decoding config object, and still exported; <c>true</c>
    /// sets the mode On.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_enable_speculative_decoding(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool enable_speculative_decoding);

    /// <summary>Turns on benchmark instrumentation so the conversation exposes timings.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_enable_benchmark(nint settings);

    /// <summary>Directory for the compiled-artifact cache (copied; null is rejected). Special values
    /// <c>:nocache</c> (disable) and <c>:memory</c> (in-RAM); empty = next to the model file. For how the
    /// runtime names the cache files, shares them between LiteRT-LM v0.18.0 and v0.20.0, and keys them within
    /// one process, see <see cref="LiteRtEngineOptions.Cache"/>, which carries the measured conditions.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_cache_dir(nint settings, string cache_dir);

    /// <summary>Whether to load the <c>.litertlm</c> file sections in parallel. Defaults to true.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_parallel_file_section_loading(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool parallel_file_section_loading);

    /// <summary>Whether YNNPACK delegates supported CPU operations before XNNPACK (experimental).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_enable_ynnpack(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool enable_ynnpack);

    /// <summary>Activation tensor precision (0=F32, 1=F16, 2=I16, 3=I8 per <c>ActivationDataType</c>). Fails
    /// with <c>INVALID_ARGUMENT</c> for any other value. Measured with LiteRT-LM v0.20.0 on win-x64 and
    /// gemma-4 E2B: on the CPU backend the value had no effect (F32, F16 and I8 gave byte-identical replies);
    /// on the GPU backend (WebGPU on Direct3D 12, one NVIDIA RTX 3080) I16 and I8 are accepted and produce
    /// unusable text.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_activation_data_type(nint settings, int activation_data_type);

    /// <summary>Prefill chunk size. Only applicable to the CPU backend with dynamic models.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_prefill_chunk_size(nint settings, int prefill_chunk_size);

    /// <summary>Number of synthetic prefill tokens for benchmarking (read by the benchmark path).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_num_prefill_tokens(nint settings, int num_prefill_tokens);

    /// <summary>Number of synthetic decode tokens for benchmarking (read by the benchmark path).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_num_decode_tokens(nint settings, int num_decode_tokens);

    /// <summary>Decode steps per GPU sync. Only honored by the Artisan GPU backend.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_gpu_decode_steps_per_sync(nint settings, int num_decode_steps_per_sync);

    /// <summary>Whether to wait for GPU weight uploads. Only honored by the Artisan GPU backend.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_gpu_wait_for_weight_uploads(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool wait_for_weight_uploads);

    /// <summary>Ringbuffer KV cache for local-attention layers (lower memory, no instant rewind).
    /// Backend-agnostic in interface but only the Artisan GPU backend implements it; unsupported
    /// models/backends log a warning and ignore it.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_settings_set_use_ringbuffers_local_attention(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool use_ringbuffers_local_attention);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_delete(nint settings);

    // --- Engine ----------------------------------------------------------

    /// <summary>Creates the engine. Caller frees <paramref name="out_engine"/> with
    /// <see cref="litert_lm_engine_delete"/>, after every conversation created from it; it is NULL on
    /// failure. The settings may be deleted right after the call.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_create(nint settings, nint* out_engine);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_delete(nint engine);

    // --- Sampler params (v0.14.0 opaque builder) ------------------------
    // v0.14.0 replaced the by-value LiteRtLmSamplerParams struct with an opaque object built through
    // setters. Create one, set all fields, pass it to session_config_set_sampler_params (which COPIES
    // the fields), then delete it — the caller retains ownership. create() zeroes all numeric fields
    // (NOT the ecosystem defaults), so the binding always sets all four explicitly.

    /// <summary>Allocates an opaque sampler-params object of the given type (all numeric fields zeroed).
    /// Fails with <c>INVALID_ARGUMENT</c> for an undeclared type. Caller frees
    /// <paramref name="out_params"/> with <see cref="litert_lm_sampler_params_delete"/>; it is NULL on
    /// failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_sampler_params_create(LiteRtLmSamplerType type, nint* out_params);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_sampler_params_delete(nint @params);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_sampler_params_set_top_k(nint @params, int top_k);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_sampler_params_set_top_p(nint @params, float top_p);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_sampler_params_set_temperature(nint @params, float temperature);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_sampler_params_set_seed(nint @params, int seed);

    // --- Session config -------------------------------------------------

    /// <summary>Allocates an empty session config. Caller frees <paramref name="out_config"/> with
    /// <see cref="litert_lm_session_config_delete"/>; it is NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_config_create(nint* out_config);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_config_set_max_output_tokens(nint config, int max_output_tokens);

    /// <summary>Copies the opaque sampler params into the session config; the caller keeps ownership of
    /// <paramref name="sampler_params"/> and deletes it afterwards. Null params and an undeclared sampler
    /// type fail with <c>INVALID_ARGUMENT</c>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_config_set_sampler_params(nint config, nint sampler_params);

    /// <summary>Sets the text LoRA weights file for the session. The native side OPENS the file
    /// immediately (validates it exists/opens), so a bad path fails here: <c>INVALID_ARGUMENT</c> for a
    /// null or empty path, otherwise the status of the open. Requires a LoRA-enabled model.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_config_set_lora_path(nint config, string lora_path);

    /// <summary>Sets the audio LoRA weights file for the session. Opens the file immediately (see
    /// <see cref="litert_lm_session_config_set_lora_path"/>).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_config_set_audio_lora_path(nint config, string audio_lora_path);

    /// <summary>Per-session speculative decoding. Unset = inherit the engine setting; true on an engine
    /// created without it lazily loads the MTP drafter on the session's first request; false disables it
    /// for this session even when the engine enables it. Deprecated in C API 1.0.0 in favor of
    /// the speculative-decoding config object, and still exported.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_session_config_set_enable_speculative_decoding(
        nint config, [MarshalAs(UnmanagedType.U1)] bool enable_speculative_decoding);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_session_config_delete(nint config);

    // --- Conversation config --------------------------------------------
    // Every setter copies its value into the config, and litert_lm_conversation_create copies the whole
    // config, so the config may be deleted right after the create.

    /// <summary>Allocates an empty conversation config. Caller frees <paramref name="out_config"/> with
    /// <see cref="litert_lm_conversation_config_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_create(nint* out_config);

    /// <summary>Copies the session config into the conversation config; the caller keeps ownership of
    /// <paramref name="session_config"/>. <c>INVALID_ARGUMENT</c> for a null session config.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_session_config(nint config, nint session_config);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_system_message(nint config, string system_message_json);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_tools(nint config, string tools_json);

    /// <summary>Sets the initial messages (conversation history / few-shot preface) as a JSON
    /// <b>array</b>, appended to the preface after the system message. The runtime prefills them with the
    /// conversation's first send, not at <c>conversation_create</c>: right after the create the token count
    /// is 0 (LiteRT-LM v0.20.0, win-x64, CPU and GPU backends, gemma-4 E2B).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_messages(nint config, string messages_json);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_enable_constrained_decoding(
        nint config, [MarshalAs(UnmanagedType.U1)] bool enable_constrained_decoding);

    /// <summary>Sets the conversation-preface extra context (a JSON object string) passed to the
    /// prompt-template renderer, e.g. <c>{"enable_thinking":true}</c> for Gemma reasoning mode.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_extra_context(
        nint config, string extra_context_json);

    /// <summary>Whether to drop channel content (in practice the thinking channel) from the KV cache,
    /// so a long reasoning block does not consume the context window on later turns. With it on, the
    /// runtime rewinds the KV cache and schedules a background refill of the reply before it emits the
    /// stream's final chunk; <see cref="litert_lm_conversation_wait_until_done"/> waits for that refill.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_filter_channel_content_from_kv_cache(
        nint config, [MarshalAs(UnmanagedType.U1)] bool filter_channel_content_from_kv_cache);

    /// <summary>Streams the raw tool-call text incrementally on the named channel while it is being
    /// generated (default off: the callback stays silent during a tool-call block and delivers it whole
    /// at the end). The complete tool-call message still arrives as before — the channel chunks are an
    /// additional progress feed (internal_callback_util.cc streams up to a safe cursor that can never
    /// contain a partial end delimiter). A null channel name keeps the native default
    /// <c>"tool_call"</c>.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_stream_tool_calls(
        nint config, [MarshalAs(UnmanagedType.U1)] bool stream_tool_calls, string? channel_name);

    /// <summary>Overrides the prompt template (e.g. a Jinja template string) for this conversation.
    /// Unset or empty = the model's / engine's default template.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_prompt_template(nint config, string prompt_template);

    /// <summary>Copies the opaque thinking config (enable flag + token budget) into the conversation
    /// config; the caller keeps ownership and deletes it afterwards. Null clears.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_thinking_config(nint config, nint thinking_config);

    /// <summary>Selects the constraint provider for custom constrained decoding (LlGuidance), BY VALUE;
    /// <see cref="LiteRtLmConstraintProviderType.None"/> unsets it, and an undeclared value fails with
    /// <c>INVALID_ARGUMENT</c>. Mutually exclusive with
    /// <see cref="litert_lm_conversation_config_set_enable_constrained_decoding"/> (tool-calling
    /// constrained decoding) per upstream docs.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_config_set_constraint_provider(
        nint config, LiteRtLmConstraintProviderType provider_type);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_delete(nint config);

    // --- Conversation optional args --------------------------------------
    // Per-send overrides passed as the last argument to send_message / send_message_stream. Both sends copy
    // every field before they return, so the optional args may be deleted right after the call. Every
    // setter fails only with INVALID_ARGUMENT (a null optional args, or a value the header rejects).

    /// <summary>Allocates empty optional args. Caller frees <paramref name="out_optional_args"/> with
    /// <see cref="litert_lm_conversation_optional_args_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_optional_args_create(nint* out_optional_args);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_optional_args_delete(nint optional_args);

    /// <summary>Sets the visual token budget (number of tokens images may consume during prefill). Not
    /// range-checked here: the send validates it against the per-image cap.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_optional_args_set_visual_token_budget(
        nint optional_args, int visual_token_budget);

    /// <summary>Sets a per-send max output-tokens cap. When present it overrides the session config's
    /// value for that one send (session_advanced.cc resolves
    /// <c>decode_config.GetMaxOutputTokens().value_or(session_config.GetMaxOutputTokens())</c>); when
    /// absent the session config value applies. Same underlying decode knob as
    /// <see cref="litert_lm_session_config_set_max_output_tokens"/>, at per-send granularity.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_optional_args_set_max_output_tokens(
        nint optional_args, int max_output_tokens);

    /// <summary>Copies the repetition-penalty config into the optional args (deep copy; the caller
    /// keeps ownership of the config). Applies to this one send's decode. Null, or a config whose
    /// penalties are all off, clears.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_optional_args_set_repetition_penalty_config(
        nint optional_args, nint repetition_penalty_config);

    /// <summary>Copies the no-repeat-ngram config into the optional args (deep copy). Applies to this
    /// one send's decode. Null, or a disabled config, clears.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_optional_args_set_no_repeat_ngram_config(
        nint optional_args, nint no_repeat_ngram_config);

    /// <summary>Copies the suppress-tokens config into the optional args (deep copy). Applies to this
    /// one send's decode. Null or an empty inner set clears.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_optional_args_set_suppress_tokens_config(
        nint optional_args, nint suppress_tokens_config);

    /// <summary>Copies the thinking config into the optional args for this one send, overriding the
    /// conversation-level thinking config (conversation.cc ResolveThinkingConfig: per-send wins).
    /// Null clears.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_optional_args_set_thinking_config(
        nint optional_args, nint thinking_config);

    /// <summary>Sets a per-send output constraint (regex or JSON Schema string) enforced by the
    /// conversation's constraint provider. Only meaningful when the conversation was created with a
    /// constraint provider (LlGuidance). <c>INVALID_ARGUMENT</c> for an undeclared
    /// <paramref name="constraint_type"/>, keeping the previous constraint. A null
    /// <paramref name="constraint_string"/> sets the type and clears the pattern.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_optional_args_set_constraint(
        nint optional_args, LiteRtLmConstraintType constraint_type, string? constraint_string);

    /// <summary>Sets the tool choice for one send (C API 1.1.0): an OpenAI Chat Completions tool choice as
    /// JSON (<c>"\"auto\""</c>, <c>"\"none\""</c>, <c>"\"required\""</c>, a forced function or an
    /// <c>allowed_tools</c> object). Used only with constrained decoding and tools. <c>INVALID_ARGUMENT</c>
    /// for text that is not one of those, keeping the previous choice; null clears. The binding does not
    /// call it: the declaration lets a test pin the export.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_optional_args_set_tool_choice(
        nint optional_args, string? tool_choice_json);

    // --- Decoding configs (v0.15.0 opaque builders) -----------------------
    // Same lifetime pattern as the sampler params: create, set fields, attach to the optional args /
    // conversation config (which deep-copies), then delete immediately — the caller retains ownership.

    /// <summary>Allocates a repetition-penalty config (defaults: repetition 1.0 = off, presence 0.0,
    /// frequency 0.0, window 0 = infinite). Caller frees <paramref name="out_config"/> with
    /// <see cref="litert_lm_repetition_penalty_config_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_repetition_penalty_config_create(nint* out_config);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_repetition_penalty_config_delete(nint config);

    /// <summary>Multiplicative repetition penalty (HuggingFace style): 1.0 = off; must be >= 1.0
    /// (values below are clamped to 1.0 during execution).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_repetition_penalty_config_set_repetition_penalty(nint config, float repetition_penalty);

    /// <summary>Subtractive presence penalty (OpenAI style): applied once if the token appeared in the window.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_repetition_penalty_config_set_presence_penalty(nint config, float presence_penalty);

    /// <summary>Subtractive frequency penalty (OpenAI style): scaled by the token's occurrence count in the window.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_repetition_penalty_config_set_frequency_penalty(nint config, float frequency_penalty);

    /// <summary>Generated-history window for the penalties. 0 = infinite; negatives clamp to 0.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_repetition_penalty_config_set_window_size(nint config, int window_size);

    /// <summary>Allocates a no-repeat-ngram config. Caller frees <paramref name="out_config"/> with
    /// <see cref="litert_lm_no_repeat_ngram_config_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_no_repeat_ngram_config_create(nint* out_config);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_no_repeat_ngram_config_delete(nint config);

    /// <summary>Ngram size banned from repeating: completing an already-seen ngram of this size forces
    /// the candidate token's logit to -inf. &lt;= 0 disables.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_no_repeat_ngram_config_set_no_repeat_ngram_size(nint config, int no_repeat_ngram_size);

    /// <summary>Generated-history window for ngram tracking. 0 = infinite; clamped up to the ngram size.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_no_repeat_ngram_config_set_window_size(nint config, int window_size);

    /// <summary>Allocates a suppress-tokens config. Caller frees <paramref name="out_config"/> with
    /// <see cref="litert_lm_suppress_tokens_config_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_suppress_tokens_config_create(nint* out_config);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_suppress_tokens_config_delete(nint config);

    /// <summary>Token ids whose logits are forced to -inf on every decode step (copied). A count of 0
    /// clears; null ids with a non-zero count fail with <c>INVALID_ARGUMENT</c>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_suppress_tokens_config_set_suppress_tokens(
        nint config, int* suppress_tokens, nuint num_tokens);

    /// <summary>Allocates a thinking config (declared in conversation.h). NOTE the native default ctor is
    /// enabled + infinite budget (-1); the binding always sets both fields explicitly. Caller frees
    /// <paramref name="out_config"/> with <see cref="litert_lm_thinking_config_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_thinking_config_create(nint* out_config);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_thinking_config_delete(nint config);

    /// <summary>Whether thinking/reasoning generation is enabled (feeds the template's
    /// <c>enable_thinking</c> variable unless explicit extra context already sets it).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_thinking_config_set_enable_thinking(
        nint config, [MarshalAs(UnmanagedType.U1)] bool enable_thinking);

    /// <summary>Token budget for the thinking block (-1 = infinite; 0 = treated as no budget by the
    /// task layer). Enforced token-by-token against the model's thinking start/end token ids
    /// (runtime/core/tasks.cc).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_thinking_config_set_thinking_token_budget(nint config, int thinking_token_budget);

    // --- Conversation ----------------------------------------------------
    // engine.h "Threading model": calls that use the same conversation must not overlap; a stream callback
    // may call only cancel_process, and only on a chunk that is not final; after cancel_process the
    // conversation must not be reused, only deleted.

    /// <summary>Creates a conversation on <paramref name="engine"/> with <paramref name="config"/> (null =
    /// the default config). The config is copied, not retained. Caller frees
    /// <paramref name="out_conversation"/> with <see cref="litert_lm_conversation_delete"/>, before the
    /// engine.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_create(nint engine, nint config, nint* out_conversation);

    /// <summary>Deletes the conversation. It first waits for every task of the conversation's session and
    /// every stream callback still running or pending for it.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_delete(nint conversation);

    /// <summary>Clones a conversation, duplicating its prefilled (KV-cache) state into a new,
    /// independent conversation. Fails with the runtime's status, for example <c>UNIMPLEMENTED</c> when the
    /// engine/backend does not implement cloning. The caller owns <paramref name="out_conversation"/> and
    /// frees it with <see cref="litert_lm_conversation_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_clone(nint conversation, nint* out_conversation);

    /// <summary>Requests the cancellation of the conversation's in-flight work and returns without waiting:
    /// a stream callback can still run afterwards. Fails only for an invalid handle.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_cancel_process(nint conversation);

    /// <summary>Blocks until the conversation's session has no task in flight and no stream callback
    /// running or pending, including the background KV-cache refill that channel filtering schedules after
    /// a reply. Never call it from a stream callback, or after <see cref="litert_lm_conversation_cancel_process"/>.
    /// A cancel from another thread can still arrive while it waits, so a caller that tracks cancels checks again
    /// after it returns. <c>DEADLINE_EXCEEDED</c> after the runtime's fixed wait limit.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_wait_until_done(nint conversation);

    /// <summary>Tokens in the conversation KV cache (prefill + decode). It does not wait for the
    /// conversation's tasks, so right after a reply whose channel content is filtered it can read the
    /// count before the background refill. <paramref name="out_count"/> is written only on success.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_get_token_count(nint conversation, nuint* out_count);

    /// <summary>Blocking send. <paramref name="message_json"/> must be a JSON object. Caller frees
    /// <paramref name="out_response"/> with <see cref="litert_lm_json_response_delete"/>; on failure it is
    /// null and the status is the generation error. <paramref name="extra_context"/> and
    /// <paramref name="optional_args"/> are copied before the send starts.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_send_message(
        nint conversation, string message_json, string? extra_context, nint optional_args, nint* out_response);

    /// <summary>Streaming send. <paramref name="callback"/> is an unmanaged function pointer with the
    /// <c>LiteRtLmStreamCallback</c> shape: <c>(void* callback_data, const LiteRtLmStreamChunk* chunk)</c>.
    /// The chunk object is only valid for the duration of the call — read it via the
    /// <c>litert_lm_stream_chunk_*</c> getters and copy the strings out. A non-OK status means the stream
    /// did not start and the callback is not called; a failure after the start arrives as the error text of
    /// a final chunk. <paramref name="extra_context"/> and <paramref name="optional_args"/> are copied
    /// before it returns.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_send_message_stream(
        nint conversation, string message_json, string? extra_context, nint optional_args,
        delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint callback_data);

    // --- Stream chunk ----------------------------------------------------
    // Read-only views into the chunk passed to the stream callback; the strings they lend are owned by the
    // chunk and only valid during the callback invocation. Each getter fails only with INVALID_ARGUMENT for
    // a null chunk or a null out pointer, and its out value is then not a chunk value.

    // The three getters run on the per-token hot path (once each per streamed chunk) and are pure field
    // reads on the native side (c/engine.cc: null checks, then `*out_text = chunk->text`) — never
    // blocking, never calling back into managed — so they qualify for [SuppressGCTransition]. Their out
    // pointers point to stack locals, which the GC never moves.

    /// <summary>The chunk's text content (a full message-JSON piece), or null for error/metadata-only
    /// chunks (including every is_final chunk, which carries no text). Lent by the chunk.</summary>
    [LibraryImport(Library)]
    [SuppressGCTransition]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_stream_chunk_get_text(nint chunk, nint* out_text);

    /// <summary>Whether this is the last chunk of the stream (done, max-tokens, cancelled or error). The out
    /// value is a one-byte C <c>bool</c>, not written on failure.</summary>
    [LibraryImport(Library)]
    [SuppressGCTransition]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_stream_chunk_is_final(nint chunk, byte* out_is_final);

    /// <summary>The chunk's error message, or null when there is no error. Lent by the chunk.</summary>
    [LibraryImport(Library)]
    [SuppressGCTransition]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_stream_chunk_get_error(nint chunk, nint* out_error);

    /// <summary>Renders a message JSON to its templated prompt string (does not send). The string in
    /// <paramref name="out_text"/> is owned by the conversation and valid only until the next message render
    /// or the conversation's deletion — copy it out immediately. Not safe to call concurrently on one
    /// conversation.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_render_message_to_string(
        nint conversation, string message_json, nint* out_text);

    /// <summary>Renders the conversation's preface (system message + tools + history preamble) to its
    /// templated string, without sending. The string in <paramref name="out_text"/> is owned by the
    /// conversation (its own <c>last_rendered_preface</c> buffer, separate from the message-render buffer)
    /// and valid only until the next <b>preface</b> render or the conversation's deletion — copy it out
    /// immediately.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_render_preface_to_string(nint conversation, nint* out_text);

    // --- JSON response ---------------------------------------------------

    /// <summary>The response JSON, lent by <paramref name="response"/>: valid until
    /// <see cref="litert_lm_json_response_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_json_response_get_string(nint response, nint* out_json);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_json_response_delete(nint response);

    // --- Benchmark info --------------------------------------------------
    // Populated only when the engine was created with benchmark enabled
    // (litert_lm_engine_settings_enable_benchmark). Prefill/decode metrics
    // accumulate per turn; the latest turn is index (num_*_turns - 1).

    /// <summary>The benchmark info for the conversation; fails with the runtime's status when it is not
    /// available (for example when benchmarking is off). Caller owns <paramref name="out_benchmark_info"/>
    /// and frees it with <see cref="litert_lm_benchmark_info_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_conversation_get_benchmark_info(nint conversation, nint* out_benchmark_info);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_benchmark_info_delete(nint benchmark_info);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_benchmark_info_get_time_to_first_token(nint benchmark_info, double* out_seconds);

    /// <summary>The engine creation time in seconds (since C API 1.1.0; before, the sum of every recorded
    /// init phase); 0 when it was not reported.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_benchmark_info_get_total_init_time_in_second(nint benchmark_info, double* out_seconds);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_benchmark_info_get_num_prefill_turns(nint benchmark_info, nuint* out_num_turns);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_benchmark_info_get_num_decode_turns(nint benchmark_info, nuint* out_num_turns);

    /// <summary>Token count of the prefill turn at <paramref name="index"/>; <c>OUT_OF_RANGE</c> past the
    /// last turn.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_benchmark_info_get_prefill_token_count_at(nint benchmark_info, nuint index, nuint* out_token_count);

    /// <summary>Token count of the decode turn at <paramref name="index"/>; <c>OUT_OF_RANGE</c> past the
    /// last turn.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_benchmark_info_get_decode_token_count_at(nint benchmark_info, nuint index, nuint* out_token_count);

    /// <summary>Prefill throughput of the turn at <paramref name="index"/>; <c>OUT_OF_RANGE</c> past the
    /// last turn.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_benchmark_info_get_prefill_tokens_per_sec_at(nint benchmark_info, nuint index, double* out_tokens_per_sec);

    /// <summary>Decode throughput of the turn at <paramref name="index"/>; <c>OUT_OF_RANGE</c> past the
    /// last turn.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_benchmark_info_get_decode_tokens_per_sec_at(nint benchmark_info, nuint index, double* out_tokens_per_sec);

    // --- Tokenizer -------------------------------------------------------
    // Exact tokenization / detokenization without running inference, plus the model's configured
    // start (BOS) and stop (EOS) tokens. Each tokenize/detokenize result, the start token and the stop-token
    // collection are caller-owned and freed by their matching *_delete (see the handles in Handles.cs); the
    // const int*/char* outputs and the collection's elements point INTO the owning object, so callers copy
    // the data out before disposing it.

    /// <summary>Tokenizes UTF-8 <paramref name="text"/> with the engine's tokenizer. Caller frees
    /// <paramref name="out_result"/> with <see cref="litert_lm_tokenize_result_delete"/>; it is NULL on
    /// failure.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_tokenize(nint engine, string text, nint* out_result);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_tokenize_result_delete(nint result);

    /// <summary>Pointer to the result's internal token-id array (valid only while the result lives); NULL
    /// on success when the result holds no tokens.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_tokenize_result_get_tokens(nint result, int** out_tokens);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_tokenize_result_get_num_tokens(nint result, nuint* out_num_tokens);

    /// <summary>Detokenizes a token-id array back to UTF-8 (null <paramref name="tokens"/> is rejected even
    /// with a zero count). Caller frees <paramref name="out_result"/> with
    /// <see cref="litert_lm_detokenize_result_delete"/>; it is NULL on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_detokenize(nint engine, int* tokens, nuint num_tokens, nint* out_result);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_detokenize_result_delete(nint result);

    /// <summary>The detokenized UTF-8 string (owned by the result; valid only while it lives).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_detokenize_result_get_string(nint result, nint* out_text);

    // A TokenUnion is one start/stop token: either a literal string or a sequence of ids (see the type).

    /// <summary>Frees a token union the caller owns (the start token). Never pass an element borrowed from
    /// a <c>LiteRtLmTokenUnions</c> collection: the collection owns and frees those.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_token_union_delete(nint token_union);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_token_union_get_type(nint token_union, LiteRtLmTokenUnionType* out_type);

    /// <summary>The string value (owned by the union); <c>INVALID_ARGUMENT</c> when the type is not
    /// <see cref="LiteRtLmTokenUnionType.String"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_token_union_get_string(nint token_union, nint* out_string);

    /// <summary>Receives the internal id array (owned by the union) and its count; <c>INVALID_ARGUMENT</c>
    /// when the type is not <see cref="LiteRtLmTokenUnionType.Ids"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_token_union_get_ids(nint token_union, int** out_tokens, nuint* out_num_tokens);

    /// <summary>Frees the collection and every element <see cref="litert_lm_token_unions_get_token_at"/>
    /// lent out.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_token_unions_delete(nint tokens);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_token_unions_get_num_tokens(nint tokens, nuint* out_num_tokens);

    /// <summary>The token union at <paramref name="index"/>, BORROWED from the collection: valid until
    /// <paramref name="tokens"/> is deleted, and never passed to <see cref="litert_lm_token_union_delete"/>.
    /// <c>OUT_OF_RANGE</c> past the end.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_token_unions_get_token_at(nint tokens, nuint index, nint* out_token);

    /// <summary>The model's configured start (BOS) token; NULL on success when the model declares none.
    /// Caller frees it with <see cref="litert_lm_token_union_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_get_start_token(nint engine, nint* out_token);

    /// <summary>The model's configured stop (EOS) tokens; NULL on success when the model declares none.
    /// Caller frees them with <see cref="litert_lm_token_unions_delete"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial LiteRtStatusCode litert_lm_engine_get_stop_tokens(nint engine, nint* out_tokens);
}

/// <summary>Mirrors <c>LiteRtLmSamplerType</c> in engine.h. v0.14.0 removed the <c>Unspecified = 0</c>
/// member and v0.18.0 restored it (for model metadata that declares no sampler); a public
/// <see cref="LiteRtSamplerType.Unspecified"/> still never reaches the sampler builder — the binding
/// skips setting sampler params entirely so the executor's internal default applies
/// (see <see cref="LiteRtConversation.Create"/>).</summary>
internal enum LiteRtLmSamplerType
{
    /// <summary>Only reported by model metadata (<c>loaded_file_sampler_type</c>); never sent.</summary>
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

/// <summary>Mirrors <c>LiteRtLmConstraintType</c> in conversation.h: the shape of a per-send
/// output constraint string.</summary>
internal enum LiteRtLmConstraintType
{
    None = 0,
    Regex = 1,
    JsonSchema = 2,
}

/// <summary>Mirrors <c>LiteRtLmConstraintProviderType</c> in conversation.h (C API 1.0.0 added
/// <see cref="None"/>, which unsets the provider).</summary>
internal enum LiteRtLmConstraintProviderType
{
    None = 0,
    LlGuidance = 1,
}


