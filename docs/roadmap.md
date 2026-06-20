# Project status and roadmap

Last updated: 2026-06-17. Source of truth for "what's done and what's pending".

## Status per platform

| Platform | Native | NuGet | CPU | GPU | Validated on |
|---|:---:|:---:|:---:|:---:|---|
| win-x64 | ✅ | ✅ | ✅ | ✅ | real hardware (+ CI, CPU) |
| linux-x64 | ✅ | ✅ | ✅ | ✅ | real hardware (+ CI, CPU) |
| android-arm64 | ✅ | ✅ | ✅ | ✅ | real device (Adreno 650) |
| osx-arm64 | ✅ | ✅ | ✅ | ✅ | CI only (macos-15; GPU via WebGPU) |
| ios-arm64 | ✅ | ⏳ | — | — | pending (needs xcframework) |

<sub>**CPU / GPU** = inference validated on that backend. **CI** = the `model-tests.yml` model leg
(all three OSes on each push via ci.yml; also runnable on demand) with a real model, incl. constrained
decoding; real-hardware results are from dev machines/devices. macOS GPU
specifics and dates are in [§macOS validation](#actionable-next-steps-suggested-order) below.</sub>

Native binaries are pinned to **LiteRT-LM v0.13.1**.

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
| Render a message to its templated prompt (`RenderMessage`) for debugging / exact-cost budgeting | ✅ |

Known constraints (documented in the README): one engine ALIVE at a time (reloading after
`Dispose` works — verified on win-x64 cpu→cpu and cpu→gpu; this is Edge Gallery's pattern for
switching model/backend without restarting); conversations are not thread-safe; `MaxNumTokens`
is the total context window; VC++ Redistributable required on win-x64; Android GPU requires
`<uses-native-library>` in the app manifest.

## C API coverage (audit 2026-06-12, header v0.13.1)

**67 of 89 `litert_lm_*` functions bound** (everything we bind exists in the header — no
drift). The remaining 22 group into the areas below, in suggested priority order:

### High value (user-facing features)

| Feature | Functions | Notes |
|---|---|---|
| ✅ Restore chat history | `conversation_config_set_messages` | **Done 2026-06-16** (`LiteRtConversationOptions.History` typed `LiteRtMessage` list + raw `HistoryJson`; `LiteRtResponse.ToMessage()` + `LiteRtMessage.Serialize/Deserialize` for the caller-owned round-trip — the C API has no history getter). Replays the history through prefill (not a KV snapshot). Verified on gemma-4-E2B (CPU + win-x64 GPU/WebGPU): a restored conversation holds strictly more KV tokens than a fresh one. See [conversation-state.md](conversation-state.md). |
| ✅ Extra context | `conversation_config_set_extra_context` | **Done 2026-06-16** (`LiteRtConversationOptions.EnableThinking` for Gemma reasoning mode + raw `ExtraContext` escape hatch). Both samples expose a thinking toggle; pairs with the KV-cache thinking filter below. |
| ✅ Conversation clone | `conversation_clone` | **Done 2026-06-16** (`LiteRtConversation.Clone()` → independent conversation duplicating the prefilled KV-cache state; throws `LiteRtException` when the engine/backend returns `Unimplemented`). NOT CPU-only — verified on gemma-4-E2B on both CPU and win-x64 GPU (WebGPU): the clone copies the parent's token count, advances on its own, and leaves the parent untouched. See [conversation-state.md](conversation-state.md). |
| ✅ Engine cache dir | `engine_settings_set_cache_dir` | **Done 2026-06-15** (`LiteRtEngineOptions.CacheDir`, + `CacheDisabled`/`CacheInMemory` sentinels). Persistent compiled-shader/weight cache → faster GPU re-init; also the fix for speculative decoding on WebGPU (set `CacheDisabled`). |
| ✅ Speculative decoding | `engine_settings_set_enable_speculative_decoding` | **Done 2026-06-15** (`EnableSpeculativeDecoding`). Measured (gemma-4-E2B): desktop CPU **regresses** (~0.78×); desktop **WebGPU works** with `CacheDir=CacheDisabled` but doesn't help here (0.85× in a fair cache-off A/B; CPU-sampling fallback) — the disk-cache requirement is an upstream issue, see watchlist; accelerators are the expected ~3× win. See [speculative-decoding.md](speculative-decoding.md). |
| ✅ Multimodal messages | `engine_settings_set_max_num_images`, `conversation_optional_args_create/delete/set_visual_token_budget` | **Done 2026-06-17.** `LiteRtAttachment` (`Image`/`ImageFile`/`Audio`/`AudioFile`) + `Send`/`SendMessage`/`SendMessageStreamingAsync` attachment overloads build the content-part wire format (`{"type":"image"\|"audio","blob":<base64>\|"path":<file>}`, byte-verified against `runtime/conversation/.../data_utils.cc`); `LiteRtEngineOptions.VisionBackend`/`AudioBackend` enable the encoders via the already-bound `engine_settings_create`; `LiteRtConversationOptions.VisualTokenBudget` → `optional_args`. **Validated against gemma-4-E2B-it (2026-06-17): vision+audio on CPU across linux-x64/win-x64/osx-arm64 and vision on the osx-arm64 GPU leg (model-tests run 27712370474), plus win-x64 GPU locally.** An image adds a ~261-token vision block (28 → 289) and the model answered "…a solid, vibrant **red** color"; a real spoken 5→0 countdown adds ~130 audio tokens (35 → 165) and the model transcribed "Five, four, three, two, one, zero." Vision runs on GPU. **Gemma 4's audio sub-model is CPU-constrained** (`audio_backend="gpu"` → `engine_create` fails with "Audio backend constraint mismatch. Model requires one of [cpu]") — a model property, not a platform one (the model-tests macOS GPU leg confirms the same skip), so MAUI runs audio on CPU when the main backend is GPU. MAUI Chat tab gains 📷/🎵 attach buttons + a modality label (gated on model capability). `set_max_num_images` is bound for Kotlin-binding parity but legacy-only per the header. |

### Medium value (developer utilities)

| Feature | Functions | Notes |
|---|---|---|
| ✅ Tokenizer surface (16) | `engine_tokenize`, `engine_detokenize`, `engine_get_start_token`, `engine_get_stop_tokens`, `tokenize_result_*` (3), `detokenize_result_*` (2), `token_union_*` (4), `token_unions_*` (3) | **Done 2026-06-19.** `LiteRtEngine.Tokenize(text)` → `int[]` and `Detokenize(ReadOnlySpan<int>)` → `string` run the model's own tokenizer with no inference (exact prompt budgeting against `MaxNumTokens`); `GetStartToken()`/`GetStopTokens()` expose the configured BOS/EOS tokens as `LiteRtTokenUnion` (a literal `Text` string or a sequence of `Ids`, per `Kind`). All 16 functions bound; result/union objects wrapped in SafeHandles, the `const int*`/`char*` views copied out before disposal. Validated on win-x64 CPU with gemma-4-E2B-it (round-trip, deterministic+monotone counts, non-empty stop tokens). |
| ✅ Benchmark API (11) | `engine_settings_enable_benchmark`, `conversation_get_benchmark_info`, `benchmark_info_*` (9) | **Done 2026-06-15** (`EnableBenchmark` → `LiteRtConversation.GetBenchmarkInfo()`). Prefill/decode tok/s, time-to-first-token, init time. Surfaced in both samples' gauges + the speculative-decoding A/B test. Per-turn getters guarded (the C wrapper does not bounds-check the turn index). |
| ✅ KV-cache thinking filter | `conversation_config_set_filter_channel_content_from_kv_cache` | **Done 2026-06-16** (`LiteRtConversationOptions.FilterThinkingFromKvCache`). Drops thinking-channel tokens from the KV cache so a long reasoning block does not consume the context window; companion to `EnableThinking`. |
| ✅ Prompt debugging | `conversation_render_message_to_string` | **Done 2026-06-20** (`LiteRtConversation.RenderMessage(text)` + raw `RenderMessageRaw(json)`). Returns the exact templated prompt a message would produce, without sending (KV cache untouched). Pairs with the tokenizer: render → `Tokenize` → exact per-turn cost including the chat template. The returned native string is conversation-owned (valid until the next render), copied out immediately. Validated on win-x64 CPU with gemma-4-E2B-it. |
| ✅ Engine tuning | `engine_settings_set_prefill_chunk_size`, `set_parallel_file_section_loading`, `set_activation_data_type` | **Done 2026-06-20** (`LiteRtEngineOptions.PrefillChunkSize` (CPU/dynamic), `ParallelFileSectionLoading` (bool?, default on), `ActivationDataType` (`LiteRtActivationDataType` F32/F16/I16/I8)). CPU prefill chunking, load parallelism, activation precision. Smoke-tested on win-x64 CPU (engine loads + generates with all three applied). |

### Low priority (advanced / niche)

| Feature | Functions | Notes |
|---|---|---|
| Raw Session API (11) | `engine_create_session`, `session_run_prefill`, `session_run_decode(_async)`, `session_generate_content(_stream)`, `session_run_text_scoring`, `session_cancel_process`, `session_config_set_apply_prompt_template`, `session_delete`, `session_get_benchmark_info` | Low-level prefill/decode bypassing chat templates; includes text scoring (log-prob ranking) and the raw no-template mode. |
| Responses introspection (10) | `responses_*` | Candidates, scores, per-token logits — only meaningful with the Session API. |
| ✅ Benchmark fake tokens | `engine_settings_set_num_prefill_tokens`, `set_num_decode_tokens` | **Done 2026-06-20** (`LiteRtEngineOptions.BenchmarkPrefillTokens` / `BenchmarkDecodeTokens`). Synthetic-token benchmarking: the prompt is padded/truncated to the prefill count and decode runs exactly the decode count (ignoring the stop token), so `GetBenchmarkInfo` reports throughput at FIXED counts — content-independent device benchmarking. **Confirmed observable through the Conversation API** (not a benchmark-main-only path): both fields feed `EngineSettings::benchmark_params_`, read by the default `EngineAdvancedImpl`/`SessionAdvanced` (source trace + win-x64 probe: a tiny "Hi" reports 256/64). Setting either also flips benchmark mode on; the reply is not a real answer. |
| NPU dispatch dir | `engine_settings_set_litert_dispatch_lib_dir` | Qualcomm/Intel NPU dispatch library location. |

> Note: the C API has **no embeddings functions** at v0.13.1 (flutter_gemma implements
> embeddings via a separate native library, not this header), so embeddings stay out of
> scope until upstream exposes them.

## Actionable next steps (suggested order)

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
   Pending follow-up: request the `LiteRtLmSharp.` ID prefix reservation on nuget.org.
4. **Upstream reports**: (a) ✅ posted to #1881 — Vulkan/Dawn FP16 shaders fail on older Adreno
   and the engine emits silent garbage instead of an error/fallback; (b) dropped — the
   `uses-native-library` requirement is documented in upstream's Kotlin getting-started docs;
   (c) ✅ posted to #2211 — bionic ignores `RTLD_NOLOAD|RTLD_GLOBAL` flag promotion, so there is
   no consumer-side workaround for the missing `DT_NEEDED`.
5. **iOS app phase**: Apple Developer Program → xcframework + `.targets` NativeReference →
   MAUI `net10.0-ios` app → CI signing → TestFlight.
6. **Optional**: binding coverage push per the "C API coverage" section above. **The high-value group is
   now complete** — ✅ history restore + clone (2026-06-16), ✅ cache dir + ✅ speculative decoding
   (2026-06-15), ✅ multimodal image/audio (2026-06-17), ✅ tokenizer surface (exact token counting,
   2026-06-19; 61/89 bound). **Multimodal is validated cross-platform** (`model-tests.yml` run 27712370474, 2026-06-17):
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
   The remaining binding areas are the smaller medium-value utilities
   (prompt rendering for debugging; engine tuning knobs: prefill chunk size, parallel file loading,
   activation dtype) and — once a stable v0.14.x lands — the new C-API surface (LoRA adapters,
   request-level max-output-tokens, FD-based load; see the watchlist).
   `android-x64` for emulators; Desktop meta-package;
   ✅ ~~CONTRIBUTING + issue templates~~ (2026-06-11: CONTRIBUTING.md, issue forms, PR template,
   SECURITY.md, Discussions enabled); scheduled smoke-test workflow that consumes the published
   packages from nuget.org; PR upstream to be listed among the language bindings (planned right
   after the nuget.org release).

## Watchlist (re-check periodically)

- **[LiteRT-LM#2211](https://github.com/google-ai-edge/LiteRT-LM/issues/2211)** — GPU samplers
  missing `DT_NEEDED` (our patchelf is the workaround). If Google ships fixed prebuilts or a
  fix, **drop the patchelf** from the android job. Also watch the related #2241, #1860 and the
  OpenCL bug #1850 (`Invalid command queue` — did not reproduce on our Adreno 650 test device,
  but hits other Adreno GPUs).
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
  **When Google republishes a fixed linux prebuilt: rebuild natives, re-run the Docker
  repro (scripts in `%TEMP%\litert-repro`), and REMOVE the guard + the `<remarks>` on
  `LiteRtConversationOptions.EnableConstrainedDecoding`.** Meanwhile the real constrained
  loop IS exercised on each push in CI on win-x64 and osx-arm64 (`model-tests.yml` matrix); linux-x64
  asserts the guard throws. Android is NOT affected
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
- **[LiteRT-LM#2073](https://github.com/google-ai-edge/LiteRT-LM/issues/2073)** — WebGPU TopK sampler
  exports only **3/7** C-ABI functions on **macOS/Windows** (Linux/Android ship 7) → `sampler_factory`
  can't resolve `LiteRtTopKWebGpuSampler_UpdateConfig` (+ 3 others) and **falls back to CPU sampling**.
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
  `metal_exported_symbols.lds` = 7 at v0.13.1). Watch for an upstream re-export.
- **New LiteRT-LM tags** — automated: `upstream-watch.yml` (Mon/Thu) opens a checklist issue
  when upstream publishes a release.
- **flutter_gemma** — releases/issues as a recipe source (e.g. their #270/#214 anticipated our
  Android GPU problems).

## Architecture decisions (log)

- P/Invoke wrapper over the **C API** (`c/engine.h`), never C++/CLI. .NET 10 only.
- **Self-built** native binaries from release tags (never loose commits — lesson from the
  streaming segfault at `032334d8`), via `native/patch_c_api.sh` + `build-native.yml`
  (`platforms` input to avoid rebuilding existing assets; the release accumulates assets).
- LLamaSharp-style distribution: pure managed + per-RID `runtime.<rid>` packages, all sharing
  one version per release (see Versioning policy above).
- Desktop (linux/win/macOS) links `libLiteRt` as a separate shared lib (`litert_link_capi_so`
  + `resolve_symbols_in_exec=false`; without the second define macOS hits an
  "illegal ambiguous match" because the repo .bazelrc defaults it to true); Android/iOS link
  it statically. macOS switched to dynamic on 2026-06-12: the prebuilt Metal sampler carries
  `@rpath/libLiteRt.dylib` and could not dlopen against the static build (CPU-sampling fallback).
- Separate solutions: `LiteRtLmSharp.slnx` (lib+tests+packaging, bare SDK, CI) and
  `samples/LiteRtLmSharp.Samples.slnx` (console + MAUI, needs workloads).
