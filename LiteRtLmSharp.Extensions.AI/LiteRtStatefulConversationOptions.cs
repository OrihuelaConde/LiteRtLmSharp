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
/// are <b>ignored</b>; only options that map to native per-send settings still apply —
/// <see cref="ChatOptions.MaxOutputTokens"/>, <see cref="ChatOptions.FrequencyPenalty"/> /
/// <see cref="ChatOptions.PresencePenalty"/>, and a JSON-schema <see cref="ChatOptions.ResponseFormat"/>
/// (which additionally requires the conversation to have carried a schema on its <b>first</b> call, or the
/// client's options template to set <c>ConstraintProvider</c> — otherwise the continuation is rejected with
/// <see cref="ArgumentException"/> and the conversation stays resumable). A system message on a continuation
/// throws (the preface cannot be rewritten). To change any fixed setting, start a new conversation (omit
/// <see cref="ChatOptions.ConversationId"/>).
/// </para>
/// <para>
/// <b>Lifetime and eviction.</b> Live conversations are held in an LRU cache bounded by
/// <see cref="MaxLiveConversations"/>. Creating a new conversation beyond that cap evicts and disposes the
/// least-recently-used one; a request whose <see cref="ChatOptions.ConversationId"/> was evicted (or was
/// never issued by this client) throws <see cref="ArgumentException"/>. Disposing the client disposes all
/// live conversations. There is no time-based expiry in this mode — size the cap for your concurrency.
/// (Multiple live conversations require native LiteRT-LM v0.15.0+: earlier runtimes silently lost a
/// suspended conversation's state when another advanced, and this mode was hard-limited to one live
/// conversation until the fix shipped.)
/// </para>
/// <para>
/// <b>Token usage.</b> <see cref="ChatResponse.Usage"/>'s <see cref="UsageDetails.TotalTokenCount"/> is the
/// conversation's cumulative KV-cache size, so across a stateful thread it grows with each turn (it now spans
/// calls rather than resetting per call as in the stateless mode).
/// </para>
/// <para>
/// <b>Context limit.</b> Because the conversation grows across calls, a long thread (or tool loop) eventually
/// approaches the engine's <see cref="LiteRtEngineOptions.MaxNumTokens"/> — watch
/// <see cref="UsageDetails.TotalTokenCount"/> against it. Load the engine with an <b>explicit</b>
/// <c>MaxNumTokens</c> to arm the binding's KV overflow guard: replies are clamped to the remaining context
/// and a send on a full conversation throws <see cref="LiteRtContextOverflowException"/> instead of
/// corrupting the native runtime (unguarded, the overflow crashes the process on a later call). A reply that
/// filled the context carries <see cref="ChatFinishReason.Length"/> <b>in the same turn</b> (deliberately
/// overriding <see cref="ChatFinishReason.ToolCalls"/> — a full conversation cannot be continued, so treat
/// <c>Length</c> as "this thread is over"). A "message doesn't fit" rejection happens before any native
/// work and <b>keeps the live conversation resumable</b> — retry the id with a shorter message; a "context
/// is full" rejection is terminal and evicts it like a cancelled one — resuming its id then throws
/// <see cref="ArgumentException"/>; start a new conversation, summarizing or trimming what must carry over.
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
    /// <remarks>
    /// <b>Each live conversation holds its own native KV cache</b>, sized against the engine's
    /// <see cref="LiteRtEngineOptions.MaxNumTokens"/> — the cap is a memory ceiling, not just a count. On
    /// memory-constrained (mobile) devices size it deliberately: 8 conversations on a 4096-token context
    /// hold up to 8 full KV caches resident. Lower it (e.g. 1-2) where memory is tighter than concurrency.
    /// </remarks>
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
