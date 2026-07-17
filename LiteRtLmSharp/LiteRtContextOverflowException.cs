namespace LiteRtLmSharp;

/// <summary>
/// Raised instead of letting a send overflow the conversation's KV cache. The KV cache is sized by
/// <see cref="LiteRtEngineOptions.MaxNumTokens"/> and the native runtime does not police it: a send
/// that grows past the limit writes beyond the allocated cache, corrupting native memory — the typical
/// symptom is a deferred process crash (<c>0xC0000005</c> / <c>0xC0000374</c>) on a LATER call, with
/// nothing pointing back at the overflowing send. The binding throws this instead, before native
/// state is damaged.
/// </summary>
/// <remarks>
/// <para>Thrown by the send methods of <see cref="LiteRtConversation"/> when the engine was loaded
/// with an explicit <see cref="LiteRtEngineOptions.MaxNumTokens"/> and either (a) the conversation is
/// already at the limit (<see cref="TokenCount"/> ≥ <see cref="MaxNumTokens"/>), or (b) the message's
/// measured prefill cost leaves no room to decode a reply. Without an explicit
/// <c>MaxNumTokens</c> the limit is internal to the engine and the guard is off.</para>
/// <para>A conversation that hit the limit cannot continue: dispose it and start a fresh one — with a
/// trimmed <see cref="LiteRtConversationOptions.History"/> if the thread must go on — or reload the
/// engine with a larger <see cref="LiteRtEngineOptions.MaxNumTokens"/>. In the Extensions.AI stateful
/// mode the client evicts the live conversation automatically when this throws; resuming its
/// <c>ConversationId</c> then fails with <see cref="ArgumentException"/> like any evicted id.</para>
/// </remarks>
public sealed class LiteRtContextOverflowException : LiteRtException
{
    /// <summary>Creates the exception. <paramref name="tokenCount"/> is the conversation's KV-cache
    /// size when the send was rejected; <paramref name="maxNumTokens"/> is the engine's configured
    /// context limit.</summary>
    public LiteRtContextOverflowException(string message, int tokenCount, int maxNumTokens)
        : base(message)
    {
        TokenCount = tokenCount;
        MaxNumTokens = maxNumTokens;
    }

    /// <summary>The conversation's KV-cache token count when the send was rejected
    /// (<see cref="LiteRtConversation.TokenCount"/>).</summary>
    public int TokenCount { get; }

    /// <summary>The engine's configured context limit (<see cref="LiteRtEngineOptions.MaxNumTokens"/>).</summary>
    public int MaxNumTokens { get; }
}
