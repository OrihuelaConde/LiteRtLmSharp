# Semantic Kernel integration

`LiteRtLmSharp.SemanticKernel` is a **separate, optional companion package** that plugs a LiteRtLmSharp
on-device model into [Microsoft Semantic Kernel](https://learn.microsoft.com/semantic-kernel/overview/)
as a standard connector. It mirrors how [LLamaSharp ships its Semantic Kernel
support](https://github.com/SciSharp/LLamaSharp/tree/master/LLama.SemanticKernel) — a small managed
package next to the core library, depending only on `Microsoft.SemanticKernel.Abstractions`, so apps that
do not use Semantic Kernel never pull it in.

| Package | Depends on | Target |
|---|---|---|
| `LiteRtLmSharp.SemanticKernel` | `LiteRtLmSharp` (same version) + `Microsoft.SemanticKernel.Abstractions` 1.77.0 | `net10.0` |

```xml
<PackageReference Include="LiteRtLmSharp" Version="0.1.0-preview.3" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="0.1.0-preview.3" />
<PackageReference Include="LiteRtLmSharp.SemanticKernel" Version="0.1.0-preview.3" />
```

## What it provides

| Semantic Kernel interface | Implementation | Use |
|---|---|---|
| `IChatCompletionService` | `LiteRtChatCompletionService` | multi-turn chat (blocking + streaming) |
| `ITextGenerationService` | `LiteRtTextGenerationService` | single-shot prompt completion (blocking + streaming) |
| registration | `builder.AddLiteRtChatCompletion(...)` / `AddLiteRtTextGeneration(...)` | over an engine you own, or from `LiteRtEngineOptions` (container-managed); on `IKernelBuilder` and `IServiceCollection` |
| settings | `LiteRtPromptExecutionSettings` | typed temperature / top-p / top-k / max-tokens / seed / thinking |

## Quick start

```csharp
using LiteRtLmSharp;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

// 1. Load the on-device engine (heavy — holds the weights). One engine per process.
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    Backend = "cpu",
    MaxNumTokens = 4096,
});

// 2. The whole integration is one line.
var builder = Kernel.CreateBuilder();
builder.AddLiteRtChatCompletion(engine, modelId: "gemma-4-E2B-it");
Kernel kernel = builder.Build();

// 3. From here it is ordinary Semantic Kernel.
var result = await kernel.InvokePromptAsync("Write one upbeat sentence about on-device AI.");
Console.WriteLine(result);
```

### Chat with history (streaming)

```csharp
IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();
var settings = new LiteRtPromptExecutionSettings { Temperature = 0.8f, MaxTokens = 256 };

var history = new ChatHistory("You are a concise, friendly assistant.");
history.AddUserMessage("Hi! What are you good at?");

var reply = new StringBuilder();
await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel))
{
    Console.Write(chunk.Content);
    reply.Append(chunk.Content);
}
history.AddAssistantMessage(reply.ToString());   // record the turn so the next call sees it
```

### Execution settings

`LiteRtPromptExecutionSettings` carries the sampler/output knobs. Every property is optional; unset
values fall back to the engine/model defaults.

| Property | JSON key | Maps to |
|---|---|---|
| `Temperature` | `temperature` | `SamplerParams.Temperature` |
| `TopP` | `top_p` | `SamplerParams.TopP` (selects the TopP sampler) |
| `TopK` | `top_k` | `SamplerParams.TopK` |
| `MaxTokens` | `max_tokens` | `LiteRtConversationOptions.MaxOutputTokens` |
| `Seed` | `seed` | `SamplerParams.Seed` |
| `EnableThinking` | `enable_thinking` | `LiteRtConversationOptions.EnableThinking` |
| `SystemPrompt` | `system_prompt` | text-generation system prompt (chat uses the System role instead) |

You can also pass a plain `PromptExecutionSettings` whose `ExtensionData` holds those snake_case keys —
for example from a prompt template's YAML — and the connector recovers a typed instance via
`LiteRtPromptExecutionSettings.FromExecutionSettings` (the same pattern the official connectors use).

## Design: a stateless connector over a stateful engine

This is the one decision worth understanding. Semantic Kernel's `IChatCompletionService` is **stateless**:
the caller passes the full `ChatHistory` on every call. A `LiteRtConversation`, by contrast, is
**stateful** — it holds a KV cache that accumulates across turns.

The connector bridges the two by being **stateless on every call**: it builds a *fresh* conversation each
time, restoring all-but-last messages as
[`LiteRtConversationOptions.History`](conversation-state.md) (replayed through prefill) and sending the
final user turn to trigger generation.

```
ChatHistory:  [System, User₁, Assistant₁, User₂]   (what SK hands the connector)
                 └──────── History ────────┘  └ Send ┘
                 (re-prefilled each call)     (generates)
```

Why not keep one long-lived conversation and only send the latest message? Because Semantic Kernel owns
the history and can edit it (truncate, summarize, branch); a persistent conversation's KV cache would
silently diverge from what SK believes the history is. Rebuilding each call keeps them in lockstep and is
predictable. The cost is an `O(history)` prefill per turn — fine for typical chats; for very long
conversations, drive the native [`LiteRtConversation`](conversation-state.md) API directly (or restore
from a summary).

### Serialization

LiteRtLmSharp allows **one live engine per process** and **conversations are not thread-safe**. The
services therefore serialize every call through an internal `SemaphoreSlim(1, 1)`: concurrent Semantic
Kernel requests queue rather than run in parallel. One service wraps one engine.

### Engine ownership — two registration styles

There are two ways to register a service, and they map cleanly onto who owns the engine:

**Bring your own engine** — you load it and dispose it (after the service). The service never disposes the
engine.

```csharp
using var engine = LiteRtEngine.Load(options);
builder.AddLiteRtChatCompletion(engine, modelId: "gemma-4-E2B-it");
// ... you dispose `engine` (the `using` does it here)
```

**From `LiteRtEngineOptions`** — the container loads, owns and disposes a shared engine for you. Because
only one engine may live per process, the engine is registered as a single shared singleton: a chat *and*
a text-generation service added from options automatically share the one engine, and it is disposed when
the kernel's service provider is disposed.

```csharp
builder.AddLiteRtChatCompletion(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm", Backend = "cpu", MaxNumTokens = 4096,
});
// no using/dispose to manage — the container owns the engine
```

By default the engine is loaded **lazily** (on first use). Pass `eager: true` to load it at registration
instead, so a bad model path or unsupported backend throws immediately rather than on the first call:

```csharp
builder.AddLiteRtChatCompletion(options, eager: true);   // load now; surfaces load errors here
```

You can also register the shared engine on its own with `AddLiteRtEngine` and add the service(s) over it
separately — they resolve the same engine. This is the form the console sample uses:

```csharp
builder.AddLiteRtEngine(options, eager: true);   // container loads, owns and disposes the engine
builder.AddLiteRtChatCompletion(modelId: "gemma-4-E2B-it");   // chat service over the registered engine
// builder.AddLiteRtTextGeneration();            // ... add more services over the same engine if needed
```

## Function calling (planned)

Function calling is a **priority on the roadmap** — it is the capability that matters most for the
library — and bridging it into Semantic Kernel is the headline next step for this connector. LiteRtLmSharp
already does function calling through its [native tools API](../README.md#function-calling) (constrained
decoding, tool-call parsing, the tool-result round-trip); what's not wired up *yet* is Semantic Kernel's
own `FunctionChoiceBehavior` auto-invoke loop, where the kernel calls your `KernelFunction`s automatically.

Until that lands:

- `FunctionChoiceBehavior` in the execution settings is currently ignored (the model won't auto-invoke
  `KernelFunction`s).
- A chat history must end with a user message; `tool`/assistant continuations throw, because the
  auto-invoke loop that produces them isn't bridged yet.
- For function calling today, drive the [native tools API](../README.md#function-calling) directly.

## Current limitations

- **The reasoning ("thinking") trace is never part of the returned content.** Setting
  `EnableThinking = true` changes how the model answers (it reasons first), but the trace itself is
  dropped so the assistant message stays clean. Use the native streaming API
  (`LiteRtStreamChunkKind.Thinking`) if you need the trace.
- **Chat roles** handled today are `system` / `user` / `assistant` (see the function-calling note above
  for `tool`).
- **Embeddings** are not provided: the LiteRT-LM C API exposes no embeddings functions at v0.13.1, so
  there is no `ITextEmbeddingGenerationService` (see the roadmap note on embeddings).
- **Not AOT/trim-clean.** Semantic Kernel itself is not, and the settings round-trip uses reflection-based
  JSON. The core `LiteRtLmSharp` package stays AOT/trim-friendly; this companion does not carry that
  guarantee.

## Sample

A runnable console sample is in
[`samples/SemanticKernel`](../samples/SemanticKernel): it loads the engine, builds a kernel with
`AddLiteRtChatCompletion`, and demonstrates a prompt function, a streaming prompt, and a multi-turn
streaming chat — pass `--interactive` for a chat loop. See its
[README](../samples/SemanticKernel/README.md) for how to run it.
