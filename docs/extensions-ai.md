# Microsoft.Extensions.AI integration (IChatClient)

`LiteRtLmSharp.Extensions.AI` is a **separate, optional companion package** that exposes a LiteRtLmSharp
on-device model as a [`Microsoft.Extensions.AI.IChatClient`](https://learn.microsoft.com/dotnet/ai/ichatclient).
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
<PackageReference Include="LiteRtLmSharp" Version="0.1.0-preview.3" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="0.1.0-preview.3" />
<PackageReference Include="LiteRtLmSharp.Extensions.AI" Version="0.1.0-preview.3" />
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
// Container loads, owns and disposes a single shared engine (one engine per process):
services.AddLiteRtChatClient(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm", Backend = LiteRtBackend.Cpu, MaxNumTokens = 4096,
});

// ... or register over an engine you already loaded (you dispose it):
services.AddLiteRtChatClient(engine, modelId: "gemma-4-E2B-it");

// Elsewhere:
IChatClient client = serviceProvider.GetRequiredService<IChatClient>();
```

`eager: true` on the options overload loads the weights at registration (so a bad model path/backend throws
there) instead of lazily on first use.

## Use with Microsoft Agent Framework

MAF builds agents over any `IChatClient` — there is no separate "MAF connector" to write:

```csharp
using Microsoft.Agents.AI;

var agent = new ChatClientAgent(client, instructions: "You are a concise, helpful assistant.");
AgentRunResponse reply = await agent.RunAsync("What can you do?");
```

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
schema-shaped tool-call arguments — strongly recommended for small on-device models. It is **off by default**
because the core throws `PlatformNotSupportedException` for it on **linux-x64** (a temporary upstream
constraint-provider bug, [google-ai-edge/LiteRT-LM#2149](https://github.com/google-ai-edge/LiteRT-LM/issues/2149));
leave it off there — tools still work, arguments are just not grammar-constrained. On a plain `ChatOptions`,
set the `enable_constrained_decoding` key in `AdditionalProperties` instead.

**Tool choice (`ChatOptions.ToolMode`).** The native API has no `tool_choice` parameter (the model always
decides), so the connector emulates the MEAI modes as best it can:

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

> The native [LiteRtLmSharp tools API](../README.md#function-calling) is still available for full control
> (constrained decoding, custom tool-call parsing); the chat client is the MEAI-idiomatic path on top of it.

## Reasoning ("thinking")

Enable the model's reasoning mode with **`LiteRtChatOptions`** — a `ChatOptions` subtype that adds the one knob
MEAI has no typed property for (it backs the `enable_thinking` key). This mirrors the Semantic Kernel
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
`StreamToolCalls`, `VisualTokenBudget`, `FilterThinkingFromKvCache`, `ExtraContext`, and a session-default
`MaxOutputTokens`. Supply them once as a **per-client template** (`LiteRtConversationOptions`) on the
constructor or the DI registration, and they apply to every call:

```csharp
using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;

var template = new LiteRtConversationOptions
{
    SystemMessage = "You are a terse, on-device assistant.",
    VisualTokenBudget = 256,          // cap what an image costs during prefill
    FilterThinkingFromKvCache = true, // keep long reasoning out of later turns' context
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

## Multimodal (image / audio)

On a multimodal model, attach an image or audio clip to the final user message as a `DataContent` (inline
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

Only the **final** (triggering) user message's media is sent; media on earlier history turns is not replayed
(the stateless connector restores prior turns as text). Remote (non-`file://`) URIs are skipped — the
on-device engine cannot fetch them, so supply bytes or a local file.

## Token usage

Every response carries `ChatResponse.Usage`. `TotalTokenCount` — the turn's prompt + reply, read from the
conversation's KV cache — is **always set, at no cost**:

```csharp
ChatResponse response = await client.GetResponseAsync("Hello", options);
long? total = response.Usage?.TotalTokenCount;   // e.g. to track how full the context window is
```

The input/output split (`InputTokenCount` / `OutputTokenCount`) is populated **only when the engine was loaded
with `EnableBenchmark = true`** — it comes from the engine's benchmark counters (the overhead is just timing
bookkeeping). Without it those stay `null`, and a note is left under `response.AdditionalProperties` (key
`litertlm.usage_note`) explaining how to enable them:

```csharp
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "…", Backend = LiteRtBackend.Cpu, EnableBenchmark = true,   // also enables Input/OutputTokenCount
});
// …
long? input = response.Usage?.InputTokenCount;     // prefill tokens
long? output = response.Usage?.OutputTokenCount;   // decode tokens
```

When streaming, the usage arrives as a final `UsageContent` update, which MEAI aggregates into the response's `Usage`.

## Design

- **Stateless.** `IChatClient` hands the full message list every call, so the client rebuilds a *fresh*
  `LiteRtConversation` each time — prior messages restored as
  [`History`](conversation-state.md) (replayed through prefill), the final user turn sent. This keeps the
  caller's history and the model's KV cache in lockstep. The cost is an `O(history)` prefill per turn; for
  very long chats, drive the native [`LiteRtConversation`](conversation-state.md) API directly.
- **Serialized.** LiteRtLmSharp allows one live engine per process and conversations are not thread-safe, so
  the client serializes calls through an internal `SemaphoreSlim`.
- **Engine ownership.** Pass a `LiteRtEngine` you own (you dispose it), or register from
  `LiteRtEngineOptions` so the container loads/owns/disposes a single shared engine. The same
  `AddLiteRtChatClient(options)` registration makes the `IChatClient` available to MAF, Semantic Kernel and
  plain MEAI at once.
- **Message roles.** `system` / `user` / `assistant` / `tool` are handled. The list must end with a user
  message, or a tool message (the function-calling continuation, appended by `UseFunctionInvocation()` /
  Semantic Kernel); the assistant tool-call turn is restored as history and the tool results are returned.
  A `system` message is restored through the [`History`](conversation-state.md) path (as a leading
  `LiteRtMessage.System(...)`), so this connector was **never** affected by the pre-v0.14.0
  `LiteRtConversationOptions.SystemMessage` bug; it does not use that property.
- **Engine options.** Any `LiteRtEngineOptions` you pass to `AddLiteRtChatClient` / `new LiteRtChatClient`
  flows straight through, including the ones added in v0.14.0 (`NumThreads` / `AudioNumThreads`, the LoRA
  ranks); the connector surface is unchanged.
- **Testing your app code.** `IChatClient` is the intended seam for unit tests: have your code depend on
  `IChatClient` and substitute a mock/stub in tests — no model file or native binaries needed. The core
  types (`LiteRtEngine` / `LiteRtConversation`) are sealed and bound to the native runtime; code that
  drives them directly is best covered by wrapping them behind your own abstraction, or by
  integration tests against a real model.

## Scope

- **Tool calling** is supported (see [Function calling](#function-calling-tools)).
- **Embeddings**: the LiteRT-LM C API exposes no embeddings functions at v0.14.0, so there is no
  `IEmbeddingGenerator`.
- **Not AOT/trim-clean.** The core `LiteRtLmSharp` package stays AOT/trim-friendly; this companion does not
  carry that guarantee.

## Sample

The Semantic Kernel console sample in [`samples/SemanticKernel`](../samples/SemanticKernel) also resolves
the underlying `IChatClient` from the kernel — see [docs/semantic-kernel.md](semantic-kernel.md) for the
Semantic Kernel layer.
