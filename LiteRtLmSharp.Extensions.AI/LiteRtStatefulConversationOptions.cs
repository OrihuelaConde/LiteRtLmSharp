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
/// <b>One live conversation at a time.</b> This mode keeps a <b>single</b> live conversation alive. Starting a
/// new conversation — any call <i>without</i> a <see cref="ChatOptions.ConversationId"/> — <b>replaces</b> the
/// previous one: the replaced conversation is disposed, and a later request that carries its id throws
/// <see cref="ArgumentException"/> (as does an id this client never issued). Disposing the client disposes the
/// live conversation. This limit is deliberate: upstream LiteRT-LM does not yet preserve a suspended
/// conversation's state when another conversation advances, so keeping two live conversations and interleaving
/// them would silently corrupt the parked one's answers. A bounded multi-conversation cache exists internally
/// (the future-ready machinery is in place) and will be enabled once upstream preserves suspended-conversation
/// state — see <c>docs/roadmap.md</c>. There is no public knob to raise the limit.
/// </para>
/// <para>
/// <b>Token usage.</b> <see cref="ChatResponse.Usage"/>'s <see cref="UsageDetails.TotalTokenCount"/> is the
/// conversation's cumulative KV-cache size, so across a stateful thread it grows with each turn (it now spans
/// calls rather than resetting per call as in the stateless mode).
/// </para>
/// </remarks>
public sealed record LiteRtStatefulConversationOptions
{
    // Intentionally empty: this record is purely the opt-in token for the stateful mode. The former
    // MaxLiveConversations knob was removed because the mode is hard-limited to one live conversation while
    // upstream LiteRT-LM cannot preserve a suspended conversation's state (see the type doc and docs/roadmap.md).
    // The internal live-conversation capacity lives on LiteRtConversationStore.LiveConversationCapacity.
}
