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

    /// <summary>Number of threads for the CPU text executor. A no-op on non-CPU backends: the setter
    /// fetches the executor's <c>CpuConfig</c> and only applies when the backend is CPU (see engine.cc
    /// <c>set_num_threads</c>).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_num_threads(nint settings, int num_threads);

    /// <summary>Number of threads for the CPU audio executor. Applies only when an audio executor is
    /// configured, and only reaches the audio backend's CPU compilation options (audio_executor path).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_audio_num_threads(nint settings, int num_threads);

    /// <summary>Sets the LoRA rank for the text executor. 0 disables LoRA. Requires a LoRA-enabled model.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_lora_rank(nint settings, int lora_rank);

    /// <summary>Sets the supported LoRA ranks for the text executor. Returns 0 on success, non-zero on
    /// null/empty input. The ranks are only honored on the GPU (Artisan) backend; on other backends the
    /// native layer accepts and ignores them (still returns success) — see engine.cc + llm_executor_settings.h.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_engine_settings_set_supported_lora_ranks(nint settings, int* lora_ranks, nuint num_ranks);

    /// <summary>Sets the LoRA rank for the audio executor. Applies only when an audio executor is configured.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_audio_lora_rank(nint settings, int lora_rank);

    /// <summary>Sets the supported LoRA ranks for the audio executor. Returns 0 on success, non-zero on
    /// null/empty input or when no audio executor is configured.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_engine_settings_set_supported_audio_lora_ranks(nint settings, int* lora_ranks, nuint num_ranks);

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

    /// <summary>Whether to load the <c>.litertlm</c> file sections in parallel. Defaults to true.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_parallel_file_section_loading(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool parallel_file_section_loading);

    /// <summary>Whether YNNPACK delegates supported CPU operations before XNNPACK (experimental). Native v0.16.0+.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_enable_ynnpack(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool enable_ynnpack);

    /// <summary>Activation tensor precision (0=F32, 1=F16, 2=I16, 3=I8 per <c>ActivationDataType</c>).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_activation_data_type(nint settings, int activation_data_type_int);

    /// <summary>Prefill chunk size. Only applicable to the CPU backend with dynamic models.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_prefill_chunk_size(nint settings, int prefill_chunk_size);

    /// <summary>Number of synthetic prefill tokens for benchmarking (read by the benchmark path).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_num_prefill_tokens(nint settings, int num_prefill_tokens);

    /// <summary>Number of synthetic decode tokens for benchmarking (read by the benchmark path).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_num_decode_tokens(nint settings, int num_decode_tokens);

    /// <summary>Decode steps per GPU sync. Only honored by the Artisan GPU backend (v0.15.0+).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_gpu_decode_steps_per_sync(nint settings, int num_decode_steps_per_sync);

    /// <summary>Whether to wait for GPU weight uploads. Only honored by the Artisan GPU backend (v0.15.0+).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_gpu_wait_for_weight_uploads(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool wait_for_weight_uploads);

    /// <summary>Ringbuffer KV cache for local-attention layers (lower memory, no instant rewind).
    /// Backend-agnostic in interface but currently only the Artisan GPU backend implements it;
    /// unsupported models/backends log a warning and ignore it (v0.15.0+).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_engine_settings_set_use_ringbuffers_local_attention(
        nint settings, [MarshalAs(UnmanagedType.U1)] bool use_ringbuffers_local_attention);

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

    // --- Sampler params (v0.14.0 opaque builder) ------------------------
    // v0.14.0 replaced the by-value LiteRtLmSamplerParams struct with an opaque object built through
    // setters. Create one, set all fields, pass it to session_config_set_sampler_params (which COPIES
    // the fields), then delete it — the caller retains ownership. create() zeroes all numeric fields
    // (NOT the ecosystem defaults), so the binding always sets all four explicitly.

    /// <summary>Allocates an opaque sampler-params object of the given type (all numeric fields zeroed).
    /// Caller frees it with <see cref="litert_lm_sampler_params_delete"/>. Null on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_sampler_params_create(LiteRtLmSamplerType type);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_sampler_params_delete(nint sampler_params);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_sampler_params_set_top_k(nint sampler_params, int top_k);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_sampler_params_set_top_p(nint sampler_params, float top_p);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_sampler_params_set_temperature(nint sampler_params, float temperature);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_sampler_params_set_seed(nint sampler_params, int seed);

    // --- Session config -------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_session_config_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_session_config_set_max_output_tokens(nint config, int max_output_tokens);

    /// <summary>Copies the opaque sampler params into the session config; the caller keeps ownership of
    /// <paramref name="sampler_params"/> and deletes it afterwards.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_session_config_set_sampler_params(nint config, nint sampler_params);

    /// <summary>Sets the text LoRA weights file for the session. The native side OPENS the file
    /// immediately (validates it exists/opens), so a bad path fails here. Returns 0 on success, non-zero
    /// on a null/empty path or an open failure. Requires a LoRA-enabled model.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_session_config_set_lora_path(nint config, string lora_path);

    /// <summary>Sets the audio LoRA weights file for the session. Opens the file immediately (see
    /// <see cref="litert_lm_session_config_set_lora_path"/>). Returns 0 on success, non-zero on failure.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_session_config_set_audio_lora_path(nint config, string audio_lora_path);

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

    /// <summary>Streams the raw tool-call text incrementally on the named channel while it is being
    /// generated (default off: the callback stays silent during a tool-call block and delivers it whole
    /// at the end). The complete tool-call message still arrives as before — the channel chunks are an
    /// additional progress feed (internal_callback_util.cc streams up to a safe cursor that can never
    /// contain a partial end delimiter). Requires native LiteRT-LM v0.14.0+.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_stream_tool_calls(
        nint config, [MarshalAs(UnmanagedType.U1)] bool stream_tool_calls, string channel_name);

    /// <summary>Overrides the prompt template (e.g. a Jinja template string) for this conversation.
    /// Unset = the model's / engine's default template (v0.15.0+).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_prompt_template(nint config, string prompt_template);

    /// <summary>Copies the opaque thinking config (enable flag + token budget) into the conversation
    /// config; the caller keeps ownership and deletes it afterwards. Null clears (v0.15.0+).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_thinking_config(nint config, nint thinking_config);

    /// <summary>Selects the constraint provider for custom constrained decoding (LlGuidance). Takes a
    /// POINTER to the enum value so null can unset it. Mutually exclusive with
    /// <see cref="litert_lm_conversation_config_set_enable_constrained_decoding"/> (tool-calling
    /// constrained decoding) per upstream docs (v0.15.0+).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_config_set_constraint_provider(nint config, int* provider_type);

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

    /// <summary>Sets a per-send max output-tokens cap. When present it overrides the session config's
    /// value for that one send (session_advanced.cc resolves
    /// <c>decode_config.GetMaxOutputTokens().value_or(session_config.GetMaxOutputTokens())</c>); when
    /// absent the session config value applies. Same underlying decode knob as
    /// <see cref="litert_lm_session_config_set_max_output_tokens"/>, at per-send granularity.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_optional_args_set_max_output_tokens(
        nint optional_args, int max_output_tokens);

    /// <summary>Copies the repetition-penalty config into the optional args (deep copy; the caller
    /// keeps ownership of the config). Applies to this one send's decode. Null clears (v0.15.0+).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_optional_args_set_repetition_penalty_config(
        nint optional_args, nint repetition_penalty_config);

    /// <summary>Copies the no-repeat-ngram config into the optional args (deep copy). Applies to this
    /// one send's decode. Null clears (v0.15.0+).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_optional_args_set_no_repeat_ngram_config(
        nint optional_args, nint no_repeat_ngram_config);

    /// <summary>Copies the suppress-tokens config into the optional args (deep copy). Applies to this
    /// one send's decode. Null or an empty inner set clears (v0.15.0+).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_optional_args_set_suppress_tokens_config(
        nint optional_args, nint suppress_tokens_config);

    /// <summary>Copies the thinking config into the optional args for this one send, overriding the
    /// conversation-level thinking config (conversation.cc ResolveThinkingConfig: per-send wins).
    /// Null clears (v0.15.0+).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_optional_args_set_thinking_config(
        nint optional_args, nint thinking_config);

    /// <summary>Sets a per-send output constraint (regex or JSON Schema string) enforced by the
    /// conversation's constraint provider. Only meaningful when the conversation was created with a
    /// constraint provider (LlGuidance) (v0.15.0+).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_conversation_optional_args_set_constraint(
        nint optional_args, LiteRtLmConstraintType constraint_type, string constraint_string);

    // --- Decoding configs (v0.15.0 opaque builders) -----------------------
    // Same lifetime pattern as the sampler params: create, set fields, attach to the optional args /
    // conversation config (which deep-copies), then delete immediately — the caller retains ownership.

    /// <summary>Allocates a repetition-penalty config (defaults: repetition 1.0 = off, presence 0.0,
    /// frequency 0.0, window 0 = infinite). Caller frees with
    /// <see cref="litert_lm_repetition_penalty_config_delete"/>. Null on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_repetition_penalty_config_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_repetition_penalty_config_delete(nint config);

    /// <summary>Multiplicative repetition penalty (HuggingFace style): 1.0 = off; must be >= 1.0
    /// (values below are clamped to 1.0 during execution).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_repetition_penalty_config_set_repetition_penalty(nint config, float repetition_penalty);

    /// <summary>Subtractive presence penalty (OpenAI style): applied once if the token appeared in the window.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_repetition_penalty_config_set_presence_penalty(nint config, float presence_penalty);

    /// <summary>Subtractive frequency penalty (OpenAI style): scaled by the token's occurrence count in the window.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_repetition_penalty_config_set_frequency_penalty(nint config, float frequency_penalty);

    /// <summary>Generated-history window for the penalties. 0 = infinite; negatives clamp to 0.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_repetition_penalty_config_set_window_size(nint config, int window_size);

    /// <summary>Allocates a no-repeat-ngram config. Caller frees with
    /// <see cref="litert_lm_no_repeat_ngram_config_delete"/>. Null on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_no_repeat_ngram_config_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_no_repeat_ngram_config_delete(nint config);

    /// <summary>Ngram size banned from repeating: completing an already-seen ngram of this size forces
    /// the candidate token's logit to -inf. &lt;= 0 disables.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_no_repeat_ngram_config_set_no_repeat_ngram_size(nint config, int no_repeat_ngram_size);

    /// <summary>Generated-history window for ngram tracking. 0 = infinite; clamped up to the ngram size.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_no_repeat_ngram_config_set_window_size(nint config, int window_size);

    /// <summary>Allocates a suppress-tokens config. Caller frees with
    /// <see cref="litert_lm_suppress_tokens_config_delete"/>. Null on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_suppress_tokens_config_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_suppress_tokens_config_delete(nint config);

    /// <summary>Token ids whose logits are forced to -inf on every decode step. Null/0 clears.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_suppress_tokens_config_set_suppress_tokens(
        nint config, int* suppress_tokens, nuint num_tokens);

    /// <summary>Allocates a thinking config. NOTE the native default ctor is enabled + infinite budget
    /// (-1); the binding always sets both fields explicitly. Caller frees with
    /// <see cref="litert_lm_thinking_config_delete"/>. Null on failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_thinking_config_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_thinking_config_delete(nint config);

    /// <summary>Whether thinking/reasoning generation is enabled (feeds the template's
    /// <c>enable_thinking</c> variable unless explicit extra context already sets it).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_thinking_config_set_enable_thinking(
        nint config, [MarshalAs(UnmanagedType.U1)] bool enable_thinking);

    /// <summary>Token budget for the thinking block (-1 = infinite; 0 = treated as no budget by the
    /// task layer). Enforced token-by-token against the model's thinking start/end token ids
    /// (runtime/core/tasks.cc).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void litert_lm_thinking_config_set_thinking_token_budget(nint config, int thinking_token_budget);

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

    /// <summary>Streaming send. <paramref name="callback"/> is an unmanaged function pointer with the
    /// v0.15.0 <c>LiteRtLmStreamCallback</c> shape: <c>(void* callback_data,
    /// const LiteRtLmStreamChunk* chunk)</c>. The chunk object is only valid for the duration of the
    /// call — read it via the <c>litert_lm_stream_chunk_*</c> getters and copy the strings out.
    /// (v0.14.0 passed <c>(callback_data, text, is_final, error_msg)</c> directly; v0.15.0 moved the
    /// same three fields behind the opaque chunk.)</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int litert_lm_conversation_send_message_stream(
        nint conversation, string message_json, string? extra_context, nint optional_args,
        delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint callback_data);

    // --- Stream chunk (v0.15.0) ------------------------------------------
    // Read-only views into the chunk passed to the stream callback; the returned strings are owned
    // by the chunk and only valid during the callback invocation.

    // The three getters run on the per-token hot path (once each per streamed chunk) and are pure
    // field reads on the native side (engine.cc: `return chunk ? chunk->text : nullptr;`) — never
    // blocking, never calling back into managed — so they qualify for [SuppressGCTransition].

    /// <summary>The chunk's text content (a full message-JSON piece), or null for error/metadata-only
    /// chunks (including every is_final chunk, which carries no text).</summary>
    [LibraryImport(Library)]
    [SuppressGCTransition]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_stream_chunk_get_text(nint chunk);

    /// <summary>True on the last chunk of the stream (done, max-tokens, cancelled or error).</summary>
    [LibraryImport(Library)]
    [SuppressGCTransition]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool litert_lm_stream_chunk_is_final(nint chunk);

    /// <summary>The chunk's error message, or null when there is no error.</summary>
    [LibraryImport(Library)]
    [SuppressGCTransition]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_stream_chunk_get_error(nint chunk);

    /// <summary>Renders a message JSON to its templated prompt string (does not send). The returned
    /// pointer is owned by the conversation and valid only until the next render call or the
    /// conversation's deletion — copy it out immediately. Null on failure.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_render_message_to_string(nint conversation, string message_json);

    /// <summary>Renders the conversation's preface (system message + tools + history preamble) to its
    /// templated string, without sending. The returned pointer is owned by the conversation (its own
    /// <c>last_rendered_preface</c> buffer, separate from the message-render buffer) and valid only until
    /// the next <b>preface</b> render or the conversation's deletion — copy it out immediately. Null on
    /// failure.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint litert_lm_conversation_render_preface_to_string(nint conversation);

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

/// <summary>Mirrors <c>LiteRtLmSamplerType</c> in engine.h. v0.14.0 removed the <c>Unspecified = 0</c>
/// member; a public <see cref="LiteRtSamplerType.Unspecified"/> never reaches this enum — the binding
/// skips setting sampler params entirely so the executor's internal default applies
/// (see <see cref="LiteRtConversation.Create"/>).</summary>
internal enum LiteRtLmSamplerType
{
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

/// <summary>Mirrors <c>LiteRtLmConstraintType</c> in engine.h (v0.15.0): the shape of a per-send
/// output constraint string.</summary>
internal enum LiteRtLmConstraintType
{
    None = 0,
    Regex = 1,
    JsonSchema = 2,
}


