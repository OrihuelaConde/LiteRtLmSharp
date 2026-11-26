namespace LiteRtLmSharp;

/// <summary>
/// Raised instead of letting a send run into the end of the conversation's KV cache, which is sized by
/// <see cref="LiteRtEngineOptions.MaxNumTokens"/>. The binding throws it before the conversation is lost.
/// </summary>
/// <remarks>
/// <para>Without the guard, the runtime behaves as follows (LiteRT-LM v0.20.0, gemma-4 E2B):</para>
/// <list type="bullet">
/// <item>It stops a reply at the limit (CPU and GPU).</item>
/// <item>When the context then stays full, it rejects the next send with <c>RESOURCE_EXHAUSTED</c> (CPU and
/// GPU). A reply with thinking leaves room, because the runtime removes the thinking from the KV cache
/// (CPU).</item>
/// <item>A send whose prefill does not fit, or lands in the last free entries, is rejected
/// (<c>RESOURCE_EXHAUSTED</c> or <c>FAILED_PRECONDITION</c>), and every later send on that conversation fails
/// (CPU and GPU).</item>
/// </list>
/// <para>Thrown by the send methods of <see cref="LiteRtConversation"/> when the engine was loaded
/// with an explicit <see cref="LiteRtEngineOptions.MaxNumTokens"/> and either (a) the conversation's
/// context is full (<see cref="LiteRtConversation.IsContextFull"/>: fewer than 128 entries left, or an earlier
/// reply was detected to have filled it), which includes every send on an engine whose
/// <c>MaxNumTokens</c> is under 128, or (b) the
/// message's measured prefill cost leaves no room to decode a reply. Without an explicit
/// <c>MaxNumTokens</c> the limit is internal to the engine and the guard is off.</para>
/// <para>A conversation that hit the limit cannot continue: dispose it and start a fresh one — with a
/// trimmed <see cref="LiteRtConversationOptions.History"/> if the thread must go on — or reload the
/// engine with a larger <see cref="LiteRtEngineOptions.MaxNumTokens"/>. In the Extensions.AI stateful
/// mode a "message doesn't fit" rejection keeps the live conversation, and a "context is full" rejection
/// evicts it, so resuming its <c>ConversationId</c> fails with <see cref="ArgumentException"/> like any evicted
/// id. The exception is a send of tool results whose turn has a checkpoint with room: the turn rolls back and the id
/// stays valid.</para>
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
