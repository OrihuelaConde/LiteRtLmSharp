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

This is the **foundation**: it depends only on `Microsoft.Extensions.AI.Abstractions`, and the Semantic
Kernel connector is built on top of it.

| Package | Depends on | Target |
|---|---|---|
| `LiteRtLmSharp.Extensions.AI` | `LiteRtLmSharp` (same version) + `Microsoft.Extensions.AI.Abstractions` 10.7.0 | `net10.0` |

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
    Backend = "cpu",
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
    ModelPath = "gemma-4-E2B-it.litertlm", Backend = "cpu", MaxNumTokens = 4096,
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
`Microsoft.Extensions.AI` package). For example, automatic tool invocation:

```csharp
IChatClient pipeline = client.AsBuilder()
    .UseFunctionInvocation()      // auto-invokes AIFunctions returned in ChatOptions.Tools
    // .UseDistributedCache(...)  // caching, telemetry, logging, … all compose here
    .Build();
```

> Tool calling is **planned** for LiteRtLmSharp, not wired up yet: the chat client does not surface
> `FunctionCallContent` from the model's native tool calls into `ChatOptions.Tools` yet. Until it does,
> drive function calling through the [native LiteRtLmSharp tools API](../README.md#function-calling).

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
- **Message roles.** `system` / `user` / `assistant` are handled today; the list must end with a user
  message (tool/assistant continuations arrive with function calling, which is planned).

## Scope

- **Tool calling** is planned (see above); the native tools API works today.
- **Embeddings**: the LiteRT-LM C API exposes no embeddings functions at v0.13.1, so there is no
  `IEmbeddingGenerator`.
- **Not AOT/trim-clean.** The core `LiteRtLmSharp` package stays AOT/trim-friendly; this companion does not
  carry that guarantee.

## Sample

The Semantic Kernel console sample in [`samples/SemanticKernel`](../samples/SemanticKernel) also resolves
the underlying `IChatClient` from the kernel — see [docs/semantic-kernel.md](semantic-kernel.md) for the
Semantic Kernel layer.
