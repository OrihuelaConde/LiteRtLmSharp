# LiteRtLmSharp

[![CI](https://github.com/OrihuelaConde/LiteRtLmSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/OrihuelaConde/LiteRtLmSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/LiteRtLmSharp)](https://www.nuget.org/packages/LiteRtLmSharp)

.NET 10 bindings for [Google's LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) — on-device LLM
inference (e.g. Gemma) for any .NET app, including MAUI. P/Invoke over LiteRT-LM's C API, with native
binaries distributed per-RID as NuGet packages (LLamaSharp-style).

> Status: **stable (1.0)**. Chat (blocking, awaitable + cancellable, streaming), function calling,
> multimodal (image/audio, including restored multi-turn history), conversation restore/clone,
> reasoning mode, tokenizer, speculative decoding and benchmarking.
>
> | Platform | Native | NuGet | CPU | GPU | Validated on |
> |---|:---:|:---:|:---:|:---:|---|
> | win-x64 | ✅ | ✅ | ✅ | ✅ | real hardware |
> | linux-x64 | ✅ | ✅ | ✅ | ✅ | real hardware |
> | android-arm64 | ✅ | ✅ | ✅ | ✅ | real device |
> | osx-arm64 | ✅ | ✅ | ✅ | ✅ | CI |
> | ios-arm64 | ✅ | ⏳ | — | — | pending |
>
> <sub>**CPU / GPU** = inference validated on that backend. macOS GPU runs in CI on the **WebGPU**
> (Dawn→Metal) delegate; the native Metal delegate ships as a real-hardware fallback. The iOS
> runtime package ships once on-device validation lands.</sub>
>
> See the [roadmap](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/roadmap.md)
> for the full status and pending work, and the
> [changelog](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/CHANGELOG.md) for release history.

## Quick start

```xml
<PackageReference Include="LiteRtLmSharp" Version="1.0.0" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="1.0.0" />
<!-- or LiteRtLmSharp.runtime.linux-x64 / android-arm64 / osx-arm64, per target -->
```

Install the managed package plus the runtime package for your platform, **always with the same
version number**. Which LiteRT-LM native build each release wraps:

| LiteRtLmSharp | LiteRT-LM native |
|---|---|
| 1.1.0 | v0.14.0 |
| 1.0.0 | v0.13.1 |
| 0.1.0-preview.3 | v0.13.1 |
| 0.1.0-preview.2 | v0.13.1 |
| 0.1.0-preview.1 | v0.13.1 |

```csharp
using LiteRtLmSharp;

using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",   // from huggingface.co/litert-community
    Backend = LiteRtBackend.Cpu,              // or .Gpu (WebGPU -> D3D12/Vulkan/Metal)
    MaxNumTokens = 4096,                       // total context window
});

using var chat = engine.CreateConversation();

// Blocking:
Console.WriteLine(chat.Send("Hello!").Text);

// Awaitable (pass a CancellationToken to cancel mid-generation):
Console.WriteLine((await chat.SendAsync("Hello!")).Text);

// Streaming: each piece is tagged (answer / thinking / tool call) — see Reasoning mode below.
await foreach (var chunk in chat.SendStreamingAsync("Tell me a joke"))
    Console.Write(chunk.Text);
```

### Function calling

```csharp
using var chat = engine.CreateConversation(new LiteRtConversationOptions
{
    Tools = [ new LiteRtTool("get_weather", "Get weather for a city",
        """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""") ],
    EnableConstrainedDecoding = true,
});

var r = chat.Send("Weather in Tokyo?");
if (r.IsToolCall)
{
    var results = r.ToolCalls.Select(c => new LiteRtToolResult(c.Name, RunTool(c)));
    Console.WriteLine(chat.SendToolResults(results).Text);
}
```

### Speculative decoding & benchmarking

Models that ship an MTP drafter (the Gemma 4 builds do) can decode faster via speculative decoding.
Turn on benchmarking to measure it (decode/prefill tok/s, time-to-first-token):

```csharp
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    Backend = LiteRtBackend.Cpu,
    EnableSpeculativeDecoding = true,   // MTP drafter
    EnableBenchmark = true,             // enables GetBenchmarkInfo()
});

using var chat = engine.CreateConversation();
chat.Send("Hello!");

if (chat.GetBenchmarkInfo() is { NumDecodeTurns: > 0 } b)
    Console.WriteLine($"{b.LastDecodeTokensPerSecond:F1} tok/s decode · TTFT {b.TimeToFirstTokenSeconds:F2}s");
```

The win is accelerator-specific — on desktop CPU it can be slower, and on the desktop WebGPU GPU
backend you must set `Cache = LiteRtCache.Disabled` (otherwise engine creation fails —
an upstream issue that also affects Google's own CLI). Measured numbers, requirements and the full
root-cause are in
[docs/speculative-decoding.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/speculative-decoding.md).

Other engine-level performance knobs — activation precision, prefill chunking, parallel file loading,
and a synthetic content-independent throughput benchmark — are covered in
[docs/engine-tuning.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/engine-tuning.md).

### Reasoning mode (thinking)

Models with a reasoning template (the Gemma builds) can be told to think before answering:

```csharp
using var chat = engine.CreateConversation(new LiteRtConversationOptions
{
    EnableThinking = true,             // Gemma reasoning mode (sets enable_thinking)
    FilterThinkingFromKvCache = true,  // keep the (long) reasoning out of the context window
});
```

The reasoning trace comes back **separate** from the answer — on the blocking `Send` via
`response.Thinking` (and the full `response.Channels` map), and when streaming each
`LiteRtStreamChunk` is tagged by `Kind` (`Answer` / `Thinking` / `ToolCall`):

```csharp
await foreach (var chunk in chat.SendStreamingAsync("Why is the sky blue?"))
    Console.Write(chunk.IsThinking ? $"\n[thinking] {chunk.Text}" : chunk.Text);
```

Reasoning mode is set per-conversation via extra context; for arbitrary template variables use the
raw `ExtraContext` (a JSON-object string) escape hatch; on a model whose template does not use
`enable_thinking` it is a harmless no-op.

See [`samples/Console`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/samples/Console)
for a runnable demo (`--tools` for the function-calling loop, `--spec` for speculative decoding, `--thinking` for reasoning mode),
[`samples/SemanticKernel`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/samples/SemanticKernel)
for the [.NET AI integrations](#net-ai-integrations-microsoftextensionsai-semantic-kernel-agent-framework), and
[`samples/Maui`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/samples/Maui) for a
full Android/Windows chat app with model download, streaming, tools, plus speculative-decoding and reasoning-mode toggles.

### Conversation state: restore & clone

Resume a chat after a restart, or branch a live one. **Restore** rebuilds a conversation from messages
you persisted; **clone** forks a live conversation's in-memory state without re-running the shared
prefix.

```csharp
// Restore: record turns as they happen, persist, then reload into a new conversation.
var history = new List<LiteRtMessage> { LiteRtMessage.User("My name is Ada.") };
history.Add(chat.Send("My name is Ada.").ToMessage());   // capture the assistant turn (role "model")
File.WriteAllText("chat.json", LiteRtMessage.Serialize(history));

using var resumed = engine.CreateConversation(new LiteRtConversationOptions
{
    History = LiteRtMessage.Deserialize(File.ReadAllText("chat.json")),  // re-prefilled on create
});
resumed.Send("What is my name?");                        // -> "Ada"
```

```csharp
// Clone: branch into independent continuations that share the prefilled prefix.
using var baseChat = engine.CreateConversation();
baseChat.Send("You are a travel agent. The user is going to Tokyo.");
using var budget = baseChat.Clone();
using var luxury = baseChat.Clone();
budget.Send("Suggest a budget itinerary.");
luxury.Send("Suggest a luxury itinerary.");              // baseChat stays untouched
```

Restoring replays the history through prefill (it counts against `MaxNumTokens`); cloning copies state
in memory and is verified on CPU and GPU (an executor that does not implement it throws, so be ready to
fall back to `History`). The C API has no history getter, so the message log is yours to keep. Full guide, the tools round-trip, and the raw
`HistoryJson` escape hatch:
[docs/conversation-state.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/conversation-state.md).

### Multimodal messages (image / audio)

On a multimodal model (the Gemma 4 E-series are text + vision + audio), enable the encoder backends at
load time and attach images or audio clips to a turn. The model decodes the bytes — no format hint is
sent.

```csharp
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    Backend = LiteRtBackend.Cpu,
    VisionBackend = LiteRtBackend.Cpu,   // enables image input; null = off
    AudioBackend = LiteRtBackend.Cpu,    // enables audio input; null = off
    MaxNumTokens = 4096,     // leave room for the media's tokens (an image is ~256)
});
using var chat = engine.CreateConversation();   // a plain conversation can send attachments

// Attach in-memory bytes (sent as a base64 blob) ...
byte[] png = File.ReadAllBytes("cat.png");
var reply = chat.Send("What is in this image?", [LiteRtAttachment.Image(png)]);

// ... or a file on disk (memory-mapped natively, no base64 — desktop):
chat.Send("Describe this photo.", [LiteRtAttachment.ImageFile("/photos/sunset.jpg")]);

// Audio works the same way; streaming has matching attachment overloads:
await foreach (var chunk in chat.SendStreamingAsync(
                  "Transcribe this.", [LiteRtAttachment.Audio(wavBytes)]))
    Console.Write(chunk.Text);
```

Attachments follow the text in content-part order; pass several to interleave them. Cap how much of the
context window an image consumes with `LiteRtConversationOptions.VisualTokenBudget`. Vision runs on CPU
or GPU; some models constrain their audio backend (Gemma 4's audio sub-model requires CPU, so
`AudioBackend = LiteRtBackend.Gpu` fails engine creation for it on any platform) — keep audio on CPU when the main
backend is GPU. A plain `CreateConversation()` can send attachments: the binding configures the
conversation for the engine's encoders automatically, so you don't have to set a sampler or output cap.
Just give `MaxNumTokens` room for the media's tokens (an image is ~256). If a send still hits a
multimodal-setup problem the binding throws a `LiteRtException` naming the likely causes (model not
multimodal, `VisionBackend` / `AudioBackend` unset, or `MaxNumTokens` too small). The MAUI sample's Chat
tab exposes 📷 / 🎵 attach buttons (and a modality indicator) for capable models.
Wire-format and validation details:
[docs/native-abi.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/native-abi.md#multimodal-messages-image--audio--verified-wire-format).

### Token counting (tokenize / detokenize)

Measure a prompt's exact token cost, or convert between text and token ids, without running
inference. Useful for budgeting against `MaxNumTokens` before sending.

```csharp
int[] ids = engine.Tokenize("How many tokens is this?");
Console.WriteLine($"{ids.Length} tokens");      // exact count, no generation
string text = engine.Detokenize(ids);           // back to text (surface form)

// The model's start (BOS) and stop (EOS) tokens, each a literal string or a sequence of ids:
foreach (var stop in engine.GetStopTokens())
    Console.WriteLine(stop);                     // e.g. an EOS token id like [1]
```

These call the model's own tokenizer, so the counts match what generation sees. `Tokenize` returns the
raw ids (no chat template); `Detokenize` returns the tokenizer's surface form, so SentencePiece models
(the Gemma builds) render word boundaries as ▁. A start/stop token is a `LiteRtTokenUnion` carrying
either `Text` or `Ids` (its `Kind` says which).

A common use is keeping a conversation inside its context window. The window holds the whole chat
(prompt plus replies, across turns), and overflowing it degrades output, so measure the next turn in
real tokens and react before sending instead of guessing by string length:

```csharp
// contextWindow is the MaxNumTokens you loaded with; leave headroom for the reply.
int next = engine.Tokenize(message).Length;
if (chat.TokenCount + next > contextWindow - replyHeadroom)
    // won't fit: warn the user, shorten the message, or drop old turns and reload via History.
    Warn("Context almost full — shorten the message or start a new chat.");
```

`Tokenize` counts the raw text; for the exact per-turn cost with the chat template included, render the
message first with `chat.RenderMessage(text)` (it returns the templated prompt without sending), then
tokenize that: `engine.Tokenize(chat.RenderMessage(text)).Length`.

### .NET AI integrations (Microsoft.Extensions.AI, Semantic Kernel, Agent Framework)

Two optional companion packages plug an on-device model into the .NET AI ecosystem. Each depends only on the
relevant abstractions, so apps that don't use them never pull them in.

| Package | Exposes the model as | Works with |
|---|---|---|
| **`LiteRtLmSharp.Extensions.AI`** | `Microsoft.Extensions.AI.IChatClient` | Microsoft Agent Framework, Semantic Kernel, plain MEAI (middleware/DI) |
| **`LiteRtLmSharp.SemanticKernel`** | `IChatCompletionService` (a thin layer over the `IChatClient`) | Semantic Kernel |

`IChatClient` is the .NET ecosystem's provider-agnostic chat abstraction (the foundation under both Semantic
Kernel and the [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/)), so the
Extensions.AI package is the broadest integration; the Semantic Kernel package builds on it.

```csharp
// Microsoft.Extensions.AI — works with Agent Framework, Semantic Kernel and MEAI:
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm", Backend = LiteRtBackend.Cpu, MaxNumTokens = 4096,
});
using IChatClient client = new LiteRtChatClient(engine);
Console.WriteLine((await client.GetResponseAsync("Write one upbeat sentence about on-device AI.")).Text);

// var agent = new ChatClientAgent(client, "You are helpful.");   // Microsoft Agent Framework
```

```csharp
// Semantic Kernel — one call registers the model (and the underlying IChatClient):
var builder = Kernel.CreateBuilder();
builder.AddLiteRtChatCompletion(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm", Backend = LiteRtBackend.Cpu, MaxNumTokens = 4096,
});
Kernel kernel = builder.Build();
Console.WriteLine(await kernel.InvokePromptAsync("Write one upbeat sentence about on-device AI."));
```

Both connectors are **stateless** (a fresh conversation is rebuilt from the supplied history each call) and
serialize calls (one engine per process). Reasoning ("thinking") is surfaced via `TextReasoningContent` on
the `IChatClient`. **Function calling** is supported on both: the model's tool calls surface as
`FunctionCallContent`, so MEAI's `UseFunctionInvocation()` and SK's `FunctionChoiceBehavior` auto-invoke your
functions (enable `EnableConstrainedDecoding` for reliable arguments on small models). **Multimodal**
(image/audio) is supported too — attach a `DataContent` / SK `ImageContent` to the user message on a model
loaded with `VisionBackend` / `AudioBackend`. Embeddings aren't available (the C API exposes none). Full guides:
[docs/extensions-ai.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/extensions-ai.md) and
[docs/semantic-kernel.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/semantic-kernel.md).

## Why .NET 10 only?

.NET 10 is the current LTS (released November 2025; .NET 8 reaches end of support in November
2026). Targeting it exclusively lets the binding use the modern interop stack as designed —
source-generated P/Invoke (`[LibraryImport]`) and `[UnmanagedCallersOnly]` callbacks with no
runtime marshalling code, which is what makes the library AOT- and trim-compatible — and a
single `net10.0` TFM is directly consumable from `net10.0-android`/`-ios`/`-windows` MAUI apps
without multi-targeting.

## Important notes

- **One engine alive at a time.** Loading a second engine while one is alive throws (it would
  hang in the native layer). To switch model or backend, dispose the conversations and the
  engine, then `LiteRtEngine.Load` again — same pattern as Google's Edge Gallery.
- **`MaxNumTokens`** is the total context window (prompt + response, across turns). Use >= 1024;
  too small can make blocking generation return nothing.
- **Conversations are not thread-safe** — serialize calls per conversation.
- **win-x64** needs the Microsoft Visual C++ Redistributable (the native DLLs import `VCRUNTIME140`).
- **Android GPU needs manifest declarations.** Android 12+ only grants access to vendor native
  libraries declared via `<uses-native-library>`; without `libOpenCL.so` the engine silently picks a
  Vulkan path that produces garbage on older Adreno drivers. Copy the `<uses-native-library>` block
  from [the MAUI sample's AndroidManifest](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/samples/Maui/Platforms/Android/AndroidManifest.xml).
  Full diagnosis in [docs/android.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/android.md).

## Building from source

Native binaries are **not** committed. To run the sample/tests locally, restore them into
`runtimes/<rid>/native/` from the `native-v*` GitHub release:

```powershell
pwsh scripts/restore-natives.ps1
```

(`-All` restores every RID, `-Rid android-arm64` a specific one; plain HTTPS, no auth needed.)

Then `dotnet build LiteRtLmSharp.slnx` and `dotnet test` (library + tests + packaging; bare .NET SDK
is enough). The samples have their own solution, `samples/LiteRtLmSharp.Samples.slnx` — the MAUI
sample in it needs the MAUI workloads (`dotnet workload install maui`). To run the model/tools
tests, set `LITERTLM_TEST_MODEL` (and `LITERTLM_TEST_TOOLS=1`) to a `.litertlm` file.

## How it's built (CI)

- [`build-native.yml`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/.github/workflows/build-native.yml)
  — builds `libLiteRtLm.so` / `LiteRtLm.dll` from a pinned LiteRT-LM tag via
  [`native/patch_c_api.sh`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/native/patch_c_api.sh)
  (adds our own shared-lib target with Windows exports and a companion rpath, on top of the tag), publishes a `native-<tag>` release.
- [`pack-nuget.yml`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/.github/workflows/pack-nuget.yml)
  — packs the managed + per-RID runtime packages.
- [`ci.yml`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/.github/workflows/ci.yml)
  — build + tests on Linux/Windows.

More detail in [`docs/`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/docs):
[native ABI](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/native-abi.md),
[native build](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/native-build.md),
[packaging](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/packaging.md),
[Android](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/android.md).

## Contributing

Issues and PRs are welcome — see [CONTRIBUTING.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/CONTRIBUTING.md)
for the dev setup and guidelines. Please open an issue first for anything beyond a small fix,
and use [Discussions](https://github.com/OrihuelaConde/LiteRtLmSharp/discussions) for questions.

## License and trademarks

Apache-2.0 (see [LICENSE.txt](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LICENSE.txt),
[NOTICE](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/NOTICE) and
[THIRD-PARTY-NOTICES.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/THIRD-PARTY-NOTICES.md)).

This is an unofficial, community-maintained project. It is **not affiliated with, sponsored,
or endorsed by Google**. LiteRT, LiteRT-LM and Gemma are trademarks of Google LLC. The native
binaries are built from [LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) source
(Apache-2.0) at pinned release tags.
