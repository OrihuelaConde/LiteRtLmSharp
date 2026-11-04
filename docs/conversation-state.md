# Conversation state: restore history and clone

LiteRtLmSharp gives you two complementary ways to manage a conversation's state beyond a single
`LiteRtConversation` lifetime:

- **Restore chat history** rebuilds a conversation from messages you persisted, so a chat survives an
  app restart (or you seed a new conversation with few-shot examples). It is durable and works across
  processes.
- **Clone** forks a live conversation, duplicating its in-memory state so you can branch into several
  continuations without re-running the shared prefix. It is fast and same-process only.

Both are pure binding features over the LiteRT-LM C API
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
    History = restored,                 // prefilled by the FIRST send, not by this call
});

Console.WriteLine(chat2.Send("What is my name and what do I like?").Text);
// -> "Your name is Ada and you love astronomy."
```

### What "restore" actually does

Restoring is a **replay through prefill**, not a zero-cost snapshot of the KV cache. The prior turns
become the conversation's preface and are prefilled on the conversation's **first send** (not when it is
created: right after `CreateConversation` the history is queued but not in the cache, so
`TokenCount` still reads 0 and the prefill cost is paid by the first `Send`/`SendStreamingAsync`), so:

- It costs a prefill of the whole history (the same work as if those turns had just been sent), and that
  cost lands on the first reply's time-to-first-token.
- The restored tokens count against the engine's `MaxNumTokens` context window. Watch
  `LiteRtConversation.TokenCount` (or `TokenCountCached`, which never waits) and trim old turns before
  you run out of room. To measure a restored history before its first send, count the tokens of
  `RenderPreface()` with `LiteRtEngine.Tokenize`.
- There is no on-disk KV-cache format to load; `Serialize` produces messages, not tensors.

### Tool calls and reasoning

`ToMessage()` keeps the assistant's tool calls so a function-calling conversation restores correctly,
and it **drops the reasoning ("thinking") trace** on purpose. Replaying a long thinking block would
re-consume the context window for no benefit, which is the same reason
[`FilterThinkingFromKvCache`](chat.md#reasoning-mode-thinking) exists. Pass
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

> **`SystemMessage` was fixed in v0.14.0 (this matters if you used the workaround).** Before v0.14.0,
> `SystemMessage` was **silently ignored**: the binding wrapped the text in a bare `{"type":"text",…}`
> object, and the chat template drops object-valued content, so the system turn rendered empty and the
> model never saw the prompt. It is now sent as a content-parts **array** and renders correctly (root-caused
> and verified with [`RenderPreface()`](#inspect-the-rendered-preface) below). The documented workaround was
> to put the system prompt as a leading `LiteRtMessage.System(...)` in `History` (the path
> `LiteRtChatClient` uses), which was **never** affected. **Migration:** if you applied that workaround AND
> also left `SystemMessage` set, remove one of the two; both now render, which produces two system turns.
> Confirm the result with `RenderPreface()`.

### Inspect the rendered preface

`LiteRtConversation.RenderPreface()` renders the conversation's whole templated **preface** (the system
message, tools and restored history) to a string, **without sending** anything (the KV cache is
untouched). It is the history-and-system counterpart to `RenderMessage(text)` (which renders a single
user turn), and it is how you confirm what the model actually sees at the top of a restored or
system-prompted conversation.

```csharp
using var chat = engine.CreateConversation(new LiteRtConversationOptions
{
    SystemMessage = "You are a concise assistant.",
    History = restored,
});

string preface = chat.RenderPreface();          // system + tools + history, templated
int prefaceTokens = engine.Tokenize(preface).Length;   // exact preamble cost against MaxNumTokens
```

Pair it with `LiteRtEngine.Tokenize` to measure the preamble's exact token cost before you send, and use
it to verify the system-prompt placement above (a correctly-set `SystemMessage` now appears in the
preface; an empty system turn signals the pre-v0.14.0 bug).

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

## Continue after a cancellation

A send that is cut short **consumes** its conversation, which can't generate again: after a cancelled `SendAsync`,
a stream abandoned before its reply ended, or `CancelProcess()` during a send, the runtime answers every later send on it with
`CANCELLED` (LiteRT-LM v0.16.0 and later; on v0.14.0 the send does not return), and
`IsCancelled` turns `true`. `CancelProcess()` on an idle conversation changes nothing. To continue, take a
checkpoint with `Clone()` **before** the send that might be cut, and continue on the checkpoint if it is. In its
stateful mode, `LiteRtChatClient` [does this for you](extensions-ai.md#cancelled-and-failed-sends).

```csharp
LiteRtConversation chat = engine.CreateConversation();
chat.Send("My name is Ada. Remember it.");

// Before a send that something more urgent might interrupt, with cts.Cancel() or chat.CancelProcess():
LiteRtConversation checkpoint = chat.Clone();
using var cts = new CancellationTokenSource();
try
{
    await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync("Write a long poem about trains.", cts.Token))
        if (chunk.Kind == LiteRtStreamChunkKind.Answer)
            Console.Write(chunk.Text);
}
catch (Exception e) when (e is OperationCanceledException or LiteRtException { StatusCode: LiteRtStatusCode.Cancelled })
{
    // cts.Cancel() surfaces as OperationCanceledException, CancelProcess() as LiteRtException.
}
finally
{
    // Also true when a break leaves the stream before the reply ends, or when the stream failed.
    if (chat.IsCancelled)
    {
        chat.Dispose();          // consumed by the cut
        chat = checkpoint;       // the conversation as it was before the cut turn
    }
    else
    {
        checkpoint.Dispose();    // the conversation is intact: the checkpoint is not needed
    }
}

Console.WriteLine(chat.Send("What is my name?").Text);
chat.Dispose();
```

A failure other than a cut leaves this block with the exception, so dispose `chat` in your own error path too.

> **Warning:** On the GPU backend, don't repeat the cut message word for word. On LiteRT-LM v0.18.0 and v0.19.0, the
> reply comes out wrong or empty, with no error, when all of these hold
> ([LiteRT-LM #4394](https://github.com/google-ai-edge/LiteRT-LM/issues/4394)):
>
> - You continue on a clone, and this is the clone's first send.
> - The send repeats a message that the original conversation, or another of its clones, already processed.
> - Another conversation generated in between.
>
> The CPU backend was not affected in the same tests. To avoid the problem, reword the message or append zero-width
> spaces (`"\u200B"`): one on the first retry, two on the second, and so on, so that the text differs from every earlier
> attempt. Plain whitespace does not help, because the model's template trims it. The reply can differ from the one the
> original message would have received.

- **Cost:** on a text conversation, creating the clone takes no measurable time or memory; it only costs memory
  once it generates.
  Measured on LiteRT-LM v0.18.0 with gemma-4 E2B, a clone of a 2,300-token conversation holds about 36 MB on
  a desktop CPU and 60 to 100 MB on a desktop GPU once used. Continuing on it skips the replay a `History`
  restore pays: for a 310-token conversation, 0.3 s instead of 0.6 s on CPU, and 48 ms instead of 202 ms on
  GPU when nothing else generated in between. On the GPU, continuing after the original generated (the
  cut send) also pays a conversation switch: about 0.3 to 0.6 s at 2,300 tokens, still less than a replay.
- **Constrained conversations cost more.** On a conversation with `EnableConstrainedDecoding` or a
  `ConstraintProvider`, the runtime rebuilds the constraints for the clone: about 0.9 s and 1 s per clone
  (gemma-4 E2B, win-x64).
- **The cut turn is not kept.** The checkpoint holds the conversation as it was before the send, so send the
  question again to get the answer; the partial text the stream produced is yours to show or discard.
- **Clone before, not after.** `Clone()` throws `InvalidOperationException` for a conversation whose send was
  cut short, because the runtime would return an empty clone that also answers `CANCELLED`. It also throws after
  a stream that failed during generation. After a blocking send that failed, it can't detect the failure and returns a
  clone that fails too, so take the checkpoint before the send. `Clone()` also needs a conversation that has completed
  a send (see [Clone a conversation](#clone-a-conversation)).
- **Inline attachments are copied.** The clone copies the message history, so an inline image or audio is copied
  once per clone. A file-path attachment avoids that. This cost was not measured.
- **Reasoning stays in a clone's context.** A clone does not carry the state that removes earlier
  reasoning from the KV cache (`FilterThinkingFromKvCache`), so on a reasoning conversation the earlier
  reasoning stays in the clone's context. In the LiteRT-LM sources, with `UseRingbuffersLocalAttention` on the
  next turns can also fail with `NOT_FOUND`; this was not measured.
- **Same engine and process only.** To move a conversation to another engine, or across a restart, restore
  its turns through `History` instead.

## Clone a conversation

`LiteRtConversation.Clone()` duplicates a conversation's prefilled (KV-cache) state into a new,
independent conversation. Use it to branch: explore several continuations from a shared prefix without
paying to prefill that prefix again.

```csharp
using var baseChat = engine.CreateConversation();
baseChat.Send("You are a travel agent. The user is going to Tokyo for 3 days.");

// Fork two independent continuations that share the prefilled prefix (no re-prefill):
using var budget = baseChat.Clone();
using var luxury = baseChat.Clone();

Console.WriteLine(budget.Send("Suggest a budget itinerary.").Text);
Console.WriteLine(luxury.Send("Suggest a luxury itinerary.").Text);
// baseChat is untouched; each clone advances on its own KV cache.
```

Other good uses: checkpoint before a risky or expensive turn so you can retry from the checkpoint (reword
the retry on the GPU backend, see [Continue after a cancellation](#continue-after-a-cancellation)); A/B two
sampler settings or two phrasings from the same context.

### Caveats

- **Send first, then clone.** `Clone()` duplicates the *prefilled* state, and creating a conversation
  prefills nothing: the system message, tools and `History` only enter the KV cache with the first
  send. A conversation that has not advanced (`TokenCount == 0`) therefore has nothing to duplicate,
  and cloning it anyway is unreliable: the first clone happens to work, but once any other
  conversation runs on the engine, the parent and its later clones continue that other conversation's
  context instead of their own (observed on LiteRT-LM v0.15.0 and v0.16.0, CPU and GPU). `Clone()`
  rejects this up front with `InvalidOperationException`. To keep a reusable base to branch from
  (a system prompt plus tools, say), send one real turn on it first, as the example above does.
- **Idle only.** Conversations are not thread-safe. Clone when the source is idle, with no in-flight
  `SendStreamingAsync`.
- **May be unsupported on some backends.** Cloning is implemented by the standard executors and is
  verified on CPU and GPU (win-x64 WebGPU). It is not a CPU-only feature, but an executor that does not
  implement it makes the native call return null and `Clone()` throw `LiteRtException`. If you target an
  exotic backend, be ready to catch it and fall back to restoring from `History`.
- **In-memory only.** A clone lives in the same process. To carry a conversation across restarts use
  `History`, not `Clone()`.
- **Lifetime.** Dispose clones like any conversation. A clone keeps the native engine loaded, so
  `LiteRtEngine.Load` can refuse a new engine until the last clone is disposed.

## Which one do I want?

| You want to… | Use | Cost |
|---|---|---|
| Resume a chat after the app restarts | `History` / `HistoryJson` | Re-prefills the history |
| Seed a conversation with few-shot examples | `History` / `HistoryJson` | Re-prefills the examples |
| Continue a chat after cancelling a send | `Clone()` before the send | Copies state, no re-prefill |
| Branch a live conversation into variants | `Clone()` | Copies state, no re-prefill |
| Checkpoint before a risky turn, then retry | `Clone()` (reword the retry on the GPU backend) | Copies state, no re-prefill |

The two compose: clone is the cheap same-process fork of live state, and the checkpoint that survives a
cancellation; restore is the durable cross-session reload.

## Cap a single send: per-send `MaxOutputTokens`

`LiteRtSendOptions.MaxOutputTokens` overrides the conversation-level `MaxOutputTokens` for **one** send,
without rebuilding the conversation. The runtime resolves the per-send value over the session value, so a
conversation configured for long answers can still emit a short one on demand (or vice versa).

```csharp
using var chat = engine.CreateConversation(new LiteRtConversationOptions
{
    MaxOutputTokens = 512,           // the conversation's default cap
});

// This one turn is capped tighter, just for this send:
var terse = chat.Send("Summarize in one line.", options: new LiteRtSendOptions { MaxOutputTokens = 32 });

// The next send with no options falls back to the conversation's 512.
var full = chat.Send("Now explain in detail.");
```

Leave it unset to use the conversation-level cap. It composes with the other per-send settings on
`LiteRtSendOptions` (e.g. `VisualTokenBudget`).

## Stream tool-call progress: `StreamToolCalls`

By default a tool call is silent while the model generates it: you see nothing until the complete parsed
`ToolCall` chunk arrives. Opt into `LiteRtConversationOptions.StreamToolCalls` and `SendStreamingAsync`
also yields the **raw, incremental** text of the call as it is produced, so a UI can show a "calling
`get_weather`…" spinner rather than a pause.

```csharp
using var chat = engine.CreateConversation(new LiteRtConversationOptions
{
    Tools = [ /* … */ ],
    StreamToolCalls = true,          // opt-in; off by default
});

await foreach (var chunk in chat.SendStreamingAsync("What's the weather in Tokyo?"))
{
    switch (chunk.Kind)
    {
        case LiteRtStreamChunkKind.ToolCallDelta:   // incremental, UNPARSED progress fragment
            ShowToolProgress(chunk.Text);           // display only, do not act on this
            break;
        case LiteRtStreamChunkKind.ToolCall:        // the complete, parsed call: act on THIS
            Invoke(chunk.ToolCall);
            break;
        // Answer / Thinking as usual
    }
}
```

Lifecycle: the `ToolCallDelta` fragments stream **first** (raw text on the native `tool_call` channel, not
yet valid JSON), then the usual complete parsed `ToolCall` chunk arrives once the call is whole. The deltas
are **for progress display only**; always act on the final parsed `ToolCall` (or the blocking
`LiteRtResponse.ToolCalls`). With `StreamToolCalls` off (the default), a tool-call block keeps today's
behavior: silence until the parsed call arrives whole.
