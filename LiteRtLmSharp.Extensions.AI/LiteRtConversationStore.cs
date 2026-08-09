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

        public Entry(LiteRtConversation conversation) => Conversation = conversation;

        /// <summary>The live native conversation.</summary>
        public LiteRtConversation Conversation { get; }

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
    }

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
            lru.Value.Value.Conversation.Dispose();
        }
        return entry;
    }

    /// <summary>Removes and disposes the conversation with <paramref name="id"/> if present (a no-op
    /// otherwise). Used to evict a conversation whose send was cancelled or abandoned — the native runtime
    /// leaves such a conversation consumed, so it must not be resumed.</summary>
    public void Remove(string id)
    {
        if (!_map.TryGetValue(id, out LinkedListNode<KeyValuePair<string, Entry>>? node))
            return;
        _lru.Remove(node);
        _map.Remove(id);
        node.Value.Value.Conversation.Dispose();
    }

    /// <summary>Disposes every live conversation and clears the store. Called from the client's <c>Dispose</c>.</summary>
    public void DisposeAll()
    {
        foreach (LinkedListNode<KeyValuePair<string, Entry>> node in EnumerateNodes())
            node.Value.Value.Conversation.Dispose();
        _lru.Clear();
        _map.Clear();
    }

    private IEnumerable<LinkedListNode<KeyValuePair<string, Entry>>> EnumerateNodes()
    {
        for (LinkedListNode<KeyValuePair<string, Entry>>? n = _lru.First; n is not null; n = n.Next)
            yield return n;
    }
}
