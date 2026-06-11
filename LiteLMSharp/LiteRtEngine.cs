using LiteLMSharp.Native;

namespace LiteLMSharp;

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
            nint settingsPtr = LiteRtLmNative.litert_lm_engine_settings_create(
                options.ModelPath, options.Backend, null, null);
            if (settingsPtr == nint.Zero)
                throw new LiteRtException("litert_lm_engine_settings_create returned null.");

            using var settings = new EngineSettingsHandle(settingsPtr);
            if (options.MaxNumTokens > 0)
                LiteRtLmNative.litert_lm_engine_settings_set_max_num_tokens(settings.Ptr, options.MaxNumTokens);

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Dispose();
        // The native engine is gone; a new one may be loaded now.
        Interlocked.Exchange(ref s_liveEngines, 0);
    }
}
