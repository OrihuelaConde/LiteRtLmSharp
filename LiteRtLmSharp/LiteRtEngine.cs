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

    // Two LIVE engines in one process is unsupported (creating a second engine while the first
    // is alive hangs in the native layer). Recreating an engine after disposing the previous one
    // works — Google's Edge Gallery switches model/backend exactly this way (engine.close() +
    // new Engine). The process-wide gate (EngineLiveness) is acquired here and released when the
    // native engine is destroyed (EngineHandle.ReleaseHandle), so a forgotten/GC'd engine also frees
    // the slot — callers get a clear exception instead of a hang. NOTE: every conversation holds a
    // strong reference to its engine (it needs the tokenizer and context limit for the KV overflow
    // guard — and finalizing an engine whose native conversations are still alive would be a
    // use-after-free anyway), so the GC recovery only applies once no conversation objects remain
    // reachable either. Disposing conversations and the engine remains the contract.

    private LiteRtEngine(EngineHandle engine, bool isMultimodal, int maxNumTokens)
    {
        _engine = engine;
        _isMultimodal = isMultimodal;
        MaxNumTokens = maxNumTokens;
    }

    /// <summary>The native engine handle, for conversations spawned from this engine.</summary>
    internal EngineHandle Handle => _engine;

    /// <summary>The context limit the engine was loaded with (<see cref="LiteRtEngineOptions.MaxNumTokens"/>);
    /// 0 when the caller left the engine default, whose value the C API does not expose — the KV overflow
    /// guard is only active when this is known.</summary>
    internal int MaxNumTokens { get; }

    /// <summary>Sets the global minimum log level (0=VERBOSE … 5=FATAL, 1000=SILENT).</summary>
    /// <exception cref="DllNotFoundException">The native LiteRT-LM library could not be loaded; the
    /// message names the fix (the missing <c>LiteRtLmSharp.runtime.&lt;rid&gt;</c> package, or a system
    /// prerequisite such as the VC++ Redistributable on Windows).</exception>
    public static void SetMinLogLevel(int level) => LiteRtLmNative.litert_lm_set_min_log_level(level);

    /// <summary>
    /// Loads a model and creates the engine. Only ONE engine may be alive at a time
    /// (a second concurrent engine hangs in the native layer). To switch model or backend,
    /// dispose every conversation and the engine first, then call <see cref="Load"/> again.
    /// </summary>
    /// <exception cref="ArgumentException">The model file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Another engine is still alive in this process.</exception>
    /// <exception cref="DllNotFoundException">The native LiteRT-LM library could not be loaded; the
    /// message names the fix (the missing <c>LiteRtLmSharp.runtime.&lt;rid&gt;</c> package, or a system
    /// prerequisite such as the VC++ Redistributable on Windows).</exception>
    /// <exception cref="LiteRtException">Native engine creation failed.</exception>
    public static LiteRtEngine Load(LiteRtEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.ModelPath))
            throw new ArgumentException(
                "LiteRtEngineOptions.ModelPath must be set — it is the only model source in this release.",
                nameof(options));
        if (!File.Exists(options.ModelPath))
            throw new ArgumentException($"Model file not found: {options.ModelPath}", nameof(options));

        if (!EngineLiveness.TryAcquire())
            throw new InvalidOperationException(
                "Another LiteRtEngine is still alive in this process; a second concurrent engine " +
                "hangs in the native layer. Dispose the existing engine (and its conversations) " +
                "first, then load the new model.");

        try
        {
            // Passing null for vision/audio leaves that modality unconfigured (NULL = "not set" per
            // the C API). Set a LiteRtBackend on a multimodal model to enable image/audio input.
            nint settingsPtr = LiteRtLmNative.litert_lm_engine_settings_create(
                options.ModelPath, options.Backend.Value, options.VisionBackend?.Value, options.AudioBackend?.Value);
            if (settingsPtr == nint.Zero)
                throw new LiteRtException("litert_lm_engine_settings_create returned null.");

            using var settings = new EngineSettingsHandle(settingsPtr);
            if (options.MaxNumTokens > 0)
                LiteRtLmNative.litert_lm_engine_settings_set_max_num_tokens(settings.Ptr, options.MaxNumTokens);
            if (options.MaxNumImages > 0)
                LiteRtLmNative.litert_lm_engine_settings_set_max_num_images(settings.Ptr, options.MaxNumImages);
            if (options.Cache.NativeValue is { } cacheDir)
                LiteRtLmNative.litert_lm_engine_settings_set_cache_dir(settings.Ptr, cacheDir);
            if (options.EnableBenchmark)
                LiteRtLmNative.litert_lm_engine_settings_enable_benchmark(settings.Ptr);
            if (options.EnableSpeculativeDecoding)
                LiteRtLmNative.litert_lm_engine_settings_set_enable_speculative_decoding(settings.Ptr, true);
            if (options.ParallelFileSectionLoading is { } parallelLoading)
                LiteRtLmNative.litert_lm_engine_settings_set_parallel_file_section_loading(settings.Ptr, parallelLoading);
            if (options.ActivationDataType is { } activationType)
                LiteRtLmNative.litert_lm_engine_settings_set_activation_data_type(settings.Ptr, (int)activationType);
            if (options.PrefillChunkSize > 0)
                LiteRtLmNative.litert_lm_engine_settings_set_prefill_chunk_size(settings.Ptr, options.PrefillChunkSize);
            if (options.BenchmarkPrefillTokens > 0)
                LiteRtLmNative.litert_lm_engine_settings_set_num_prefill_tokens(settings.Ptr, options.BenchmarkPrefillTokens);
            if (options.BenchmarkDecodeTokens > 0)
                LiteRtLmNative.litert_lm_engine_settings_set_num_decode_tokens(settings.Ptr, options.BenchmarkDecodeTokens);
            if (options.NumThreads is { } numThreads)
                LiteRtLmNative.litert_lm_engine_settings_set_num_threads(settings.Ptr, numThreads);
            if (options.AudioNumThreads is { } audioNumThreads)
                LiteRtLmNative.litert_lm_engine_settings_set_audio_num_threads(settings.Ptr, audioNumThreads);
            if (options.GpuDecodeStepsPerSync is { } gpuSteps)
                LiteRtLmNative.litert_lm_engine_settings_set_gpu_decode_steps_per_sync(settings.Ptr, gpuSteps);
            if (options.GpuWaitForWeightUploads is { } gpuWait)
                LiteRtLmNative.litert_lm_engine_settings_set_gpu_wait_for_weight_uploads(settings.Ptr, gpuWait);
            if (options.UseRingbuffersLocalAttention is { } ringbuffers)
                LiteRtLmNative.litert_lm_engine_settings_set_use_ringbuffers_local_attention(settings.Ptr, ringbuffers);
            if (options.LoraRank is { } loraRank)
                LiteRtLmNative.litert_lm_engine_settings_set_lora_rank(settings.Ptr, loraRank);
            if (options.AudioLoraRank is { } audioLoraRank)
                LiteRtLmNative.litert_lm_engine_settings_set_audio_lora_rank(settings.Ptr, audioLoraRank);
            if (options.SupportedLoraRanks is { } supportedLoraRanks)
                ApplySupportedLoraRanks(settings.Ptr, supportedLoraRanks, audio: false);
            if (options.SupportedAudioLoraRanks is { } supportedAudioLoraRanks)
                ApplySupportedLoraRanks(settings.Ptr, supportedAudioLoraRanks, audio: true);

            nint enginePtr = LiteRtLmNative.litert_lm_engine_create(settings.Ptr);
            if (enginePtr == nint.Zero)
                throw new LiteRtException(
                    "litert_lm_engine_create returned null. The C API does not expose the reason " +
                    "(it is logged to the native stderr). Common causes: corrupt/incomplete model " +
                    "file, or a backend the model does not support — some published .litertlm " +
                    "files carry a backend constraint (e.g. GPU-only) and refuse to load on CPU.");

            return new LiteRtEngine(
                new EngineHandle(enginePtr),
                isMultimodal: options.VisionBackend is not null || options.AudioBackend is not null,
                maxNumTokens: options.MaxNumTokens);
        }
        catch
        {
            // Creation failed before the engine handle took ownership of the slot — release it so a
            // corrected retry can load.
            EngineLiveness.Release();
            throw;
        }
    }

    /// <summary>
    /// Marshals a supported-LoRA-ranks list to the native <c>const int*</c>/count setter (text or audio)
    /// and surfaces a non-zero return as a <see cref="LiteRtException"/>. The list is validated non-empty
    /// with positive elements by <see cref="LiteRtEngineOptions.SupportedLoraRanks"/> at init time.
    /// </summary>
    private static unsafe void ApplySupportedLoraRanks(nint settings, IReadOnlyList<int> ranks, bool audio)
    {
        int[] arr = ranks as int[] ?? [.. ranks];
        int rc;
        fixed (int* p = arr)
            rc = audio
                ? LiteRtLmNative.litert_lm_engine_settings_set_supported_audio_lora_ranks(settings, p, (nuint)arr.Length)
                : LiteRtLmNative.litert_lm_engine_settings_set_supported_lora_ranks(settings, p, (nuint)arr.Length);
        if (rc != 0)
            throw new LiteRtException(
                $"litert_lm_engine_settings_set_supported{(audio ? "_audio" : "")}_lora_ranks failed (returned {rc}). " +
                (audio ? "Audio LoRA ranks require an audio executor (set AudioBackend)." : "Check the provided ranks."));
    }

    /// <summary>Creates a new stateful conversation from this engine.</summary>
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
    /// <exception cref="EntryPointNotFoundException">The native binary predates the tokenizer API.</exception>
    public int[] Tokenize(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        nint resultPtr = LiteRtLmNative.litert_lm_engine_tokenize(_engine.Ptr, text);
        if (resultPtr == nint.Zero)
            throw new LiteRtException("litert_lm_engine_tokenize returned null.");

        using var result = new TokenizeResultHandle(resultPtr);
        nuint count = LiteRtLmNative.litert_lm_tokenize_result_get_num_tokens(result.Ptr);
        if (count == 0)
            return [];

        nint tokensPtr = LiteRtLmNative.litert_lm_tokenize_result_get_tokens(result.Ptr);
        if (tokensPtr == nint.Zero)
            return [];

        var tokens = new int[count];
        // The pointer is into the result's internal buffer; copy out before the handle disposes it.
        Marshal.Copy(tokensPtr, tokens, 0, checked((int)count));
        return tokens;
    }

    /// <summary>Counts <paramref name="text"/>'s tokens without materializing the id array — the native
    /// result already carries the count, so this skips the managed copy <see cref="Tokenize"/> pays.
    /// Used by the KV overflow guard on every measured send.</summary>
    /// <exception cref="LiteRtException">The native tokenizer call failed.</exception>
    internal int CountTokens(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint resultPtr = LiteRtLmNative.litert_lm_engine_tokenize(_engine.Ptr, text);
        if (resultPtr == nint.Zero)
            throw new LiteRtException("litert_lm_engine_tokenize returned null.");

        using var result = new TokenizeResultHandle(resultPtr);
        return checked((int)LiteRtLmNative.litert_lm_tokenize_result_get_num_tokens(result.Ptr));
    }

    /// <summary>
    /// Detokenizes token ids back to text with the model's tokenizer — the inverse of
    /// <see cref="Tokenize"/>. An empty span returns the empty string without calling native.
    /// </summary>
    /// <exception cref="LiteRtException">The native detokenizer call failed (e.g. an out-of-range id).</exception>
    /// <exception cref="EntryPointNotFoundException">The native binary predates the tokenizer API.</exception>
    public string Detokenize(ReadOnlySpan<int> tokens)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (tokens.IsEmpty)
            return string.Empty;

        nint resultPtr;
        unsafe
        {
            fixed (int* p = tokens)
                resultPtr = LiteRtLmNative.litert_lm_engine_detokenize(_engine.Ptr, p, (nuint)tokens.Length);
        }
        if (resultPtr == nint.Zero)
            throw new LiteRtException("litert_lm_engine_detokenize returned null.");

        using var result = new DetokenizeResultHandle(resultPtr);
        nint strPtr = LiteRtLmNative.litert_lm_detokenize_result_get_string(result.Ptr);
        return Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
    }

    /// <summary>
    /// The model's configured start (BOS) token, or <c>null</c> when the model declares none. Reported
    /// either as a literal string or a token-id sequence — see <see cref="LiteRtTokenUnion.Kind"/>.
    /// </summary>
    public LiteRtTokenUnion? GetStartToken()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint ptr = LiteRtLmNative.litert_lm_engine_get_start_token(_engine.Ptr);
        if (ptr == nint.Zero)
            return null;

        using var handle = new TokenUnionHandle(ptr);
        return LiteRtTokenUnion.FromNative(handle.Ptr);
    }

    /// <summary>
    /// The model's configured stop (EOS) tokens, or an empty list when none. Generation halts when the
    /// model emits any of these; each is a literal string or a token-id sequence — see
    /// <see cref="LiteRtTokenUnion.Kind"/>.
    /// </summary>
    public IReadOnlyList<LiteRtTokenUnion> GetStopTokens()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint ptr = LiteRtLmNative.litert_lm_engine_get_stop_tokens(_engine.Ptr);
        if (ptr == nint.Zero)
            return [];

        using var handle = new TokenUnionsHandle(ptr);
        nuint count = LiteRtLmNative.litert_lm_token_unions_get_num_tokens(handle.Ptr);
        if (count == 0)
            return [];

        var stops = new List<LiteRtTokenUnion>(checked((int)count));
        for (nuint i = 0; i < count; i++)
        {
            nint unionPtr = LiteRtLmNative.litert_lm_token_unions_get_token_at(handle.Ptr, i);
            if (unionPtr == nint.Zero)
                continue;
            // get_token_at hands back a NEW token union that the caller must delete.
            using var unionHandle = new TokenUnionHandle(unionPtr);
            stops.Add(LiteRtTokenUnion.FromNative(unionHandle.Ptr));
        }
        return stops;
    }

    /// <summary>Disposes the engine, freeing the native model weights and releasing the process-wide
    /// one-engine slot so another model can be loaded. Dispose every conversation first.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Disposing the handle deletes the native engine and frees the one-engine slot
        // (EngineHandle.ReleaseHandle → EngineLiveness.Release), so a new engine can be loaded.
        _engine.Dispose();
    }
}
