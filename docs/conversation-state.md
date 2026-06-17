# Conversation state: restore history and clone

LiteRtLmSharp gives you two complementary ways to manage a conversation's state beyond a single
`LiteRtConversation` lifetime:

- **Restore chat history** rebuilds a conversation from messages you persisted, so a chat survives an
  app restart (or you seed a new conversation with few-shot examples). It is durable and works across
  processes.
- **Clone** forks a live conversation, duplicating its in-memory state so you can branch into several
  continuations without re-running the shared prefix. It is fast and same-process only.

Both are pure binding features over the LiteRT-LM v0.13.1 C API
(`conversation_config_set_messages` and `conversation_clone`); they need no special native build.
Verified end to end on `gemma-4-E2B-it`, on both CPU and GPU (win-x64 WebGPU).

## Restore chat history

The LiteRT-LM C API has **no way to read a conversation's history back** out of the engine. That is by
design: a chat UI already keeps the messages it renders, so the round-trip is yours to own. You record
each turn as it happens, persist the list, and on the next run recreate the conversation seeded with it.

Messages are modeled by `LiteRtMessage`. Build them with the factory methods, and capture an assistant
reply straight from a `LiteRtResponse` with `ToMessage()`:

```csharp
var history = new List<LiteRtMessage>();

// User turn:
history.Add(LiteRtMessage.User("Hi, my name is Ada and I love astronomy."));

// Assistant turn, captured from the reply:
LiteRtResponse reply = chat.Send("Hi, my name is Ada and I love astronomy.");
history.Add(reply.ToMessage());   // role "model"; preserves text (and tool calls)
```

Persist the list with `LiteRtMessage.Serialize` (a plain JSON string you can write anywhere), and reload
it with `LiteRtMessage.Deserialize`:

```csharp
// Persist (to a file, a database, app storage, wherever):
File.WriteAllText("chat.json", LiteRtMessage.Serialize(history));

// Later, in a new process: reload and recreate the conversation seeded with the history.
IReadOnlyList<LiteRtMessage> restored = LiteRtMessage.Deserialize(File.ReadAllText("chat.json"));

using var chat2 = engine.CreateConversation(new LiteRtConversationOptions
{
    History = restored,                 // re-prefilled into the KV cache on create
});

Console.WriteLine(chat2.SendMessage("What is my name and what do I like?"));
// -> "Your name is Ada and you love astronomy."
```

### What "restore" actually does

Restoring is a **replay through prefill**, not a zero-cost snapshot of the KV cache. The prior turns
become the conversation's preface and are prefilled when the conversation is created, so:

- It costs a prefill of the whole history (the same work as if those turns had just been sent).
- The restored tokens count against the engine's `MaxNumTokens` context window. Watch
  `LiteRtConversation.TokenCount` and trim old turns before you run out of room.
- There is no on-disk KV-cache format to load; `Serialize` produces messages, not tensors.

### Tool calls and reasoning

`ToMessage()` keeps the assistant's tool calls so a function-calling conversation restores correctly,
and it **drops the reasoning ("thinking") trace** on purpose. Replaying a long thinking block would
re-consume the context window for no benefit, which is the same reason
[`FilterThinkingFromKvCache`](../README.md#reasoning-mode-thinking) exists. Pass
`ToMessage(includeToolCalls: false)` if you want text only.

A full tools round-trip looks like this:

```csharp
history.Add(LiteRtMessage.User("What's the weather in Tokyo?"));
LiteRtResponse r = chat.Send("What's the weather in Tokyo?");      // r.IsToolCall == true
history.Add(r.ToMessage());                                         // model turn carrying the tool call

var results = new[] { new LiteRtToolResult("get_weather", """{"temp":15,"unit":"C"}""") };
history.Add(LiteRtMessage.Tool(results));                           // tool-results turn
LiteRtResponse final = chat.SendToolResults(results);
history.Add(final.ToMessage());                                     // model's final answer
```

### System prompt placement

Set the system prompt in **one** place: either `LiteRtConversationOptions.SystemMessage` or as a leading
`LiteRtMessage.System(...)` in `History`, not both. The native side prepends `SystemMessage` before the
history, so doing both yields two system turns. The common pattern is to keep `SystemMessage` as the
active system prompt and store only user/model/tool turns in the history.

### Raw escape hatch

If you already hold the messages as the wire-format JSON array (for example a string you persisted with
`Serialize`, or one that includes content the typed model does not cover yet such as image or audio
parts), pass it verbatim through `HistoryJson` instead of re-parsing it into typed messages:

```csharp
using var chat = engine.CreateConversation(new LiteRtConversationOptions
{
    HistoryJson = File.ReadAllText("chat.json"),   // validated as a JSON array on create
});
```

`History` wins when it is non-empty (an empty list falls through to `HistoryJson`). `HistoryJson` is
validated as a JSON array when the conversation is created and throws `ArgumentException` otherwise.

## Clone a conversation

`LiteRtConversation.Clone()` duplicates a conversation's prefilled (KV-cache) state into a new,
independent conversation. Use it to branch: explore several continuations from a shared prefix without
paying to prefill that prefix again.

```csharp
using var baseChat = engine.CreateConversation();
baseChat.SendMessage("You are a travel agent. The user is going to Tokyo for 3 days.");

// Fork two independent continuations that share the prefilled prefix (no re-prefill):
using var budget = baseChat.Clone();
using var luxury = baseChat.Clone();

Console.WriteLine(budget.SendMessage("Suggest a budget itinerary."));
Console.WriteLine(luxury.SendMessage("Suggest a luxury itinerary."));
// baseChat is untouched; each clone advances on its own KV cache.
```

Other good uses: checkpoint before a risky or expensive turn so you can retry from the checkpoint;
A/B two sampler settings or two phrasings from the same context.

### Caveats

- **Idle only.** Conversations are not thread-safe. Clone when the source is idle, with no in-flight
  `SendMessageStreamingAsync`.
- **May be unsupported on some backends.** Cloning is implemented by the standard executors and is
  verified on CPU and GPU (win-x64 WebGPU). It is not a CPU-only feature, but an executor that does not
  implement it makes the native call return null and `Clone()` throw `LiteRtException`. If you target an
  exotic backend, be ready to catch it and fall back to restoring from `History`.
- **In-memory only.** A clone lives in the same process. To carry a conversation across restarts use
  `History`, not `Clone()`.
- **Lifetime.** Dispose clones like any conversation, and dispose all conversations (clones included)
  before the engine.

## Which one do I want?

| You want to… | Use | Cost |
|---|---|---|
| Resume a chat after the app restarts | `History` / `HistoryJson` | Re-prefills the history |
| Seed a conversation with few-shot examples | `History` / `HistoryJson` | Re-prefills the examples |
| Branch a live conversation into variants | `Clone()` | Copies state, no re-prefill |
| Checkpoint before a risky turn, then retry | `Clone()` | Copies state, no re-prefill |

The two compose: clone is the cheap same-process fork of live state; restore is the durable
cross-session reload.
