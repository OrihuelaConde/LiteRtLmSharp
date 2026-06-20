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
    private bool _disposed;

    static LiteRtEngine() => NativeLibraryResolver.Initialize();

    // Two LIVE engines in one process is unsupported (creating a second engine while the first
    // is alive hangs in the native layer). Recreating an engine after disposing the previous one
    // works — Google's Edge Gallery switches model/backend exactly this way (engine.close() +
    // new Engine). Guard with a process-wide live count so callers get a clear exception
    // instead of a hang.
    private static int s_liveEngines;

    private LiteRtEngine(EngineHandle engine) => _engine = engine;

    /// <summary>Sets the global minimum log level (0=VERBOSE … 5=FATAL, 1000=SILENT).</summary>
    public static void SetMinLogLevel(int level) => LiteRtLmNative.litert_lm_set_min_log_level(level);

    /// <summary>
    /// Loads a model and creates the engine. Only ONE engine may be alive at a time
    /// (a second concurrent engine hangs in the native layer). To switch model or backend,
    /// dispose every conversation and the engine first, then call <see cref="Load"/> again.
    /// </summary>
    /// <exception cref="ArgumentException">The model file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Another engine is still alive in this process.</exception>
    /// <exception cref="LiteRtException">Native engine creation failed.</exception>
    public static LiteRtEngine Load(LiteRtEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.ModelPath))
            throw new ArgumentException($"Model file not found: {options.ModelPath}", nameof(options));

        if (Interlocked.CompareExchange(ref s_liveEngines, 1, 0) != 0)
            throw new InvalidOperationException(
                "Another LiteRtEngine is still alive in this process; a second concurrent engine " +
                "hangs in the native layer. Dispose the existing engine (and its conversations) " +
                "first, then load the new model.");

        try
        {
            // Passing null for vision/audio leaves that modality unconfigured (NULL = "not set" per
            // the C API). Set them to "cpu"/"gpu" on a multimodal model to enable image/audio input.
            nint settingsPtr = LiteRtLmNative.litert_lm_engine_settings_create(
                options.ModelPath, options.Backend, options.VisionBackend, options.AudioBackend);
            if (settingsPtr == nint.Zero)
                throw new LiteRtException("litert_lm_engine_settings_create returned null.");

            using var settings = new EngineSettingsHandle(settingsPtr);
            if (options.MaxNumTokens > 0)
                LiteRtLmNative.litert_lm_engine_settings_set_max_num_tokens(settings.Ptr, options.MaxNumTokens);
            if (options.MaxNumImages > 0)
                LiteRtLmNative.litert_lm_engine_settings_set_max_num_images(settings.Ptr, options.MaxNumImages);
            if (!string.IsNullOrEmpty(options.CacheDir))
                LiteRtLmNative.litert_lm_engine_settings_set_cache_dir(settings.Ptr, options.CacheDir);
            if (options.EnableBenchmark)
                LiteRtLmNative.litert_lm_engine_settings_enable_benchmark(settings.Ptr);
            if (options.EnableSpeculativeDecoding)
                LiteRtLmNative.litert_lm_engine_settings_set_enable_speculative_decoding(settings.Ptr, true);

            nint enginePtr = LiteRtLmNative.litert_lm_engine_create(settings.Ptr);
            if (enginePtr == nint.Zero)
                throw new LiteRtException(
                    "litert_lm_engine_create returned null. The C API does not expose the reason " +
                    "(it is logged to the native stderr). Common causes: corrupt/incomplete model " +
                    "file, or a backend the model does not support — some published .litertlm " +
                    "files carry a backend constraint (e.g. GPU-only) and refuse to load on CPU.");

            return new LiteRtEngine(new EngineHandle(enginePtr));
        }
        catch
        {
            // Creation failed — allow a corrected retry.
            Interlocked.Exchange(ref s_liveEngines, 0);
            throw;
        }
    }

    /// <summary>Creates a new stateful conversation from this engine.</summary>
    public LiteRtConversation CreateConversation(LiteRtConversationOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return LiteRtConversation.Create(_engine, options);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Dispose();
        // The native engine is gone; a new one may be loaded now.
        Interlocked.Exchange(ref s_liveEngines, 0);
    }
}
