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

    private LiteRtEngine(EngineHandle engine) => _engine = engine;

    /// <summary>Sets the global minimum log level (0=VERBOSE … 5=FATAL, 1000=SILENT).</summary>
    public static void SetMinLogLevel(int level) => LiteRtLmNative.litert_lm_set_min_log_level(level);

    /// <summary>Loads a model and creates the engine.</summary>
    /// <exception cref="ArgumentException">The model file does not exist.</exception>
    /// <exception cref="LiteRtException">Native engine creation failed.</exception>
    public static LiteRtEngine Load(LiteRtEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.ModelPath))
            throw new ArgumentException($"Model file not found: {options.ModelPath}", nameof(options));

        nint settingsPtr = LiteRtLmNative.litert_lm_engine_settings_create(
            options.ModelPath, options.Backend, null, null);
        if (settingsPtr == nint.Zero)
            throw new LiteRtException("litert_lm_engine_settings_create returned null.");

        using var settings = new EngineSettingsHandle(settingsPtr);
        if (options.MaxNumTokens > 0)
            LiteRtLmNative.litert_lm_engine_settings_set_max_num_tokens(settings.Ptr, options.MaxNumTokens);

        nint enginePtr = LiteRtLmNative.litert_lm_engine_create(settings.Ptr);
        if (enginePtr == nint.Zero)
            throw new LiteRtException("litert_lm_engine_create returned null. Check the model path and backend.");

        return new LiteRtEngine(new EngineHandle(enginePtr));
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
    }
}
