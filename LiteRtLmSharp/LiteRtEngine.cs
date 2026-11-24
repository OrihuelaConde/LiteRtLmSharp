using System.Runtime.InteropServices;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>
/// A LiteRT-LM inference engine. Heavyweight: holds the model weights. Create one per
/// model and spawn lightweight <see cref="LiteRtConversation"/> objects from it.
/// </summary>
public sealed class LiteRtEngine : IDisposable
{
    private readonly EngineHandle _engine;
    // Whether the engine was loaded with a vision/audio encoder enabled. Conversations spawned from a
    // multimodal engine get a session config attached automatically (the encoder executor only loads
    // when one is present), so a plain CreateConversation() can send image/audio without extra setup.
    private readonly bool _isMultimodal;
    private bool _disposed;

    static LiteRtEngine() => NativeLibraryResolver.Initialize();

    // One live chat engine per process is the default rule (it dates from self-built v0.13.1
    // libraries, where a second live engine was reported to hang in the native layer; never reproduced). Recreating an engine after
    // disposing the previous one works — Google's Edge Gallery switches model/backend exactly this way
    // (engine.close() + new Engine). Engines loaded with LiteRtEngineOptions.AllowMultipleEngines may
    // coexist with each other, never with one that did not opt in. The process-wide gate
    // (EngineLiveness.Default) is acquired here and released when the native engine is destroyed
    // (EngineHandle.ReleaseHandle), so a forgotten/GC'd engine also frees its slot. Every conversation holds a
    // strong reference to its engine (it needs the tokenizer and context limit for the KV overflow
    // guard), and its native handle holds a reference on the engine's handle, so the native engine is
    // deleted, and the slot freed, only after its last conversation is gone, whichever is disposed or
    // finalized first. Disposing conversations and the engine remains the contract.

    private LiteRtEngine(EngineHandle engine, bool isMultimodal, int maxNumTokens, bool benchmarkEnabled)
    {
        _engine = engine;
        _isMultimodal = isMultimodal;
        MaxNumTokens = maxNumTokens;
        BenchmarkEnabled = benchmarkEnabled;
    }

    /// <summary>The native engine handle, for conversations spawned from this engine.</summary>
    internal EngineHandle Handle => _engine;

    /// <summary>The context limit the engine was loaded with (<see cref="LiteRtEngineOptions.MaxNumTokens"/>);
    /// 0 when the caller left the engine default, whose value the C API does not expose — the KV overflow
    /// guard is only active when this is known.</summary>
    internal int MaxNumTokens { get; }

    /// <summary>Whether the engine was loaded with <see cref="LiteRtEngineOptions.EnableBenchmark"/>, so its
    /// conversations record benchmark turns.</summary>
    internal bool BenchmarkEnabled { get; }

    /// <summary>The id of the token that opens the thinking channel in Gemma 4's chat template
    /// (<c>&lt;|channel&gt;</c>), or <c>null</c> when the model's tokenizer has no single token that round-trips
    /// to that text, or when the lookup failed. A send with <see cref="LiteRtSendOptions.EnableThinking"/>
    /// <c>false</c> suppresses it. Resolved on first use and kept; it does not check that this object is disposed,
    /// as <see cref="CountTokens"/>.</summary>
    internal int? ThinkingChannelOpenToken
    {
        get
        {
            int id = Volatile.Read(ref _thinkingChannelOpenToken);
            if (id == UnresolvedToken)
            {
                id = SingleTokenOf(ThinkingChannelOpenText, TokenizeIds, ids => DetokenizeIds(ids)) ?? NoToken;
                // Two first uses at once compute the same value.
                Volatile.Write(ref _thinkingChannelOpenToken, id);
            }
            return id >= 0 ? id : null;
        }
    }

    /// <summary>The id of the one token that <paramref name="text"/> tokenizes to and that detokenizes back to
    /// <paramref name="text"/>; <c>null</c> when it tokenizes to another number of tokens, when the round trip
    /// gives other text, or when either call throws <see cref="LiteRtException"/>.</summary>
    internal static int? SingleTokenOf(string text, Func<string, int[]> tokenize, Func<int[], string> detokenize)
    {
        try
        {
            int[] ids = tokenize(text);
            return ids.Length == 1 && detokenize(ids) == text ? ids[0] : null;
        }
        catch (LiteRtException)
        {
            return null;
        }
    }

    private const string ThinkingChannelOpenText = "<|channel>";
    private const int UnresolvedToken = -2, NoToken = -1;
    private int _thinkingChannelOpenToken = UnresolvedToken;

    /// <summary>
    /// Gets how many chat engines are alive in this process: 0 or 1 unless the live engines were loaded
    /// with <see cref="LiteRtEngineOptions.AllowMultipleEngines"/>. A <see cref="LiteRtEmbeddingEngine"/> is
    /// not counted. A disposed engine counts until its last conversation is disposed too, because the native
    /// engine lives until then.
    /// </summary>
    public static int LiveEngineCount => EngineLiveness.Default.LiveCount;

    /// <summary>Sets the global minimum log level (0=VERBOSE … 5=FATAL, 1000=SILENT). Any other value
    /// silences the log, like 1000.</summary>
    /// <remarks>Above 5 (SILENT included) LiteRT also stops recording the reason of the errors its status
    /// macros raise, so a <see cref="LiteRtException"/> then names the status (for example
    /// <c>INVALID_ARGUMENT</c>) without saying why. Some failures keep their reason at any level, such as
    /// <see cref="Load"/> or <see cref="LiteRtModelInfo.Read"/> on a file that is not a model. Every level
    /// from 0 to 5 keeps the reasons; 4 (ERROR) logs errors only.</remarks>
    /// <exception cref="DllNotFoundException">The native LiteRT-LM library could not be loaded; the
    /// message names the fix (the missing <c>LiteRtLmSharp.runtime.&lt;rid&gt;</c> package, a runtime
    /// package for another architecture, or a missing system library).</exception>
    /// <exception cref="LiteRtException">The native library's C API version does not match this binding
    /// (checked the first time any native function is used), or the native call failed.</exception>
    public static void SetMinLogLevel(int level)
    {
        NativeLibraryResolver.EnsureCompatible();
        NativeError.ThrowIfFailed(
            LiteRtLmNative.litert_lm_set_min_log_level(NativeLogLevel(level)),
            nameof(LiteRtLmNative.litert_lm_set_min_log_level));
    }

    /// <summary><c>LiteRtLmLogSeverity</c>'s silent level.</summary>
    internal const int SilentLogLevel = 1000;

    /// <summary>The level sent to the runtime: a value <c>LiteRtLmLogSeverity</c> declares (0 to 5 and
    /// 1000) as is, any other value as <see cref="SilentLogLevel"/>. LiteRT-LM v0.18.0 treated every
    /// undeclared value as silent and later versions reject it with <c>INVALID_ARGUMENT</c>, so the binding
    /// maps it to keep the earlier effect.</summary>
    internal static int NativeLogLevel(int level) => level is >= 0 and <= 5 or SilentLogLevel ? level : SilentLogLevel;

    /// <summary>
    /// Loads a model and creates the engine. By default only ONE chat engine may be alive at a time; a
    /// <see cref="LiteRtEmbeddingEngine"/> does not count. To switch model or backend, dispose every
    /// conversation and the engine first, then call <see cref="Load"/> again. To keep several chat engines
    /// alive, load each of them with <see cref="LiteRtEngineOptions.AllowMultipleEngines"/> (see its remarks
    /// for what has been measured).
    /// </summary>
    /// <exception cref="ArgumentException">The model file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Another chat engine is alive in this process, and this
    /// engine or a live one was loaded without <see cref="LiteRtEngineOptions.AllowMultipleEngines"/>. An engine
    /// already disposed is still alive while one of its conversations is.</exception>
    /// <exception cref="DllNotFoundException">The native LiteRT-LM library could not be loaded; the
    /// message names the fix (the missing <c>LiteRtLmSharp.runtime.&lt;rid&gt;</c> package, a runtime
    /// package for another architecture, or a missing system library).</exception>
    /// <exception cref="LiteRtException">Native engine creation failed, or the native library's C API
    /// version does not match this binding.</exception>
    public static LiteRtEngine Load(LiteRtEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.ModelPath))
            throw new ArgumentException(
                "LiteRtEngineOptions.ModelPath must be set — it is the only model source in this release.",
                nameof(options));
        if (!File.Exists(options.ModelPath))
            throw new ArgumentException($"Model file not found: {options.ModelPath}", nameof(options));

        // Before the engine slot is taken, so a refused native library cannot leak it.
        NativeLibraryResolver.EnsureCompatible();
        switch (EngineLiveness.Default.TryAcquire(options.AllowMultipleEngines))
        {
            case EngineAdmission.NewEngineNotOptedIn:
                throw new InvalidOperationException(
                    "Another LiteRtEngine is still alive in this process, and this engine was loaded without " +
                    "LiteRtEngineOptions.AllowMultipleEngines. Dispose the existing engine (and its " +
                    "conversations) first, then load the new model, or set AllowMultipleEngines = true on " +
                    "every engine that should stay alive at the same time.");
            case EngineAdmission.LiveEngineNotOptedIn:
                throw new InvalidOperationException(
                    "A LiteRtEngine loaded without LiteRtEngineOptions.AllowMultipleEngines is still alive in " +
                    "this process, so no other engine can load next to it, even one that opts in. Dispose it " +
                    "(and its conversations) first, or load it with AllowMultipleEngines = true as well.");
        }

        EngineHandle? handle = null;
        try
        {
            // Passing null for vision/audio leaves that modality unconfigured (NULL = "not set" per
            // the C API). Set a LiteRtBackend on a multimodal model to enable image/audio input.
            nint settingsPtr;
            LiteRtStatusCode status;
            unsafe
            {
                status = LiteRtLmNative.litert_lm_engine_settings_create(
                    options.ModelPath, options.Backend.Value, options.VisionBackend?.Value, options.AudioBackend?.Value,
                    &settingsPtr);
            }
            NativeError.ThrowIfFailed(status, nameof(LiteRtLmNative.litert_lm_engine_settings_create));

            using var settings = new EngineSettingsHandle(settingsPtr);
            nint s = settings.Ptr;
            if (options.MaxNumTokens > 0)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_max_num_tokens(s, options.MaxNumTokens),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_max_num_tokens));
            if (options.MaxNumImages > 0)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_max_num_images(s, options.MaxNumImages),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_max_num_images));
            if (options.MaxVisionTokensPerImage is { } maxVisionTokens)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_max_vision_tokens_per_image(s, maxVisionTokens),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_max_vision_tokens_per_image));
            if (options.EnableMetalResidencySet is { } metalResidency)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_gpu_enable_metal_residency_set(s, metalResidency),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_gpu_enable_metal_residency_set));
            if (options.Cache.NativeValue is { } cacheDir)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_cache_dir(s, cacheDir),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_cache_dir));
            if (options.EnableBenchmark)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_enable_benchmark(s),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_enable_benchmark));
            if (options.EnableSpeculativeDecoding)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_enable_speculative_decoding(s, true),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_enable_speculative_decoding));
            if (options.ParallelFileSectionLoading is { } parallelLoading)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_parallel_file_section_loading(s, parallelLoading),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_parallel_file_section_loading));
            if (options.ActivationDataType is { } activationType)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_activation_data_type(s, (int)activationType),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_activation_data_type));
            if (options.PrefillChunkSize > 0)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_prefill_chunk_size(s, options.PrefillChunkSize),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_prefill_chunk_size));
            if (options.BenchmarkPrefillTokens > 0)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_num_prefill_tokens(s, options.BenchmarkPrefillTokens),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_num_prefill_tokens));
            if (options.BenchmarkDecodeTokens > 0)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_num_decode_tokens(s, options.BenchmarkDecodeTokens),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_num_decode_tokens));
            if (options.NumThreads is { } numThreads)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_num_threads(s, numThreads),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_num_threads));
            if (options.AudioNumThreads is { } audioNumThreads)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_audio_num_threads(s, audioNumThreads),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_audio_num_threads));
            if (options.GpuDecodeStepsPerSync is { } gpuSteps)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_gpu_decode_steps_per_sync(s, gpuSteps),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_gpu_decode_steps_per_sync));
            if (options.GpuWaitForWeightUploads is { } gpuWait)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_gpu_wait_for_weight_uploads(s, gpuWait),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_gpu_wait_for_weight_uploads));
            if (options.EnableYnnpack is { } ynnpack)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_enable_ynnpack(s, ynnpack),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_enable_ynnpack));
            if (options.UseRingbuffersLocalAttention is { } ringbuffers)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_use_ringbuffers_local_attention(s, ringbuffers),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_use_ringbuffers_local_attention));
            if (options.LoraRank is { } loraRank)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_lora_rank(s, loraRank),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_lora_rank));
            if (options.AudioLoraRank is { } audioLoraRank)
                NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_engine_settings_set_audio_lora_rank(s, audioLoraRank),
                    nameof(LiteRtLmNative.litert_lm_engine_settings_set_audio_lora_rank));
            if (options.SupportedLoraRanks is { } supportedLoraRanks)
                ApplySupportedLoraRanks(s, supportedLoraRanks, audio: false);
            if (options.SupportedAudioLoraRanks is { } supportedAudioLoraRanks)
                ApplySupportedLoraRanks(s, supportedAudioLoraRanks, audio: true);

            nint enginePtr;
            unsafe
            {
                status = LiteRtLmNative.litert_lm_engine_create(s, &enginePtr);
            }
            if (status != LiteRtStatusCode.Ok)
                throw NativeError.Exception(
                    status,
                    "litert_lm_engine_create failed.",
                    // The YNNPACK note fits only the failure it explains: UNIMPLEMENTED (or no reason) on a
                    // library without the kernels, which is every official one but linux-arm64.
                    // The speculative-decoding note fits only a failure about the drafter.
                    report => options.EnableYnnpack == true
                              && (report is null || report.Value.Code == LiteRtStatusCode.Unimplemented)
                              && !(OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
                        ? YnnpackUnavailableHint
                        : options.EnableSpeculativeDecoding && IsMissingDrafterFailure(report?.Message)
                            ? MissingDrafterHint : null,
                    fallbackHint: "The runtime reported no reason (see the native stderr). Common causes: a " +
                    "corrupt or incomplete model file, or a backend the model does not support: some published " +
                    ".litertlm files carry a backend constraint (for example GPU-only) and refuse to load on CPU.");

            handle = new EngineHandle(enginePtr, options.AllowMultipleEngines);
            return new LiteRtEngine(
                handle,
                isMultimodal: options.VisionBackend is not null || options.AudioBackend is not null,
                maxNumTokens: options.MaxNumTokens,
                benchmarkEnabled: options.EnableBenchmark);
        }
        catch
        {
            // Once the handle exists it owns the slot: disposing it deletes the native engine, then frees the
            // slot. Before that, release the slot here so a corrected retry can load.
            if (handle is not null)
                handle.Dispose();
            else
                EngineLiveness.Default.Release(options.AllowMultipleEngines);
            throw;
        }
    }

    /// <summary>Appended to a failed engine creation when <see cref="LiteRtEngineOptions.EnableYnnpack"/> is
    /// set: the official libraries carry the YNNPACK kernels only for linux-arm64, and since v0.18.0 the
    /// other platforms reject the flag at engine creation (UNIMPLEMENTED) instead of ignoring it.</summary>
    internal const string YnnpackUnavailableHint =
        "EnableYnnpack is set: the official native libraries carry the YNNPACK kernels only for linux-arm64, " +
        "and on other platforms engine creation fails with UNIMPLEMENTED. Leave EnableYnnpack unset there.";

    /// <summary>Appended to a failed engine creation with <see cref="LiteRtEngineOptions.EnableSpeculativeDecoding"/>
    /// set when the runtime's reason is about the drafter: a model without one fails with NOT_FOUND
    /// ("tf_lite_mtp_drafter not found in the model") or, without an embedding lookup, a bare RET_CHECK.</summary>
    internal const string MissingDrafterHint =
        "EnableSpeculativeDecoding is set, and speculative decoding needs a model that ships a " +
        "Multi-Token-Prediction drafter (LiteRtModelInfo.SupportsSpeculativeDecoding tells before loading). " +
        "Turn it off for this model.";

    /// <summary>Whether a native reason points at the speculative-decoding drafter or what it needs.</summary>
    internal static bool IsMissingDrafterFailure(string? nativeMessage) =>
        nativeMessage is not null
        && (nativeMessage.Contains("mtp_drafter", StringComparison.OrdinalIgnoreCase)
            || nativeMessage.Contains("embedding_lookup", StringComparison.OrdinalIgnoreCase)
            || nativeMessage.Contains("Speculative decoding", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Marshals a supported-LoRA-ranks list to the native <c>const int*</c>/count setter (text or audio)
    /// and surfaces a failed status as a <see cref="LiteRtException"/>. The list is validated non-empty
    /// with positive elements by <see cref="LiteRtEngineOptions.SupportedLoraRanks"/> at init time.
    /// </summary>
    private static unsafe void ApplySupportedLoraRanks(nint settings, IReadOnlyList<int> ranks, bool audio)
    {
        int[] arr = ranks as int[] ?? [.. ranks];
        LiteRtStatusCode status;
        fixed (int* p = arr)
            status = audio
                ? LiteRtLmNative.litert_lm_engine_settings_set_supported_audio_lora_ranks(settings, p, (nuint)arr.Length)
                : LiteRtLmNative.litert_lm_engine_settings_set_supported_lora_ranks(settings, p, (nuint)arr.Length);
        NativeError.ThrowIfFailed(
            status,
            audio
                ? nameof(LiteRtLmNative.litert_lm_engine_settings_set_supported_audio_lora_ranks)
                : nameof(LiteRtLmNative.litert_lm_engine_settings_set_supported_lora_ranks),
            hint: audio ? "Audio LoRA ranks require an audio executor (set AudioBackend)." : "Check the provided ranks.");
    }

    /// <summary>Creates a new stateful conversation from this engine.</summary>
    /// <remarks>
    /// <b>This call is subject to the engine's serializer.</b> The native conversation is created through
    /// the engine, so it waits for a generation that is in flight on any conversation of this engine to
    /// finish first — measured at 46 s (gemma-4 E2B, CPU) behind a 1000-token reply, with no way to cancel
    /// the wait. Create the conversations you need before starting a long generation, or accept the wait.
    /// (The same applies to <see cref="LiteRtConversation.TokenCount"/>; <see cref="LiteRtEngine.Tokenize"/>
    /// and disposing an idle conversation are unaffected.)
    /// </remarks>
    public LiteRtConversation CreateConversation(LiteRtConversationOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return LiteRtConversation.Create(this, options, _isMultimodal);
    }

    /// <summary>
    /// Tokenizes <paramref name="text"/> with the model's own tokenizer and returns the token ids,
    /// without running inference. Use it to measure a prompt's exact token cost or to budget a
    /// conversation against <see cref="LiteRtEngineOptions.MaxNumTokens"/> before sending. The ids are
    /// the raw tokenizer output (no chat template is applied); pair with <see cref="Detokenize"/> for
    /// the round trip.
    /// </summary>
    /// <exception cref="LiteRtException">The native tokenizer call failed.</exception>
    public int[] Tokenize(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        return TokenizeIds(text);
    }

    /// <summary>The body of <see cref="Tokenize"/>, without the disposed check (see <see cref="CountTokens"/>).</summary>
    private unsafe int[] TokenizeIds(string text)
    {
        using var result = TokenizeNative(text);
        nuint count = TokenCountOf(result);
        if (count == 0)
            return [];

        int* tokensPtr;
        if (LiteRtLmNative.litert_lm_tokenize_result_get_tokens(result.Ptr, &tokensPtr) != LiteRtStatusCode.Ok)
        {
            // Read as "no tokens", like a result without an id array.
            NativeError.Clear();
            return [];
        }
        if (tokensPtr is null)
            return [];

        // The pointer is into the result's internal buffer; copy out before the handle disposes it.
        return new ReadOnlySpan<int>(tokensPtr, checked((int)count)).ToArray();
    }

    /// <summary>Counts <paramref name="text"/>'s tokens without materializing the id array — the native
    /// result already carries the count, so this skips the managed copy <see cref="Tokenize"/> pays.
    /// Used by the KV overflow guard on every measured send. It does not check that this object is disposed:
    /// the calling conversation holds a reference on the native engine, so the guard keeps working on a
    /// conversation that outlives <see cref="Dispose"/>.</summary>
    /// <exception cref="LiteRtException">The native tokenizer call failed.</exception>
    internal int CountTokens(string text)
    {
        using var result = TokenizeNative(text);
        return checked((int)TokenCountOf(result));
    }

    /// <summary>Runs the native tokenizer; the caller disposes the result.</summary>
    private unsafe TokenizeResultHandle TokenizeNative(string text)
    {
        nint resultPtr;
        NativeError.ThrowIfFailed(
            LiteRtLmNative.litert_lm_engine_tokenize(_engine.Ptr, text, &resultPtr),
            nameof(LiteRtLmNative.litert_lm_engine_tokenize));
        return new TokenizeResultHandle(resultPtr);
    }

    private static unsafe nuint TokenCountOf(TokenizeResultHandle result)
    {
        nuint count;
        NativeError.ThrowIfFailed(
            LiteRtLmNative.litert_lm_tokenize_result_get_num_tokens(result.Ptr, &count),
            nameof(LiteRtLmNative.litert_lm_tokenize_result_get_num_tokens));
        return count;
    }

    /// <summary>
    /// Detokenizes token ids back to text with the model's tokenizer — the inverse of
    /// <see cref="Tokenize"/>. An empty span returns the empty string without calling native.
    /// </summary>
    /// <exception cref="LiteRtException">The native detokenizer call failed (e.g. an out-of-range id).</exception>
    public string Detokenize(ReadOnlySpan<int> tokens)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return DetokenizeIds(tokens);
    }

    /// <summary>The body of <see cref="Detokenize"/>, without the disposed check (see <see cref="CountTokens"/>).</summary>
    private unsafe string DetokenizeIds(ReadOnlySpan<int> tokens)
    {
        // Required, not only a shortcut: fixed over an empty span yields a null pointer, which the runtime
        // rejects even with a zero count.
        if (tokens.IsEmpty)
            return string.Empty;

        nint resultPtr;
        LiteRtStatusCode status;
        fixed (int* p = tokens)
            status = LiteRtLmNative.litert_lm_engine_detokenize(_engine.Ptr, p, (nuint)tokens.Length, &resultPtr);
        NativeError.ThrowIfFailed(status, nameof(LiteRtLmNative.litert_lm_engine_detokenize));

        using var result = new DetokenizeResultHandle(resultPtr);
        nint strPtr;
        if (LiteRtLmNative.litert_lm_detokenize_result_get_string(result.Ptr, &strPtr) != LiteRtStatusCode.Ok)
        {
            // Read as an empty string, like a result without text.
            NativeError.Clear();
            return string.Empty;
        }
        return Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
    }

    /// <summary>
    /// The model's configured start (BOS) token, or <c>null</c> when the model declares none. Reported
    /// either as a literal string or a token-id sequence — see <see cref="LiteRtTokenUnion.Kind"/>.
    /// </summary>
    /// <exception cref="LiteRtException">The runtime could not report the token's kind.</exception>
    public unsafe LiteRtTokenUnion? GetStartToken()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint ptr;
        if (LiteRtLmNative.litert_lm_engine_get_start_token(_engine.Ptr, &ptr) != LiteRtStatusCode.Ok)
        {
            // Read as "none declared", the value the binding has always returned for a failed read.
            NativeError.Clear();
            return null;
        }
        if (ptr == nint.Zero)
            return null;

        // The start token is caller-owned: delete it once its data is copied out.
        using var handle = new TokenUnionHandle(ptr);
        return LiteRtTokenUnion.FromNative(handle.Ptr);
    }

    /// <summary>
    /// The model's configured stop (EOS) tokens, or an empty list when none. Generation halts when the
    /// model emits any of these; each is a literal string or a token-id sequence — see
    /// <see cref="LiteRtTokenUnion.Kind"/>.
    /// </summary>
    /// <exception cref="LiteRtException">The runtime could not report how many stop tokens there are, or the
    /// kind of one of them.</exception>
    public unsafe IReadOnlyList<LiteRtTokenUnion> GetStopTokens()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint ptr;
        if (LiteRtLmNative.litert_lm_engine_get_stop_tokens(_engine.Ptr, &ptr) != LiteRtStatusCode.Ok)
        {
            // Read as "none declared", the value the binding has always returned for a failed read.
            NativeError.Clear();
            return [];
        }
        if (ptr == nint.Zero)
            return [];

        using var handle = new TokenUnionsHandle(ptr);
        nuint count;
        NativeError.ThrowIfFailed(
            LiteRtLmNative.litert_lm_token_unions_get_num_tokens(handle.Ptr, &count),
            nameof(LiteRtLmNative.litert_lm_token_unions_get_num_tokens));
        if (count == 0)
            return [];

        var stops = new List<LiteRtTokenUnion>(checked((int)count));
        for (nuint i = 0; i < count; i++)
        {
            nint unionPtr;
            if (LiteRtLmNative.litert_lm_token_unions_get_token_at(handle.Ptr, i, &unionPtr) != LiteRtStatusCode.Ok)
            {
                // Skipped, as an element the runtime does not hand out has always been.
                NativeError.Clear();
                continue;
            }
            if (unionPtr == nint.Zero)
                continue;
            // get_token_at lends an element the collection owns: read it while the collection is alive and
            // never delete it (the collection's delete frees it).
            stops.Add(LiteRtTokenUnion.FromNative(unionPtr));
        }
        return stops;
    }

    /// <summary>Disposes the engine, freeing the native model weights and releasing the process-wide
    /// one-engine slot so another model can be loaded. Dispose every conversation first: while one of them is
    /// alive, the weights and the slot stay in use, <see cref="Load"/> refuses another engine unless both opted
    /// in, and the last conversation releases them when it is disposed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Disposing the handle deletes the native engine and frees the one-engine slot
        // (EngineHandle.ReleaseHandle → EngineLiveness.Default.Release), so a new engine can be loaded. A
        // conversation still alive holds a reference on the handle and defers both until it is disposed.
        _engine.Dispose();
    }
}
