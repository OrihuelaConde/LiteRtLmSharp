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
re-consume the context window for no benefit, which is the same reason the runtime keeps the reasoning
out of the KV cache after each reply (see [Reasoning mode](chat.md#reasoning-mode-thinking)). Pass
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

> **Note:** In LiteRtLmSharp versions before 1.1.0, `SystemMessage` was ignored, and the documented
> workaround was a leading `LiteRtMessage.System(...)` in `History`. If you applied that workaround and also
> set `SystemMessage`, remove one of the two: both render, which produces two system turns. Confirm the
> result with [`RenderPreface()`](#inspect-the-rendered-preface).

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
it to verify the system-prompt placement above (`SystemMessage` appears in the preface as a system turn).

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
`CANCELLED`, and `IsCancelled` turns `true`. With LiteRT-LM v0.20.0, `CancelProcess()` on an idle conversation
left it usable, but the C API allows only deleting a conversation after a cancel, so call `CancelProcess()` only
while a send runs. To
continue, take a checkpoint with `Clone()` **before** the send that might be cut, and continue on the checkpoint if it is. In its
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

> **Note:** With LiteRT-LM v0.20.0, sending the cut message again, as it was, gave a correct reply on the GPU
> backend in 100 of 100 runs. That was measured on win-x64 (WebGPU on Direct3D 12, one NVIDIA RTX 3080) with
> gemma-4 E2B and `Float32`. Other GPUs, `Float16` and other models were not measured, and the upstream issue
> ([LiteRT-LM #4394](https://github.com/google-ai-edge/LiteRT-LM/issues/4394)) is still open.
>
> With LiteRT-LM v0.18.0, the version in LiteRtLmSharp 1.3.0, the GPU backend returned a wrong reply, with no
> error, in 25 of 25 runs of the same cases. The failure appeared when all of these held:
>
> - You continue on a clone, and this is the clone's first send.
> - The send repeats a message that the original conversation, or another of its clones, already processed.
> - Another conversation generated in between.

- **Cost:** on a text conversation, creating the clone takes no measurable time or memory; it only costs memory
  once it generates. Measured on LiteRT-LM v0.18.0 with gemma-4 E2B, a clone of a 2,300-token conversation
  holds about 36 MB on a desktop CPU and 60 to 100 MB on a desktop GPU once used.
- **Speed:** continuing on a clone skips the replay that a `History` restore pays. On the GPU, it saves time
  only when nothing else generated in between:
  - Measured with LiteRT-LM v0.18.0, gemma-4 E2B and a 310-token conversation: 0.3 s instead of 0.6 s on
    CPU, and 48 ms instead of 202 ms on GPU when nothing else generated in between.
  - LiteRT-LM v0.20.0, GPU: after the original conversation (the cut send) or another conversation
    generated, the clone's first send took as long as a `History` restore of the same conversation, or
    longer. Measured on win-x64 (WebGPU on Direct3D 12, one NVIDIA RTX 3080) with gemma-4 E2B, `Float32`
    and a 2,293-token conversation.
- **Constrained conversations cost more.** On a conversation with `EnableConstrainedDecoding` or a
  `ConstraintProvider`, the runtime rebuilds the constraints for the clone. That took about 0.9 s per clone
  with constrained decoding and 1 s with the LlGuidance provider, with gemma-4 E2B on win-x64, measured with
  LiteRT-LM v0.18.0.
- **The cut turn is not kept.** The checkpoint holds the conversation as it was before the send, so send the
  question again to get the answer; the partial text the stream produced is yours to show or discard.
  To have the model finish the partial text instead, see
  [Continue a reply that was cut](sessions.md#continue-a-reply-that-was-cut).
- **Clone before, not after.** Take the checkpoint before the send. `Clone()` throws
  `InvalidOperationException` for a conversation whose send was cut short, because the runtime would return an
  empty clone that also answers `CANCELLED`. It also throws after a stream that failed during generation. After
  a blocking send that failed, `Clone()` returns a clone, and whether that clone works depends on where the
  runtime rejected the send:
  - If the runtime rejected the message before the prefill (an image it could not decode, for example), the
    conversation and its clone kept working.
  - If it rejected the prefill itself, the clone failed every send.

  Both cases were measured with LiteRT-LM v0.20.0 and gemma-4 E2B on win-x64, CPU and GPU. `Clone()` also
  needs a conversation that has completed a send (see [Clone a conversation](#clone-a-conversation)).
- **Inline attachments are copied.** The clone copies the message history, so an inline image or audio is copied
  once per clone. A file-path attachment avoids that. This cost was not measured.
- **Earlier reasoning leaves the KV cache.** The runtime removes the reasoning from the KV cache right after
  each reply. After a 966-token reply that held 668 thinking tokens, the conversation held 339 tokens
  (LiteRT-LM v0.20.0, win-x64, CPU, gemma-4 E2B). A clone taken after such a reply holds the same token
  count, without the reasoning (CPU, gemma-4 E2B). The replies a clone generates later were not measured, and
  neither was the GPU.
- **Ring buffers can break a clone's later turns.** In the LiteRT-LM sources, with
  `UseRingbuffersLocalAttention` on, the next turns on a clone can fail with `NOT_FOUND`; this was not
  measured.
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

Other good uses: checkpoint before a risky or expensive turn so you can retry from the checkpoint (see
[Continue after a cancellation](#continue-after-a-cancellation)); A/B two sampler settings or two phrasings
from the same context.

### Caveats

- **Send first, then clone.** `Clone()` duplicates the *prefilled* state, and creating a conversation
  prefills nothing: the system message, tools and `History` only enter the KV cache with the first
  send. Until then, `TokenCount` stays 0, even with a system message, so there is nothing to duplicate,
  and `Clone()` rejects the conversation with `InvalidOperationException`. To keep a reusable base to
  branch from (a system prompt plus tools, say), send one real turn on it first, as the example above does.

  The rejection guards against an older runtime behavior. On LiteRT-LM v0.15.0 and v0.16.0 (CPU and GPU),
  cloning such a conversation worked once, but after another conversation ran on the engine, the parent
  and its later clones continued that other conversation's context instead of their own. With LiteRT-LM v0.20.0 this did not reproduce: the
  conversation and its clones kept their own context in 10 of 10 runs (win-x64; gemma-4 E2B on CPU,
  gemma-4 E4B on GPU).
- **Idle only.** Conversations are not thread-safe. Clone when the source is idle, with no in-flight
  `SendStreamingAsync`.
- **May be unsupported on some backends.** Cloning is implemented by the standard executors and is
  verified on CPU and GPU (win-x64 WebGPU). It is not a CPU-only feature. For an executor that does not
  implement it, the native call returns an error status and `Clone()` throws `LiteRtException`; no backend
  we test with takes this path, so it was not measured. If you target an exotic backend, be ready to catch
  it and fall back to restoring from `History`.
- **In-memory only.** A clone lives in the same process. To carry a conversation across restarts use
  `History`, not `Clone()`.
- **Lifetime.** Dispose clones like any conversation. A clone keeps the native engine loaded, so
  `LiteRtEngine.Load` can refuse a new engine until the last clone is disposed.

## Which one do I want?

| You want to… | Use | Cost |
|---|---|---|
| Resume a chat after the app restarts | `History` / `HistoryJson` | Re-prefills the history |
| Seed a conversation with few-shot examples | `History` / `HistoryJson` | Re-prefills the examples |
| Continue a chat after cancelling a send | `Clone()` before the send | Copies state, no re-prefill; on the GPU, the first send after the cut can take as long as a `History` restore |
| Branch a live conversation into variants | `Clone()` | Copies state, no re-prefill |
| Checkpoint before a risky turn, then retry | `Clone()` | Copies state, no re-prefill |
| Do the same on a raw session, where you write the prompt text | `LiteRtSession.SaveCheckpoint` and `RewindTo`, experimental (see [Sessions](sessions.md#experimental-checkpoints-rewind-and-reset)) | No re-prefill; not after a cancel |

On a conversation, `Clone()` and `History` compose: clone is the same-process fork of live state, and the
checkpoint that survives a cancellation; restore is the durable cross-session reload. On the GPU, a clone's
first send after the original or another conversation generated can take as long as a `History` restore
(see [Continue after a cancellation](#continue-after-a-cancellation)).

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
var terse = chat.Send("Summarize in one line.", attachments: null, options: new LiteRtSendOptions { MaxOutputTokens = 32 });

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
