# Microsoft.Extensions.AI integration (IChatClient)

`LiteRtLmSharp.Extensions.AI` is a **separate, optional companion package** that exposes a LiteRtLmSharp
on-device model as a [`Microsoft.Extensions.AI.IChatClient`](https://learn.microsoft.com/dotnet/ai/ichatclient)
(and an embedding model as an `IEmbeddingGenerator`; see [Embeddings](#embeddings-iembeddinggenerator)).
`IChatClient` is the .NET ecosystem's provider-agnostic abstraction for chat models, so this one package
makes the model usable from:

- **[Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) (MAF)** — `new ChatClientAgent(chatClient, …)`;
- **[Semantic Kernel](https://learn.microsoft.com/semantic-kernel/)** — via the
  [`LiteRtLmSharp.SemanticKernel`](semantic-kernel.md) connector (a thin layer over this `IChatClient`);
- **plain Microsoft.Extensions.AI** — the middleware pipeline (function invocation, caching, telemetry),
  dependency injection, ASP.NET Core, etc.

This is the **foundation**: it depends only on lightweight abstractions
(`Microsoft.Extensions.AI.Abstractions` + `Microsoft.Extensions.DependencyInjection.Abstractions` for the DI
helpers), and the Semantic Kernel connector is built on top of it.

| Package | Depends on | Target |
|---|---|---|
| `LiteRtLmSharp.Extensions.AI` | `LiteRtLmSharp` (same version) + `Microsoft.Extensions.AI.Abstractions` 10.7.0 + `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.9 | `net10.0` |

```xml
<PackageReference Include="LiteRtLmSharp" Version="1.3.0" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="1.3.0" />
<PackageReference Include="LiteRtLmSharp.Extensions.AI" Version="1.3.0" />
```

## Quick start

```csharp
using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;

using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    Backend = LiteRtBackend.Cpu,
    MaxNumTokens = 4096,
});

using IChatClient client = new LiteRtChatClient(engine, modelId: "gemma-4-E2B-it");

// Blocking:
ChatResponse response = await client.GetResponseAsync("Write one upbeat sentence about on-device AI.");
Console.WriteLine(response.Text);

// Streaming:
await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("Tell me a joke"))
    Console.Write(update.Text);
```

### Dependency injection

```csharp
// Container loads, owns and disposes the engine:
services.AddLiteRtChatClient(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm", Backend = LiteRtBackend.Cpu, MaxNumTokens = 4096,
});

// ... or register over an engine you already loaded (you dispose it):
services.AddLiteRtChatClient(engine, modelId: "gemma-4-E2B-it");

// Elsewhere:
IChatClient client = serviceProvider.GetRequiredService<IChatClient>();
```

`eager: true` on the options overload loads the weights at registration, so a bad model path or backend throws
there instead of later. It has no effect when an earlier registration from equal options, or a `LiteRtEngine` you
registered under the same key, already provides the engine: that engine loads when the earlier registration says.
An eager engine that is never resolved lives until the process ends.

### Register several models

To register several models side by side, for example one per agent, give each one a key:

```csharp
services.AddKeyedLiteRtChatClient("fast", new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm", MaxNumTokens = 4096, AllowMultipleEngines = true,
});
services.AddKeyedLiteRtChatClient("smart", new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E4B-it.litertlm", MaxNumTokens = 4096, AllowMultipleEngines = true,
});

IChatClient smart = serviceProvider.GetRequiredKeyedService<IChatClient>("smart");
```

This example loads two engines, because the options differ. Keys with equal options, where every
`LiteRtEngineOptions` property matches, share one engine, so the model loads once. Use that to give one model
several roles:

```csharp
var gemma = new LiteRtEngineOptions { ModelPath = "gemma-4-E2B-it.litertlm", MaxNumTokens = 4096 };

services.AddKeyedLiteRtChatClient("translator", gemma,
    optionsTemplate: new LiteRtConversationOptions { SystemMessage = "Translate the text to Spanish." });
services.AddKeyedLiteRtChatClient("assistant", gemma,
    optionsTemplate: new LiteRtConversationOptions { SystemMessage = "You are a concise assistant." });
```

| | Equal options | Different options |
|---|---|---|
| **Engines loaded** | One, shared by the keys | One per key |
| **`AllowMultipleEngines`** | Not needed | Required on every one of them |
| **Replies** | One at a time across the keys | Can run at the same time, but a second engine adds throughput only on a desktop CPU (measured with LiteRT-LM v0.18.0) |
| **Memory** | One model in memory | Each engine holds its own |
| **`LoraPath` set in a key's template** | Applies to that key | Applies to that key |

Even on a shared engine, a key without `LoraPath` answers normally next to a key with an adapter, because an
adapter applies only to the conversations that set it. This was measured on the CPU backend with LiteRT-LM v0.20.0
on win-x64, using a LiteRT-LM test model and its rank-32 adapter. The GPU backend wasn't measured.

These rules apply to every registration under a key:

- Each key's engine loads when its client or engine is first resolved, not at the first call, unless `eager: true`
  loaded it at registration. If another engine is alive and any of them lacks `AllowMultipleEngines`, that load throws
  `InvalidOperationException`.
- Registering again, under a key or without one, with the same engine or equal options does nothing, and the settings
  of the first registration stay.
- Registering again with a different engine or different options throws `InvalidOperationException`.
- An `IChatClient` or `LiteRtEngine` you registered yourself under the key isn't replaced. Register it before you
  call `AddKeyedLiteRtChatClient`.
- A key registered from options also has its engine under that key. To call `Tokenize`, for example, resolve it
  with `GetRequiredKeyedService<LiteRtEngine>("smart")`.

## Use with Microsoft Agent Framework

MAF builds agents over any `IChatClient` — there is no separate "MAF connector" to write:

```csharp
using Microsoft.Agents.AI;

var agent = new ChatClientAgent(client, instructions: "You are a concise, helpful assistant.");
AgentResponse reply = await agent.RunAsync("What can you do?");
```

With [stateful conversations](#stateful-conversations-opt-in) enabled, an agent session keeps the
`ConversationId` the client returns. If a run is cancelled or fails while the model is generating, the client
discards that run's turn, and the session continues from the turn before it. Some cases end the conversation
instead, and [Cancelled and failed sends](#cancelled-and-failed-sends) lists them.

> **Note:** One of those cases is `EnableConstrainedDecoding`, which is recommended for tools. A run cancelled while
> the model is generating ends a session that uses it, unless you set `CheckpointConstrainedConversations`, which adds
> about a second to every turn (measured with LiteRT-LM v0.18.0).

A stateful conversation lives in memory, so the session does not survive a process restart. To persist sessions,
use the default stateless mode, where Agent Framework keeps the messages.

## Use with the Microsoft.Extensions.AI pipeline

Because it's a standard `IChatClient`, it composes with the ecosystem's middleware (from the
`Microsoft.Extensions.AI` package) — caching, telemetry, logging, and **function invocation**:

```csharp
IChatClient pipeline = client.AsBuilder()
    .UseFunctionInvocation()      // auto-invokes AIFunctions returned in ChatOptions.Tools
    // .UseDistributedCache(...)  // caching, telemetry, logging, … all compose here
    .Build();
```

## Function calling (tools)

Function tools in `ChatOptions.Tools` are passed to the model; the model's tool calls are surfaced as
`FunctionCallContent` (with `ChatFinishReason.ToolCalls`). Compose `UseFunctionInvocation()` and the pipeline
runs the loop: the model emits a call → the matching `AIFunction` is invoked → its result is returned to the
model → the model answers from it.

```csharp
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;

AIFunction getWeather = AIFunctionFactory.Create(
    (string city) => $"22°C and sunny in {city}",
    name: "get_weather", description: "Gets the current weather for a city.");

IChatClient client = new LiteRtChatClient(engine)
    .AsBuilder()
    .UseFunctionInvocation()      // drives the tool loop
    .Build();

var options = new LiteRtChatOptions
{
    Tools = [getWeather],
    EnableConstrainedDecoding = true,   // recommended (see below); off by default
};

ChatResponse response = await client.GetResponseAsync("What's the weather in Paris?", options);
Console.WriteLine(response.Text);       // "The weather in Paris is 22°C and sunny."
```

**Constrained decoding.** Set `LiteRtChatOptions.EnableConstrainedDecoding = true` so the model emits valid,
schema-shaped tool-call arguments — strongly recommended for small on-device models. It is off by default and
works on every platform (the linux-x64 restriction of earlier releases is gone: the official LiteRT-LM v0.16.0
prebuilts embed the constraint provider). Tools still work without it; arguments are just not
grammar-constrained. On a plain `ChatOptions`, set the `enable_constrained_decoding` key in
`AdditionalProperties` instead.

**Tool choice (`ChatOptions.ToolMode`).** The connector emulates the MEAI modes as best it can. The C API
declares a native `tool_choice` setting, but the connector doesn't use it, because on LiteRT-LM v0.20.0
(measured on win-x64) it doesn't force a call:

- Without constrained decoding, the runtime accepts every value and ignores it.
- With constrained decoding, every value other than `auto` fails with `FAILED_PRECONDITION`.

The modes map as follows:

| `ToolMode` | Behavior |
|---|---|
| `Auto` (default) | All tools are offered; the model decides whether to call one. |
| `None` | **No** tools are offered, so the model can't call any. |
| `RequireAny` | All tools offered **+ a system-prompt instruction** to call one — *best-effort*. |
| `RequireSpecific(name)` | **Only that tool** is offered, plus the instruction naming it — *best-effort*. |

`RequireAny` / `RequireSpecific` are **best-effort, not a guarantee**: unlike a cloud API's server-enforced
`tool_choice: "required"`, the connector can only *instruct* the on-device model (and narrow the tool list) —
the decoder isn't forced. In practice a capable model calls the tool when the request is plausibly related to
it, but may ignore the instruction for a clearly-unrelated prompt. (Semantic Kernel's
`FunctionChoiceBehavior.Auto()/None()/Required()` map to these same modes.)

> The native [LiteRtLmSharp tools API](chat.md#function-calling) is still available for full control
> (constrained decoding, custom tool-call parsing); the chat client is the MEAI-idiomatic path on top of it.

## Structured output (`ResponseFormat`)

A JSON-schema `ChatOptions.ResponseFormat` is **enforced during sampling**: the client arms the LlGuidance
constraint provider on the conversation and attaches the schema as a per-send constraint, so every token the model emits is masked to schema-conforming continuations — the
reply is *guaranteed* to be a JSON document matching the schema, not just nudged toward it.

```csharp
ChatResponse r = await client.GetResponseAsync(messages, new ChatOptions
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema(mySchemaJsonElement),
});
// r.Text parses as JSON conforming to mySchemaJsonElement — enforced, not prompted.
```

Rules and caveats:

- **Schema-less `ChatResponseFormat.Json` ("JSON mode") is not enforced** — without a schema there is
  nothing precise to constrain, so it keeps the previous prompt-driven behavior.
- **Not combinable with `Tools` on one request.** A request carrying both throws `ArgumentException`.
  Through the C API, which has no such check, a send that carried both tools and a regex constraint
  returned plain text with no tool call (measured once, LiteRT-LM v0.20.0, win-x64, CPU, gemma-4 E2B).
  Run the tool phase first, then request the schema-formatted answer in a separate call.
  In stateful mode both phases can share one conversation: set `ConstraintProvider` on the client's
  conversation-options template, run the tool request without a `ResponseFormat`, then send the
  schema-formatted request as a continuation of the same `ConversationId` — the reply is
  schema-enforced and informed by the tool results (covered by a model test).
- **Stateful mode: the schema must be present on the conversation's first call** (the constraint
  provider is fixed at creation). A schema arriving only on a continuation throws `ArgumentException`
  and leaves the conversation resumable; to make every conversation schema-capable, set
  `LiteRtConversationOptions.ConstraintProvider = LiteRtConstraintProvider.LlGuidance` on the
  client's conversation-options template.
- When a schema request is active, the client drops the tool-calling `enable_constrained_decoding`
  knob for that conversation. That is the binding's rule: it uses one constrained-decoding mode per
  conversation, and the tool path has no use without tools. The runtime itself accepts both modes on
  one conversation, but whether both take effect together wasn't measured.

## Banning repeats and tokens: `NoRepeatNgramSize` / `SuppressTokens`

`ChatOptions.FrequencyPenalty` / `PresencePenalty` already map to the native repetition penalties. Two more
logit processors have no MEAI property, so `LiteRtChatOptions` exposes them, backed by the
`no_repeat_ngram_size` and `suppress_tokens` keys in `AdditionalProperties` (a plain `ChatOptions` with those
keys works too, including options deserialized from JSON/YAML):

```csharp
int[] banned = [.. engine.Tokenize(" Paris"), .. engine.Tokenize("Paris")];   // both surface forms
var options = new LiteRtChatOptions
{
    MaxOutputTokens = 128,
    NoRepeatNgramSize = 3,      // never repeat a 3-gram already produced in this reply
    SuppressTokens = banned,    // these ids can never be sampled
};
```

- `NoRepeatNgramSize` bans any n-gram of that many tokens the reply already produced (whole-reply window).
  Useful when a small model echoes a prompt template verbatim. Zero or negative is rejected.
- `SuppressTokens` forces the listed ids' logits to `-inf` on every step. Find ids with `LiteRtEngine.Tokenize`;
  most words tokenize differently with and without a leading space, so ban both. Negative ids are rejected;
  ids outside the vocabulary are ignored natively.
- Both apply per request. The Semantic Kernel connector exposes the same two knobs on
  `LiteRtPromptExecutionSettings`.

## Reasoning ("thinking")

Enable the model's reasoning mode with **`LiteRtChatOptions`** — a `ChatOptions` subtype that adds the knobs
MEAI has no typed property for (`EnableThinking`, `EnableConstrainedDecoding`, `NoRepeatNgramSize`,
`SuppressTokens`, each backed by a key in `AdditionalProperties`). This mirrors the Semantic Kernel
connector's `LiteRtPromptExecutionSettings`:

```csharp
var options = new LiteRtChatOptions { MaxOutputTokens = 512, EnableThinking = true };
ChatResponse r = await client.GetResponseAsync(messages, options);
```

(`LiteRtChatOptions` *is* a `ChatOptions`, so it works anywhere one is accepted — `ChatOptions` already exposes
`Temperature`/`TopP`/`TopK`/`MaxOutputTokens`/`Seed`. Setting the `enable_thinking` key directly on
`ChatOptions.AdditionalProperties` is equivalent.)

The reasoning trace is surfaced as a **`TextReasoningContent`** on the response (and as reasoning updates
when streaming). It is excluded from `ChatResponse.Text`, so the answer stays clean while the reasoning
stays accessible:

```csharp
string answer = r.Text;
string? reasoning = r.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().FirstOrDefault()?.Text;
```

**The reasoning shares the `MaxOutputTokens` budget with the answer.** Give thinking models headroom — with
too small a budget the reasoning can consume it and the answer comes back empty. When that happens the
response carries the reasoning and a `FinishReason` of `Length`, so an empty answer is diagnosable rather
than silent:

```csharp
if (r.FinishReason == ChatFinishReason.Length && string.IsNullOrWhiteSpace(r.Text))
    Console.WriteLine("(no answer — the reasoning consumed the budget; raise MaxOutputTokens)");
```

## Conversation-options template (per-client)

`ChatOptions` covers the per-request knobs (sampler, `MaxOutputTokens`, tools, thinking), but a few
conversation-level settings have no MEAI surface: `SystemMessage`, `LoraPath` / `AudioLoraPath`,
`StreamToolCalls`, `VisualTokenBudget`, `FilterThinkingFromKvCache`, `ExtraContext`,
`EnableSpeculativeDecoding`, and a session-default `MaxOutputTokens`. Supply them once as a **per-client template** (`LiteRtConversationOptions`) on the
constructor or the DI registration, and they apply to every call:

```csharp
using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;

var template = new LiteRtConversationOptions
{
    SystemMessage = "You are a terse, on-device assistant.",
    VisualTokenBudget = 70,           // ~70 tokens per image instead of ~260 (Gemma 4: 70, 140 or 280)
    FilterThinkingFromKvCache = true, // optional: with LiteRT-LM v0.20.0 the runtime drops reasoning from the context anyway (measured on CPU)
};

using IChatClient client = new LiteRtChatClient(engine, modelId: "gemma-4-E2B-it", optionsTemplate: template);

// ... or via DI (the template flows to the registered client):
services.AddLiteRtChatClient(engineOptions, optionsTemplate: template);
```

The merge is per-call-wins: any value the request's `ChatOptions` supplies (sampler, thinking, constrained
decoding, tools) overrides the template, and the template fills the rest. The `SystemMessage` rule is the
one to note: a system message on the request (the leading `system` chat message) always wins, and the
template's `SystemMessage` is used **only** when the request carries none, so there are never two system
turns. The template must not set `History` or `HistoryJson` (history is always per-call); doing so throws at
construction (or at registration, for the DI overloads).

When the template sets `StreamToolCalls = true`, the streaming path surfaces each raw tool-call fragment as a
content-less `ChatResponseUpdate` whose `AdditionalProperties` carries the fragment under the key
`litertlm.tool_call_delta`. Use it for progress display only, and act on the complete `FunctionCallContent`
that the following tool-call update carries. Without `StreamToolCalls` no such updates are emitted, so it is
invisible unless you opt in.

## Stateful conversations (opt-in)

By default the client is **stateless**: it rebuilds a fresh `LiteRtConversation` from the full message list
every call (see [Design](#design)). You can instead opt into **stateful conversations**, where the client
keeps the live native conversation alive between calls and resumes it, so each turn re-prefills only the new
messages rather than the whole thread. This is Microsoft.Extensions.AI's canonical stateful-provider contract,
built on `ChatResponse.ConversationId` / `ChatOptions.ConversationId`.

Pass a `LiteRtStatefulConversationOptions` to the constructor (or a DI registration) to turn it on:

```csharp
using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;

using IChatClient client = new LiteRtChatClient(
    engine, modelId: "gemma-4-E2B-it",
    statefulConversations: new LiteRtStatefulConversationOptions());

// ... or via DI (the option flows to the registered client):
services.AddLiteRtChatClient(engineOptions, statefulConversations: new LiteRtStatefulConversationOptions());
```

Then follow the canonical MEAI loop: the first response carries a `ConversationId`; set it on the options and
send only the new user message on each subsequent turn.

```csharp
var options = new ChatOptions();
List<ChatMessage> outgoing = [new(ChatRole.User, "Remember that my favorite color is teal.")];

while (true)
{
    ChatResponse response = await client.GetResponseAsync(outgoing, options);
    Console.WriteLine(response.Text);

    // Reuse the id and send ONLY the next turn's new messages (the live conversation keeps the rest):
    options.ConversationId = response.ConversationId;
    outgoing = [new(ChatRole.User, Console.ReadLine()!)];
}
```

Because the response now carries a non-null `ConversationId`, `UseFunctionInvocation()` becomes incremental
too: `FunctionInvokingChatClient` sends only the new function-result message(s) on its next iteration (with
the id set) instead of replaying the accumulated history, so multi-round tool loops resume the same live
conversation with no extra work on your part.

What is fixed once a conversation is created (and therefore **ignored** on a continuation, i.e. any call that
carries a `ConversationId`):

- the sampler, thinking mode, tools, constrained decoding, the system message, and any template values;
- options that map to native **per-send** settings still apply on a continuation: `MaxOutputTokens`,
  `FrequencyPenalty`/`PresencePenalty`, and a JSON-schema `ResponseFormat` (see
  [Structured output](#structured-output-responseformat): the conversation must have carried a schema on its **first** call, or the client's options template
  must set `ConstraintProvider`; otherwise the continuation throws `ArgumentException` and the conversation
  stays resumable);
- a **system message on a continuation throws** `InvalidOperationException` (the preface cannot be rewritten).
  To change any fixed setting, start a new conversation by omitting `ConversationId`.

### Conversation lifetime

- The client keeps live conversations in an LRU cache of `MaxLiveConversations` entries (8 by default). Creating
  one more disposes the least recently used one, and its id stops working.
- A request with an id that stopped working, or that this client never issued, throws `ArgumentException`.
- Disposing the client disposes every live conversation. Conversations don't expire with time, so size the cap
  for your concurrency.

### Cancelled and failed sends

A send that is cut short [leaves its native conversation unusable](conversation-state.md#continue-after-a-cancellation).
To protect you from that, the client takes a checkpoint, a `Clone()` of the conversation, before each turn. If a
send of the turn is cancelled or fails, the conversation goes back to that checkpoint, and its `ConversationId` keeps
working.

A *turn* starts with a user message and ends with the first reply that has no tool calls. Without tools, a turn
is one message and its reply. With tools, it spans several sends: the message, the model's tool call, your
results, and the final reply.

In this example, the second turn is cancelled and the conversation continues from the first one:

```csharp
var options = new ChatOptions();
ChatResponse first = await client.GetResponseAsync("My name is Ada.", options);
options.ConversationId = first.ConversationId;

using var cts = new CancellationTokenSource();
try
{
    // Something cancels this while the model is replying.
    await client.GetResponseAsync("Write a long poem about trains.", options, cts.Token);
}
catch (OperationCanceledException)
{
    // The poem request and its partial reply are gone. Send the message again if you still want the poem.
}

// The id still works, and the conversation holds the first turn.
ChatResponse next = await client.GetResponseAsync("What is my name?", options);   // "Ada"
```

With tools, the client discards the whole turn, not only the send that was cut. Suppose a user asks "What's the
weather in Paris?":

1. The client sends the message, and the model calls `get_weather`.
2. Your tool runs, and the results go back to the client.
3. The send of those results is cancelled while the model writes its answer.

The question, the tool call and the results are all discarded. This matches what Agent Framework keeps after a
failed run. Send the question again to get the answer, which runs the tool again. If your code still holds the
tool calls of the discarded turn, sending their results throws `ArgumentException`, and the id keeps working.

The result depends on when the send stops:

| What happened | The turn | The id | What to do next |
|---|---|---|---|
| A send is cancelled or fails | Discarded | Keeps working | Send the message again |
| You stop reading a stream before the reply ends | Discarded | Keeps working | Send the message again |
| You stop reading a stream after the reply ends, for example once you have its tool calls | Kept | Keeps working | Continue as usual |
| A tool loop or agent run ends while no send is running, for example cancelled while a tool executes | Kept, because there is no send to undo | Keeps working | Send the tool results or a new message |

A send that the overflow guard rejects follows [Context limit](#context-limit) instead: a message or tool results that
don't fit leave the turn as it was.

Some turns can't go back to a checkpoint. If a send of one of these turns is cancelled or fails, the conversation
ends: its id stops working, and using it throws `ArgumentException`, so start a new conversation.

| Turn | Why |
|---|---|
| The first turn of a conversation, tool calls included | Nothing comes before it. This applies even when stream updates already carried the id |
| Any turn of a conversation with constrained decoding or a constraint provider, by default | Cloning it is slow, as the next paragraph explains |
| A turn that starts with tool results, when no earlier turn is waiting for them | A retry would repeat the discarded tool results, and this client's rollback of such a turn was not measured. Through the core conversation API, sending the tool results again on a checkpoint taken before a cut send gave the same reply as an uncut turn (LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and GPU, one tool and one prompt). With LiteRT-LM v0.18.0, repeating a discarded send gave a wrong reply on the GPU (see **Sending the same message again** under "Other things to know"). A fork doesn't inherit its parent's waiting turn, so results sent on a branch count |
| Any turn on a backend that cannot clone | The checkpoint is a clone |

**Constrained conversations.** Cloning a conversation with `EnableConstrainedDecoding`, a `ConstraintProvider`, or a
JSON-schema `ResponseFormat` on its first call rebuilds its constraints, which takes about a second (measured with
LiteRT-LM v0.18.0). The client would pay that on every turn,
whether or not the turn is cancelled, so by default it takes no checkpoint on these conversations. To keep their id
after a cancelled send, accept that cost and turn the checkpoint on:

```csharp
using IChatClient client = new LiteRtChatClient(engine, statefulConversations: new LiteRtStatefulConversationOptions
{
    CheckpointConstrainedConversations = true,
});
```

**Other things to know:**

- **Cost.** On a text conversation, the checkpoint costs no measurable time or memory until it is used. Inline
  attachments are copied once per checkpoint, which a file-path attachment avoids.
- **Cost of a rollback on the GPU.** On the GPU backend, going back to a checkpoint saved the replay of the
  thread only when nothing else had generated in between. When the original conversation or another one had
  generated in between, a checkpoint's first send was as slow as restoring the same conversation from `History`,
  or slower. This was measured on clones of a conversation directly, not through the chat client, with LiteRT-LM
  v0.20.0 on win-x64 (NVIDIA RTX 3080, gemma-4 E2B, `Float32`, a 2,293-token conversation). The CPU backend
  wasn't measured for this case.
- **Sending the same message again.** The client sends a repeated message as it is. With LiteRT-LM v0.20.0 on
  the GPU backend, a checkpoint's first send that repeated the discarded message, after another conversation had
  generated, gave a correct reply in 100 of 100 runs on win-x64 (NVIDIA RTX 3080, gemma-4 E2B, `Float32`). Other
  GPUs, `Float16`, and other models weren't measured. With v0.18.0, the same case gave a wrong reply
  ([LiteRT-LM #4394](https://github.com/google-ai-edge/LiteRT-LM/issues/4394)).
- **Tool calls in a stream.** In the stateful mode, a stream delivers a reply's tool calls together when the reply
  ends, after any text in it. That is why stopping at them keeps the turn.
- **Reasoning.** The runtime removes a reply's reasoning from the KV cache right after the reply, so a checkpoint
  holds no earlier reasoning: a clone taken after a reply with reasoning had the token count without it. This was
  measured on the CPU backend (LiteRT-LM v0.20.0, win-x64, gemma-4 E2B). A rollback of this client after such a
  reply, the GPU backend, and replies that a checkpoint generates later weren't measured.

### Forking a conversation

In the stateful mode the client also offers a provider-specific **forking** hatch: branch a live conversation
into an independent copy that shares the parent's prefilled context (a cheap native KV-cache clone — no
re-prefill of the shared prefix) and diverges from there. Useful for exploring several continuations of one
prompt (best-of-N, A/B prompting, tree search) without re-establishing the common prefix each time.

```csharp
var branching = (LiteRtConversationBranching)client.GetService(typeof(LiteRtConversationBranching))!;

ChatResponse seeded = await client.GetResponseAsync(seedMessages, options);
string branchId = await branching.ForkAsync(seeded.ConversationId!);

// The branch id behaves like any other ConversationId: resume it, fork it again, or let the cache drop it.
options.ConversationId = branchId;
```

`GetService(typeof(LiteRtConversationBranching))` returns the hatch only in the stateful mode (`null`
otherwise — there are no live conversations to fork). The fork counts toward `MaxLiveConversations` like any
other live conversation, and it inherits the parent's synthesized-call-id map, so function-calling
continuations keep resolving on the branch.

### Context limit

`ChatResponse.Usage.TotalTokenCount` in this mode counts every token the conversation holds in its KV cache, the
model's working memory of the thread, so it grows across the thread rather than resetting per call as in the
stateless mode.

**The context limit is a hard wall, so let the guard police it.** A long stateful thread, a multi-round tool
loop especially, eventually approaches the engine's `MaxNumTokens`. Load the engine with an **explicit**
`MaxNumTokens`, as the snippets above do, to arm LiteRtLmSharp's overflow guard: replies are clamped to the
remaining context, and a send that no longer fits throws `LiteRtContextOverflowException`. Sends carrying media get
only the conversation-full check, because LiteRtLmSharp can't measure their token cost ahead of time, so leave
headroom when attachments are in play.

Without the guard, the runtime stops a reply when the context is full and rejects the next send with
`RESOURCE_EXHAUSTED`. It also rejects a message that doesn't fit, or that lands in the last free entries of the
context, and after that every send on the conversation fails with `INTERNAL`. No crash was seen. This was
measured with LiteRT-LM v0.20.0 on win-x64 with gemma-4 E2B, on the CPU and, in the cases measured there, on the
GPU.

The guard rejects a send for one of two reasons. Both throw `LiteRtContextOverflowException`, and the start of its
message tells them apart:

| Message starts with | The conversation | What to do next |
|---|---|---|
| `This message does not fit` | Untouched, because no native work happened | Retry the same `ConversationId` with a shorter message or shorter tool results |
| `The conversation's context is full` | Disposed, and its id stops working | Start a new conversation, carrying over a summary or a trimmed history |

The second rejection has one exception. On a send of tool results, when the checkpoint from before the turn has room,
the conversation goes back to that checkpoint as in [Cancelled and failed sends](#cancelled-and-failed-sends), and its
id keeps working.

You don't have to wait for that exception to find out. A reply that filled the context, or that the guard cut at the
context limit, carries `ChatFinishReason.Length` in the same turn, on the blocking response and on the final update
of a clamped stream, which otherwise stops without a signal. It takes priority over `ToolCalls` even when the
response carries tool calls, because you shouldn't continue a reply that was cut at the limit as it is.

`Length` doesn't always mean that the next send throws. After a reply with reasoning, the runtime removes the
reasoning from the KV cache, so the context can have room again and the thread can continue;
`Usage.TotalTokenCount` shows how much the conversation holds. For example, with LiteRT-LM v0.20.0 on
win-x64 (gemma-4 E2B, `MaxNumTokens` 1,024), a reply cut at 966 tokens finished with `Length` and left the
conversation holding:

| Backend | Reasoning tokens in the reply | Tokens held after the reply |
|---|---|---|
| CPU | 668 | 339 |
| GPU (NVIDIA RTX 3080, `Float32`) | 818 | 189 |

`FunctionInvokingChatClient` doesn't check the finish reason and keeps looping on tool calls, so an unattended tool
loop can still end in the exception. To act before you hit the wall, track `Usage.TotalTokenCount` against
`MaxNumTokens` and wind down early.

## Multimodal (image / audio)

On a multimodal model, attach an image or audio clip to a user message as a `DataContent` (inline
bytes) or a file-path `UriContent`, with an `image/*` or `audio/*` media type:

```csharp
using Microsoft.Extensions.AI;

byte[] png = File.ReadAllBytes("photo.png");
var message = new ChatMessage(ChatRole.User,
[
    new TextContent("What is in this image?"),
    new DataContent(png, "image/png"),
]);

ChatResponse response = await client.GetResponseAsync([message]);
```

The engine must have been loaded with the matching modality enabled — `LiteRtEngineOptions.VisionBackend` for
images, `AudioBackend` for audio — on a multimodal model (e.g. the Gemma 4 E-series). Without it the send
throws with a message naming the likely cause.

In the stateless (default) mode, the client also sends the media of earlier user turns with the restored
history, so the model can answer about an image from an earlier turn. This was measured with an image, with
LiteRT-LM v0.20.0 on win-x64 (gemma-4 E2B, CPU and GPU). The overflow guard doesn't count the tokens of
those earlier images. With `MaxNumTokens` set to 1,024, a long reply to such a request ran to the end of the
context instead of stopping at the guard's margin, and it finished with `ChatFinishReason.Length` (same
conditions). Remote (non-`file://`) URIs are skipped — the on-device engine cannot fetch them, so supply bytes
or a local file.

## Token usage

Every response carries `ChatResponse.Usage`. `TotalTokenCount` — what the conversation's KV cache holds after the
reply, which leaves a reply's reasoning out — is **always set, at no cost**:

```csharp
ChatResponse response = await client.GetResponseAsync("Hello", options);
long? total = response.Usage?.TotalTokenCount;   // e.g. to track how full the context window is
```

The input/output split (`InputTokenCount` / `OutputTokenCount`) is populated **only when the engine was loaded
with `EnableBenchmark = true`** — it comes from the engine's benchmark counters (the overhead is just timing
bookkeeping). Without it those stay `null`, and a note is left under `response.AdditionalProperties` (key
`litertlm.usage_note`) explaining how to enable them.

`InputTokenCount` is the prefill of the send's own messages (`LiteRtBenchmarkInfo.LastSendPrefillTokenCount`). After
a reply with reasoning, the runtime records the refill of the KV cache as a later prefill turn; `InputTokenCount`
leaves that refill out. This holds on CPU and GPU with LiteRT-LM v0.20.0 (win-x64, gemma-4 E2B).

`TotalTokenCount` is what the KV cache holds after the reply, and the runtime removes a reply's reasoning from the
KV cache, while `OutputTokenCount` counts every decoded token, reasoning included. So after a reply with reasoning,
`TotalTokenCount` can be lower than `InputTokenCount` plus `OutputTokenCount`. For example, after a 966-token reply
that held 668 thinking tokens, the conversation held 339 tokens (LiteRT-LM v0.20.0, win-x64, CPU, gemma-4 E2B).

```csharp
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "…", Backend = LiteRtBackend.Cpu, EnableBenchmark = true,   // also enables Input/OutputTokenCount
});
// …
long? input = response.Usage?.InputTokenCount;     // prefill tokens of this send
long? output = response.Usage?.OutputTokenCount;   // decode tokens
```

When streaming, the usage arrives as a final `UsageContent` update, which MEAI aggregates into the response's `Usage`.

## Embeddings (`IEmbeddingGenerator`)

`LiteRtEmbeddingGenerator` exposes an embedding model, such as EmbeddingGemma 2, as an
`IEmbeddingGenerator<string, Embedding<float>>`, and `AddLiteRtEmbeddingGenerator` registers it next to the
chat client:

```csharp
services.AddLiteRtEmbeddingGenerator(
    new LiteRtEmbeddingEngineOptions { ModelPath = "embeddinggemma-2-text-270m.litertlm" },
    modelId: "embeddinggemma-2-text-270m");
```

An embedding engine does not count toward the one-live-engine rule, so it coexists with the chat engine.
Options, task instructions and measurements are in the [Embeddings guide](embeddings.md#microsoftextensionsai).

## Design

- **Stateless by default.** `IChatClient` hands the full message list every call, so the client rebuilds a
  *fresh* `LiteRtConversation` each time — prior messages restored as
  [`History`](conversation-state.md) (replayed through prefill), the final user turn sent. This keeps the
  caller's history and the model's KV cache in lockstep. The cost is an `O(history)` prefill per turn; for
  very long chats, opt into [stateful conversations](#stateful-conversations-opt-in) (keep the live native
  conversation alive between calls and re-prefill only the new turn) or drive the native
  [`LiteRtConversation`](conversation-state.md) API directly.
- **Serialized per engine.** Every client over one engine shares a gate, so one generation runs at a time per
  engine. The gate is the binding's choice, and a `LiteRtConversation` isn't thread-safe. Through the core
  conversation API, which has no gate, with gemma-4 E2B on win-x64 and LiteRT-LM v0.20.0, concurrent sends on
  different conversations of one engine gave correct replies on the CPU, where the runtime queued them per
  engine, and, with blocking sends, on the GPU. Clients over different engines (loaded with
  `AllowMultipleEngines`) do not wait for each other. A stream holds the gate until you read it to the end or
  dispose it, so calling any client over the same engine (or `ForkAsync`) from inside
  the loop that reads the stream waits forever: finish or dispose the stream first.
- **Engine ownership.** Pass a `LiteRtEngine` you own (you dispose it), or register from
  `LiteRtEngineOptions` so the container loads/owns/disposes the engine. The same
  `AddLiteRtChatClient(options)` registration makes the `IChatClient` available to MAF, Semantic Kernel and
  plain MEAI at once. Disposing an engine while one of its conversations is alive is safe: the native engine
  stays loaded until the last of them is disposed.
- **Message roles.** `system` / `user` / `assistant` / `tool` are handled. The list must end with a user
  message, or a tool message (the function-calling continuation, appended by `UseFunctionInvocation()` /
  Semantic Kernel); the assistant tool-call turn is restored as history and the tool results are returned.
  A `system` message is restored through the [`History`](conversation-state.md) path (as a leading
  `LiteRtMessage.System(...)`).
- **Engine options.** Any `LiteRtEngineOptions` you pass to `AddLiteRtChatClient` / `new LiteRtChatClient`
  flows straight through, including `NumThreads` / `AudioNumThreads` and the LoRA ranks. Engine-level
  settings such as `ActivationDataType` (`Float32` by default since 1.3.0, see [Engine tuning](engine-tuning.md)) are fixed when the engine
  loads, so they go in those options, not in `ChatOptions`. The same holds for
  `LiteRtEmbeddingEngineOptions` and `AddLiteRtEmbeddingGenerator`.
- **Testing your app code.** `IChatClient` is the intended seam for unit tests: have your code depend on
  `IChatClient` and substitute a mock/stub in tests — no model file or native binaries needed. The core
  types (`LiteRtEngine` / `LiteRtConversation`) are sealed and bound to the native runtime; code that
  drives them directly is best covered by wrapping them behind your own abstraction, or by
  integration tests against a real model.

## Scope

- **Tool calling** is supported (see [Function calling](#function-calling-tools)).
- **Embeddings** are supported through `LiteRtEmbeddingGenerator` (see
  [Embeddings](#embeddings-iembeddinggenerator)).
- **Not AOT/trim-clean.** The core `LiteRtLmSharp` package stays AOT/trim-friendly; this companion does not
  carry that guarantee.

## Sample

The Semantic Kernel console sample in [`samples/SemanticKernel`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/samples/SemanticKernel) also resolves
the underlying `IChatClient` from the kernel — see [docs/semantic-kernel.md](semantic-kernel.md) for the
Semantic Kernel layer.
