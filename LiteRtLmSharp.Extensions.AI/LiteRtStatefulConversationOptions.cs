using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// Opts a <see cref="LiteRtChatClient"/> into <b>stateful conversations</b> — the canonical
/// Microsoft.Extensions.AI stateful-provider contract over LiteRtLmSharp's inherently stateful native
/// <see cref="LiteRtConversation"/>. When supplied (to the constructor or a DI registration), the client
/// keeps the live native conversation alive between calls instead of rebuilding it from the full message
/// list every turn (the default, stateless behavior).
/// </summary>
/// <remarks>
/// <para>
/// <b>The MEAI contract.</b> A stateful provider returns a <see cref="ChatResponse.ConversationId"/>; the
/// caller then sets <see cref="ChatOptions.ConversationId"/> on the next request and sends <b>only the
/// messages the provider has not seen yet</b> (typically just the new user turn). The client resumes the
/// matching live conversation — no history re-prefill — so each turn costs only its own tokens rather than
/// re-processing the whole thread. The id is fixed for the lifetime of the conversation (the same id comes
/// back on every response for that thread).
/// </para>
/// <para>
/// <b>Tool loops become incremental automatically.</b> <c>FunctionInvokingChatClient</c>
/// (<c>UseFunctionInvocation()</c>) detects the non-null <see cref="ChatResponse.ConversationId"/>, clears
/// its accumulated history, and sends only the new function-result message(s) on the next iteration with the
/// id set — so a stateful client turns a multi-round tool loop into a series of incremental sends with no
/// extra work on your part.
/// </para>
/// <para>
/// <b>What is fixed at creation.</b> A live conversation's session settings are chosen when it is first
/// created and cannot change on a continuation: the sampler, thinking mode, tools, constrained decoding, the
/// system message, and any template values are all locked in on the first (no-<see cref="ChatOptions.ConversationId"/>)
/// call. On a continuation request (with a <see cref="ChatOptions.ConversationId"/>) those per-request knobs
/// are <b>ignored</b>; only per-send options that the native runtime resolves per send — currently
/// <see cref="ChatOptions.MaxOutputTokens"/> — still apply. A system message on a continuation throws (the
/// preface cannot be rewritten). To change any fixed setting, start a new conversation (omit
/// <see cref="ChatOptions.ConversationId"/>).
/// </para>
/// <para>
/// <b>Lifetime and eviction.</b> Live conversations are held in an LRU cache bounded by
/// <see cref="MaxLiveConversations"/>. Creating a new conversation beyond that cap evicts and disposes the
/// least-recently-used one; a request whose <see cref="ChatOptions.ConversationId"/> was evicted (or was
/// never issued by this client) throws <see cref="ArgumentException"/>. Disposing the client disposes all
/// live conversations. There is no time-based expiry in this mode — size the cap for your concurrency.
/// </para>
/// <para>
/// <b>Token usage.</b> <see cref="ChatResponse.Usage"/>'s <see cref="UsageDetails.TotalTokenCount"/> is the
/// conversation's cumulative KV-cache size, so across a stateful thread it grows with each turn (it now spans
/// calls rather than resetting per call as in the stateless mode).
/// </para>
/// </remarks>
public sealed record LiteRtStatefulConversationOptions
{
    private readonly int _maxLiveConversations = 8;

    /// <summary>
    /// The maximum number of live native conversations kept alive at once (an LRU cache). Creating a new
    /// conversation beyond this cap evicts and disposes the least-recently-used one, after which a request
    /// carrying that conversation's id throws <see cref="ArgumentException"/>. Must be at least 1. Defaults
    /// to 8.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxLiveConversations
    {
        get => _maxLiveConversations;
        init
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "MaxLiveConversations must be at least 1.");
            _maxLiveConversations = value;
        }
    }
}
