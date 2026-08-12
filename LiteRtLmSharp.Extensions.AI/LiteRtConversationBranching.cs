using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// The provider-specific hatch for <b>forking</b> a live stateful conversation of a
/// <see cref="LiteRtChatClient"/>: <see cref="ForkAsync"/> branches a conversation into an independent copy
/// that shares the parent's prefilled context but diverges from there.
/// </summary>
/// <remarks>
/// <para>
/// Requires native LiteRT-LM v0.15.0+: earlier runtimes silently lost a suspended conversation's state
/// when another advanced, which is why this type stayed internal (and the stateful mode single-conversation)
/// until that fix shipped.
/// </para>
/// <para>
/// Forking builds on LiteRtLmSharp's native <see cref="LiteRtConversation.Clone"/>: the branch starts from a
/// copy of the parent's prefilled KV-cache state (cheap — no re-prefill of the shared prefix) and then
/// advances on its own. Neither branch affects the other, and the parent is untouched. Typical uses are
/// exploring several continuations of one prompt (best-of-N, A/B prompting, tree search) without paying to
/// re-establish the common prefix each time.
/// </para>
/// <para>
/// <b>Stateful mode only.</b> <c>GetService(typeof(LiteRtConversationBranching))</c> returns an instance only
/// when the client was constructed with <see cref="LiteRtStatefulConversationOptions"/> (the opt-in stateful
/// mode); in the default stateless mode it returns <c>null</c>, because there are no live conversations to
/// fork. See <see cref="LiteRtChatClient.GetService"/>.
/// </para>
/// <para>
/// <b>The fork is an ordinary conversation id.</b> <see cref="ForkAsync"/> returns a new
/// <c>ConversationId</c> with exactly the same semantics as any other: set it on
/// <see cref="ChatOptions.ConversationId"/> to resume the branch, fork it again, or let it be evicted. The
/// fork is added to the <b>same</b> live-conversation cache as every other live conversation, so it counts
/// toward the cache's capacity (<see cref="LiteRtStatefulConversationOptions.MaxLiveConversations"/>) and can
/// evict the least-recently-used conversation — potentially the parent it was cloned from (the clone is fully
/// independent native state, so the branch survives its parent's eviction; only the parent's id becomes
/// unresumable).
/// </para>
/// <para>
/// <b>Tool continuations keep working on a branch.</b> The parent's accumulated synthesized-call-id → tool-name
/// map is copied into the fork, so a function-calling continuation sent on the branch (whose assistant
/// tool-call turn is not resent) still resolves its tool names. Forking after an assistant tool-call turn and
/// sending the tool results on the fork's id works.
/// </para>
/// </remarks>
public sealed class LiteRtConversationBranching
{
    private readonly LiteRtChatClient _client;

    /// <summary>Created by <see cref="LiteRtChatClient"/> (in the stateful mode) and handed out via
    /// <see cref="LiteRtChatClient.GetService"/>; not constructible directly.</summary>
    internal LiteRtConversationBranching(LiteRtChatClient client) => _client = client;

    /// <summary>
    /// Forks the live conversation identified by <paramref name="conversationId"/> into a new, independent
    /// conversation that starts from a copy of the parent's prefilled context, and returns the new
    /// conversation's id.
    /// </summary>
    /// <param name="conversationId">
    /// The id of a live conversation to fork — a <c>ConversationId</c> previously surfaced on a
    /// <see cref="ChatResponse"/> from this client (or itself the result of a prior fork) that has not since
    /// been evicted.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting to acquire the client's call gate. Cloning itself is a
    /// fast, non-cancellable native memory copy.</param>
    /// <returns>
    /// A fresh <c>ConversationId</c> for the branch. It has identical semantics to any other conversation id:
    /// set it on <see cref="ChatOptions.ConversationId"/> to resume, re-fork, or evict it. The branch inherits
    /// the parent's prefilled context and diverges from there; the parent is unaffected.
    /// </returns>
    /// <remarks>
    /// Runs under the client's call gate, so it cannot race an in-flight send or stream (forking while a
    /// stream is live is impossible by construction — the gate is held for the whole stream). The new branch
    /// is stored in the same live-conversation cache as every other live conversation: it counts toward the
    /// cache's capacity and adding it may evict the least-recently-used conversation (potentially the
    /// parent). Forking a conversation before its first send is not a scenario reachable
    /// through this client (an id is only handed out after a send completes), but the underlying clone supports
    /// it — a fork of an unsent base simply pays its prefill on the branch's first send.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="conversationId"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// No live conversation has that id — it was never issued by this client, or it was evicted from the
    /// live-conversation cache.
    /// </exception>
    /// <exception cref="LiteRtException">The native clone failed (an engine/backend that does not implement
    /// cloning; see <see cref="LiteRtConversation.Clone"/>).</exception>
    /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
    public Task<string> ForkAsync(string conversationId, CancellationToken cancellationToken = default)
        => _client.ForkConversationAsync(conversationId, cancellationToken);
}
