using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Services;
using Microsoft.SemanticKernel.TextGeneration;

namespace LiteRtLmSharp.SemanticKernel;

/// <summary>
/// A Semantic Kernel <see cref="ITextGenerationService"/> backed by a LiteRtLmSharp on-device model.
/// Each call is a single-shot completion: a fresh, stateless <see cref="LiteRtConversation"/> is created,
/// the prompt is sent, and the reply is returned. Use this for prompt-template (non-chat) scenarios; for
/// multi-turn chat use <see cref="LiteRtChatCompletionService"/>.
/// </summary>
/// <remarks>
/// Calls are serialized with an internal gate (one live engine per process, conversations not thread-safe).
/// An optional system prompt can be supplied via <see cref="LiteRtPromptExecutionSettings.SystemPrompt"/>.
/// The reasoning ("thinking") trace, if enabled, is not included in the returned text.
/// </remarks>
public sealed class LiteRtTextGenerationService : ITextGenerationService, IDisposable
{
    private readonly LiteRtEngine _engine;
    private readonly LiteRtPromptExecutionSettings? _defaultSettings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, object?> _attributes = new();
    private readonly IReadOnlyDictionary<string, object?> _attributesView;
    private bool _disposed;

    /// <summary>Creates a text-generation service over an already-loaded <paramref name="engine"/>. The
    /// service does not own the engine: dispose the engine yourself (after the service). To have a
    /// container manage the engine's lifetime, register from <see cref="LiteRtEngineOptions"/> via
    /// <c>AddLiteRtTextGeneration</c>.</summary>
    /// <param name="engine">The loaded LiteRtLmSharp engine. Must outlive this service.</param>
    /// <param name="defaultSettings">Settings used when a call passes none. Optional.</param>
    /// <param name="modelId">Identifier surfaced via <see cref="Attributes"/>. Optional.</param>
    public LiteRtTextGenerationService(
        LiteRtEngine engine,
        LiteRtPromptExecutionSettings? defaultSettings = null,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _defaultSettings = defaultSettings;
        if (!string.IsNullOrEmpty(modelId))
            _attributes[AIServiceExtensions.ModelIdKey] = modelId;
        // Expose a read-only view so callers can't downcast Attributes and mutate the backing dictionary.
        _attributesView = new ReadOnlyDictionary<string, object?>(_attributes);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?> Attributes => _attributesView;

    private string? ModelId => _attributes.TryGetValue(AIServiceExtensions.ModelIdKey, out object? v) ? v as string : null;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TextContent>> GetTextContentsAsync(
        string prompt,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(prompt);
        LiteRtPromptExecutionSettings settings = LiteRtPromptExecutionSettings.FromExecutionSettings(executionSettings ?? _defaultSettings);
        LiteRtConversationOptions options = BuildOptions(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using LiteRtConversation conv = _engine.CreateConversation(options);
            // Blocking native call offloaded so the gate wait + pre-call cancellation are honored; it does
            // NOT interrupt generation mid-flight (use the streaming overload for that).
            LiteRtResponse response = await Task.Run(() => conv.Send(prompt), cancellationToken).ConfigureAwait(false);
            return [new TextContent(response.Text ?? string.Empty, modelId: ModelId, innerContent: response)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StreamingTextContent> GetStreamingTextContentsAsync(
        string prompt,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(prompt);
        LiteRtPromptExecutionSettings settings = LiteRtPromptExecutionSettings.FromExecutionSettings(executionSettings ?? _defaultSettings);
        LiteRtConversationOptions options = BuildOptions(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LiteRtConversation? conv = null;
        try
        {
            conv = _engine.CreateConversation(options);
            await foreach (LiteRtStreamChunk chunk in conv.SendMessageStreamingAsync(prompt, cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Kind == LiteRtStreamChunkKind.Answer && chunk.Text.Length > 0)
                    yield return new StreamingTextContent(chunk.Text, modelId: ModelId, innerContent: chunk);
            }
        }
        finally
        {
            conv?.Dispose();
            _gate.Release();
        }
    }

    // Internal + static so the mapping can be unit-tested without a model. Unlike chat completion, the
    // text path maps SystemPrompt -> SystemMessage (there is no chat history to carry a System-role turn).
    internal static LiteRtConversationOptions BuildOptions(LiteRtPromptExecutionSettings settings) => new()
    {
        SystemMessage = settings.SystemPrompt,
        Sampler = settings.ToSamplerParams(),
        MaxOutputTokens = settings.MaxTokens ?? 0,
        EnableThinking = settings.EnableThinking,
    };

    /// <summary>Releases the gate. The engine is not disposed here — it is owned by whoever created it
    /// (you, for the engine constructor; the container, when registered from <see cref="LiteRtEngineOptions"/>).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}
