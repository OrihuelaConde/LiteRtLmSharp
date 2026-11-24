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
/// never issued by this client) throws <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// <b>Cancelled and failed sends.</b> The client takes a <see cref="LiteRtConversation.Clone"/> checkpoint before
/// each turn. A turn starts with a user message and ends with the first reply that has no tool calls, so with
/// function invocation it spans several sends. If a send of the turn is cancelled or fails, the conversation goes
/// back to the checkpoint under the same id, and the whole turn is discarded, so re-send the message to get the
/// answer. This matches what Agent Framework keeps after a failed run. Tool results sent for the discarded calls
/// throw <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// A turn is kept when no native send was cancelled or failed: a run that ends between sends, for example cancelled while a tool
/// runs, or a stream the consumer stops reading after the reply ended, for example at its tool calls.
/// </para>
/// <para>
/// A cancelled or failed send ends the conversation instead in these cases, and resuming its id throws
/// <see cref="ArgumentException"/>: the turn that started the conversation, tool calls included (even when stream
/// updates already carried its id); a turn started by tool results with no turn waiting for them, which includes results
/// sent on a fork, because a fork doesn't inherit its parent's waiting turn; a conversation with constrained decoding,
/// a constraint provider, or a JSON-schema <see cref="ChatOptions.ResponseFormat"/> on its first call, unless
/// <see cref="CheckpointConstrainedConversations"/> is set, because cloning one rebuilds its constraints, about a second
/// on LiteRT-LM v0.18.0; and a backend that cannot clone.
/// </para>
/// <para>
/// The checkpoint is a second native conversation outside <see cref="MaxLiveConversations"/>. On text conversations
/// it costs no measurable time or memory until it is used; inline attachments are copied once per checkpoint, which
/// a file-path attachment avoids. A rollback does not remove earlier reasoning from the KV cache
/// (<see cref="LiteRtConversationOptions.FilterThinkingFromKvCache"/>).
/// </para>
/// <para>
/// Disposing the client disposes all live conversations. There is no time-based expiry in this mode — size
/// the cap for your concurrency.
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
/// and a send on a full conversation throws <see cref="LiteRtContextOverflowException"/> instead of reaching
/// the runtime (unguarded, the runtime rejects it, and a rejected prefill leaves the conversation failing every
/// later send). A reply that
/// the guard cut at the context limit carries <see cref="ChatFinishReason.Length"/> <b>in the same turn</b>
/// (deliberately overriding <see cref="ChatFinishReason.ToolCalls"/>). Usually the context is then full and the
/// thread is over. After a reply with thinking, the runtime removes the thinking from the KV cache, so the
/// context has room again and the thread can continue; <see cref="UsageDetails.TotalTokenCount"/> shows how much
/// of the context is in use. A "message doesn't fit" rejection happens before any native
/// work and <b>keeps the live conversation resumable</b> — retry the id with a shorter message; a "context
/// is full" rejection evicts it, so resuming its id throws <see cref="ArgumentException"/> and you start a new
/// conversation, summarizing or trimming what must carry over. The exception is a send of tool results whose turn
/// has a checkpoint with room: the turn rolls back and the id stays valid.
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
    /// <b>Each live conversation keeps its own native KV state</b> — the cap is a memory ceiling, not just a
    /// count. On a model with a dynamic context such as gemma-4 E2B, that state grows with the tokens the
    /// conversation has used, up to the engine's <see cref="LiteRtEngineOptions.MaxNumTokens"/>: measured on
    /// LiteRT-LM v0.18.0, a conversation of 2,300 tokens holds about 36 MB on a desktop CPU and 60 to 100 MB on
    /// a desktop GPU. A model with a static context keeps a full-size cache for every live conversation. On
    /// memory-constrained (mobile) devices size the cap deliberately, and lower it (e.g. 1-2) where memory is
    /// tighter than concurrency.
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

    /// <summary>
    /// Takes the checkpoint behind the rollback on conversations with constrained decoding or a constraint provider
    /// too. Default <c>false</c>: a cancelled or failed send on such a conversation evicts its id, so resuming it
    /// throws <see cref="ArgumentException"/> and the caller starts a new conversation.
    /// </summary>
    /// <remarks>
    /// Cloning such a conversation rebuilds its constraints, which costs about 0.9 s with constrained decoding and
    /// 1 s with the LlGuidance provider per clone (gemma-4 E2B, LiteRT-LM v0.18.0, win-x64, CPU and GPU), so with this
    /// on, every turn takes that much longer, whether or not it is cancelled. Turn it on when a cancelled send has
    /// to keep the conversation, for example an Agent Framework session whose user can stop a reply: the session
    /// keeps its id after a failed run, so without this every later run on it throws. A slower device takes longer;
    /// that cost was not measured.
    /// </remarks>
    public bool CheckpointConstrainedConversations { get; init; }
}
