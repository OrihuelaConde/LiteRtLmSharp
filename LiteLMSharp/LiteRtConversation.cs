using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using LiteLMSharp.Native;

namespace LiteLMSharp;

/// <summary>
/// A stateful conversation over a <see cref="LiteRtEngine"/>. Handles chat templating
/// internally (mirrors the Gemini Chat APIs). Not thread-safe: serialize calls per instance.
/// </summary>
public sealed class LiteRtConversation : IDisposable
{
    private readonly ConversationHandle _conversation;
    private readonly ConversationConfigHandle? _config;
    private readonly SessionConfigHandle? _sessionConfig;
    private bool _disposed;

    private LiteRtConversation(
        ConversationHandle conversation, ConversationConfigHandle? config, SessionConfigHandle? sessionConfig)
    {
        _conversation = conversation;
        _config = config;
        _sessionConfig = sessionConfig;
    }

    internal static unsafe LiteRtConversation Create(EngineHandle engine, LiteRtConversationOptions? options)
    {
        ConversationConfigHandle? config = null;
        SessionConfigHandle? sessionConfig = null;

        try
        {
            nint configPtr = nint.Zero;
            if (options is not null && (options.SystemMessage is not null || options.Sampler is not null || options.MaxOutputTokens > 0))
            {
                configPtr = LiteRtLmNative.litert_lm_conversation_config_create();
                if (configPtr == nint.Zero)
                    throw new LiteRtException("litert_lm_conversation_config_create returned null.");
                config = new ConversationConfigHandle(configPtr);

                if (options.Sampler is not null || options.MaxOutputTokens > 0)
                {
                    nint sessionPtr = LiteRtLmNative.litert_lm_session_config_create();
                    if (sessionPtr == nint.Zero)
                        throw new LiteRtException("litert_lm_session_config_create returned null.");
                    sessionConfig = new SessionConfigHandle(sessionPtr);

                    if (options.MaxOutputTokens > 0)
                        LiteRtLmNative.litert_lm_session_config_set_max_output_tokens(sessionPtr, options.MaxOutputTokens);

                    if (options.Sampler is { } s)
                    {
                        var native = new LiteRtLmSamplerParams
                        {
                            Type = (LiteRtLmSamplerType)s.Type,
                            TopK = s.TopK,
                            TopP = s.TopP,
                            Temperature = s.Temperature,
                            Seed = s.Seed,
                        };
                        LiteRtLmNative.litert_lm_session_config_set_sampler_params(sessionPtr, &native);
                    }

                    LiteRtLmNative.litert_lm_conversation_config_set_session_config(configPtr, sessionPtr);
                }

                if (options.SystemMessage is not null)
                    LiteRtLmNative.litert_lm_conversation_config_set_system_message(configPtr, BuildSystemMessage(options.SystemMessage));
            }

            nint convPtr = LiteRtLmNative.litert_lm_conversation_create(engine.Ptr, configPtr);
            if (convPtr == nint.Zero)
                throw new LiteRtException("litert_lm_conversation_create returned null.");

            return new LiteRtConversation(new ConversationHandle(convPtr), config, sessionConfig);
        }
        catch
        {
            sessionConfig?.Dispose();
            config?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Tokens currently held in this conversation's KV cache (prefill + decode, accumulated across
    /// turns). When this approaches the engine's <see cref="LiteRtEngineOptions.MaxNumTokens"/> the
    /// context is full and further generation degrades — manage history before that point.
    /// </summary>
    public int TokenCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return LiteRtLmNative.litert_lm_conversation_get_token_count(_conversation.Ptr);
        }
    }

    /// <summary>Sends a user message and returns the full model response (blocking).</summary>
    public string SendMessage(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        nint responsePtr = LiteRtLmNative.litert_lm_conversation_send_message(
            _conversation.Ptr, BuildUserMessage(text), null, nint.Zero);
        if (responsePtr == nint.Zero)
            throw new LiteRtException("litert_lm_conversation_send_message returned null.");

        using var response = new JsonResponseHandle(responsePtr);
        nint strPtr = LiteRtLmNative.litert_lm_json_response_get_string(response.Ptr);
        string json = Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
        return ExtractText(json);
    }

    /// <summary>Sends a user message and streams the response text chunks as they arrive.</summary>
    public async IAsyncEnumerable<string> SendMessageStreamingAsync(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);

        var channel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var state = new StreamState(channel);
        var gcHandle = GCHandle.Alloc(state);

        int rc;
        unsafe
        {
            rc = LiteRtLmNative.litert_lm_conversation_send_message_stream(
                _conversation.Ptr, BuildUserMessage(text), null, nint.Zero,
                &OnStreamChunk, GCHandle.ToIntPtr(gcHandle));
        }

        if (rc != 0)
        {
            if (gcHandle.IsAllocated) gcHandle.Free();
            throw new LiteRtException($"litert_lm_conversation_send_message_stream failed with code {rc}.");
        }

        try
        {
            await foreach (string chunk in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
                LiteRtLmNative.litert_lm_conversation_cancel_process(_conversation.Ptr);
        }
    }

    /// <summary>Unmanaged streaming callback. Recovers state from the GCHandle in <paramref name="callbackData"/>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnStreamChunk(nint callbackData, nint chunk, byte isFinal, nint errorMsg)
    {
        var gcHandle = GCHandle.FromIntPtr(callbackData);
        if (gcHandle.Target is not StreamState state)
            return;

        if (errorMsg != nint.Zero)
        {
            string msg = Marshal.PtrToStringUTF8(errorMsg) ?? "unknown error";
            state.Channel.Writer.TryComplete(new LiteRtException(msg));
        }
        else if (chunk != nint.Zero)
        {
            // Each streaming chunk is a full JSON message ({"content":[{"text":"..."}]}),
            // so extract the text fragment before handing it to the consumer.
            string? piece = Marshal.PtrToStringUTF8(chunk);
            if (!string.IsNullOrEmpty(piece))
            {
                string text = ExtractText(piece);
                if (text.Length > 0)
                    state.Channel.Writer.TryWrite(text);
            }
        }

        if (isFinal != 0)
        {
            state.Channel.Writer.TryComplete();
            if (Interlocked.Exchange(ref state.Freed, 1) == 0)
                gcHandle.Free();
        }
    }

    private sealed class StreamState(Channel<string> channel)
    {
        public readonly Channel<string> Channel = channel;
        public int Freed;
    }

    private static string BuildUserMessage(string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildSystemMessage(string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Extracts <c>content[0].text</c> from a response JSON, falling back to the raw string.</summary>
    private static string ExtractText(string json)
    {
        if (string.IsNullOrEmpty(json))
            return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array
                && content.GetArrayLength() > 0
                && content[0].TryGetProperty("text", out var textEl))
            {
                return textEl.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Not JSON we recognize — return as-is.
        }
        return json;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conversation.Dispose();
        _config?.Dispose();
        _sessionConfig?.Dispose();
    }
}
