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
    public bool LoadedThinking { get; private set; }
    /// <summary>Whether the engine was loaded with the image encoder enabled.</summary>
    public bool LoadedVision { get; private set; }
    /// <summary>Whether the engine was loaded with the audio encoder enabled.</summary>
    public bool LoadedAudio { get; private set; }
    public bool IsLoaded => Engine is not null;

    /// <summary>Shared "speculative on/off" label so every tab's header says it the same way.</summary>
    public string SpeculativeLabel => LoadedSpeculative ? "speculative on" : "speculative off";

    /// <summary>Shared "thinking on/off" label so every tab's header says it the same way.</summary>
    public string ThinkingLabel => LoadedThinking ? "thinking on" : "thinking off";

    /// <summary>Which input modalities the loaded engine actually accepts — shown in the chat header so
    /// it is obvious whether image/audio attachments will work on this load.</summary>
    public string ModalityLabel => (LoadedVision, LoadedAudio) switch
    {
        (true, true) => "vision+audio",
        (true, false) => "vision",
        (false, true) => "audio",
        _ => "text only",
    };

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
    public async Task LoadAsync(ModelInfo model, string modelPath, string backend, bool enableSpeculativeDecoding, bool enableThinking)
    {
        if (_switching)
            throw new InvalidOperationException("Another model load is already in progress.");
        _switching = true;
        try
        {
            await UnloadAsync();

            // Enable the image/audio encoders when the model is multimodal (the Gemma 4 E-series are);
            // null leaves the modality off (text-only models). Vision runs on the main backend (GPU
            // works). Gemma 4's audio sub-model is CPU-constrained — the model declares "requires one of
            // [cpu]", so audio=gpu fails engine creation on any platform (verified 2026-06-17) — so run
            // audio on CPU whenever the main backend is GPU.
            LiteRtBackend? visionBackend = model.SupportsVision ? LiteRtBackend.Parse(backend) : null;
            LiteRtBackend? audioBackend = model.SupportsAudio
                ? LiteRtBackend.Parse(backend == "gpu" ? "cpu" : backend) : null;

            LiteRtEngine.SetMinLogLevel(3);
            // Engine creation is heavy (GBs of weights) — never on the UI thread.
            Engine = await Task.Run(() => LiteRtEngine.Load(new LiteRtEngineOptions
            {
                ModelPath = modelPath,
                Backend = LiteRtBackend.Parse(backend),
                VisionBackend = visionBackend,
                AudioBackend = audioBackend,
                MaxNumTokens = ContextTokens,
                EnableSpeculativeDecoding = enableSpeculativeDecoding, // MTP drafter → faster decode
                EnableBenchmark = true,                                // gauge shows decode tok/s
                // Default disk cache is safe with speculative decoding on GPU since LiteRT-LM
                // v0.14.0 (the v0.13.1 shared weight-cache mmap failure, upstream #2572, is fixed
                // in the tag and re-verified — see docs/speculative-decoding.md).
                Cache = LiteRtCache.Default,
            }));
            LoadedModel = model;
            LoadedBackend = backend;
            LoadedSpeculative = enableSpeculativeDecoding;
            LoadedThinking = enableThinking;
            LoadedVision = visionBackend is not null;
            LoadedAudio = audioBackend is not null;
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
        LoadedThinking = false;
        LoadedVision = false;
        LoadedAudio = false;
        await Task.Run(engine.Dispose);
    }

    public LiteRtConversation NewConversation() =>
        Engine?.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = "You are a concise, helpful assistant.",
            Sampler = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.TopP, TopK = 40, TopP = 0.95f, Temperature = 0.8f },
            EnableThinking = LoadedThinking,
            // The chat reuses one conversation across turns, so drop the (long) reasoning from the
            // KV cache or it eats the context window on later turns.
            FilterThinkingFromKvCache = LoadedThinking,
        })
        ?? throw new InvalidOperationException("No model loaded.");

    /// <summary>A conversation for the Tools page: tools are fixed per conversation.</summary>
    public LiteRtConversation NewToolConversation(IReadOnlyList<LiteRtTool> tools) =>
        Engine?.CreateConversation(new LiteRtConversationOptions
        {
            SystemMessage = "You are a helpful assistant. Use the available tools when needed.",
            Tools = tools,
            EnableThinking = LoadedThinking, // honor the loaded reasoning choice here too
            FilterThinkingFromKvCache = LoadedThinking,
            EnableConstrainedDecoding = true,
            // Reasoning emits a thinking block before the (constrained) tool call, so give it more
            // room when thinking is on — 256 can be eaten by the trace before the call is produced.
            MaxOutputTokens = LoadedThinking ? 1024 : 256,
        })
        ?? throw new InvalidOperationException("No model loaded.");
}
