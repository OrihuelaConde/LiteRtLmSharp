using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;

namespace LiteRtLmSharp.SemanticKernel;

/// <summary>
/// A Semantic Kernel <see cref="IChatCompletionService"/> backed by a LiteRtLmSharp on-device model.
/// Wraps one <see cref="LiteRtEngine"/> and answers chat completions by rebuilding a fresh, stateless
/// <see cref="LiteRtConversation"/> per call (history restored via prefill, the final user turn sent).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stateless by design.</b> Semantic Kernel hands the full <see cref="ChatHistory"/> on every call, so
/// the service re-prefills the prior turns each time rather than holding a long-lived conversation — this
/// keeps SK's history and the model's KV cache from diverging. The cost is an O(history) prefill per turn;
/// for very long chats prefer the native <see cref="LiteRtConversation"/> API directly.
/// </para>
/// <para>
/// <b>Serialized.</b> Calls are serialized with an internal gate: LiteRtLmSharp allows only one live
/// engine per process and conversations are not thread-safe. Concurrent SK calls queue rather than run in
/// parallel.
/// </para>
/// <para>
/// <b>Scope.</b> Function calling through Semantic Kernel (<c>FunctionChoiceBehavior</c>) is not bridged by
/// the connector yet — that setting is currently ignored — but it is planned. LiteRtLmSharp already
/// supports function calling through its native tools API; this connector will grow a Semantic Kernel
/// auto-invoke path. The reasoning ("thinking") trace is never included in the returned content; enable it
/// via <see cref="LiteRtPromptExecutionSettings.EnableThinking"/> to influence the answer only.
/// </para>
/// </remarks>
public sealed class LiteRtChatCompletionService : IChatCompletionService, IDisposable
{
    private readonly LiteRtEngine _engine;
    private readonly LiteRtPromptExecutionSettings? _defaultSettings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, object?> _attributes = new();
    private readonly IReadOnlyDictionary<string, object?> _attributesView;
    private bool _disposed;

    /// <summary>
    /// Creates a chat-completion service over an already-loaded <paramref name="engine"/>. The service does
    /// not own the engine: dispose the engine yourself (after the service). To have a container manage the
    /// engine's lifetime instead, register from <see cref="LiteRtEngineOptions"/> via
    /// <c>AddLiteRtChatCompletion</c>.
    /// </summary>
    /// <param name="engine">The loaded LiteRtLmSharp engine. Must outlive this service.</param>
    /// <param name="defaultSettings">Settings used when a call passes none. Optional.</param>
    /// <param name="modelId">Identifier surfaced via <see cref="Attributes"/> (Semantic Kernel's
    /// <c>ModelId</c>). Optional — defaults to unset.</param>
    public LiteRtChatCompletionService(
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
    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LiteRtPromptExecutionSettings settings = LiteRtPromptExecutionSettings.FromExecutionSettings(executionSettings ?? _defaultSettings);
        (IReadOnlyList<LiteRtMessage> history, string userText) = LiteRtChatMapping.Split(chatHistory);
        LiteRtConversationOptions options = BuildOptions(history, settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using LiteRtConversation conv = _engine.CreateConversation(options);
            // Send is a blocking native call; offload it so the async contract holds and the gate wait +
            // pre-call cancellation are honored. Note this does NOT interrupt generation mid-flight — the
            // native Send observes no token. For cooperative mid-generation cancellation use the streaming
            // overload (it flows tokens through the channel and calls cancel_process).
            LiteRtResponse response = await Task.Run(() => conv.Send(userText), cancellationToken).ConfigureAwait(false);
            var content = new ChatMessageContent(
                AuthorRole.Assistant, response.Text ?? string.Empty, modelId: ModelId, innerContent: response);
            return [content];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LiteRtPromptExecutionSettings settings = LiteRtPromptExecutionSettings.FromExecutionSettings(executionSettings ?? _defaultSettings);
        (IReadOnlyList<LiteRtMessage> history, string userText) = LiteRtChatMapping.Split(chatHistory);
        LiteRtConversationOptions options = BuildOptions(history, settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LiteRtConversation? conv = null;
        try
        {
            conv = _engine.CreateConversation(options);
            await foreach (LiteRtStreamChunk chunk in conv.SendMessageStreamingAsync(userText, cancellationToken).ConfigureAwait(false))
            {
                // Emit only answer-text deltas as content. The reasoning ("thinking") trace is dropped so the
                // assistant message stays clean; tool-call chunks never occur (this conversation has no tools).
                if (chunk.Kind == LiteRtStreamChunkKind.Answer && chunk.Text.Length > 0)
                    yield return new StreamingChatMessageContent(AuthorRole.Assistant, chunk.Text, innerContent: chunk, modelId: ModelId);
            }
        }
        finally
        {
            conv?.Dispose();
            _gate.Release();
        }
    }

    // Internal + static so the mapping can be unit-tested without a model. The chat path deliberately does
    // NOT set SystemMessage — the system prompt travels as a System-role message in the history.
    internal static LiteRtConversationOptions BuildOptions(IReadOnlyList<LiteRtMessage> history, LiteRtPromptExecutionSettings settings) => new()
    {
        History = history.Count > 0 ? history : null,
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
