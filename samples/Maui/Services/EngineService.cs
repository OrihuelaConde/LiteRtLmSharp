using LiteRtLmSharp;

namespace LiteRtLmSharp.SampleMaui.Services;

/// <summary>
/// Holds the single <see cref="LiteRtEngine"/> for the app. Only one engine may be ALIVE at a
/// time, but switching model or backend works without restarting: dispose every conversation,
/// dispose the engine, load the new one (same pattern as Google's Edge Gallery).
/// </summary>
public sealed class EngineService
{
    public const int ContextTokens = 4096;

    public LiteRtEngine? Engine { get; private set; }
    public ModelInfo? LoadedModel { get; private set; }
    public string? LoadedBackend { get; private set; }
    public bool LoadedSpeculative { get; private set; }
    public bool IsLoaded => Engine is not null;

    /// <summary>Shared "speculative on/off" label so every tab's header says it the same way.</summary>
    public string SpeculativeLabel => LoadedSpeculative ? "speculative on" : "speculative off";

    /// <summary>Raised after a model finishes loading (on the thread that loaded it).</summary>
    public event Action? Loaded;

    /// <summary>
    /// Raised before the engine is disposed. Handlers MUST finish any in-flight generation and
    /// dispose every conversation they created — disposing the engine while a conversation is
    /// alive (or generating) is not safe.
    /// </summary>
    public event Func<Task>? Unloading;

    private bool _switching;

    /// <summary>Loads a model, first unloading the current one if any.</summary>
    public async Task LoadAsync(ModelInfo model, string modelPath, string backend, bool enableSpeculativeDecoding)
    {
        if (_switching)
            throw new InvalidOperationException("Another model load is already in progress.");
        _switching = true;
        try
        {
            await UnloadAsync();

            LiteRtEngine.SetMinLogLevel(3);
            // Engine creation is heavy (GBs of weights) — never on the UI thread.
            Engine = await Task.Run(() => LiteRtEngine.Load(new LiteRtEngineOptions
            {
                ModelPath = modelPath,
                Backend = backend,
                MaxNumTokens = ContextTokens,
                EnableSpeculativeDecoding = enableSpeculativeDecoding, // MTP drafter → faster decode
                EnableBenchmark = true,                                // gauge shows decode tok/s
                // Speculative decoding on the WebGPU GPU backend needs the disk cache off, or the
                // MTP drafter's shared weight-cache file fails to open ("Access denied") on Windows
                // and the engine fails to load (upstream issue — see docs/speculative-decoding.md).
                CacheDir = enableSpeculativeDecoding && backend == "gpu"
                    ? LiteRtEngineOptions.CacheDisabled : null,
            }));
            LoadedModel = model;
            LoadedBackend = backend;
            LoadedSpeculative = enableSpeculativeDecoding;
            Loaded?.Invoke();
        }
        finally
        {
            _switching = false;
        }
    }

    /// <summary>Disposes the engine (after letting pages release their conversations).</summary>
    public async Task UnloadAsync()
    {
        if (Engine is null)
            return;

        foreach (var handler in Unloading?.GetInvocationList() ?? [])
            await ((Func<Task>)handler)();

        var engine = Engine;
        Engine = null;
        LoadedModel = null;
        LoadedBackend = null;
        LoadedSpeculative = false;
        await Task.Run(engine.Dispose);
    }

    public LiteRtConversation NewConversation() =>
        Engine?.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = "You are a concise, helpful assistant.",
            Sampler = new SamplerParams { Type = SamplerType.TopP, TopK = 40, TopP = 0.95f, Temperature = 0.8f },
        })
        ?? throw new InvalidOperationException("No model loaded.");

    /// <summary>A conversation for the Tools page: tools are fixed per conversation.</summary>
    public LiteRtConversation NewToolConversation(IReadOnlyList<LiteRtTool> tools) =>
        Engine?.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = "You are a helpful assistant. Use the available tools when needed.",
            Tools = tools,
            EnableConstrainedDecoding = true,
            MaxOutputTokens = 256,
        })
        ?? throw new InvalidOperationException("No model loaded.");
}
