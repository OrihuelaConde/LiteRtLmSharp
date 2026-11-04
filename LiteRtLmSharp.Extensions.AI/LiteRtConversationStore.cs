namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// A bounded LRU store of live native <see cref="LiteRtConversation"/>s, keyed by the string id the
/// <see cref="LiteRtChatClient"/> surfaces as <c>ChatResponse.ConversationId</c>. Backs the client's opt-in
/// stateful-conversations mode: a new conversation is kept alive between calls (rather than rebuilt from
/// history each turn), resumed on a request that carries its id.
/// </summary>
/// <remarks>
/// <b>Not internally synchronized.</b> Every method is called from inside the client's existing gate
/// (<c>SemaphoreSlim</c>), which already serializes all client calls, so the store needs no lock of its own.
/// The most-recently-used entry sits at the front of the list; eviction takes the back. The entry currently
/// in use is always at the front (a lookup moves it there, and a fresh add lands there), so it is never the
/// one evicted.
/// </remarks>
internal sealed class LiteRtConversationStore
{
    /// <summary>A stored live conversation plus the synthesized-call-id → tool-name map the client
    /// accumulates as the conversation emits tool calls. The map lets a later continuation resolve tool
    /// names for <c>FunctionResultContent</c>s whose only link back is a <c>CallId</c> — the assistant
    /// tool-call turn that named them is not resent on a stateful continuation.</summary>
    internal sealed class Entry
    {
        private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(0);
        private Dictionary<string, string>? _callIdToName;
        private Dictionary<string, int>? _cutMessages;

        public Entry(LiteRtConversation conversation) => Conversation = conversation;

        /// <summary>The live native conversation. Replaced (see <see cref="Replace"/>) when a send that was
        /// cancelled or failed rolls the conversation back to the checkpoint taken before its turn.</summary>
        public LiteRtConversation Conversation { get; set; }

        /// <summary>The rollback point of a turn in progress, or <c>null</c> between turns. A reply that ends in
        /// tool calls continues with their results, so the checkpoint taken before the turn's first send stays
        /// here until a reply without tool calls completes the turn.</summary>
        public TurnCheckpoint? PendingTurn { get; set; }

        /// <summary>Whether cloning this conversation rebuilds its constraint machinery (it was created with
        /// constrained decoding or a constraint provider): about a second per clone on LiteRT-LM v0.18.0, so the
        /// client takes no checkpoint for it and a cancelled or failed send evicts it instead. Copied to forks.</summary>
        public bool CloneIsSlow { get; set; }

        /// <summary>Whether the conversation was created with a custom constraint provider (LlGuidance),
        /// i.e. whether per-send constraints (a JSON-schema <c>ResponseFormat</c>) can be enforced on it.
        /// Set when the conversation is stored; copied to forks. Lets the client reject a constrained
        /// continuation on a provider-less conversation BEFORE any native work — in MEAI vocabulary and
        /// without destroying the (untouched, perfectly resumable) live conversation.</summary>
        public bool HasConstraintProvider { get; set; }

        /// <summary>Records a synthesized call id and the tool name it stands for (idempotent; last write wins).</summary>
        public void RecordToolCall(string callId, string name)
            => (_callIdToName ??= new Dictionary<string, string>(StringComparer.Ordinal))[callId] = name;

        /// <summary>Copies <paramref name="source"/>'s accumulated call-id → tool-name entries into this one
        /// (used when a fork inherits its parent's map so tool continuations keep resolving on the branch).
        /// A no-op when the source map is empty; existing entries with the same key are overwritten.</summary>
        public void CopyToolCallsFrom(Entry source)
        {
            IReadOnlyDictionary<string, string> src = source.CallIdToName;
            if (src.Count == 0)
                return;
            Dictionary<string, string> dst = _callIdToName ??= new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in src)
                dst[kv.Key] = kv.Value;
        }

        /// <summary>The accumulated synthesized-call-id → tool-name map (empty until the conversation emits a tool call).</summary>
        public IReadOnlyDictionary<string, string> CallIdToName => _callIdToName ?? Empty;

        /// <summary>A copy of the tool-call map, for a <see cref="TurnCheckpoint"/>.</summary>
        public IReadOnlyDictionary<string, string> SnapshotToolCalls()
            => _callIdToName is null ? Empty : new Dictionary<string, string>(_callIdToName, StringComparer.Ordinal);

        /// <summary>Puts back the tool-call map a <see cref="TurnCheckpoint"/> recorded, so the calls of a
        /// rolled-back turn no longer resolve.</summary>
        public void RestoreToolCalls(IReadOnlyDictionary<string, string> snapshot)
            => _callIdToName = snapshot.Count == 0 ? null : new Dictionary<string, string>(snapshot, StringComparer.Ordinal);

        /// <summary>Records the user message of a turn that was rolled back. See <see cref="AvoidRepeatingACutMessage"/>.</summary>
        public void RecordCutMessage(string? userText)
        {
            if (userText is null)
                return;
            _cutMessages ??= new Dictionary<string, int>(StringComparer.Ordinal);
            string key = userText.Trim();
            _cutMessages[key] = _cutMessages.GetValueOrDefault(key) + 1;
        }

        /// <summary>
        /// Returns the text to send for a user message, changed if it repeats a message rolled back since the last
        /// completed send. The checkpoint still shares its native state with the cut conversation, which already
        /// processed that message, and when a first send matches that state completely the runtime skips its
        /// prefill. On the GPU backend (LiteRT-LM v0.18.0 and v0.19.0), after another conversation generated, the
        /// reply then comes out wrong or empty, with no error. Any other token avoids the match. The templates trim
        /// whitespace, so this adds zero-width spaces, one more each time the same message was cut.
        /// </summary>
        public string? AvoidRepeatingACutMessage(string? userText)
            => userText is not null && _cutMessages is not null && _cutMessages.TryGetValue(userText.Trim(), out int cuts)
                ? userText + new string('\u200B', cuts)
                : userText;

        /// <summary>Forgets the rolled-back messages: a completed send separates the conversation's native state
        /// from the cut conversation's.</summary>
        public void ClearCutMessages() => _cutMessages = null;

        /// <summary>Disposes the live conversation and the checkpoint of a turn in progress.</summary>
        public void Dispose()
        {
            Conversation.Dispose();
            PendingTurn?.Checkpoint?.Dispose();
            PendingTurn = null;
        }
    }

    /// <summary>Where a stateful turn rolls back to if one of its sends is cancelled or fails: a clone of the
    /// conversation taken before the turn's first send, the tool-call map at that point, and the user message
    /// that started the turn. The clone is <c>null</c> when the turn created the conversation or no checkpoint could
    /// be taken; a failure then evicts the conversation.</summary>
    internal sealed record TurnCheckpoint(LiteRtConversation? Checkpoint, IReadOnlyDictionary<string, string> ToolCalls, string? UserText);

    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>> _map;
    private readonly LinkedList<KeyValuePair<string, Entry>> _lru;   // front = most recently used

    public LiteRtConversationStore(int capacity)
    {
        _capacity = capacity;
        _map = new Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>>(StringComparer.Ordinal);
        _lru = new LinkedList<KeyValuePair<string, Entry>>();
    }

    /// <summary>Looks up a conversation by id, promoting it to most-recently-used, or returns <c>null</c>
    /// when the id is unknown or was evicted.</summary>
    public Entry? Get(string id)
    {
        if (!_map.TryGetValue(id, out LinkedListNode<KeyValuePair<string, Entry>>? node))
            return null;
        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value.Value;
    }

    /// <summary>Stores a freshly created conversation under <paramref name="id"/> as most-recently-used,
    /// evicting and disposing the least-recently-used conversation when the store is over capacity. Returns
    /// the new entry.</summary>
    public Entry Add(string id, LiteRtConversation conversation)
    {
        var entry = new Entry(conversation);
        var node = new LinkedListNode<KeyValuePair<string, Entry>>(new KeyValuePair<string, Entry>(id, entry));
        _lru.AddFirst(node);
        _map[id] = node;

        // Evict from the back (least-recently-used) until within capacity. The just-added node is at the
        // front, so it is never the one evicted.
        while (_map.Count > _capacity && _lru.Last is { } lru)
        {
            _lru.RemoveLast();
            _map.Remove(lru.Value.Key);
            lru.Value.Value.Dispose();
        }
        return entry;
    }

    /// <summary>
    /// Puts <paramref name="conversation"/> in place of the live conversation stored under
    /// <paramref name="id"/> and disposes the one it replaces. The entry (its flags) and its LRU position stay,
    /// so the id keeps resolving. Used to roll a stateful conversation back to the checkpoint taken before a
    /// turn whose send was cancelled or failed. When the id is no longer stored, <paramref name="conversation"/>
    /// is disposed instead.
    /// </summary>
    public void Replace(string id, LiteRtConversation conversation)
    {
        if (!_map.TryGetValue(id, out LinkedListNode<KeyValuePair<string, Entry>>? node))
        {
            conversation.Dispose();
            return;
        }
        Entry entry = node.Value.Value;
        LiteRtConversation consumed = entry.Conversation;
        entry.Conversation = conversation;
        consumed.Dispose();
    }

    /// <summary>Removes and disposes the conversation with <paramref name="id"/> if present (a no-op
    /// otherwise). Used to evict a conversation that cannot continue: a send rejected because its context is
    /// full, or a cancelled or failed send with no checkpoint to roll back to (the native runtime leaves such a
    /// conversation consumed, so it must not be resumed).</summary>
    public void Remove(string id)
    {
        if (!_map.TryGetValue(id, out LinkedListNode<KeyValuePair<string, Entry>>? node))
            return;
        _lru.Remove(node);
        _map.Remove(id);
        node.Value.Value.Dispose();
    }

    /// <summary>Disposes every live conversation and clears the store. Called from the client's <c>Dispose</c>.</summary>
    public void DisposeAll()
    {
        foreach (LinkedListNode<KeyValuePair<string, Entry>> node in EnumerateNodes())
            node.Value.Value.Dispose();
        _lru.Clear();
        _map.Clear();
    }

    private IEnumerable<LinkedListNode<KeyValuePair<string, Entry>>> EnumerateNodes()
    {
        for (LinkedListNode<KeyValuePair<string, Entry>>? n = _lru.First; n is not null; n = n.Next)
            yield return n;
    }
}
