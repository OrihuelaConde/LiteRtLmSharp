# Changelog

All notable changes to **LiteRtLmSharp** are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The package version is independent of the
LiteRT-LM native version it wraps (see the compatibility table in the [README](README.md)); the
managed `LiteRtLmSharp` package and every `LiteRtLmSharp.runtime.<rid>` package share one version and
are published together.

## [Unreleased]

Requires **LiteRT-LM v0.14.0** native binaries (the `LiteRtLmSharp.runtime.<rid>` packages bump to
match). The managed surface is source-compatible with 1.0.0 — all changes below are additive.

### Added

- **KV overflow guard** — the native runtime does not police `LiteRtEngineOptions.MaxNumTokens`: a send
  that grows the conversation past it writes beyond the allocated KV cache and corrupts native memory
  (typically a deferred `0xC0000005`/`0xC0000374` process crash on a later call — reachable from a long
  function-calling loop in the stateful mode). When the engine is loaded with an explicit `MaxNumTokens`,
  every send now measures its real prefill cost (render + tokenize, no inference), clamps the reply's
  decode budget to the remaining context via the per-send output cap, and throws the new
  `LiteRtContextOverflowException` (with `TokenCount`/`MaxNumTokens`) once the conversation is full or the
  message cannot fit — instead of crashing the process. `LiteRtException` is now unsealed so the new
  exception derives from it (existing `catch (LiteRtException)` handlers keep working). In the
  Extensions.AI stateful mode the full conversation is evicted on throw, like a cancelled one. The
  measurement is best-effort: messages with media, sends carrying `SendRaw`'s per-send `extraContext`,
  and natives that cannot render/tokenize keep only the already-full hard stop (their prefill cost is not
  measurable managed-side — leave headroom under the limit on those paths). In the stateful mode, a
  "message doesn't fit" rejection happens before any native work and keeps the live conversation
  resumable (retry the id with a shorter message); only a "context is full" rejection evicts it. Without
  an explicit `MaxNumTokens` the limit is internal to the engine (the C API has no getter) and behavior
  is unchanged. Measured overhead (gemma-4-E2B, win-x64 CPU): ~0.2 ms per mid-conversation send, ~4 ms on
  the first send of a conversation carrying a ~1.4k-token preface — the render + tokenize run no
  inference, and the token count is read off the native result without copying the id array.

  The guard also signals **in the same turn** that a reply hit the wall, so callers never depend on the
  next call's exception to learn the conversation is over: the new **`LiteRtConversation.IsContextFull`**
  is `true` exactly when the next send would throw — a clamped reply is detected exactly, from the guard's
  own counts (tolerating measurement drift up to the safety margin either way), with a `TokenCount`
  threshold as the backstop that also covers unmeasured media sends. Check it right after a send, or after
  a stream completes; it shares its predicate with the guard's hard stop, so the two can never disagree. The
  Extensions.AI client maps it to **`ChatFinishReason.Length`** on the clamped response (and on the final
  update of a clamped stream). `Length` deliberately wins over `ToolCalls` there: even a complete tool
  call cannot be continued on a full conversation — sending its results would throw — so `Length` is the
  cue to wind the thread down. Note that `FunctionInvokingChatClient` loops on function-call content
  regardless of finish reason, so an unattended tool loop still ends in the (clean, evicting)
  `LiteRtContextOverflowException`; the signal is for callers who look.

- **CPU thread counts** — `LiteRtEngineOptions.NumThreads` and `AudioNumThreads` set the CPU text /
  audio executor thread counts (`null` = engine default). CPU-backend knobs: no-op on non-CPU backends,
  and the audio one only applies when an audio executor is configured. Non-positive values are rejected.
- **LoRA surface** — engine-level `LiteRtEngineOptions.LoraRank`, `SupportedLoraRanks`, `AudioLoraRank`,
  `SupportedAudioLoraRanks`, plus per-conversation `LiteRtConversationOptions.LoraPath` /
  `AudioLoraPath` (the adapter file is opened when the conversation is created, so a bad path fails fast
  with `LiteRtException`). Requires a LoRA-enabled model; supported-ranks are only honored on the GPU
  (Artisan) backend. **The TEXT LoRA path is stubbed in the LiteRT-LM v0.14.0 runtime**: loading a valid
  text adapter succeeds at conversation creation, but the first generation fails upstream with
  `Lora is not supported` (tracked internally by Google). The audio LoRA path is wired upstream. Our surface
  is ready either way — the adapter loads and, where the runtime stubs generation, the failure is surfaced
  coherently as a `LiteRtException`.
- **`LiteRtConversation.RenderPreface()`** — renders the templated conversation preface (system message
  + tools + history) to a string without sending, complementing `RenderMessage`. Pair with
  `LiteRtEngine.Tokenize` to measure the preamble's token cost.
- **Per-send output cap** — `LiteRtSendOptions.MaxOutputTokens` overrides the conversation-level
  `MaxOutputTokens` for a single send (maps to the v0.14.0 `optional_args_set_max_output_tokens`; the
  runtime resolves the per-send value over the session value).
- **Tool-call streaming** — `LiteRtConversationOptions.StreamToolCalls` streams the raw text of a tool
  call while the model generates it: `SendStreamingAsync` yields the new
  `LiteRtStreamChunkKind.ToolCallDelta` chunks (incremental, unparsed progress fragments) before the
  usual complete `ToolCall` chunk. Default off — without it a tool-call block keeps today's behavior
  (silence until the parsed call arrives whole). Progress display only; act on the final parsed chunk.
- **`.NET AI connectors` — opt-in stateful conversations (MEAI `ConversationId` contract)** —
  `LiteRtChatClient` can now keep the live native `LiteRtConversation` alive between calls instead of
  rebuilding it from the full message list each turn. Opt in with a new `LiteRtStatefulConversationOptions`
  on the constructor and on both `AddLiteRtChatClient(...)` and Semantic Kernel's `AddLiteRtChatCompletion(...)`
  overloads (an appended optional parameter; `null` = today's stateless behavior, byte-identical). When
  enabled the client implements Microsoft.Extensions.AI's canonical stateful-provider contract: the first
  call (no `ChatOptions.ConversationId`) returns a `ChatResponse.ConversationId`, and setting that id on the
  next request sends only the not-yet-seen messages (typically just the new user turn) to resume the live
  conversation with no history re-prefill, so each turn costs only its own tokens. Because a non-null
  `ConversationId` makes `FunctionInvokingChatClient` (`UseFunctionInvocation()`) send only the new
  function-result message(s) on its next iteration, multi-round tool loops become incremental automatically.
  The conversation's session settings (sampler, thinking, tools, constrained decoding, system message,
  template values) are fixed when it is created; on a continuation those per-request knobs are ignored (only
  the per-send `MaxOutputTokens` still applies) and a system message throws. This mode keeps a **single** live
  conversation: starting a new conversation (a call without a `ConversationId`) replaces the previous one, and
  resuming a replaced (or never-issued) id throws `ArgumentException`. Multi-live-conversation support is held
  back by a current LiteRT-LM runtime limitation (a suspended conversation's state is not preserved when
  another advances), so there is no knob to raise the limit. Disposing the client disposes the live
  conversation. `ChatResponse.Usage.TotalTokenCount` now reflects the conversation's cumulative total across
  the stateful thread. See `docs/extensions-ai.md`.
- **`.NET AI connectors` — per-client conversation-options template** — `LiteRtChatClient` takes an
  optional `LiteRtConversationOptions optionsTemplate`, and the DI registrations expose it too
  (`AddLiteRtChatClient(...)` and Semantic Kernel's `AddLiteRtChatCompletion(...)`). It surfaces the
  conversation-level settings MEAI's `ChatOptions` cannot express — `SystemMessage`, `LoraPath` /
  `AudioLoraPath`, `StreamToolCalls`, `VisualTokenBudget`, `FilterThinkingFromKvCache`, `ExtraContext`,
  and a session-default `MaxOutputTokens` — applied to every call. Per-call `ChatOptions` values still
  win where MEAI supplies them (sampler, thinking, constrained decoding, tools, system message); the
  template's `SystemMessage` is used only when the request carries no system message (never two system
  turns). When the template enables `StreamToolCalls`, streaming now surfaces each tool-call delta as a
  content-less `ChatResponseUpdate` carrying the raw fragment under `AdditionalProperties`
  (`litertlm.tool_call_delta`) — opt-in by construction, invisible otherwise. A template that carries
  `History` / `HistoryJson` is rejected at registration (history is per-call).

### Fixed

- **`LiteRtConversationOptions.SystemMessage` was silently ignored** — the binding wrapped the text in
  a bare `{"type":"text",...}` part object; the native side JSON-parses that into the message content
  and the chat template drops object-valued content, so the system turn rendered empty
  (`<|turn>system\n<turn|>`) and the model never saw the prompt. The system text is now sent as a
  content-parts **array**, which the template renders. Diagnosed and verified with the new
  `RenderPreface()` (the system prompt now appears in the templated preface). Workaround on older
  versions: pass the system prompt as a leading `LiteRtMessage.System` history message instead
  (the path `LiteRtChatClient` uses, which was never affected). **Migration note:** if you used that
  History workaround while ALSO leaving `SystemMessage` set, remove one of the two — both now render,
  which yields two system turns (the long-documented behavior of setting the system prompt in both
  places; check with `RenderPreface()`).

### Changed

- **GPU + speculative decoding no longer needs the disk cache off** — the upstream shared
  weight-cache mmap failure ([LiteRT-LM#2572](https://github.com/google-ai-edge/LiteRT-LM/issues/2572))
  is fixed in the v0.14.0 natives and re-verified end to end (engine create, generation, and cache
  readback across reloads on win-x64 WebGPU). The samples' automatic `Cache = LiteRtCache.Disabled`
  workaround for that combination is removed; `docs/speculative-decoding.md` keeps the v0.13.1
  behavior as a historical record.
- **`.NET AI connector` maps `ChatOptions.MaxOutputTokens` per-send (internal)** — the
  Microsoft.Extensions.AI `LiteRtChatClient` now maps the MEAI per-request `MaxOutputTokens` to the
  v0.14.0 per-send cap (`LiteRtSendOptions.MaxOutputTokens`) instead of the conversation-level session
  config. Behavior-neutral (the client already creates a fresh conversation per call), a more faithful
  match for MEAI's per-request semantics, and it avoids allocating a session config when the output cap
  is the only option set. Multimodal-safe: a multimodal engine forces the session config into existence
  regardless, so the encoder still loads.
- **Sampler params migration (internal)** — v0.14.0 replaced the by-value `LiteRtLmSamplerParams` struct
  with an opaque builder API (`sampler_params_create` + `set_top_k/top_p/temperature/seed`, copied into
  the session config then deleted). The binding was rewired to the builder; no public surface change.
  The native enum dropped its `unspecified` member — the public `LiteRtSamplerType.Unspecified` is
  retained (at `0`, no break) and now means what its docs always said: no sampler parameters are sent
  and the executor's internal default sampling applies (the same effective behavior as v0.13.1, where
  the unspecified type made the native sampler factory defer to the executor).

## [1.0.0] — 2026-07-02

The first stable release: everything shipped in the `0.1.0-preview` line plus the changes below.
Breaking changes for preview consumers are listed under **Changed**. Built against
**LiteRT-LM v0.13.1** (same native binaries as preview.2/preview.3).

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
    so when it is off). Depends only on lightweight abstractions — `Microsoft.Extensions.AI.Abstractions` and
    `Microsoft.Extensions.DependencyInjection.Abstractions` (+ the core `LiteRtLmSharp` package).
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
- **Multi-turn multimodal history.** A restored conversation history can carry image/audio:
  `LiteRtMessage` gains an `Attachments` property and a `User(text, attachments)` factory,
  `LiteRtMessage.Serialize`/`Deserialize` round-trip media content parts, and history attachments are
  re-encoded through prefill — so a thread can refer back to an image/audio sent in an earlier turn.
  The MEAI / Semantic Kernel connectors restore prior-turn `DataContent` / file-`UriContent` media the
  same way.
- **`LiteRtBackend.Npu`** plus **`LiteRtBackend.Custom(string)`** — NPU support and a passthrough escape
  hatch for custom native builds exposing additional backends.
- **XML documentation now ships** in all three packages (`GenerateDocumentationFile`), so the public API
  appears in consumers' IntelliSense (the hand-written `///` docs were previously not emitted).
- **Cancellation.** `LiteRtConversation.CancelProcess()` cancels any in-flight generation from any
  thread (parity with the Kotlin/JS bindings' `cancelProcess`/`cancel` — the native cancel reaches the
  execution manager, so it cuts blocking sends short too, not just streams). The new awaitable
  `SendAsync(text[, attachments], ct)` and `SendToolResultsAsync(results, ct)` wrap the blocking send
  with that mechanism: cancelling the token cancels the native inference mid-generation and the task
  faults with `OperationCanceledException`. After a cancelled send, dispose the conversation and
  continue on a fresh one (restoring `History` if needed): sending again on a cancelled conversation
  hangs inside the native runtime — reproduced with Google's own binaries at v0.13.1 — while the engine
  itself is unaffected. The `IChatClient` connector's `GetResponseAsync` now honors its
  `CancellationToken` mid-generation as well (its stateless one-conversation-per-call design already
  matches the dispose-after-cancel contract).
- **Actionable native-load errors.** When the native library cannot be resolved, the
  `DllNotFoundException` now says what to do instead of a bare "Unable to load DLL": a missing binary
  names the `LiteRtLmSharp.runtime.<rid>` package for the process's RID (and the searched paths), while a
  binary that is present but fails to load points at the usual system prerequisites (VC++ Redistributable
  on Windows, missing system libraries on Linux, architecture mismatch).

### Changed (breaking — update `0.1.0-preview` consumers)

Surface clean-ups for the first stable release; source-breaking for preview consumers.

- **One send family on `LiteRtConversation`.** `Send(text[, attachments[, options]])` returning
  `LiteRtResponse` is the single blocking entry point: the string-returning
  `SendMessage(text[, attachments])` convenience is removed — read the answer from `Send(...).Text`
  (tool calls and the reasoning trace live on the same response, so nothing is silently dropped).
  `SendMessageStreamingAsync` is renamed to `SendStreamingAsync` and `SendMessageRaw` to `SendRaw`, so
  the whole family shares the `Send` stem. `attachments` is now uniformly nullable (null/empty =
  text-only) across the blocking, async and streaming variants, and every send accepts an optional
  **`LiteRtSendOptions`** — per-send settings (today `VisualTokenBudget`, overriding the
  conversation-level value) with room for the per-send knobs future native versions add.
- **`LiteRtEngineOptions.ModelPath` is no longer `required`** (it must still be set —
  `LiteRtEngine.Load` throws when it is empty). This leaves room for alternate model sources (e.g. the
  file-descriptor load newer native versions expose) to land as sibling properties without breaking.
- **Sampler types renamed** to match the `LiteRt` prefix every other public type uses:
  `SamplerType` → `LiteRtSamplerType`, `SamplerParams` → `LiteRtSamplerParams`, and
  `LiteRtSamplerParams.Type` → `.Strategy`. The knobs now validate on assignment (`TopK` positive,
  `TopP` in [0, 1], `Temperature` non-negative, NaN rejected). The defaults — `TopP`, 40, 0.95, 1.0,
  seed 0 — are the same values Google's official bindings fill in, so a partial configuration behaves
  identically across the LiteRT-LM ecosystem; seed 0 means sampling is **deterministic by default**
  (identical context reproduces the identical reply) — pass e.g. `Random.Shared.Next()` for varied
  output.
- **Backends are a typed `LiteRtBackend`** smart enum (`Cpu` / `Gpu` / `Npu` / `Custom(string)` /
  `Parse(string)`) instead of magic strings. `LiteRtEngineOptions.Backend` is now `LiteRtBackend` (was
  `string`), and `VisionBackend` / `AudioBackend` are `LiteRtBackend?` (were `string?`; `null` still = off).
- **Cache configuration is a typed `LiteRtCache`** (`Default` / `Disabled` / `InMemory` / `Directory(path)`).
  The `LiteRtEngineOptions.CacheDir` string and the `CacheDisabled` / `CacheInMemory` string sentinels are
  replaced by `LiteRtEngineOptions.Cache`.
- **Public enums carry explicit numeric values** (`LiteRtMessageRole`, `LiteRtAttachmentKind`,
  `LiteRtStreamChunkKind`) so inserting a member can't silently renumber the others.
- **Construction-time validation tightened** where a bad value previously surfaced as an opaque native
  failure: `LiteRtAttachment.Image/Audio` reject empty bytes, `LiteRtCache.Directory` rejects paths
  starting with `':'` (reserved for the engine's special cache tokens), and `LiteRtBackend.Parse`
  whitespace-normalizes custom values so `Parse(" xpu ")` equals `Parse("xpu")`.
- **`LiteRtMessage.Deserialize` rejects unknown roles.** A message whose role is not
  `system`/`user`/`model` (or `assistant`)/`tool` now throws `ArgumentException` instead of being
  silently parsed as a user turn — on the recommended persistence path, failing loudly beats
  misattributing who said what. Content parts of unknown types are still skipped (documented).

### Fixed

- **Streaming teardown no longer deadlocks on early exit.** Abandoning `SendStreamingAsync`
  mid-generation — cancelling the token, or simply `break`ing out of the `await foreach` — could hang
  forever: the teardown awaited the channel's completion without draining the chunks the consumer never
  read, and a channel only completes once its buffer is empty. The teardown now cancels the native
  inference and drains the channel, so early exits return promptly.
- **Blocking-send lifetime edge case.** A caller whose last use of a conversation was a blocking send
  could, in principle, have the wrapper garbage-collected while the native call was still running — the
  handle's finalizer would then delete the native conversation mid-generation. The blocking send now
  pins the wrapper for the duration of the call.
- **One-engine-alive counter leak.** The process-wide "single live engine" slot is now released when the
  native engine is destroyed — on `Dispose` *and* on finalization — so an engine that is garbage-collected
  without `Dispose` no longer leaves the slot stuck (previously the process could never load another model).

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
