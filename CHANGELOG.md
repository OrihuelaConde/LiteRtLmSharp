# Changelog

All notable changes to **LiteRtLmSharp** are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The package version is independent of the
LiteRT-LM native version it wraps (see the compatibility table in the [README](README.md)); the
managed `LiteRtLmSharp` package and every `LiteRtLmSharp.runtime.<rid>` package share one version and
are published together.

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
