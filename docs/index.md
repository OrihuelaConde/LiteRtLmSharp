# LiteRtLmSharp

**.NET 10 bindings for [Google's LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) — on-device
LLM inference (e.g. Gemma) for any .NET app, including MAUI.** P/Invoke over LiteRT-LM's C API, with
native binaries distributed per-RID as NuGet packages.

Chat (blocking, awaitable + cancellable, streaming), function calling, multimodal (image/audio),
conversation restore/clone, reasoning mode, tokenizer, speculative decoding and benchmarking — all
running locally, no server.

## Install

```xml
<PackageReference Include="LiteRtLmSharp" Version="1.1.0" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="1.1.0" />
<!-- or LiteRtLmSharp.runtime.linux-x64 / android-arm64 / osx-arm64, per target -->
```

Install the managed package plus the runtime package for your platform, always with the same version
number. Optional integrations:
[`LiteRtLmSharp.Extensions.AI`](extensions-ai.md) (`IChatClient` — Microsoft Agent Framework, MEAI) and
[`LiteRtLmSharp.SemanticKernel`](semantic-kernel.md) (`IChatCompletionService`).

## First tokens

```csharp
using LiteRtLmSharp;

using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",   // from huggingface.co/litert-community
    Backend = LiteRtBackend.Cpu,              // or .Gpu (WebGPU -> D3D12/Vulkan/Metal)
    MaxNumTokens = 4096,                       // total context window
});

using var chat = engine.CreateConversation();

await foreach (var chunk in chat.SendStreamingAsync("Tell me a joke"))
    Console.Write(chunk.Text);
```

The [repository README](https://github.com/OrihuelaConde/LiteRtLmSharp#readme) walks through every
feature with runnable snippets; the guides here go deeper per topic.

## Where to go next

- **[Chat & generation](chat.md)** — function calling, reasoning mode, multimodal (image/audio),
  token counting.
- **[Microsoft.Extensions.AI integration](extensions-ai.md)** — plug the model into the .NET AI
  ecosystem as an `IChatClient` (Agent Framework, Semantic Kernel, MEAI middleware).
- **[Semantic Kernel connector](semantic-kernel.md)** — `IChatCompletionService` over the same bridge.
- **[Conversation state](conversation-state.md)** — persist/restore chats, clone live conversations.
- **[Engine tuning](engine-tuning.md)** — precision, prefill chunking, thread counts, benchmarking.
- **[Speculative decoding](speculative-decoding.md)** — the MTP drafter: when it helps and what it needs.
- **[Android](android.md)** — device setup, backends, and MAUI notes.
- **[API Reference](api/LiteRtLmSharp.yml)** — the full public surface, generated from the XML docs.

## Samples

- [`samples/Console`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/samples/Console) —
  chat loop with `--tools`, `--spec`, and `--thinking` demos.
- [`samples/Maui`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/samples/Maui) — full
  Android/Windows chat app: model download, streaming, multimodal attachments, function calling.
- [`samples/SemanticKernel`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/samples/SemanticKernel) —
  kernel registration, streaming, and a `[KernelFunction]` plugin.

## Project status

The [roadmap](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/roadmap.md) is the
status source of truth; release history is in the
[changelog](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/CHANGELOG.md).
