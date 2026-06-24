# Semantic Kernel integration

`LiteRtLmSharp.SemanticKernel` is a **separate, optional companion package** that plugs a LiteRtLmSharp
on-device model into [Microsoft Semantic Kernel](https://learn.microsoft.com/semantic-kernel/overview/) as a
standard `IChatCompletionService`.

It is a **thin layer over the [Microsoft.Extensions.AI `IChatClient`](extensions-ai.md)**: it registers the
LiteRtLmSharp `IChatClient` and exposes it to Semantic Kernel through SK's own
[`AsChatCompletionService`](https://learn.microsoft.com/dotnet/api/microsoft.semantickernel.chatcompletion.chatcompletionserviceextensions.aschatcompletionservice)
adapter. So all of Semantic Kernel's chat machinery — message conversion and function calling — flows
through that one chat client, and the underlying model is simultaneously available to
Microsoft Agent Framework and plain MEAI from the same registration.

| Package | Depends on | Target |
|---|---|---|
| `LiteRtLmSharp.SemanticKernel` | `LiteRtLmSharp` + `LiteRtLmSharp.Extensions.AI` (same version) + `Microsoft.SemanticKernel.Abstractions` 1.77.0 + `Microsoft.Extensions.AI` 10.7.0 | `net10.0` |

```xml
<PackageReference Include="LiteRtLmSharp" Version="0.1.0-preview.3" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="0.1.0-preview.3" />
<PackageReference Include="LiteRtLmSharp.SemanticKernel" Version="0.1.0-preview.3" />
```

## Quick start

```csharp
using LiteRtLmSharp;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

// One call is the whole integration: the container loads/owns/disposes a single shared engine and exposes
// the model as an IChatCompletionService (and, underneath, as a Microsoft.Extensions.AI IChatClient).
var builder = Kernel.CreateBuilder();
builder.AddLiteRtChatCompletion(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    Backend = "cpu",
    MaxNumTokens = 4096,
});
Kernel kernel = builder.Build();

// From here it is ordinary Semantic Kernel.
Console.WriteLine(await kernel.InvokePromptAsync("Write one upbeat sentence about on-device AI."));
```

`eager: true` loads the weights at registration (surfacing a bad model path/backend there) instead of lazily
on first use. To use an engine you already loaded (and will dispose yourself), pass it instead of options:
`builder.AddLiteRtChatCompletion(engine, modelId: "gemma-4-E2B-it")`. Both overloads exist on `IKernelBuilder`
and `IServiceCollection`, and take an optional `serviceId` for keyed registration (e.g. an on-device service
alongside a cloud one).

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

`LiteRtPromptExecutionSettings` carries the sampler/output knobs. Every property is optional; unset values
fall back to the engine/model defaults. The values are stored in `ExtensionData` under the well-known keys
below, so they flow through Semantic Kernel's `PromptExecutionSettings → ChatOptions` conversion to the
chat client — and a plain `PromptExecutionSettings` whose `ExtensionData` holds the same keys (e.g. from a
prompt template's YAML) works equally well.

| Property | Key | Maps to |
|---|---|---|
| `Temperature` | `temperature` | sampler temperature |
| `TopP` | `top_p` | sampler top-p (selects the TopP sampler) |
| `TopK` | `top_k` | sampler top-k |
| `MaxTokens` | `max_tokens` | max output tokens |
| `Seed` | `seed` | sampler seed |
| `EnableThinking` | `enable_thinking` | reasoning mode (see below) |
| `EnableConstrainedDecoding` | `enable_constrained_decoding` | force schema-constrained tool-call arguments ([Function calling](#function-calling)) |

## Design: a stateless connector over a stateful engine

Semantic Kernel's `IChatCompletionService` (and the `IChatClient` underneath) is **stateless**: the caller
passes the full `ChatHistory` on every call. A `LiteRtConversation`, by contrast, is **stateful** — it holds
a KV cache that accumulates across turns.

The connector bridges the two by being **stateless on every call**: it builds a *fresh* conversation each
time, restoring all-but-last messages as [`History`](conversation-state.md) (replayed through prefill) and
sending the final user turn to trigger generation.

```
ChatHistory:  [System, User₁, Assistant₁, User₂]   (what SK hands the connector)
                 └──────── History ────────┘  └ Send ┘
                 (re-prefilled each call)     (generates)
```

This keeps SK's history and the model's KV cache in lockstep (SK owns the history and can edit it). The cost
is an `O(history)` prefill per turn — fine for typical chats; for very long conversations, drive the native
[`LiteRtConversation`](conversation-state.md) API directly. Calls are **serialized** (one live engine per
process; conversations are not thread-safe), and the engine lifecycle is handled by the
[Extensions.AI registration](extensions-ai.md) the connector builds on.

## Function calling

Set a `FunctionChoiceBehavior` on the execution settings and Semantic Kernel's functions are offered to the
model; when the model calls one, it is auto-invoked and the result fed back so the model answers from it —
the standard SK function-calling experience.

```csharp
using System.ComponentModel;
using Microsoft.SemanticKernel;

// A plugin the model may call.
sealed class WeatherPlugin
{
    [KernelFunction, Description("Gets the current weather for a city.")]
    public string GetWeather([Description("The city.")] string city) => $"22°C and sunny in {city}";
}

kernel.Plugins.AddFromObject(new WeatherPlugin());

var settings = new LiteRtPromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
    EnableConstrainedDecoding = true,   // recommended for small models (see note); off by default
};

var answer = await kernel.InvokePromptAsync(
    "What's the weather in Paris? Use the weather tool.", new KernelArguments(settings));
Console.WriteLine(answer);              // "The weather in Paris is 22°C and sunny."
```

**How it works.** Semantic Kernel's `AsChatCompletionService` adapter passes the kernel's functions to the
chat client as tools but does **not** run the auto-invoke loop itself, so the connector wraps the `IChatClient`
with MEAI's function-invocation middleware (`UseFunctionInvocation`). That wrapping is built into
`AddLiteRtChatCompletion`, so `FunctionChoiceBehavior.Auto()` just works; it is a no-op when a request carries
no functions. Under the hood this is the same bridge the [Microsoft.Extensions.AI integration](extensions-ai.md#function-calling-tools)
exposes (the model's tool calls become `FunctionCallContent`).

**Constrained decoding.** `LiteRtPromptExecutionSettings.EnableConstrainedDecoding = true` makes the model emit
valid, schema-shaped tool-call arguments — recommended for small on-device models. It is **off by default**
because it throws `PlatformNotSupportedException` on **linux-x64** (a temporary upstream constraint-provider
bug, [google-ai-edge/LiteRT-LM#2149](https://github.com/google-ai-edge/LiteRT-LM/issues/2149)); leave it off
there — tools still work, arguments are just not grammar-constrained.

**Function choice (`FunctionChoiceBehavior`).** `Auto()` (the model decides), `None()` (no functions offered)
and `Required()` (the model must call one) flow through to the chat client's `ChatOptions.ToolMode`. `Required`
is **best-effort** on-device — the connector instructs the model to call a function and narrows the offered set,
but cannot *force* a call the way a cloud API's server-enforced `tool_choice: "required"` does. See
[Tool choice](extensions-ai.md#function-calling-tools) for the per-mode behavior.

## Reasoning (thinking) and the output-token budget

With `EnableThinking = true` the model emits a reasoning trace **before** the answer, and that trace shares
the `MaxTokens` output budget with the answer. So **give thinking models headroom**: with too small a budget
the reasoning can consume it all and the answer comes back **empty**. For example, on gemma-4 the reasoning
for a simple prompt runs ~200 tokens, so `MaxTokens = 200` with thinking on leaves nothing for the answer,
whereas `MaxTokens = 512` works. This affects both blocking and streaming equally — it is a budget effect,
not a connector bug.

A caveat specific to the Semantic Kernel path: SK's `IChatCompletionService` adapter does **not** surface the
reasoning trace or a truncation signal — `kernel.InvokePromptAsync` / `GetChatMessageContentsAsync` return
just the answer text (empty when the reasoning ate the budget). The underlying
[Microsoft.Extensions.AI `IChatClient`](extensions-ai.md) does surface both: the reasoning as a
`TextReasoningContent` (excluded from `ChatResponse.Text`), and a `ChatResponse.FinishReason` of `Length`
when the answer was truncated. The same registration provides it, so resolve it from the kernel when you need
rich reasoning / truncation handling:

```csharp
using LiteRtLmSharp.Extensions.AI;   // for LiteRtChatOptions
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

IChatClient chatClient = kernel.Services.GetRequiredService<IChatClient>();
ChatResponse response = await chatClient.GetResponseAsync(messages,
    new LiteRtChatOptions { MaxOutputTokens = 512, EnableThinking = true });

string? reasoning = response.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().FirstOrDefault()?.Text;
if (response.FinishReason == ChatFinishReason.Length && string.IsNullOrWhiteSpace(response.Text))
    Console.WriteLine("(no answer — the reasoning consumed the budget; raise MaxOutputTokens)");
```

## Multimodal (image / audio)

On a multimodal model, add an `ImageContent` or `AudioContent` (with inline data) to a chat message — Semantic
Kernel's adapter forwards it to the model as an attachment through the underlying `IChatClient`:

```csharp
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

byte[] png = File.ReadAllBytes("photo.png");
var history = new ChatHistory();
history.Add(new ChatMessageContent(AuthorRole.User, new ChatMessageContentItemCollection
{
    new TextContent("What is in this image?"),
    new ImageContent(png, "image/png"),
}));

IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();
var reply = await chat.GetChatMessageContentsAsync(history);
```

The engine must have been loaded with the matching modality enabled (`LiteRtEngineOptions.VisionBackend` /
`AudioBackend`) on a multimodal model. Only the final user message's media is sent (prior turns are restored as
text). See [docs/extensions-ai.md](extensions-ai.md#multimodal-image--audio) for the details.

## Scope

- **Function calling** is supported (see [Function calling](#function-calling)) via `FunctionChoiceBehavior`.
- **Multimodal** (image / audio) is supported (see [Multimodal](#multimodal-image--audio)).
- **Text generation** (`ITextGenerationService`) is not provided — Semantic Kernel and the wider .NET AI
  stack are chat-centric; use chat completion.
- **Embeddings** are not provided (the LiteRT-LM C API exposes none at v0.13.1).
- **Not AOT/trim-clean.** Semantic Kernel itself is not; the core `LiteRtLmSharp` package stays
  AOT/trim-friendly, this companion does not carry that guarantee.

## Sample

A runnable console sample is in [`samples/SemanticKernel`](../samples/SemanticKernel): it builds a kernel with
`AddLiteRtChatCompletion` and demonstrates a prompt function, a streaming prompt, a multi-turn streaming
chat, and function calling (a `[KernelFunction]` plugin) — pass `--interactive` for a chat loop. See its [README](../samples/SemanticKernel/README.md) for how to
run it. The broader Microsoft.Extensions.AI / Agent Framework story is in [docs/extensions-ai.md](extensions-ai.md).
