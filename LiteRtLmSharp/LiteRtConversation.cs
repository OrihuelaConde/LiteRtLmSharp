using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

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
            bool hasTools = options?.Tools is { Count: > 0 };
            bool needsConfig = options is not null &&
                (options.SystemMessage is not null || options.Sampler is not null ||
                 options.MaxOutputTokens > 0 || hasTools || options.EnableConstrainedDecoding);

            if (needsConfig)
            {
                configPtr = LiteRtLmNative.litert_lm_conversation_config_create();
                if (configPtr == nint.Zero)
                    throw new LiteRtException("litert_lm_conversation_config_create returned null.");
                config = new ConversationConfigHandle(configPtr);

                if (options!.Sampler is not null || options.MaxOutputTokens > 0)
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
                    LiteRtLmNative.litert_lm_conversation_config_set_system_message(configPtr, LiteRtJson.SystemMessage(options.SystemMessage));

                if (hasTools)
                    LiteRtLmNative.litert_lm_conversation_config_set_tools(configPtr, LiteRtJson.Tools(options.Tools!));

                if (options.EnableConstrainedDecoding)
                    LiteRtLmNative.litert_lm_conversation_config_set_enable_constrained_decoding(configPtr, true);
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

    /// <summary>Sends a user message and returns only the text answer (blocking).
    /// For function calling use <see cref="Send"/>, which also surfaces tool calls.</summary>
    public string SendMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Send(text).Text ?? string.Empty;
    }

    /// <summary>Sends a user message and returns the structured response (text or tool calls).</summary>
    public LiteRtResponse Send(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return LiteRtResponse.Parse(SendMessageRaw(LiteRtJson.UserMessage(text)));
    }

    /// <summary>
    /// Sends the results of executed tools back to the model and returns its next response.
    /// Call after a <see cref="LiteRtResponse"/> with <see cref="LiteRtResponse.IsToolCall"/> = true.
    /// </summary>
    public LiteRtResponse SendToolResults(IEnumerable<LiteRtToolResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return LiteRtResponse.Parse(SendMessageRaw(LiteRtJson.ToolResults(results)));
    }

    /// <summary>
    /// Low-level escape hatch: sends a raw message JSON and returns the raw response JSON.
    /// Use when you need full control over the wire format.
    /// </summary>
    public string SendMessageRaw(string messageJson, string? extraContext = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messageJson);

        nint responsePtr = LiteRtLmNative.litert_lm_conversation_send_message(
            _conversation.Ptr, messageJson, extraContext, nint.Zero);
        if (responsePtr == nint.Zero)
            throw new LiteRtException("litert_lm_conversation_send_message returned null.");

        using var response = new JsonResponseHandle(responsePtr);
        nint strPtr = LiteRtLmNative.litert_lm_json_response_get_string(response.Ptr);
        return Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
    }

    /// <summary>
    /// Sends a user message and streams the response text chunks as they arrive (text only;
    /// for tool calls use the blocking <see cref="Send"/>).
    /// <para>
    /// Requires a sound native build: the async decode thread crashed on the interim commit
    /// 032334d8, but works on release tags (verified on v0.13.1) and on community 0.12.0-a.
    /// </para>
    /// </summary>
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
                _conversation.Ptr, LiteRtJson.UserMessage(text), null, nint.Zero,
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
            // The native streaming thread invokes the callback until is_final, which completes the
            // channel. Before freeing the GCHandle (and letting the caller dispose the conversation)
            // we MUST ensure that thread is done — otherwise it dereferences freed state / a deleted
            // conversation and segfaults. If the consumer abandoned early, cancel and then wait.
            if (!channel.Reader.Completion.IsCompleted)
            {
                LiteRtLmNative.litert_lm_conversation_cancel_process(_conversation.Ptr);
                try { await channel.Reader.Completion.ConfigureAwait(false); }
                catch { /* completion may surface the cancel error; ignore here */ }
            }
            if (gcHandle.IsAllocated)
                gcHandle.Free();
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
                string text = LiteRtResponse.Parse(piece).Text ?? string.Empty;
                if (text.Length > 0)
                    state.Channel.Writer.TryWrite(text);
            }
        }

        if (isFinal != 0)
            state.Channel.Writer.TryComplete();
        // NOTE: do NOT free the GCHandle here — the async iterator's finally owns its lifetime and
        // only frees it once the channel is fully completed, so this callback can never run against
        // a freed handle.
    }

    private sealed class StreamState(Channel<string> channel)
    {
        public readonly Channel<string> Channel = channel;
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
