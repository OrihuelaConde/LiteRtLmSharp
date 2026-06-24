# Changelog

All notable changes to **LiteRtLmSharp** are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The package version is independent of the
LiteRT-LM native version it wraps (see the compatibility table in the [README](README.md)); the
managed `LiteRtLmSharp` package and every `LiteRtLmSharp.runtime.<rid>` package share one version and
are published together.

## [Unreleased]

### Added

- **.NET AI ecosystem integration (two optional companion packages).** An on-device model can now be plugged
  into the .NET AI stack:
  - **`LiteRtLmSharp.Extensions.AI`** exposes the model as a `Microsoft.Extensions.AI.IChatClient`
    (`LiteRtChatClient`) — the provider-agnostic chat abstraction that underpins both Semantic Kernel and the
    Microsoft Agent Framework, so this one package makes the model usable from **Agent Framework**
    (`new ChatClientAgent(client, …)`), **plain Microsoft.Extensions.AI** (the middleware pipeline, DI), and
    Semantic Kernel. Blocking + streaming, with `AddLiteRtChatClient(engine | options)` DI registration.
    Reasoning ("thinking") is surfaced as `TextReasoningContent` (excluded from `ChatResponse.Text`), and a
    truncated answer (the reasoning consuming the output budget) is flagged with a `Length` finish reason.
    `ChatResponse.Usage` is populated: `TotalTokenCount` always (free), with the `InputTokenCount`/`OutputTokenCount`
    split when the engine is loaded with `EnableBenchmark = true` (a note on `response.AdditionalProperties` says
    so when it is off). Depends only on `Microsoft.Extensions.AI.Abstractions` (+ the core `LiteRtLmSharp` package).
  - **`LiteRtLmSharp.SemanticKernel`** adds the model to [Semantic Kernel](https://learn.microsoft.com/semantic-kernel/overview/)
    as an `IChatCompletionService` via `builder.AddLiteRtChatCompletion(engine | options)`. It is a thin layer
    over the `IChatClient` (exposed through SK's `AsChatCompletionService` adapter), with a
    `LiteRtPromptExecutionSettings` (temperature / top-p / top-k / max-tokens / seed / thinking / constrained
    decoding) whose knobs flow through SK's `PromptExecutionSettings → ChatOptions` conversion. Depends on
    `LiteRtLmSharp.Extensions.AI` + `Microsoft.SemanticKernel.Abstractions` + `Microsoft.Extensions.AI`.
  - **Function calling** is supported on both connectors: function tools in `ChatOptions.Tools` are passed to
    the model and the model's tool calls surface as `FunctionCallContent`, so MEAI's `UseFunctionInvocation()`
    and Semantic Kernel's `FunctionChoiceBehavior` auto-invoke your functions and feed the results back. (The SK
    connector wraps the client with MEAI's function-invocation middleware, since SK's `AsChatCompletionService`
    adapter does not run the auto-invoke loop itself.) Opt-in `EnableConstrainedDecoding` (off by default; blocked
    on linux-x64) makes small models emit valid tool-call arguments. `ChatOptions.ToolMode` /
    `FunctionChoiceBehavior` is honored: `None` offers no tools; `RequireAny`/`RequireSpecific` are best-effort
    (the connector instructs the model and narrows the offered tools, but the on-device decoder can't be *forced*
    like a cloud `tool_choice: required`).
  - **Multimodal** (image / audio) is supported on both connectors: image/audio content on the final user
    message — a MEAI `DataContent` (inline bytes) or file-path `UriContent`, or a Semantic Kernel
    `ImageContent` / `AudioContent` (which the SK adapter forwards as `DataContent`) — is sent to the model as
    an attachment. Requires the engine loaded with the matching modality (`LiteRtEngineOptions.VisionBackend` /
    `AudioBackend`) on a multimodal model; only the triggering turn's media is sent (the stateless connector
    restores prior turns as text).
  - Both connectors are **stateless** (a fresh `LiteRtConversation` is rebuilt from the supplied history each
    call, prior turns replayed through prefill) and serialize calls (one live engine per process). Embeddings
    remain unavailable while the C API exposes none (v0.13.1). A console sample is in
    [`samples/SemanticKernel`](samples/SemanticKernel) (prompt function, streaming, multi-turn chat, function
    calling); guides: [docs/extensions-ai.md](docs/extensions-ai.md) and [docs/semantic-kernel.md](docs/semantic-kernel.md).
    Validated end-to-end on win-x64 CPU and GPU/WebGPU with gemma-4-E2B-it; mapping/settings/registration logic is
    unit-tested model-free in CI, with gated model-backed tests (chat blocking/streaming, multi-turn history replay,
    reasoning + truncation, function calling for both MEAI and SK, and image/audio attachments) under `LITERTLM_TEST_MODEL`.

## [0.1.0-preview.3] — 2026-06-20

Built against **LiteRT-LM v0.13.1** (same native binaries as preview.2 — this release is binding
and CI work).

### Added

- **Token counting (tokenizer surface).** `LiteRtEngine.Tokenize(text)` and `Detokenize(ids)` run the
  model's own tokenizer with no inference (exact prompt budgeting against `MaxNumTokens`), and
  `GetStartToken()` / `GetStopTokens()` expose the model's configured start (BOS) / stop (EOS) tokens as
  `LiteRtTokenUnion` values, each a literal string or a token-id sequence. Binds 16 more C-API functions
  (now 61 of 89). Validated on win-x64 CPU with gemma-4-E2B-it.
- **Prompt rendering.** `LiteRtConversation.RenderMessage(text)` (and raw `RenderMessageRaw(json)`)
  returns the exact templated prompt a message would produce, without sending it (the KV cache is
  untouched). Pairs with the tokenizer to measure a turn's real token cost, chat template included; the
  Console sample's tokenizer demo shows the raw-vs-templated token contrast.
- **Engine tuning settings.** `LiteRtEngineOptions.PrefillChunkSize` (CPU/dynamic models),
  `ParallelFileSectionLoading` (parallel `.litertlm` load, on by default), and `ActivationDataType`
  (`LiteRtActivationDataType`: F32 / F16 / I16 / I8) for activation precision.
- **Synthetic benchmark.** `LiteRtEngineOptions.BenchmarkPrefillTokens` / `BenchmarkDecodeTokens` run a
  content-independent throughput benchmark — the prompt is padded/truncated to the prefill count and
  decode runs exactly the decode count, so `GetBenchmarkInfo` reports timings at fixed token counts (the
  reply is not a real answer; setting either also enables benchmark mode).

### Fixed

- **Multimodal works from a plain conversation.** A conversation created with `CreateConversation()`
  (no options) on an engine loaded with `VisionBackend` / `AudioBackend` can now send image/audio
  attachments. The vision/audio executor only loads when the conversation carries a session config; the
  binding now attaches one automatically when the engine is multimodal. Previously a bare conversation
  failed with "Vision executor should not be null" unless you also set `MaxOutputTokens` or a sampler.
  This also corrects earlier guidance that blamed a too-small `MaxNumTokens`: the real cause was the
  missing session config; `MaxNumTokens` only needs room for the media's tokens (~256 for an image).

### Changed

- **Clearer multimodal setup error.** When an image or audio send still fails because the engine cannot
  process it (model not multimodal, `VisionBackend` / `AudioBackend` not set, or `MaxNumTokens` cannot
  hold the media), the binding now throws a `LiteRtException` naming those causes, instead of the bare
  native "Vision/Audio executor should not be null". Applies to both the blocking and streaming paths.

### Testing & CI

_Repository changes, not part of the shipped library._

- **Model-backed tests now run on every push/PR** across linux-x64 / win-x64 / osx-arm64 (CPU, plus
  macOS GPU), instead of weekly: `model-tests.yml` became a reusable workflow that `ci.yml` calls, and
  the no-model build matrix also gained macOS. `model-tests.yml` stays dispatchable for a single OS on
  demand.

## [0.1.0-preview.2] — 2026-06-17

Built against **LiteRT-LM v0.13.1** (same native binaries as preview.1 — this release is binding,
sample and CI work).

### Added

- **Multimodal messages (image / audio).** Attach images or audio clips to a turn with
  `LiteRtAttachment` (`Image`/`ImageFile`/`Audio`/`AudioFile`) via new `Send`, `SendMessage` and
  `SendMessageStreamingAsync` overloads. Enable the encoders with `LiteRtEngineOptions.VisionBackend` /
  `AudioBackend`; bound `LiteRtConversationOptions.VisualTokenBudget` and `LiteRtEngineOptions.MaxNumImages`.
- **Conversation restore.** `LiteRtConversationOptions.History` / `HistoryJson` re-prefill prior turns;
  `LiteRtMessage` with `Serialize`/`Deserialize` and `LiteRtResponse.ToMessage()` for a caller-owned
  persist/reload round-trip.
- **Conversation clone.** `LiteRtConversation.Clone()` forks a conversation from a copy of its prefilled
  KV-cache state.
- **Reasoning ("thinking") mode.** `LiteRtConversationOptions.EnableThinking`, `ExtraContext` and
  `FilterThinkingFromKvCache`; streamed chunks are tagged answer / thinking / tool-call
  (`LiteRtStreamChunkKind`), and `LiteRtResponse.Thinking` / `Channels` expose the reasoning trace.
- **Speculative decoding.** `LiteRtEngineOptions.EnableSpeculativeDecoding` (MTP drafter).
- **Benchmark API.** `LiteRtEngineOptions.EnableBenchmark` + `LiteRtConversation.GetBenchmarkInfo()`
  (prefill / decode tokens-per-second, time-to-first-token, init time).
- **Engine cache directory control.** `LiteRtEngineOptions.CacheDir` with `CacheDisabled` / `CacheInMemory`
  sentinels.

### Changed

- **MAUI sample:** tools page, in-app model/backend switching, reasoning and speculative toggles,
  image/audio attach buttons with a modality indicator, and a context/benchmark gauge. **Console sample:**
  streaming, tools, speculative and thinking flags.

### Fixed

- **linux-x64 constrained decoding no longer crashes the process.** `EnableConstrainedDecoding = true`
  now throws a clear `PlatformNotSupportedException` because the upstream prebuilt constraint provider
  shipped with LiteRT-LM v0.13.1 returns broken constraints
  ([LiteRT-LM#2149](https://github.com/google-ai-edge/LiteRT-LM/issues/2149)). Tools still work with
  constrained decoding off. The guard is removed once upstream ships a fixed binary.

### Testing & CI

_Repository changes, not part of the shipped library._

- **macOS (osx-arm64) validated in CI** on CPU and GPU (the WebGPU / Dawn→Metal delegate); the GPU pass
  is a required check.
- **Weekly model-tests matrix** across linux-x64 / win-x64 / osx-arm64 covering blocking chat, streaming,
  tool calling (with and without constrained decoding), a speculative-decoding A/B benchmark, and
  multimodal image/audio.

### Known issues

- Multimodal needs a sufficient context window: set `LiteRtEngineOptions.MaxNumTokens` to **4096 or
  more**. A small window (e.g. 2048) cannot fit the image's vision tokens and the first image send fails
  with "Vision executor should not be null".
- The Gemma 4 audio sub-model is **CPU-only** — `AudioBackend = "gpu"` fails engine creation ("Audio
  backend constraint mismatch. Model requires one of [cpu]") on every platform; run audio on CPU. Vision
  runs on GPU.
- Speculative decoding does not speed up decode on desktop (CPU regresses; desktop WebGPU is neutral and
  requires `CacheDir = CacheDisabled` to load —
  [LiteRT-LM#2572](https://github.com/google-ai-edge/LiteRT-LM/issues/2572)); the win is on accelerators.
- The GPU TopK sampler falls back to CPU sampling on Windows/macOS
  ([LiteRT-LM#2073](https://github.com/google-ai-edge/LiteRT-LM/issues/2073)) — output is correct and the
  perf impact is negligible.

## [0.1.0-preview.1] — 2026-06-11

First public release on nuget.org (renamed from the internal *LiteLMSharp*). Built against
**LiteRT-LM v0.13.1**.

### Added

- Chat: blocking and per-token streaming with cancellation.
- Function calling / tools (constrained decoding, Gemma special-token sanitization).
- System prompt, sampler parameters, max output tokens, and KV-cache token count.
- Engine reload after `Dispose` — switch model or backend without restarting the process.
- AOT/trim-friendly interop (`[LibraryImport]`, `[UnmanagedCallersOnly]`, no reflection).
- LLamaSharp-style distribution: a pure managed `LiteRtLmSharp` package plus per-RID
  `LiteRtLmSharp.runtime.<rid>` native packages (win-x64, linux-x64, android-arm64, osx-arm64), all
  sharing one version, published via nuget.org Trusted Publishing (OIDC).
- Console and .NET MAUI samples: model download/management, chat, and tools.
