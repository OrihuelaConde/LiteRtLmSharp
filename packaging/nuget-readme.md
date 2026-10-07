# LiteRtLmSharp

.NET 10 bindings for [Google's LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) — on-device
LLM inference (e.g. Gemma) for any .NET app, including MAUI. No server, no cloud: the model runs
locally on CPU or GPU.

## Install

Install the managed package plus the native runtime package for your platform, **always with the
same version number**:

```xml
<PackageReference Include="LiteRtLmSharp" Version="1.3.0" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="1.3.0" />
<!-- or LiteRtLmSharp.runtime.linux-x64 / linux-arm64 / android-arm64 / android-x64 (emulator) / osx-arm64, per target -->
```

Optional integrations: `LiteRtLmSharp.Extensions.AI` (a `Microsoft.Extensions.AI.IChatClient` and an
`IEmbeddingGenerator` — works with the Microsoft Agent Framework, Semantic Kernel and plain MEAI) and
`LiteRtLmSharp.SemanticKernel` (an `IChatCompletionService` and an embedding generator).

## First tokens

```csharp
using LiteRtLmSharp;

using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",   // from huggingface.co/litert-community
    Backend = LiteRtBackend.Cpu,              // or .Gpu
    MaxNumTokens = 4096,                       // total context window
});

using var chat = engine.CreateConversation();

await foreach (var chunk in chat.SendStreamingAsync("Tell me a joke"))
    Console.Write(chunk.Text);
```

## Features

- Chat: blocking, awaitable + cancellable, and streaming sends
- Function calling with constrained decoding for reliable JSON arguments
- Reasoning mode (Gemma "thinking"), surfaced separately from the answer
- Multimodal input: image and audio attachments
- Conversation restore & clone, token counting, speculative decoding, benchmarking
- Text embeddings (EmbeddingGemma 2) for semantic search and RAG, next to a chat model
- Model metadata (type, context size, inputs, backends) read without loading the model
- AOT- and trim-compatible (source-generated P/Invoke)

## Documentation

- Docs & guides: https://orihuelaconde.github.io/LiteRtLmSharp/
- API reference: https://orihuelaconde.github.io/LiteRtLmSharp/api/LiteRtLmSharp.html
- Repository & samples: https://github.com/OrihuelaConde/LiteRtLmSharp
- Changelog: https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/CHANGELOG.md

## License and trademarks

Apache-2.0. This is an unofficial, community-maintained project — **not affiliated with, sponsored,
or endorsed by Google**. LiteRT, LiteRT-LM and Gemma are trademarks of Google LLC. The native
binaries are Google's official LiteRT-LM prebuilts (Apache-2.0) at pinned release tags.
