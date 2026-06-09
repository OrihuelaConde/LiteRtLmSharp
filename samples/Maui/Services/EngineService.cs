using LiteLMSharp;

namespace LiteLMSharp.SampleMaui.Services;

/// <summary>
/// Holds the single <see cref="LiteRtEngine"/> for the app. LiteRT-LM's native environment
/// initializes once per process, so only ONE model can be loaded per app run — switching
/// models requires restarting the app. The UI surfaces this constraint.
/// </summary>
public sealed class EngineService
{
    public const int ContextTokens = 4096;

    public LiteRtEngine? Engine { get; private set; }
    public ModelInfo? LoadedModel { get; private set; }
    public bool IsLoaded => Engine is not null;

    /// <summary>Raised after a model finishes loading (on the thread that loaded it).</summary>
    public event Action? Loaded;

    public async Task LoadAsync(ModelInfo model, string modelPath, string backend)
    {
        if (IsLoaded)
            throw new InvalidOperationException("A model is already loaded. Restart the app to switch models.");

        LiteRtEngine.SetMinLogLevel(3);
        // Engine creation is heavy (GBs of weights) — never on the UI thread.
        Engine = await Task.Run(() => LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = modelPath,
            Backend = backend,
            MaxNumTokens = ContextTokens,
        }));
        LoadedModel = model;
        Loaded?.Invoke();
    }

    public LiteRtConversation NewConversation() =>
        Engine?.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = "You are a concise, helpful assistant.",
            Sampler = new SamplerParams { Type = SamplerType.TopP, TopK = 40, TopP = 0.95f, Temperature = 0.8f },
        })
        ?? throw new InvalidOperationException("No model loaded.");
}
