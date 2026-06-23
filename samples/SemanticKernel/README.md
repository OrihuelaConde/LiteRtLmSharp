# LiteRtLmSharp × Semantic Kernel — console sample

Demonstrates the [`LiteRtLmSharp.SemanticKernel`](../../docs/semantic-kernel.md) connector: an on-device
LiteRT-LM model used through Microsoft Semantic Kernel as a standard `IChatCompletionService`.

The only LiteRtLmSharp-specific line is the registration on the kernel builder —
`builder.AddLiteRtChatCompletion(options)` — which loads the on-device engine from the options (the container
owns its lifetime) and exposes it as an `IChatCompletionService`. Everything else is ordinary Semantic Kernel.
[`Program.cs`](Program.cs) is the file worth reading. For the underlying Microsoft.Extensions.AI / Agent
Framework story, see [docs/extensions-ai.md](../../docs/extensions-ai.md).

## What it shows

1. **Demo A** — a Semantic Kernel prompt function (`kernel.InvokePromptAsync`) with a templated prompt
   and per-call execution settings.
2. **Demo B** — a streaming prompt (`kernel.InvokePromptStreamingAsync`).
3. **Demo C** — a multi-turn streaming chat through `IChatCompletionService` with a `ChatHistory`
   (system prompt + two user turns; the second turn builds on the first, proving history replay works).

Pass `--interactive` (or `-i`) for a chat loop after the demos.

## Running it

You need a `.litertlm` model and the native binaries for your platform.

1. **Native binaries** — restore them into `runtimes/<rid>/native/` (the samples pick them up
   automatically):

   ```powershell
   pwsh scripts/restore-natives.ps1        # from the repo root
   ```

2. **A model** — e.g. `gemma-4-E2B-it.litertlm` from
   [huggingface.co/litert-community](https://huggingface.co/litert-community). Place it under `models/`
   in the repo root (the sample auto-discovers a single `*.litertlm` there) or pass the path.

3. **Run** — from the repo root:

   ```powershell
   dotnet run --project samples/SemanticKernel -c Release -- models/gemma-4-E2B-it.litertlm
   ```

   Options:

   ```
   LiteRtLmSharp.Sample.SemanticKernel [path-to-model.litertlm] [--backend cpu|gpu] [--interactive]
   ```

   With no model path it looks for a single `*.litertlm` under `./models` (or the repo's `models/`).

## Notes

- The connector is **stateless**: each Semantic Kernel call rebuilds a fresh conversation from the
  supplied `ChatHistory`. Record each assistant turn back into the history so the next call sees the full
  conversation (the sample does this with a `StringBuilder`). See
  [docs/semantic-kernel.md](../../docs/semantic-kernel.md) for the design.
- Native `WARNING: … npu_registry` / `mel_filterbank` lines on startup are harmless — the NPU probe and
  the multimodal model's audio sub-model initializing on CPU.
- win-x64 needs the Microsoft Visual C++ Redistributable (the native DLLs import `VCRUNTIME140`).
