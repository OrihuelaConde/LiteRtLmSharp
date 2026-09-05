# Project status and roadmap

Last updated: 2026-09-05 (v0.16.0 evaluation of the official C API prebuilts). Source of truth for "what's done and what's pending".

## Status per platform

| Platform | Native | NuGet | CPU | GPU | Validated on |
|---|:---:|:---:|:---:|:---:|---|
| win-x64 | ✅ | ✅ | ✅ | ✅ | real hardware (+ CI, CPU) |
| linux-x64 | ✅ | ✅ | ✅ | ✅ | real hardware (+ CI, CPU) |
| android-arm64 | ✅ | ✅ | ✅ | ✅ | real device (Adreno 650) |
| osx-arm64 | ✅ | ✅ | ✅ | ✅ | CI only (macos-15; GPU via WebGPU) |
| ios-arm64 | ✅ | ⏳ | — | — | CI build/link only (no device); on-device runtime + publish pending |

<sub>**CPU / GPU** = inference validated on that backend. **CI** = the `model-tests.yml` model leg
(all three OSes on each push via ci.yml; also runnable on demand) with a real model, incl. constrained
decoding; real-hardware results are from dev machines/devices. macOS GPU
specifics and dates are in [§macOS validation](#actionable-next-steps-suggested-order) below.</sub>

Native binaries are pinned to **LiteRT-LM v0.15.0** (repinned from v0.14.0 on 2026-08-09).

## Versioning policy

Package versions are **independent** of the LiteRT-LM native version (LLamaSharp/Whisper.net
model). The managed package and every `LiteRtLmSharp.runtime.<rid>` package share one version
and are published together; `Directory.Build.props` pins the native tag via `LiteRtLmVersion`
and the README compatibility table maps each release to it. Bump **minor** for native bumps or
new features, **patch** for binding-only fixes; tag the repo `v<version>` per published release.

## Binding functionality

| Area | Status |
|---|---|
| Chat (blocking + per-token streaming, cancellation) | ✅ |
| Function calling / tools (constrained decoding, Gemma token sanitization) | ✅ |
| System prompt, sampler params, max tokens, token count (context gauge) | ✅ |
| Speculative decoding (MTP drafter) + benchmark API (decode/prefill tok/s, TTFT, init time) | ✅ |
| Reasoning mode (`enable_thinking` via extra context) + KV-cache thinking filter | ✅ |
| Restore chat history (`History`/`HistoryJson`, `LiteRtMessage`) + conversation clone (`Clone()`) | ✅ |
| AOT/trim-friendly (`[LibraryImport]`, `[UnmanagedCallersOnly]`, no reflection) | ✅ |
| Multimodal messages (image/audio attachments, vision/audio backend, visual token budget) | ✅ |
| Tokenize/detokenize + start/stop tokens (exact token counting, no inference) | ✅ |
| Render a message (`RenderMessage`) or the whole preface (`RenderPreface`) to its templated prompt for debugging / exact-cost budgeting | ✅ |
| CPU thread counts, LoRA adapters (engine ranks + per-conversation paths), per-send output cap, tool-call streaming (v0.14.0 surface) | ✅ |
| .NET AI integrations: `Microsoft.Extensions.AI` `IChatClient` (+ Agent Framework) and a Semantic Kernel connector (separate packages) | ✅ |
| KV overflow guard (`LiteRtContextOverflowException`): sends clamp their reply to the remaining context and throw instead of overflowing the KV cache, which the native runtime does not police (heap corruption + deferred `0xC0000005`/`0xC0000374` crash — found by a downstream consumer app, 2026-07-13, in a stateful tool loop). Same-turn signal: `LiteRtConversation.IsContextFull` (true exactly when the next send would throw) → `ChatFinishReason.Length` in the MEAI client, overriding `ToolCalls` on a full conversation. Armed by an explicit `MaxNumTokens`; pure binding, no new native surface. **Done 2026-07-15** | ✅ |

Known constraints (documented in the README): one engine ALIVE at a time (reloading after
`Dispose` works — verified on win-x64 cpu→cpu and cpu→gpu; this is Edge Gallery's pattern for
switching model/backend without restarting); conversations are not thread-safe; `MaxNumTokens`
is the total context window; VC++ Redistributable required on win-x64; Android GPU requires
`<uses-native-library>` in the app manifest.

## C API coverage (audit 2026-08-09, header v0.15.0)

**115 of 140 `litert_lm_*` functions bound** (everything we bind exists in the header, no drift).
v0.15.0 grew the surface 109 → 140 with zero removals; the only signature changes formalize two
`int` parameters into enums with identical values (`set_activation_data_type`,
`set_min_log_level`), so no existing binding changed shape — but the **stream-callback typedef DID
change** (opaque `LiteRtLmStreamChunk*` + getters instead of `(text, is_final, error_msg)`
parameters), which is why the managed assembly requires same-version natives from this era on. All
31 new functions are bound (audit trail in CHANGELOG `[Unreleased]`):

- **Per-send decoding configs** (18): repetition-penalty builder (multiplicative HF-style +
  subtractive OpenAI-style + window), no-repeat-ngram builder, suppress-tokens builder, thinking
  config builder (enable + token budget, settable at conversation AND per-send level), and their
  optional-args setters → `LiteRtSendOptions.RepetitionPenalties/NoRepeatNgram/SuppressTokens/`
  `EnableThinking/ThinkingTokenBudget` + `LiteRtConversationOptions.ThinkingTokenBudget`.
- **Custom constrained decoding** (2): `conversation_config_set_constraint_provider` (LlGuidance,
  compiled from the llguidance Rust crate — in the tree since v0.13.1, only now exposed) +
  per-send `optional_args_set_constraint` (regex / JSON Schema) →
  `LiteRtConversationOptions.ConstraintProvider` + `LiteRtSendOptions.Constraint`. Note the C-level
  interplay verified in source: the per-send constraint needs only the provider
  (conversation.cc:398 — `enable_constrained_decoding` is the TOOL-calling path's switch), and the
  linux-x64 guard stays scoped to the tool path (the broken prebuilt is not involved here).
- **Prompt template** (1): `conversation_config_set_prompt_template` →
  `LiteRtConversationOptions.PromptTemplate` (Jinja override).
- **Engine knobs** (3): `gpu_decode_steps_per_sync`, `gpu_wait_for_weight_uploads` (both
  Artisan-GPU-only), `use_ringbuffers_local_attention` → the matching `LiteRtEngineOptions`.
- **Stream chunk** (3): `stream_chunk_get_text/is_final/get_error` — required, not optional: they
  are the only access to the v0.15.0 callback's payload.
- Readiness per the protocol below: real-engine upstream e2e tests exist for the new surface
  (engine_test.cc incl. an LlGuidance send), it is documented in upstream's docs tree
  (docs/api/cpp/constrained-decoding.md), and our own model scenarios assert content
  (DecodingOptionsTests). Text-LoRA remains STUBBED at v0.15.0 (resource_manager.cc:584, TODO
  b/462499294) — surface unchanged, stub-pin test still passing.

The v0.14.0 audit below is kept for history (denominators are the 109-function header). The
remaining 25 unbound functions are unchanged: the raw Session API (13), responses introspection
(10), the raw-FD engine load, and the NPU dispatch dir.

### High value (user-facing features)

| Feature | Functions | Notes |
|---|---|---|
| ✅ Restore chat history | `conversation_config_set_messages` | **Done 2026-06-16** (`LiteRtConversationOptions.History` typed `LiteRtMessage` list + raw `HistoryJson`; `LiteRtResponse.ToMessage()` + `LiteRtMessage.Serialize/Deserialize` for the caller-owned round-trip — the C API has no history getter). Replays the history through prefill (not a KV snapshot). Verified on gemma-4-E2B (CPU + win-x64 GPU/WebGPU): a restored conversation holds strictly more KV tokens than a fresh one. See [conversation-state.md](conversation-state.md). |
| ✅ Extra context | `conversation_config_set_extra_context` | **Done 2026-06-16** (`LiteRtConversationOptions.EnableThinking` for Gemma reasoning mode + raw `ExtraContext` escape hatch). Both samples expose a thinking toggle; pairs with the KV-cache thinking filter below. |
| ✅ Conversation clone | `conversation_clone` | **Done 2026-06-16** (`LiteRtConversation.Clone()` → independent conversation duplicating the prefilled KV-cache state; throws `LiteRtException` when the engine/backend returns `Unimplemented`). NOT CPU-only — verified on gemma-4-E2B on both CPU and win-x64 GPU (WebGPU): the clone copies the parent's token count, advances on its own, and leaves the parent untouched. See [conversation-state.md](conversation-state.md). |
| ✅ Engine cache dir | `engine_settings_set_cache_dir` | **Done 2026-06-15** (`LiteRtEngineOptions.CacheDir`, + `CacheDisabled`/`CacheInMemory` sentinels). Persistent compiled-shader/weight cache → faster GPU re-init; also the fix for speculative decoding on WebGPU (set `CacheDisabled`). |
| ✅ Speculative decoding | `engine_settings_set_enable_speculative_decoding` | **Done 2026-06-15** (`EnableSpeculativeDecoding`). Measured (gemma-4-E2B): desktop CPU **regresses** (~0.78×); desktop **WebGPU works** with `CacheDir=CacheDisabled` but doesn't help here (0.85× in a fair cache-off A/B; CPU-sampling fallback) — the disk-cache requirement is an upstream issue, see watchlist; accelerators are the expected ~3× win. See [speculative-decoding.md](speculative-decoding.md). |
| ✅ Multimodal messages | `engine_settings_set_max_num_images`, `conversation_optional_args_create/delete/set_visual_token_budget` | **Done 2026-06-17.** `LiteRtAttachment` (`Image`/`ImageFile`/`Audio`/`AudioFile`) + `Send`/`SendMessage`/`SendMessageStreamingAsync` attachment overloads build the content-part wire format (`{"type":"image"\|"audio","blob":<base64>\|"path":<file>}`, byte-verified against `runtime/conversation/.../data_utils.cc`); `LiteRtEngineOptions.VisionBackend`/`AudioBackend` enable the encoders via the already-bound `engine_settings_create`; `LiteRtConversationOptions.VisualTokenBudget` → `optional_args`. **Validated against gemma-4-E2B-it (2026-06-17): vision+audio on CPU across linux-x64/win-x64/osx-arm64 and vision on the osx-arm64 GPU leg (model-tests run 27712370474), plus win-x64 GPU locally.** An image adds a ~261-token vision block (28 → 289) and the model answered "…a solid, vibrant **red** color"; a real spoken 5→0 countdown adds ~130 audio tokens (35 → 165) and the model transcribed "Five, four, three, two, one, zero." Vision runs on GPU. **Gemma 4's audio sub-model is CPU-constrained** (`audio_backend="gpu"` → `engine_create` fails with "Audio backend constraint mismatch. Model requires one of [cpu]") — a model property, not a platform one (the model-tests macOS GPU leg confirms the same skip), so MAUI runs audio on CPU when the main backend is GPU. MAUI Chat tab gains 📷/🎵 attach buttons + a modality label (gated on model capability). `set_max_num_images` is bound for Kotlin-binding parity but legacy-only per the header. |
| ✅ LoRA adapters | `engine_settings_set_lora_rank`, `set_supported_lora_ranks`, `set_audio_lora_rank`, `set_supported_audio_lora_ranks`, `session_config_set_lora_path`, `session_config_set_audio_lora_path` | **Done 2026-07-10** (`LiteRtEngineOptions.LoraRank`/`SupportedLoraRanks`/`AudioLoraRank`/`SupportedAudioLoraRanks` + `LiteRtConversationOptions.LoraPath`/`AudioLoraPath`). The adapter file is opened when the conversation is created, so a bad path fails fast with `LiteRtException`. Requires a LoRA-enabled model; the supported-ranks lists are only honored on the GPU (Artisan) backend. **Not yet validated end-to-end against a real adapter** (no adapter artifact on hand), but the plumbing is in place and the failure modes surface coherently. |
| ✅ Tool-call streaming | `conversation_config_set_stream_tool_calls` | **Done 2026-07-10** (`LiteRtConversationOptions.StreamToolCalls` + `LiteRtStreamChunkKind.ToolCallDelta`). Streams the raw, unparsed text of a tool call as the model generates it (incremental progress fragments on the native `tool_call` channel), ahead of the usual complete parsed `ToolCall` chunk. Opt-in, off by default; progress display only, act on the final parsed chunk. |

### Medium value (developer utilities)

| Feature | Functions | Notes |
|---|---|---|
| ✅ Tokenizer surface (16) | `engine_tokenize`, `engine_detokenize`, `engine_get_start_token`, `engine_get_stop_tokens`, `tokenize_result_*` (3), `detokenize_result_*` (2), `token_union_*` (4), `token_unions_*` (3) | **Done 2026-06-19.** `LiteRtEngine.Tokenize(text)` → `int[]` and `Detokenize(ReadOnlySpan<int>)` → `string` run the model's own tokenizer with no inference (exact prompt budgeting against `MaxNumTokens`); `GetStartToken()`/`GetStopTokens()` expose the configured BOS/EOS tokens as `LiteRtTokenUnion` (a literal `Text` string or a sequence of `Ids`, per `Kind`). All 16 functions bound; result/union objects wrapped in SafeHandles, the `const int*`/`char*` views copied out before disposal. Validated on win-x64 CPU with gemma-4-E2B-it (round-trip, deterministic+monotone counts, non-empty stop tokens). |
| ✅ Benchmark API (11) | `engine_settings_enable_benchmark`, `conversation_get_benchmark_info`, `benchmark_info_*` (9) | **Done 2026-06-15** (`EnableBenchmark` → `LiteRtConversation.GetBenchmarkInfo()`). Prefill/decode tok/s, time-to-first-token, init time. Surfaced in both samples' gauges + the speculative-decoding A/B test. Per-turn getters guarded (the C wrapper does not bounds-check the turn index). |
| ✅ KV-cache thinking filter | `conversation_config_set_filter_channel_content_from_kv_cache` | **Done 2026-06-16** (`LiteRtConversationOptions.FilterThinkingFromKvCache`). Drops thinking-channel tokens from the KV cache so a long reasoning block does not consume the context window; companion to `EnableThinking`. |
| ✅ Prompt debugging | `conversation_render_message_to_string` | **Done 2026-06-20** (`LiteRtConversation.RenderMessage(text)` + raw `RenderMessageRaw(json)`). Returns the exact templated prompt a message would produce, without sending (KV cache untouched). Pairs with the tokenizer: render → `Tokenize` → exact per-turn cost including the chat template. The returned native string is conversation-owned (valid until the next render), copied out immediately. Validated on win-x64 CPU with gemma-4-E2B-it. |
| ✅ Engine tuning | `engine_settings_set_prefill_chunk_size`, `set_parallel_file_section_loading`, `set_activation_data_type` | **Done 2026-06-20** (`LiteRtEngineOptions.PrefillChunkSize` (CPU/dynamic), `ParallelFileSectionLoading` (bool?, default on), `ActivationDataType` (`LiteRtActivationDataType` F32/F16/I16/I8)). CPU prefill chunking, load parallelism, activation precision. Smoke-tested on win-x64 CPU (engine loads + generates with all three applied). |
| ✅ CPU thread counts (2) | `engine_settings_set_num_threads`, `set_audio_num_threads` | **Done 2026-07-10** (`LiteRtEngineOptions.NumThreads`/`AudioNumThreads`). CPU text / audio executor thread counts (`null` = engine default). CPU-backend only: no-op on non-CPU backends, and the audio one applies only when an audio executor is configured; non-positive values are rejected. See [engine-tuning.md](engine-tuning.md). |
| ✅ Per-send output cap | `conversation_optional_args_set_max_output_tokens` | **Done 2026-07-10** (`LiteRtSendOptions.MaxOutputTokens`). Overrides the conversation-level `MaxOutputTokens` for a single send; the runtime resolves the per-send value over the session value. |
| ✅ Preface rendering | `conversation_render_preface_to_string` | **Done 2026-07-10** (`LiteRtConversation.RenderPreface()`). Renders the templated conversation preface (system message + tools + history) to a string without sending (companion to `RenderMessage`); pair with `LiteRtEngine.Tokenize` to measure the preamble's token cost. Also the tool that diagnosed the `SystemMessage` fix. See [conversation-state.md](conversation-state.md). |
| ✅ Sampler builder (6) | `sampler_params_create`/`_delete`, `set_top_k`/`set_top_p`/`set_temperature`/`set_seed` | **Done 2026-07-10** (internal migration, no public surface change). v0.14.0 replaced the by-value `LiteRtLmSamplerParams` struct with an opaque builder (create + setters, copied into the session config then deleted); the binding was rewired to it. The native enum dropped its `unspecified` member, so `LiteRtSamplerType.Unspecified` is retained (value `0`) and now sends **no** sampler params at all (the executor's internal default), matching the pre-v0.14.0 effective behavior. |

### Low priority (advanced / niche)

| Feature | Functions | Notes |
|---|---|---|
| Raw Session API (13) | `engine_create_session`, `session_run_prefill`, `session_run_decode(_async)`, `session_generate_content(_stream)`, `session_run_text_scoring`, `session_cancel_process`, `session_config_set_apply_prompt_template`, `session_delete`, `session_get_benchmark_info`, `input_data_create`/`_delete` | Low-level prefill/decode bypassing chat templates; includes text scoring (log-prob ranking) and the raw no-template mode. v0.14.0 added `input_data_create`/`_delete` and changed the `run_prefill`/`generate_content` signatures; still out of scope (the Conversation API covers our use cases). |
| Responses introspection (10) | `responses_*` | Candidates, scores, per-token logits — only meaningful with the Session API. |
| Raw-FD engine load | `engine_settings_create_from_raw_file_descriptor` | **Deferred** (new in v0.14.0). Loads a model from an open file descriptor (mainly Android `content://` scenarios); the path-based `engine_settings_create` covers the desktop/MAUI paths we ship. |
| ✅ Benchmark fake tokens | `engine_settings_set_num_prefill_tokens`, `set_num_decode_tokens` | **Done 2026-06-20** (`LiteRtEngineOptions.BenchmarkPrefillTokens` / `BenchmarkDecodeTokens`). Synthetic-token benchmarking: the prompt is padded/truncated to the prefill count and decode runs exactly the decode count (ignoring the stop token), so `GetBenchmarkInfo` reports throughput at FIXED counts — content-independent device benchmarking. **Confirmed observable through the Conversation API** (not a benchmark-main-only path): both fields feed `EngineSettings::benchmark_params_`, read by the default `EngineAdvancedImpl`/`SessionAdvanced` (source trace + win-x64 probe: a tiny "Hi" reports 256/64). Setting either also flips benchmark mode on; the reply is not a real answer. |
| NPU dispatch dir | `engine_settings_set_litert_dispatch_lib_dir` | Qualcomm/Intel NPU dispatch library location. |

> Note: the C API still has **no embeddings functions** at v0.14.0 (flutter_gemma implements
> embeddings via a separate native library, not this header), so embeddings stay out of
> scope until upstream exposes them.

## Actionable next steps (suggested order)

-3. **v0.16.0 CYCLE — evaluation of Google's official C API prebuilts DONE (2026-08-12 →
   2026-09-02, branch `capi-prebuilts-probe`); the repin itself is PENDING the maintainer's go.**
   Upstream v0.16.0 (2026-08-11; v0.16.1 is the same commit with a Kotlin/Windows build-flag fix
   and no new artifacts) ships the first versioned C API prebuilts: `litert_lm_c_api-0.1.0.zip`, one
   monolithic shared library per platform (win/linux/mac/android, plus linux-arm64 and
   android-x86_64) with the constraint provider, the GPU accelerators and LlGuidance embedded and a
   static CRT, and `CLiteRTLM.xcframework` for iOS (device + simulator slices). C API 140 → 144
   functions, no removals and no signature changes (the v0.15.0 stream-callback ABI is unchanged);
   new: `engine_settings_set_enable_ynnpack` and three session checkpoint/rewind functions (raw
   Session API, out of scope). The zip lacks the three capabilities functions the xcframework
   exports (147 vs 144; fixed upstream after the release, PR #3273).
   **Measured, not assumed** (full 258-test suite with the CI flags, probe workflows
   `capi-prebuilt-probe.yml` run 31618878076 and `capi-xcframework-probe.yml` run 33669809621):
   | RID | Official prebuilt | Notes |
   |---|---|---|
   | linux-x64 | suite green (Docker + CI) and **tools + constrained decoding PASS** — the
     LiteRT-LM#2149 segfault is gone (embedded provider, the same build as the Python wheel) |
     hard `DT_NEEDED libvulkan.so.1` (the library does not load without the Vulkan loader);
     130 MB raw / 47 MB compressed vs ~83 / 35 self-built; 211k dynamic symbols |
   | win-x64 | CPU 257/258, GPU 254/258 on an RTX 3080 (the one failure is the LoRA stub-pin
     sentinel, see below) | GPU only with our `dxcompiler.dll` + `dxil.dll` next to it: the
     official Dawn requires DXC (`dxil.dll Error 87`) and the zip ships neither; no VC++
     Redistributable needed (static CRT); 48 MB vs ~95 MB self-built |
   | osx-arm64 | CI CPU + GPU (WebGPU) green | 68 MB |
   | ios-arm64 | official xcframework inspected + a `net10.0-ios` consumer linked against it alone |
     147 exports, install name `@rpath/CLiteRTLM.framework/CLiteRTLM`, min iOS 15.0; the
     provider is embedded but the Metal accelerator and sampler are still dlopen'd by dylib name |
   | android-arm64 | **real device (Moto G100 / Adreno 650 / Android 12, 2026-09-05):** the sample
     app with the single official `.so` loads gemma-4 E2B on GPU (OpenCL picked, 15.7 tok/s decode
     warm, TTFT 0.7 s) and on CPU (12.2 tok/s), and the tools tab (constrained decoding, embedded
     provider) answers both demo tools; APK 60 MB vs 78 MB with the self-built set | the OpenCL
     sampler is **embedded** (`LiteRtTopKOpenClSampler_*` plus the `_Static` pointers): the runtime
     logs "falling back to statically linked C API" and never falls back to CPU sampling, so
     LiteRT-LM#2211 (missing `DT_NEEDED`) and the patchelf become moot. **Same-device A/B: our
     self-built `native-v0.15.0` set FAILS on GPU** — upstream's v0.15.0 prebuilt sampler exports
     only 4 of the 7 functions the v0.15.0 factory requires (`CanHandleInput` missing), the
     WebGPU sampler then fails with `NOT_FOUND` and generation errors out (LiteRT-LM#3135, open
     since 2026-08-05, reported by the Unity binding); v0.14.0 was fine and the v0.16.0 prebuilt
     exports all 7. The self-built Android GPU path was broken from the v0.15.0 repin onward
     (never published: 1.1.1 ships v0.14.0) |
   Era findings, each verified with the correct binary: text LoRA is implemented (the runtime
   test bundle + rank-32 adapter changes the output; an invalid file fails fast) — published gemma-4
   bundles likely carry no LoRA slots (LiteRT-LM#3173, our data posted); the new statically linked
   sampler makes an explicit TopK work on desktop GPU (LiteRT-LM#2073 / PR #2801 moot, and upstream
   exported the samplers on its own in PR #3244); LiteRT-LM#2080 (sampler params ignored after the
   first generation) does not reproduce through the C API; the `filter_channel_content_from_kv_cache`
   default flip is neutral for gemma-4 (the next turn re-prefills without the thinking either way,
   on v0.15.0 too); the KV guard calibration holds unchanged (a raw C API ladder near the limit is
   identical on both versions, every case fails cleanly), while a `MaxNumTokens` below the model's
   largest prefill signature (1024 for gemma) breaks prefill outright — worth a managed validation.
   Item 8 below (clone state) turned out to be a contract violation on our side, resolved in
   `master`. Community context: flutter_gemma moved to v0.16.0 but still self-builds. **Verdict
   after the device run: every platform we can test is green on the official prebuilts, and the
   Android result turns the repin from a preference into a fix** (see the android-arm64 row).
   **Repin checklist (in this order, after the go):** (1) `LiteRtLmVersion` → v0.16.0 (the zip
   lives on that tag). (2) Natives: a CI job that fetches the official zip and repackages it into
   our `runtimes/<rid>/native` layout under our library names (no SONAME / install-name stands in
   the way) for win/linux/mac; win adds the DXC pair as companions; linux documents `libvulkan1`
   and the size; Android also switches to the official monolith (validated on device); iOS switches to the official
   xcframework with a resolver name mapping (`CLiteRTLM`) and optional Metal companions.
   (3) Drop the linux-x64 tools+constrained guard and flip its CI test. (4) LoRA: retire the
   stub-pin test, document text LoRA (LoRA-enabled bundles only), keep the fail-fast messages.
   (5) Bind `set_enable_ynnpack` (116/144; note the session checkpoint/rewind trio as out of
   scope). (6) Document the thinking-filter default and the `MaxNumTokens` floor. (7) Item 7 (MEAI
   surface for `NoRepeatNgram` / `SuppressTokens`). (8) Re-check the consumer-smoke "Dawn init
   lines" signature (upstream downgraded most INFO logs to VLOG). (9) The 1.2.0 release runbook:
   CHANGELOG sections for both cycles, README compat row `1.2.0 | v0.16.0`, version sweep, pack
   dry-run, ConsumerSmoke, publish only with the explicit go. (10) Upstream housekeeping: close
   PR #2801 as superseded, comment on #2149 once the official build ships. (11) Retire or fold the
   probe workflows.

-2. ✅ **v0.15.0 REPIN CYCLE — MERGED to master 2026-08-12 (PR #5, 13 commits; closed the
   upstream-watch issue #4).** Hardened pre-merge by a 6-lens adversarial review (17 findings
   applied — see the PR) and full real-model coverage of every new feature across core, MEAI and
   SK (suite 257/0 local CPU+GPU; CI model legs green on all 3 OSes). **Publication decision
   (maintainer, 2026-08-12): SKIP the NuGet release at v0.15.0** — upstream tagged v0.16.0 on
   2026-08-11 (one week after v0.15.0, shipping the first official C API prebuilts), so 1.2.0
   ships with the v0.16.0 cycle instead and this cycle's CHANGELOG section rides along.
   Original summary: native `native-v0.15.0` release published (defines trimmed to
   `litert_runtime_link_mode=dynamic` + `resolve_symbols_in_exec=false`; win-x64 collect step's
   lost companion copy restored); all 31 new C-API functions bound (115/140); the v0.15.0
   stream-callback ABI change absorbed internally; multi-conversation stateful mode + forking
   unlocked (see watchlist); KV-overflow guard recalibrated to the new native prefill-reserve
   behavior. Full model suite 239/0 on win-x64 CPU; GPU clean run + documented intermittent
   pre-existing churn crash. **Next after merge:** release prep runbook (bump to 1.2.0 per the
   versioning policy — native bump = minor —, CHANGELOG section cut, README compat row
   `1.2.0 | v0.15.0`, version sweep in guides/snippets, pack dry-run + ConsumerSmoke, publish only
   with the explicit GO), then the docs/memory consolidation pass. Follow-up candidates from the
   cycle: #2807 closing comment, CPU-sampler-factory upstream report, GPU churn-crash
   investigation, #2149 third-era blob probe (docker harness).

-1. ✅ **1.1.1 RELEASED — 2026-07-18** (GPU native-layout fix). Published to nuget.org (all 7
   packages, shared-version policy; runtimes carry the same v0.14.0 natives), tag `v1.1.1` +
   GitHub release with assets. Post-publish smoke against the public feed (`--no-cache`, indexed
   in ~4 min): GPU raw + GPU IChatClient (21 Dawn init lines each) + CPU Semantic Kernel — all
   generating. This release also fixes the packaged README: 1.1.0 shipped the repo-root README
   (HTML hero nuget.org can't render); 1.1.1 embeds the dedicated `packaging/nuget-readme.md`.
   The first downstream NuGet adoption hit `litert_lm_engine_create returned null` with `Backend=Gpu`
   on Windows, framework-dependent: the engine's own runtime `LoadLibrary` of the accelerators
   (`libLiteRtWebGpuAccelerator.dll` ← libLiteRt, `libLiteRtTopKWebGpuSampler.dll` ← LiteRtLm,
   `dxcompiler.dll`/`dxil.dll` ← Dawn at first shader compile — all confirmed by PE import-table +
   strings analysis) searches exe dir + PATH, never NuGet's `runtimes/win-x64/native/`. CPU works
   (static imports resolve via the altered search path); ProjectReference never saw it (flat dev
   copy, `Pack=false`). **Fix (managed-only; the runtime packages bump to 1.1.1 per the
   shared-version policy but carry the same v0.14.0 natives — runtime 1.1.0 + base 1.1.1 also
   works):**
   `NativeLibraryResolver.PreloadCompanions` now also runs on Windows — preloads companions by
   absolute path so later by-name loads hit the loader's already-loaded-module short-circuit
   (flag-independent, unlike `AddDllDirectory`); skips the unreferenced non-`lib`-prefixed twin
   DLLs (avoid double-mapping Dawn) and only applies to the `runtimes/` layout (flat exe-dir
   already works natively; sweeping the app output would load unrelated DLLs). Unit tests green.
   **E2E verified 2026-07-18** via the consumer-harness verify script (`.tmp/consumer/`,
   gemma-4-E2B): negative control
   on 1.1.0 GPU fails with 0 Dawn lines (reproduces the bug); packed 1.1.1 GPU generates text with
   the 21 healthy WebGPU/Dawn init lines — real generation, so the lazy dxc shader-compile path is
   proven, not just engine_create; CPU leg passes (side effect: GPU stack now mapped, ~8 dawn
   mentions on stderr, no behavior change); `dotnet publish` leg passes. Full suite 200 passed /
   20 skipped (platform-gated) / 0 failed, model tests included. **Next:** release runbook (see
   step 0) with maintainer GO; afterwards the consumer app drops its temporary flatten-natives
   workaround + repins + re-runs its GPU smoke. Follow-ups — BOTH PREPARED 2026-07-18:
   (1) pack-nuget.yml now runs a consumer-smoke job (windows-latest) that gates the publish job:
   CPU legs generate through all three package layers from the packed feed, and a GPU leg asserts
   the accelerator LOAD PATH (WebGPU/Dawn init lines on stderr — needs no physical GPU; zero
   lines is the exact signature of this bug class). (2) build-native.yml no longer ships the
   prefixless twin DLL copies (~26 MB dead weight; PE/strings-verified unreferenced) — takes
   effect at the next natives rebuild, and the win-x64 package slims from ~54 to ~28 MB then; the
   resolver's twin filter keeps handling old tarballs either way.

0. ✅ **1.1.0 RELEASED — 2026-07-17.** The deliberate wait paid off: dogfooding the stateful mode in
   a downstream consumer app surfaced the KV-overflow bug (→ native heap corruption), which shipped fixed —
   guard + same-turn `IsContextFull`/`FinishReason.Length` signal, hardened by an 8-angle adversarial
   review (10 findings applied) — instead of as a 1.1.1 patch. Runbook executed as written: (a)-(c)
   version/CHANGELOG/README, (d) dry-run + artifact inspection (7 packages, XML docs, no iOS),
   (e) ConsumerSmoke from a local feed (core chat/streaming/cancellation + IChatClient stateless AND
   stateful recall + SK — all green), (f) push=true via Trusted Publishing with the maintainer's GO,
   (g) tag `v1.1.0` + GitHub release with assets, then the smoke re-run against the PUBLIC nuget.org
   feed with a purged cache — all green. Original runbook (kept for the next release):
   (a) bump `<Version>` to 1.1.0 + refresh `PackageReleaseNotes` in `Directory.Build.props`;
   (b) rename CHANGELOG `[Unreleased]` → `[1.1.0] — <date>` (re-read it start to end first — it is
   the release body);
   (c) add the README compat row `1.1.0 | v0.14.0`, and bump the version in every install
   snippet: README quick start + `packaging/nuget-readme.md` (the packaged README) +
   `docs/index.md` + the `PackageReference` blocks in
   `docs/{android,extensions-ai,semantic-kernel,packaging}.md` (grep the old version; the
   guides shipped stale preview versions until the 2026-07-17 sweep);
   (d) dispatch `pack-nuget.yml` with `push=false` → inspect artifacts (XML docs inside, versions,
   no iOS) — note the new tag-exists guard will pass (no `v1.1.0` tag yet);
   (e) ConsumerSmoke from a local feed (the 1.0.0 procedure: core chat/streaming/cancellation +
   IChatClient incl. the new stateful mode + SK kernel);
   (f) `push=true` ONLY with the maintainer's explicit publish OK (`native_ref` defaults from the
   props = v0.14.0);
   (g) post-publish: re-run the smoke against the public nuget.org feed (`--no-cache`), then the
   docs/memory consolidation pass.

1. ✅ ~~Android GPU sampling~~: verified on a physical device — the patched samplers load (no
   CPU-sampling fallback) and output is correct. ✅ ~~Roadmap follow-up: expose
   `EnableSpeculativeDecoding`~~ — **done 2026-06-15** alongside the benchmark API, an A/B model
   test, and sample toggles (Console `--spec`/menu, MAUI per-model switch). **Measured findings**
   (gemma-4-E2B, dev box): desktop CPU regresses (~0.78×, 29.9→23.4 tok/s — drafter overhead not
   amortized on CPU); desktop **WebGPU works only with the disk cache off** (`CacheDir=CacheDisabled`)
   and doesn't help there either (0.85× in a fair cache-off A/B, 35.5 vs 41.8 tok/s — our WebGPU
   sampler falls back to CPU; plain GPU with the cache is ~85 tok/s).
   The cache requirement is an upstream file-sharing bug confirmed against Google's own CLI (see
   watchlist), fixed our side by binding `engine_settings_set_cache_dir`. ✅ Real-device check
   (Moto G100 / Adreno 650 / OpenCL, 2026-06-16) is **also neutral (~1.01×)**: logcat confirms MTP
   runs correctly (drafter compiles on OpenCL, GPU sampler loads via the patchelf, no CPU fallback),
   but draft acceptance is only **~32%** (399 drafted / 126 verified — model/prompt-bound, same as
   desktop's ~0.317), too low to beat the drafter overhead on this older GPU. A newer flagship GPU is
   the remaining thing to try for the ~3×. Full write-up: [speculative-decoding.md](speculative-decoding.md).
2. **macOS validation**: ✅ CI, CPU and GPU — `model-tests.yml` runs the full suite on each push (and
   on demand) on `macos-15` (Apple Silicon): CPU 6/6 and GPU 6/6 (run 27458626459, 2026-06-13), both
   including the real constrained-decoding loop. The GPU pass is now a REQUIRED check (no
   longer `continue-on-error`) and runs the WebGPU delegate, not the native Metal one (why
   below). Real-hardware validation still pending (no Apple Silicon machine on hand).
   - ✅ ~~Metal sampler dlopen failure~~ (fixed 2026-06-12): the prebuilt
     `libLiteRtTopKMetalSampler.dylib` needs `@rpath/libLiteRt.dylib`, which the static-link
     macOS build excluded → CPU-sampling fallback. macos-arm64 now builds with
     `litert_link_capi_so=true` + `resolve_symbols_in_exec=false` (the second define is
     mandatory — see Architecture decisions) and ships `libLiteRt.dylib`; the re-run GPU
     pass shows no "Metal sampler not available" fallback (run 27436193072). iOS does NOT
     need (and cannot use) this fix: Google's iOS prebuilt sampler has no
     @rpath/libLiteRt.dylib load command — its 166 LiteRt* imports are DYNAMIC_LOOKUP and
     the static libLiteRtLm.dylib exports all of them (verified by Mach-O inspection,
     2026-06-12); the dynamic recipe also fails to build on iOS (@litert routes iOS through
     a macos_dylib whose transition pulls XNNPACK SSE kernels into the -fembed-bitcode ios
     config, run 27436848074).
   - `ToolCalling_Unconstrained` failed on backend=gpu with a malformed tool call
     (`call:get_current_weather{location}`, unparseable) — **root-caused 2026-06-12 to the
     upstream native METAL delegate on the paravirtual runner, NOT our binding and NOT the
     sampler**, via a manual workflow that ran Google's own litert_lm_main on the runner
     (`mac-gpu-cli-probe.yml`, removed once the question was settled — see git history). Four passes, same runner/model:
     | Compute | Sampler | Output | Decode |
     |---|---|---|---|
     | WebGPU (Dawn→Metal) | CPU-fallback | ✅ byte-perfect | 30 tok/s |
     | Metal | Metal (#2073) | ❌ `the the the…` | 10 tok/s |
     | Metal | CPU (flutter_gemma config) | ❌ `the the the…` | 30 tok/s |

     Removing the sampler entirely (3rd row, flutter_gemma's exact macOS recipe) did NOT fix
     it, so the broken Metal sampler (#2073) is exonerated — the Metal *compute* path is wrong
     on GitHub's `Apple Paravirtual device`. Runs 27446959593 / 27447067775 / 27447850507 /
     27457151573. `gpu_registry`'s desktop order is WebGpu→OpenCL→Metal, so Google's own CLI
     (full prebuilt set) runs WebGPU and is clean; our package excluded the WebGPU pair
     (inherited from flutter_gemma, a461607) so it always hit Metal. **Decision (user, 2026-06-13):
     ship Google's complete macos_arm64 set — WebGPU pair + Metal pair — so the engine runs
     WebGPU on the runner (correct) and keeps Metal as a real-hardware fallback.** This matches
     upstream's macOS default (PR #2302). flutter_gemma keeps Metal only because it runs on real
     Apple Silicon, where Metal works (their 5/5 tests); we cannot validate Metal in CI because
     the runner's GPU is paravirtual. Whether real-hardware WebGPU-on-macOS is equally clean is
     the remaining unknown (mac test kit); on the runner WebGPU is both correct and ~3× faster.
     **✅ DONE (2026-06-13):** `build-native.yml` macos-arm64 collect now ships the full set;
     `native-v0.13.1` rebuilt+republished and the model-tests osx-arm64 GPU pass is 6/6 green
     (run 27458626459 — log shows "Created a WebGPU environment", WebGPU sampler falls back to
     CPU cleanly, all tools tests pass). Our binding needs NO `gpu_registry` patch (unlike
     flutter_gemma): the upstream WebGpu→Metal desktop order already picks WebGPU, and
     `NativeLibraryResolver`'s RTLD_GLOBAL preload of the whole dir caused no symbol clash with
     the also-shipped Metal pair. `libLiteRtWebGpuAccelerator.dylib` is self-contained (Dawn
     static, no `@rpath/libLiteRt.dylib` dep), so no extra companion was needed.
   Real-hardware Metal validation (the "mac test kit": console sample published for
   osx-arm64 + natives + instructions) is now unblocked and still worthwhile (it would also
   tell us whether real-hardware WebGPU-on-macOS is as clean as it is on the runner).
3. ✅ ~~Public release~~ (2026-06-11): renamed to `LiteRtLmSharp`, repo public,
   `0.1.0-preview.1` published to nuget.org via Trusted Publishing (OIDC, no API key),
   consumer smoke test passed, announced in #2535 and listing PR opened
   ([LiteRT-LM#2552](https://github.com/google-ai-edge/LiteRT-LM/pull/2552)).
   ✅ Follow-up done: the `LiteRtLmSharp.` ID prefix reservation on nuget.org was granted on
   2026-06-16.
4. **Upstream reports**: (a) ✅ posted to #1881 — Vulkan/Dawn FP16 shaders fail on older Adreno
   and the engine emits silent garbage instead of an error/fallback; (b) dropped — the
   `uses-native-library` requirement is documented in upstream's Kotlin getting-started docs;
   (c) ✅ posted to #2211 — bionic ignores `RTLD_NOLOAD|RTLD_GLOBAL` flag promotion, so there is
   no consumer-side workaround for the missing `DT_NEEDED`.
5. **iOS app phase**. ✅ Done: the `LiteRtLmSharp.runtime.ios-arm64` package (dynamic `.framework`
   xcframeworks via `NativeReference Kind=Framework` + buildTransitive `.targets`), the resolver's
   iOS branch, the xcframework build in `build-native.yml`, the MAUI sample's macOS-gated
   `net10.0-ios` target, and a build/link CI check (`ios-package-check.yml`).
   **Deferred to the device-validation phase** (needs a paid Apple Developer account + a physical
   iPhone; the package is NOT device-functional until these land — do not publish it to nuget.org
   before then):
   - **Companion `dlopen` patch** — point the engine's internal dlopen of the Metal accelerator /
     constraint provider at the framework layout (`@executable_path/Frameworks/<X>.framework/<X>`),
     mirroring flutter_gemma (their `gpu_registry.cc`/`sampler_factory.cc` patch, `patch_c_api.sh` §10b,
     ITMS-90432). Without it the app links green but the engine fails to load the companions at runtime.
   - **Resolver runtime robustness** — the iOS branch must load `libLiteRtLm` with global visibility
     (RTLD_GLOBAL-equivalent) and eagerly preload the companions in dependency order, so the sampler's
     flat-namespace (DYNAMIC_LOOKUP) imports resolve. Currently it loads only the main framework; this
     is hardware-untested and intentionally not guessed at until a device is available.
   - **Device validation** — TestFlight (paid account + cloud macOS CI, no Mac of your own needed):
     build+sign+upload via fastlane with an App Store Connect **Team** API key, internal tester, install
     via the TestFlight app. This is the only path that gives real-hardware validation.
6. **Optional**: binding coverage push per the "C API coverage" section above. **The high-value group is
   now complete** — ✅ history restore + clone (2026-06-16), ✅ cache dir + ✅ speculative decoding
   (2026-06-15), ✅ multimodal image/audio (2026-06-17), ✅ tokenizer surface (exact token counting,
   2026-06-19; cumulative **67/89** at preview.3). **Multimodal is validated cross-platform** (`model-tests.yml` run 27712370474, 2026-06-17):
   vision **and** audio pass on the CPU leg of **linux-x64, win-x64 and osx-arm64**, and vision passes on
   the **osx-arm64 GPU** leg (WebGPU→Metal). Audio-on-GPU does NOT run because **gemma-4's audio sub-model
   is CPU-constrained** (the model declares "requires one of [cpu]"); the macOS GPU leg's audio test skips
   with the exact `Audio backend constraint mismatch` message — identical to win-x64 — confirming it's a
   model property, not a platform limitation. So audio always runs on CPU (the sample falls it back).
   **Multimodal robustness ✅ DONE 2026-06-20:** root-caused and fixed the "Vision/Audio executor should
   not be null" footgun. The encoder executor only loads when the conversation carries a session config;
   the binding now attaches one automatically when the engine was loaded with `VisionBackend` /
   `AudioBackend`, so a plain `CreateConversation()` can send attachments (a bare session config is
   enough, verified). This corrects the 2026-06-17 mis-diagnosis that blamed `MaxNumTokens`: it was a
   confound (the failing case used a bare conversation, the working one set `MaxOutputTokens`); the real
   `MaxNumTokens` floor is just the media's token count (~256/image — fails at 256, works at 384), not
   4096. When a send still fails (not multimodal / backend unset / context can't hold the media) the
   binding wraps it in a managed `LiteRtException` naming those causes on both the blocking and streaming
   paths. See [native-abi.md](native-abi.md#multimodal-messages-image--audio--verified-wire-format).
   The medium-value utilities are all bound (prompt rendering for debugging; engine tuning knobs:
   prefill chunk size, parallel file loading, activation dtype). **The v0.14.0 repin (2026-07-10)
   landed the new C-API surface**: LoRA adapters, CPU thread counts, per-send max-output-tokens,
   tool-call streaming and preface rendering, taking coverage to **84/109**. FD-based load
   (`engine_settings_create_from_raw_file_descriptor`, mainly Android `content://`) is the one
   deferred item left; the raw Session API and responses introspection stay out of scope.
   `android-x64` for emulators; Desktop meta-package;
   ✅ ~~CONTRIBUTING + issue templates~~ (2026-06-11: CONTRIBUTING.md, issue forms, PR template,
   SECURITY.md, Discussions enabled); scheduled smoke-test workflow that consumes the published
   packages from nuget.org; PR upstream to be listed among the language bindings (planned right
   after the nuget.org release).

7. **PENDING — MEAI surface for `NoRepeatNgram` and `SuppressTokens`** (raised by a downstream
   consumer app, 2026-08-18). Both live on `LiteRtSendOptions` and are reachable only through the
   native API: `LiteRtChatMapping.ToSendOptions(ChatOptions)` reads just `MaxOutputTokens`,
   `FrequencyPenalty`, `PresencePenalty` and the schema constraint, so a consumer that talks only
   `IChatClient` cannot get to them.

   Suggested shape — `LiteRtChatOptions` is already the established extension point for knobs MEAI
   does not cover (it stores them in `AdditionalProperties`, so they survive middleware that clones
   the options), and today exposes `EnableThinking` and `EnableConstrainedDecoding`:

   ```csharp
   // LiteRtChatOptions
   public int? NoRepeatNgramSize { get; set; }               // key "no_repeat_ngram_size"
   public IReadOnlyList<int>? SuppressTokens { get; set; }   // key "suppress_tokens"
   ```

   Consumer use case, so the design has something concrete to serve: on the small model (gemma-4
   E2B) the consumer sees the model echo-copy prompt templates verbatim — it emits an option template
   (`CHOICE: OTHER — the user gives...`) as if it were a verdict, and once "resolved" a pending
   question with that garbage. Today the consumer masks it with a deterministic sanity guard on the
   output. `SuppressTokens` over the sentinel's ids, or `NoRepeatNgram` over the template, would
   attack it during sampling instead.

   Why the existing penalties do not substitute: they discount ANY repeated token, and the
   consumer's extraction phase must repeat literals from the user's message (a name or a date
   appears two or three times). Measured on E4B, `FrequencyPenalty`/`PresencePenalty` at 0.3 were
   inert (43/44, identical to baseline) — the wrong instrument for this failure.

   Note for whoever designs it: `SuppressTokens` needs token ids from `LiteRtEngine.Tokenize`, and
   most words tokenize differently with and without a leading space, so a consumer will want to
   seed both variants. That pushes id computation into the composition root (the engine is not
   reachable from `IChatClient`), which is worth a line in the docs.


8. ✅ **RESOLVED 2026-09-02 (commit 18250e5) — cloning a conversation that has never advanced is
   unsupported by construction; `Clone()` now rejects it.** Originally reported as "a conversation
   whose clone has been used loses its own state once another conversation runs on the same
   engine: its next send (directly or through a new clone) continues the OTHER conversation's
   context" (found by a downstream consumer app, 2026-08-19, native v0.15.0, win-x64 GPU +
   Float32, gemma-4 E4B; reproduced identically on the v0.16.0 official prebuilts, CPU and GPU,
   E2B and E4B). **Root cause (maintainer's hypothesis, confirmed by probe):** the native clone
   "duplicates its prefilled state" (C API doc) and creating a conversation prefills nothing, so a
   parent created with only a system message has no state to duplicate; its first clone pays the
   prefill itself and looks fine, but after any other conversation runs on the engine the parent
   and its later clones inherit the resident context. A parent that has sent one real turn
   (`TokenCount > 0`) is stable across every interleaving tried (clone before, clone after, direct
   send after another conversation). Not an upstream bug: the documented contract was not met.
   **Binding:** `Clone()` throws `InvalidOperationException` when `TokenCount == 0`, docs + guide +
   CHANGELOG updated, model test added; the MEAI fork was never exposed (ids exist only after a
   send). **Consumer advice:** warm a reusable base with a direct send, not with a throwaway
   clone (that was the workaround tried, and it cannot work). The original narrative follows for
   the record.

   Repro with the binding alone, no consumer code:

   ```csharp
   using var engine = LiteRtEngine.Load(opts);              // GPU, MaxNumTokens 4096, F32
   var routerOpts = new LiteRtConversationOptions {
       History = [LiteRtMessage.System(routerPrompt)],       // "Reply with exactly one word."
       EnableThinking = false,
   };
   using var base_ = engine.CreateConversation(routerOpts);

   using (var c = base_.Clone())                             // -> "SAVE"     correct
       await c.SendAsync(msg, null, new LiteRtSendOptions { MaxOutputTokens = 16 });

   using (var other = engine.CreateConversation(new LiteRtConversationOptions {
       History = [LiteRtMessage.System("You are a memory assistant. Query the graph with tools.")],
       Tools = tools, EnableConstrainedDecoding = true, EnableThinking = false }))
       await other.SendAsync("¿Y mis primos?", null, new LiteRtSendOptions { MaxOutputTokens = 256 });

   using (var c = base_.Clone())                             // -> "qué primos estás hablando…"  WRONG
       await c.SendAsync(msg, null, new LiteRtSendOptions { MaxOutputTokens = 16 });
   ```

   The second clone returns a literal continuation of what was asked of the *other* conversation,
   and in other runs emits that conversation's tool-call format
   (`<|tool_call>call:describe_entity{...}<tool_call|>`) even though the router prompt has no tools.
   `base_` is never advanced, so its prefilled state should be intact — the clone is picking up the
   most recently active conversation instead of its parent.

   Isolation matrix (2026-09-02, probe in `.tmp/capi-gpu-probe`, same sequence with one factor
   changed at a time; "leak" = the reply is a continuation of the other conversation's text):
   - the other conversation with Tools but no constrained decoding → leak; with NO tools at all,
     plain chat → **leak** (so tool/constrained grammar state is NOT what leaks — the consumer's
     original "interleaved conversation without tools is fine" does not hold in this sequence);
   - step 2 sending on `base_` itself instead of a new clone → leak (`Clone()` at step 2 is
     irrelevant);
   - the step-1 clone kept alive instead of disposed → leak (disposal is irrelevant);
   - CPU + E2B → leak; GPU F32 + E4B → leak (backend- and model-independent);
   - v0.15.0 self-built natives and v0.16.0 official prebuilts → identical.
   - **Minimal sequence WITHOUT step 1** (create `base_`, create+run the other conversation, then
     the first-ever send on `base_`) → **no leak**, on CPU and GPU. So the trigger is specifically:
     a conversation that has never advanced itself but has had a clone advance, followed by any
     other conversation running on the engine. This also explains the consumer's two observations:
     "warming" the base with a throwaway clone+send is exactly the step that arms the bug, and
     re-creating the base (a parent with no used clones) is the case that works.

   Previously ruled out by the consumer: a null `Sampler`, `EnableThinking`, the History-with-
   system-turn shape, and the number of live bases (2 through 6 all fine).

   Consumer impact: it silently breaks a phase rather than failing loudly. In the consumer app the
   router degraded every message to "retrieve", which in the real app means nothing would ever be saved.
   The consumer worked around it by dropping all its cached conversations before any call that
   goes through another path.

   Relation to LiteRT-LM#2807: unrelated in the end. #2807's v0.15.0 fix covers conversations that
   have advanced on their own (our interleaved-recall canary), and this case only ever involved a
   parent with no prefilled state. No upstream report filed (decision 2026-09-02): the behavior
   matches the documented clone contract.


## Ecosystem integrations (.NET AI: MEAI / Semantic Kernel / Agent Framework)

✅ **Merged to `master` on 2026-06-24** (PR #2, commits `4bef780`→`083675b`); first published in
**1.0.0**. **Architecture pivot**:
after research (Microsoft extracted the chat/embedding abstractions OUT of Semantic Kernel into
`Microsoft.Extensions.AI` (MEAI); SK is now succeeded by the **Microsoft Agent Framework (MAF)**, and BOTH SK
and MAF consume MEAI's `IChatClient` — MAF has NO own provider abstraction, it uses `IChatClient`). So the
durable integration for a model provider is `IChatClient`, not an SK-specific connector. Two packages:

- **`LiteRtLmSharp.Extensions.AI`** (the foundation; deps: `LiteRtLmSharp` +
  `Microsoft.Extensions.AI.Abstractions` 10.7.0 + `Microsoft.Extensions.DependencyInjection.Abstractions`).
  `LiteRtChatClient : IChatClient` (`GetResponseAsync`/`GetStreamingResponseAsync`/`GetService`), blocking +
  streaming. Works directly with MAF (`new ChatClientAgent(client)`), MEAI middleware (`UseFunctionInvocation`
  etc.), and SK. DI: `AddLiteRtChatClient(engine | options[, eager])` registers a shared `IChatClient`
  (TryAdd, idempotent — one engine/process). **Reasoning surfaced as `TextReasoningContent`** (excluded from
  `ChatResponse.Text`); **truncation signal**: empty answer + reasoning ⇒ `FinishReason = Length` (reasoning
  shares the `MaxOutputTokens` budget — small budget + thinking = empty answer, symmetric blocking/streaming).
  **Usage**: `ChatResponse.Usage.TotalTokenCount` always (from `conv.TokenCount`, free); `Input`/`OutputTokenCount`
  only when the engine has `EnableBenchmark = true` (else a `litertlm.usage_note` is left on `AdditionalProperties`).
- **`LiteRtLmSharp.SemanticKernel`** (thin over the above; deps: `LiteRtLmSharp` + `LiteRtLmSharp.Extensions.AI`
  + `Microsoft.SemanticKernel.Abstractions` 1.77.0 + `Microsoft.Extensions.AI` for the function-invocation
  middleware). `AddLiteRtChatCompletion(engine | options[, modelId, serviceId, eager])` on
  `IKernelBuilder`/`IServiceCollection`: registers the `IChatClient` and exposes it (wrapped with
  `UseFunctionInvocation` for function calling) as `IChatCompletionService` via SK's `AsChatCompletionService`
  adapter. `LiteRtPromptExecutionSettings`
  (temperature/top_p/top_k/max_tokens/seed/enable_thinking/enable_constrained_decoding) stores its knobs in
  `ExtensionData` under the keys SK's `PES→ChatOptions` converter reads (confirmed against SK source), so they
  flow through the adapter.
  **ITextGenerationService DROPPED** (legacy; chat-centric stack). **SK-adapter caveat (verified): the SK
  adapter does NOT surface `TextReasoningContent` or `FinishReason`** — only `ChatMessageContent.InnerContent`
  carries the native `LiteRtResponse`. So rich reasoning/truncation handling is done via the `IChatClient`
  (resolve `kernel.Services.GetRequiredService<IChatClient>()`).

**Stateless mapping** (both): each call rebuilds a fresh `LiteRtConversation` (prior turns → `History` prefill,
final user turn → `Send`); `SemaphoreSlim`-serialized. Wired into `LiteRtLmSharp.slnx`, `ci.yml` (builds the
sample + model-free tests), `pack-nuget.yml` (packs both), `samples/LiteRtLmSharp.Samples.slnx`. Console sample
`samples/SemanticKernel` (pure SK — InvokePrompt + streaming + multi-turn + function calling) validated
end-to-end on win-x64 CPU and GPU/WebGPU. Gated model-backed tests (`LITERTLM_TEST_MODEL`): chat
blocking/streaming + multi-turn history replay (both MEAI and SK paths), reasoning surfacing + truncation, and
function calling (MEAI `UseFunctionInvocation` blocking + streaming, SK `FunctionChoiceBehavior.Auto`), and
image/audio attachments (`LITERTLM_TEST_VISION=1`). Guides:
[extensions-ai.md](extensions-ai.md), [semantic-kernel.md](semantic-kernel.md).

**Function calling (DONE, 2026-06-23).** Implemented ONCE in the `IChatClient`, inherited by SK and MAF:
`ChatOptions.Tools` (AIFunction) → `LiteRtTool` (schema = `AIFunction.JsonSchema`), the model's native tool
calls → `FunctionCallContent` (+ `FinishReason.ToolCalls`), and a tool-message continuation → native
`SendToolResults` (the assistant tool-call turn is restored as history; `FunctionResultContent.CallId` →
native tool name via a call-id↔name map). MEAI's `UseFunctionInvocation()` drives it directly. For SK, an
empirical probe proved the `AsChatCompletionService` adapter passes the kernel functions as tools but does NOT
run the auto-invoke loop, so `AddLiteRtChatCompletion` wraps the client with `UseFunctionInvocation` (no-op
when a request has no tools). Opt-in `EnableConstrainedDecoding` (off by default; blocked on linux-x64 per
the core guard) makes small models emit valid tool-call arguments — used in the gated tests/sample off-Linux.
Streaming surfaces tool-call chunks as `FunctionCallContent` updates; the post-tool continuation uses a
blocking `SendToolResults` fallback (no native streaming tool-results call). `ChatOptions.ToolMode` /
`FunctionChoiceBehavior` honored: `None` → no tools offered; `RequireAny`/`RequireSpecific` → best-effort
(system-prompt instruction + offering only the named tool; the native API has no `tool_choice`, so it nudges
but can't force — verified: gemma-4 calls the required tool for a related prompt, ignores it for an unrelated
one). The `FunctionInvokingChatClient` loop resets `Required`→`null` after the first turn, so no infinite loop.
Gated tests pass end-to-end on win-x64 (gemma-4-E2B-it) for MEAI (blocking + streaming) and SK
(`FunctionChoiceBehavior.Auto`).

**Multimodal (DONE, 2026-06-23).** Image/audio on the final user message maps to native `LiteRtAttachment`:
MEAI `DataContent` (inline bytes) / file-path `UriContent`, or Semantic Kernel `ImageContent`/`AudioContent`
(an empirical probe confirmed SK's `AsChatCompletionService` forwards these as MEAI `DataContent`, media type
preserved). `conv.Send(text, attachments)` / streaming overload. Requires the engine loaded with
`VisionBackend`/`AudioBackend`; only the triggering turn's media is sent (history restored as text); remote
(non-file) URIs skipped. Gated tests pass on win-x64 (vision + audio, `LITERTLM_TEST_VISION=1`). No sample
change (per the user). Remaining: embeddings blocked (no C-API embeddings at v0.13.1). **Neither companion is
AOT/trim-clean** (MEAI/SK aren't); the core `LiteRtLmSharp` package keeps its AOT guarantee.

## Watchlist (re-check periodically)

**Last re-checked: 2026-08-09 (v0.15.0 repin cycle).** Headline findings of the cycle:

- **#2807 (multi-conversation state loss, ours): FIXED by v0.15.0.** The sentinel that pinned the
  loss fired on the first v0.15.0 suite run (the suspended conversation now recalls its own facts
  after interleaving) and the full parked suite passes — the resource-management rework in the tag
  (~800 changed lines across the serial/threaded execution managers) delivered suspended-state
  preservation. The activation checklist was executed: `MaxLiveConversations` public again
  (default 8), `LiteRtConversationBranching` public, MULTICONV test gate dropped, sentinel flipped
  into a positive-recall canary (it failing again = upstream regressed → re-park per commit
  382e369). ✅ Verification comment posted on #2807 (2026-08-12, issuecomment-5263183045):
  confirms the interleaved fix with our cross-platform suite evidence and distinguishes it from
  the still-broken concurrent case (john-rocky's 0.16.0 measurements), asking only for the
  thread-safety contract to be documented. Close/retitle left to the maintainers.
- **New at v0.15.0: the executor rejects prefill below the model's smallest prefill-signature
  length** (`FAILED_PRECONDITION: Chosen prefill work group size exceeds available state entries`,
  absent at v0.14.0). Near the context limit this replaces the silent KV corruption with a clean
  native failure; the binding's overflow guard now reserves that minimum (128 for the gemma
  conversions) so the typed exception + same-turn `IsContextFull` signal keep firing first.
- **CPU sampler factory only implements TopP — both eras.** An explicit Greedy or TopK sampler
  fails a send with `UNIMPLEMENTED: Sampler type: N not implemented yet` whenever a fresh CPU
  sampler must be created, and the failure is order-dependent (an earlier TopP/default conversation
  leaves a sampler behind that later conversations silently reuse). Verified against v0.14.0 AND
  v0.15.0 natives on win-x64 (sampler_factory.cc `CreateCpuSampler`: TOP_P + unspecified only).
  Never seen before because everything of ours defaults to TopP/none. Candidate upstream report +
  possible managed guard/doc on `LiteRtSamplerParams` (decide with the maintainer).
- **Intermittent native crash on the GPU backend under rapid engine churn** (testhost dies mid-run
  with no managed failure; ~2 of 4 full-suite GPU runs locally, different tests each time, clean
  runs in between). Same failure class as the intermittent CI win-x64 crash first seen 2026-07-17
  (which the model-tests forensics telemetry was added for) — pre-existing, NOT a v0.15.0
  regression gate. Needs a dedicated investigation session.
- **LlGuidance constrained decoding works end-to-end** (regex + JSON Schema verified on win-x64
  CPU/GPU with real-model assertions) and is compiled from source — the linux-x64 model-tests leg
  now exercises it on every push, giving Linux its first working constrained-decoding path while
  the tool-calling provider prebuilt stays broken (#2149 guard unchanged).
- Export lists at the v0.15.0 tag: **still 3/7** (PR #2801 not taken) — the desktop-GPU
  CPU-sampling fallback persists. Text-LoRA still stubbed. The prebuilt companions were all
  refreshed at the tag (new LFS OIDs, same file set) — a third-era `libGemmaModelConstraintProvider.so`
  for the next #2149 probe (not yet re-run; the docker harness in `.tmp/litert-repro/` applies).

**Addendum 2026-08-12:** (a) **upstream tagged v0.16.0 on 2026-08-11** — one week after v0.15.0;
decision: land the validated v0.15.0 cycle (PR #5) as-is and run v0.16.0 as its own cycle (header
diff first). (b) New #2807 data (john-rocky, measured on 0.16.0): **CONCURRENT** decoding on
multiple conversations of one engine is non-deterministic (CPU: divergent replies at greedy;
GPU: Dawn buffer-validation errors, occasional empty reply) — distinct from the *interleaved
sequential* case v0.15.0 fixed, and still broken at 0.16.0. Separate processes are clean, so the
engine sharing is what breaks. Consequence for us: docs tightened from "serialize per
conversation" to "serialize sends per ENGINE" (README + core XML); the MEAI client already
serializes through its gate, so connector consumers were never exposed. Our planned #2807 comment
should distinguish the two cases (interleaved verified fixed by our suite; concurrent not our
measurement to close).

**Addendum 2026-09-02 (issue-tracker sweep during the v0.16.0 evaluation):** #2529 and #2154 were
closed by upstream announcing the C API prebuilts and asking for adoption feedback. New and
relevant: #3139 (`engine_settings_create_from_raw_file_descriptor` kills the process on Windows
when the caller does not share the DLL's CRT — one more reason to keep the FD-based load unbound),
#3444 (bundles whose smallest prefill signature exceeds our 128-token reserve fail natively near
the limit — the guard's constant is gemma-specific, as documented), #3446 (~1 MB retained per
conversation create/destroy on macOS/Metal, 0.16.0), #2957 (the `litert_link_capi_so` define is
gone upstream — already dropped from our build). Our own threads (#2807, #2801, #2073, #2149) saw
no maintainer activity in August; PR #2801 is superseded by upstream's PR #3244.

- **Gemma 4 weight refresh (announced 2026-07-15, official @googlegemma post): NOT yet available for
  us.** Google silently updated the Gemma 4 weights under the same names (tool-calling fixes, agentic
  gains, vision resolution, FA4 on Hopper — the tool-calling fixes are the part that matters to this
  stack). The refresh landed in the `google/gemma-4-*` HF repos (E2B-it lastModified 2026-07-15), but
  the **`litert-community` `.litertlm` conversions still predate it** (E2B-it-litert-lm last commit
  2026-07-10 — device variants only; E4B-it-litert-lm 2026-06-01). Verified byte-exact: our local
  `models/gemma-4-E2B-it.litertlm` (2,588,147,712 B) and `gemma-4-E4B-it.litertlm` (3,659,530,240 B)
  match current HF main — we are up to date with everything that EXISTS in `.litertlm` form.
  **Action when litert-community re-converts** (watch the two repos' commits): re-download E2B/E4B,
  re-run the model suite, and flag the era change to the downstream consumer app (new weights = new era =
  full re-benchmark; the tool-calling fixes could move its rotating tool-calling failures).
- **#2807 (multi-conv state loss, ours): TRIAGED** — labeled + assigned 2026-07-13 (nireekshak), no
  comment yet. First movement; keep watching for the activation checklist below.
- **PR #2801 (sampler exports, ours):** open, no review, no comments (unchanged since filing).
- **#2073 / #2080 / PR #2081 (GPU sampler):** no movement (last real comment 2026-06-19: another user
  confirms GPU params still ignored in 0.13.1 Python). #2081 still unmerged.
- **#2149 (CPU decode crash):** our 2026-07-10 v0.14.0 data table is still the last comment.
- **#2529 (prebuilt C API lib):** new voice 2026-07-10 — Uralstech wants a prebuilt multiplatform C
  lib to build a **Unity C# package via DllImport**. Community-relevant: a prospective C#/.NET
  consumer adjacent to this binding.
- **Releases:** none after v0.14.0 (2026-07-08) — no repin trigger.
- **PrismML Bonsai (1-bit/ternary Qwen3.6, Apache 2.0 — the "27B on a phone" model, reported
  Apple-evaluation talks) on LiteRT-LM: two triggers to watch.** No official `.litertlm` exists;
  a faithful port is blocked on the runtime (LiteRT has no sub-2-bit kernels; ternary weights fit
  int4 exactly but at ~2.3× Bonsai's packing — 27B ternary 5.9 GB → ~13.5 GB int4, desktop-only,
  which defeats the model's point; 8B/4B/1.7B at int4 would be the realistic conversions). The
  toolchain is otherwise public (litert-torch has qwen/qwen3 conversion examples; Bonsai publishes
  unpacked/AWQ weights). Triggers: (1) **LiteRT ships sub-2-bit/ternary kernels** — the real
  unlock; (2) **[Tdamre/Bonsai-27B-litert-lm](https://huggingface.co/Tdamre/Bonsai-27B-litert-lm)
  completes the full 64-layer model** (today it is a 4-layer structural prototype built with
  litert-torch-nightly — proof the pipeline works, not a usable model). Either firing → grab the
  `.litertlm`, run it through our model suite (nothing to change binding-side, same as
  Ministral/Phi), and tell the downstream consumer app (which can meanwhile try Bonsai via its
  Ollama provider — the GGUF releases run there today).

Previous re-check (2026-07-10): The repin trigger FIRED: LiteRT-LM tagged a stable **v0.14.0** and we
repinned to it on 2026-07-10 (self-built `native-v0.14.0`; C API 89 → 109, 84 bound; CI green on all
three OSes including the model tests). The tag lands several watched fixes, so each issue below is
re-stated against v0.14.0. Note that penalties / no-repeat-ngram sampling did NOT reach the C API in this
tag, so they stay unbound.

Comparative research vs Google's official CI and flutter_gemma (2026-07-10): our desktop define trio
matches Google's own v0.14.0 dynamic-linking CI exactly, DXC and the dawn shipping match, and
flutter_gemma is still on v0.13.1 (their eventual 0.14 migration will hit the double-runtime crash we
root-caused — nobody has reported it upstream yet). Items for the NEXT repin (0.15.x): upstream
deprecated `litert_link_capi_so` (commit `daa8ea819`; `litert_runtime_link_mode=dynamic` is the
surviving knob) and is queuing `resolve_symbols_in_exec` for removal (#2234/#2237) — trim our defines
then. Also worth verifying against our binding at some point (flutter_gemma patches these in source;
we do not): `set_cache_dir` does not propagate to the vision/audio executors upstream, and GPU
sampling only partially honors session sampler params (#2080, PR #2081 unmerged). Watch #2529 (asks
Google to publish an official prebuilt C-API shared lib — would let us drop patch_c_api.sh).

### Feature-readiness protocol (adopted 2026-07-10; the C header is NOT the source of truth)

Two same-day case studies proved that a function existing in `c/engine.h` says nothing about the
feature working: the v0.14.0 **text-LoRA** surface loads an adapter fine and then the first
generation dies on an unconditional runtime stub ("Lora is not supported.", internal TODO
b/462499294), and **multi-conversation interleaving** silently loses the suspended conversation's
state on every distribution we tested, including Google's own wheel (entry below). Before binding or
shipping NEW native surface, check ALL of:

1. **Release notes + official docs** (developers.google.com/edge/litert-lm) mention it — Google's
   curated "this shipped" signal. The header and even the Python wheel surface are aspirational
   (the wheel exposes `LoraConfig` while the runtime stubs it).
2. **An upstream END-TO-END test exercises the engine/session path** (`c/engine_test.cc`,
   `runtime/engine/...`). Component tests are not enough: LoRA's weight-application has passing
   component tests while the session path is stubbed; no upstream e2e test interleaves two
   conversations and re-asks content.
3. **Grep the full consumption chain for stubs**: `not supported`, `Unimplemented`, `TODO: b/` from
   the C wrapper down to the executor. This found the LoRA stub in minutes.
4. **Our own real-model probe before the feature ships** — load, exercise, and assert CONTENT, not
   just absence of errors (the multi-conversation loss hid behind token-count-only assertions for
   two native eras). The Kotlin surface is a useful secondary signal (the most production-hardened
   binding: it exposes neither clone nor LoRA today).

- **✅ RESOLVED at v0.15.0 (2026-08-09): multi-conversation / clone interleaving preserved.** The
  activation checklist below was executed during the repin (sentinel fired → multi-conv suite
  verified → capacity/forking/docs restored; details in the 2026-08-09 watchlist block above).
  Historical record of the limitation follows.
  **Multi-conversation / clone interleaving loses the suspended conversation's state (upstream,
  UNRELEASED capability)** — found 2026-07-10 while building conversation forking. Minimal repro
  (two independent conversations, interleaved sends, re-ask content): the suspended conversation
  answers as if its own turns never happened, with NO error. Reproduced identically on our v0.13.1
  natives, our v0.14.0 natives, and **Google's official `litert-lm==0.14.0` wheel (pure-Python
  repro)** — so it is not a build/linking issue on our side. Status evidence says "under
  construction, never released": no release-notes mention of clone/multi-session ever; the Python
  wheel deliberately does not expose clone (`interfaces.py` TODO b/482060476 "Add clone() API once
  switching to advanced engine"); Kotlin does not either; the runtime HAS a purpose-built
  copy-on-write context subsystem (`ContextHandler`/`SharedProcessedContext`,
  `resource_manager.cc` save-on-switch-out + `RestoreContext` on switch-in, internal design doc
  `go/llm_resource_manager`) that does not yet preserve the physical KV across switches.
  **What we hold back because of this**: the MEAI stateful mode keeps exactly ONE live conversation
  (the internal store is a ready LRU, capacity pinned to 1); the conversation-forking feature
  (`LiteRtConversationBranching` over native `Clone()`, fully implemented + tested) is in-tree but
  INTERNAL, not reachable from the public API. **Activation checklist when upstream ships suspended-
  state preservation**: the sentinel model test (interleaved-loss pin) starts FAILING → verify with
  the multi-conv gated tests (`LITERTLM_TEST_MULTICONV=1`), raise the store capacity, make
  `LiteRtConversationBranching` public, restore its docs/CHANGELOG entries, re-run the fork
  divergence suite. ✅ **Filed [#2807](https://github.com/google-ai-edge/LiteRT-LM/issues/2807)**
  (2026-07-10): status question + the pure-Python wheel repro + the ask to fail loudly instead of
  answering incorrectly while unfinished. Watch for a maintainer response.

- **[LiteRT-LM#2211](https://github.com/google-ai-edge/LiteRT-LM/issues/2211)**: **unchanged at
  v0.14.0.** GPU samplers still ship missing `DT_NEEDED` (our patchelf is the workaround). If Google
  ships fixed prebuilts or a fix, **drop the patchelf** from the android job. Also watch the related
  #2241, #1860 and the OpenCL bug #1850 (`Invalid command queue`, which did not reproduce on our Adreno
  650 test device, but hits other Adreno GPUs).
- **[LiteRT-LM#2552](https://github.com/google-ai-edge/LiteRT-LM/pull/2552)** — our PR to be
  listed in upstream's Supported Language APIs table (announced in #2535). Watch for review
  feedback.
- **[LiteRT-LM#2149](https://github.com/google-ai-edge/LiteRT-LM/issues/2149)** — ROOT CAUSE
  FOUND (2026-06-12, full gdb analysis): the upstream prebuilt
  `prebuilt/linux_x86_64/libGemmaModelConstraintProvider.so` (rev `b41cb271`, the only
  Gemma4-capable one) returns half-initialized constraints (internal FST = NULL) →
  SIGSEGV in `fst_constraints::Constraint::start()` on the first decode step. Google's
  wheel works (embeds the provider from internal source); same-rev Windows DLL works.
  **Our binding ships a TEMPORARY GUARD**: `EnableConstrainedDecoding = true` on linux-x64
  throws `PlatformNotSupportedException` instead of dying (LiteRtConversation.Create).
  **Re-tested 2026-07-10 at v0.14.0: STILL BROKEN, guard stays.** The Docker repro run with our
  v0.14.0 engine **and** the v0.14.0 provider blob (rev `075e6021`) exited 139 with the identical
  SIGSEGV fingerprint (engine + conversation create fine, the FST builds, crash at the first
  constrained send). That kills the matched-engine hypothesis: no *public* provider blob works on any
  engine we can build (Google's wheel passes only because it embeds the provider from internal source),
  so version-matching the natives is not the fix. The guard and the `<remarks>` on
  `LiteRtConversationOptions.EnableConstrainedDecoding` remain. Next actionable is a follow-up upstream
  comment carrying the two-era (v0.13.1 + v0.14.0) repro matrix, to be drafted with the repo maintainer.
  Meanwhile the real constrained loop IS exercised on each push in CI on win-x64 and osx-arm64
  (`model-tests.yml` matrix); linux-x64 asserts the guard throws. Android is NOT affected
  (tools validated on physical device, CPU and GPU; upstream #1859 looks like a
  custom-model issue, discarded).
- **Speculative decoding + WebGPU needs the disk cache off** (root-caused 2026-06-15) — on the
  desktop WebGPU/D3D12 GPU backend, `EnableSpeculativeDecoding = true` with the default disk cache
  fails `litert_lm_engine_create`: the MTP drafter and main model share one
  `…_mldrift_weight_cache.bin` and on Windows the second `mmap` of that file fails ("Access denied",
  `serialization_weight_cache/mmap_handle.cc:147` → `llm_litert_mtp_drafter.cc:197`). **Confirmed
  UPSTREAM, not our binding**: Google's own `litert-lm` CLI (pip, v0.13.1) reproduces it exactly with
  `--cache disk` and works with `--cache no` (= `cache_dir ":nocache"`). A custom cache dir does NOT
  help (same shared file). **Fixed our side** by binding `engine_settings_set_cache_dir` and exposing
  `LiteRtEngineOptions.CacheDir` (set `CacheDisabled` for GPU+spec); both samples apply it
  automatically. **Upstream landscape (researched 2026-06-15):** no dedicated, indexed issue exists
  for the Windows/WebGPU case — only **[#2503](https://github.com/google-ai-edge/LiteRT-LM/issues/2503)**
  (OPEN, iOS/Metal sibling: same shared `mldrift_weight_cache.bin`, sibling string "Cannot insert a
  buffer in a cache that is not building") and a **buried comment on
  [#2461](https://github.com/google-ai-edge/LiteRT-LM/issues/2461)** where `@vladimirvivien` posts the
  exact Windows/WebGPU "Access is denied" trace on v0.13.1 and calls it a **regression from v0.12.0**
  (and hit it WITHOUT MTP → the Windows mldrift-cache mmap collision is broader than just the two-
  consumer MTP case). Source-level root cause (v0.13.1): `CreateCompilationOptions` in
  `runtime/executor/llm_executor_settings_utils.cc` applies the `.mtp_drafter` cache suffix only on the
  CPU/XNNPACK branch, NOT the GPU/MlDrift branch → main + drafter resolve to one cache file. PR #2372
  (merged) is a different Windows MTP mapping fix (`.litertlm` sections, not the weight cache) and does
  NOT fix this. ✅ **Filed [#2572](https://github.com/google-ai-edge/LiteRT-LM/issues/2572)**
  (2026-06-15), cross-ref #2503/#2461; named the `CreateCompilationOptions` GPU-branch `cache_suffix`
  omission. flutter_gemma has no matching report. Full write-up: [speculative-decoding.md](speculative-decoding.md).
  **v0.14.0 status: FIXED AND VERIFIED (2026-07-10).** The tag carries `4aa96a019` + `0a6590988`;
  re-tested end to end on win-x64 WebGPU with our v0.14.0 natives (spec + default disk cache: engine
  create, generation, and cache readback across two loads all pass). **The `Cache = Disabled`
  workaround is DROPPED from the samples and docs.** Follow-up candidate: post a close-request on
  #2572 with the verification (draft with the user first).
- **[LiteRT-LM#2073](https://github.com/google-ai-edge/LiteRT-LM/issues/2073)** — WebGPU TopK sampler
  exports only **3/7** C-ABI functions on **macOS/Windows** (Linux/Android ship 7) → `sampler_factory`
  can't resolve `LiteRtTopKWebGpuSampler_UpdateConfig` (+ 3 others) and **falls back to CPU sampling**.
  (At v0.14.0 the engine additionally probes `LiteRtTopKWebGpuSampler_UpdateConfig` on every load and
  logs the miss — same graceful fallback, now noisier and observed directly in our spec re-test.)
  This is our exact GPU-sampling fallback (confirmed against the committed `webgpu_exported_symbols.lds`
  / `windows_exported_symbols.def` at v0.13.1; Metal was fixed to 7/7, WebGPU left at 3/7). OPEN, filed
  by flutter_gemma's author (DenisovAV), **zero Google engagement, no fix PR**; #2502 was closed as a
  dup. **We CANNOT self-fix this**: we ship Google's *prebuilt* WebGPU sampler (LFS, no public source),
  and you can't add exports to a compiled binary — the Android `patchelf --add-needed` trick fixes
  `DT_NEEDED` (#2211), not exports. Strictly needs an upstream prebuilt re-export. Per flutter_gemma
  #287 the fallback costs only ~3% of steady-state decode, but it hurts the speculative draft/verify
  loop more. ✅ **Commented our Windows/WebGPU v0.13.1 repro on #2073** (2026-06-15,
  [issuecomment-4705832501](https://github.com/google-ai-edge/LiteRT-LM/issues/2073#issuecomment-4705832501));
  verified the export lists ourselves (`webgpu_exported_symbols.lds`/`windows_exported_symbols.def` = 3,
  `metal_exported_symbols.lds` = 7 at v0.13.1). **Re-verified at v0.14.0: still NOT fixed.** The
  windows/webgpu export lists are unchanged at 3/7, so the desktop-GPU CPU-sampling fallback stays.
  NEW: [#2745](https://github.com/google-ai-edge/LiteRT-LM/issues/2745) (2026-07-07, assigned)
  confirms the 4 missing symbols EXIST in the binaries but are local/unexported — so the fix is an
  8-line export-list change. ✅ **Filed [PR #2801](https://github.com/google-ai-edge/LiteRT-LM/pull/2801)**
  (2026-07-10): adds the 4 symbols to `webgpu_exported_symbols.lds` + `windows_exported_symbols.def`,
  mirroring the Metal list, and notes the prebuilt-rebuild requirement and the #2080 interaction.
  Upstream may mirror it internally rather than merge; either way the fix is on record. When rebuilt
  sampler prebuilts ship: re-verify 7/7 exports, re-measure the digit-corruption case and speculative decoding on GPU
  (real GPU sampling changes both pictures).
- **[LiteRT-LM#2080](https://github.com/google-ai-edge/LiteRT-LM/issues/2080)**: sampler params were
  not being read from the runtime config on some paths. **PARTIALLY addressed in v0.14.0**:
  `InitializeSampler` now reads the `runtime_config` sampler params when present. **The
  digit-corruption A/B was settled 2026-07-17**: the residual deterministic corruption on desktop
  GPU is caused by the **F16 activation default** of the GPU text executor, not by sampler params —
  `ActivationDataType = Float32` takes a 16-check structured-extraction benchmark from 13/16
  (rotating errors) to 16/16 across 3 runs, replicated on a non-Gemma model (Ministral-3-3B), with
  no measurable speed cost on an RTX 3080. Documented in [engine-tuning.md](engine-tuning.md)
  (which also cross-refs the upstream threads that misattribute this symptom: #2637/#2727/#2202).
- **New LiteRT-LM tags** — automated: `upstream-watch.yml` (Mon/Thu) opens a checklist issue
  when upstream publishes a release.
- **flutter_gemma** — releases/issues as a recipe source (e.g. their #270/#214 anticipated our
  Android GPU problems).

## Architecture decisions (log)

- P/Invoke wrapper over the **C API** (`c/engine.h`), never C++/CLI. .NET 10 only.
- **Public type prefix is `LiteRt*`, not `LiteRtLm*`** (settled 2026-07-02, ahead of 1.0). Upstream's C
  naming reserves `LiteRt*` for the base runtime and `LiteRtLm*` for the LM engine, so our prefix
  knowingly "occupies" the base product's name — chosen anyway because the disambiguation already
  lives at the package/namespace boundary (`LiteRtLmSharp`), the double abbreviation (`LiteRtLmEngine`)
  reads worse on every consumer line forever, peers set the same precedent (LLamaSharp wraps llama.cpp,
  Whisper.net wraps whisper.cpp), and the collision it would guard against (a .NET binding of base
  LiteRT used in-process with this one) does not exist. Bonus: the internal native-mirror layer keeps
  the `LiteRtLm*` names (`LiteRtLmNative`, raw structs), cleanly separated from the public surface.
- **Self-built** native binaries from release tags (never loose commits — lesson from the
  streaming segfault at `032334d8`), via `native/patch_c_api.sh` + `build-native.yml`
  (`platforms` input to avoid rebuilding existing assets; the release accumulates assets).
  v0.14.0 now ships its **own** shared-lib target (`cc_binary litert-lm` in `c/BUILD`, the
  Python-wheel build), but `patch_c_api.sh` keeps adding **our** target, because it needs the Windows
  `.def`, the `LiteRt*` wildcard exports and the companion rpath that the wheel target does not
  provide; so the patch's idempotence guard now greps for our target name (not the absence of any
  shared-lib target). v0.14.0 also ships a new `libwebgpu_dawn` shared lib on every desktop platform,
  now carried in the release tarballs and picked up by the collect globs.
- LLamaSharp-style distribution: pure managed + per-RID `runtime.<rid>` packages, all sharing
  one version per release (see Versioning policy above).
- Desktop (linux/win/macOS) links `libLiteRt` as a separate shared lib (`litert_link_capi_so`
  + `resolve_symbols_in_exec=false`; without the second define macOS hits an
  "illegal ambiguous match" because the repo .bazelrc defaults it to true); Android/iOS link
  it statically. macOS switched to dynamic on 2026-06-12: the prebuilt Metal sampler carries
  `@rpath/libLiteRt.dylib` and could not dlopen against the static build (CPU-sampling fallback).
- Separate solutions: `LiteRtLmSharp.slnx` (lib+tests+packaging, bare SDK, CI) and
  `samples/LiteRtLmSharp.Samples.slnx` (console + MAUI, needs workloads).
- iOS consumes the natives as **dynamic `.framework`s** (`NativeReference Kind=Framework`,
  embedded + code-signed in the app bundle), resolved at runtime by the existing
  `[LibraryImport("LiteRtLm")]` + `NativeLibraryResolver` — the same managed model as every
  other RID, since the prebuilt companions are dynamic dylibs with no static archives.
  Decided 2026-06-21.
