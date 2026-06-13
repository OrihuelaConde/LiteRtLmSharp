# Project status and roadmap

Last updated: 2026-06-12. Source of truth for "what's done and what's pending".

## Status per platform

| Platform | Native binaries (CI) | NuGet package | Runtime validation |
|---|---|---|---|
| win-x64 | ✅ | ✅ | ✅ (local + CI, incl. weekly model tests with real constrained decoding) |
| linux-x64 | ✅ | ✅ | ✅ (CI, real model load) |
| android-arm64 | ✅ | ✅ | ✅ physical device (Adreno 650): CPU and GPU |
| osx-arm64 | ✅ | ✅ | ✅ CPU validated in CI (macos-15, 2026-06-12, 6/6 incl. real constrained decoding); GPU/Metal partial — see next steps |
| ios-arm64 | ✅ | ⏳ (needs xcframework packaging) | ⏳ |

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
| AOT/trim-friendly (`[LibraryImport]`, `[UnmanagedCallersOnly]`, no reflection) | ✅ |
| Multimodal (image/audio), tokenize/detokenize, benchmark API | 🔜 see C API coverage below |

Known constraints (documented in the README): one engine ALIVE at a time (reloading after
`Dispose` works — verified on win-x64 cpu→cpu and cpu→gpu; this is Edge Gallery's pattern for
switching model/backend without restarting); conversations are not thread-safe; `MaxNumTokens`
is the total context window; VC++ Redistributable required on win-x64; Android GPU requires
`<uses-native-library>` in the app manifest.

## C API coverage (audit 2026-06-12, header v0.13.1)

**24 of 89 `litert_lm_*` functions bound** (everything we bind exists in the header — no
drift). The remaining 65 group into six areas, in suggested priority order:

### High value (user-facing features)

| Feature | Functions | Notes |
|---|---|---|
| Restore chat history | `conversation_config_set_messages` | Rebuild a conversation from persisted messages — pairs with context-window management (upstream #1878). |
| Extra context | `conversation_config_set_extra_context` | e.g. `enable_thinking` for Gemma 4 reasoning mode. |
| Conversation clone | `conversation_clone` | Fork/branch a conversation reusing the KV cache. |
| Engine cache dir | `engine_settings_set_cache_dir` | Persistent compiled-shader/weight cache → much faster GPU re-init. |
| Speculative decoding | `engine_settings_set_enable_speculative_decoding` | Already a follow-up from the #2211 work (~3× decode with the MTP drafter). |
| Multimodal messages | `engine_settings_set_max_num_images`, `conversation_optional_args_create/delete/set_visual_token_budget` | Image/audio content is mostly **wrapper work**: the bound `send_message` already accepts `{"type":"image","blob":<base64>}` content parts; vision/audio backends are parameters of the already-bound `engine_settings_create`. |

### Medium value (developer utilities)

| Feature | Functions | Notes |
|---|---|---|
| Tokenizer surface (16) | `engine_tokenize`, `engine_detokenize`, `engine_get_start_token`, `engine_get_stop_tokens`, `tokenize_result_*` (3), `detokenize_result_*` (2), `token_union_*` (4), `token_unions_*` (3) | Exact token counting / prompt budgeting without running inference. |
| Benchmark API (11) | `engine_settings_enable_benchmark`, `conversation_get_benchmark_info`, `benchmark_info_*` (9) | Prefill/decode tok/s, time-to-first-token, init time — for the MAUI sample and perf regression tracking. |
| KV-cache channel filter | `conversation_config_set_filter_channel_content_from_kv_cache` | Drop thinking-channel tokens from the KV cache. |
| Prompt debugging | `conversation_render_message_to_string` | See the rendered (templated) prompt for a message. |
| Engine tuning | `engine_settings_set_prefill_chunk_size`, `set_parallel_file_section_loading`, `set_activation_data_type` | CPU prefill chunking, load parallelism, force-F32. |

### Low priority (advanced / niche)

| Feature | Functions | Notes |
|---|---|---|
| Raw Session API (11) | `engine_create_session`, `session_run_prefill`, `session_run_decode(_async)`, `session_generate_content(_stream)`, `session_run_text_scoring`, `session_cancel_process`, `session_config_set_apply_prompt_template`, `session_delete`, `session_get_benchmark_info` | Low-level prefill/decode bypassing chat templates; includes text scoring (log-prob ranking) and the raw no-template mode. |
| Responses introspection (10) | `responses_*` | Candidates, scores, per-token logits — only meaningful with the Session API. |
| Benchmark fake tokens | `engine_settings_set_num_prefill_tokens`, `set_num_decode_tokens` | Synthetic-token benchmarking. |
| NPU dispatch dir | `engine_settings_set_litert_dispatch_lib_dir` | Qualcomm/Intel NPU dispatch library location. |

> Note: the C API has **no embeddings functions** at v0.13.1 (flutter_gemma implements
> embeddings via a separate native library, not this header), so embeddings stay out of
> scope until upstream exposes them.

## Actionable next steps (suggested order)

1. ✅ ~~Android GPU sampling~~: verified on a physical device — the patched samplers load (no
   CPU-sampling fallback) and output is correct. Roadmap follow-up: expose
   `EnableSpeculativeDecoding` in `LiteRtEngineOptions` (the C API exists, default off) — per
   #2211 that is what unlocks the ~3× decode speedup with the MTP drafter.
2. **macOS validation**: ✅ CPU — `model-tests.yml` runs the full suite weekly on `macos-15`
   (Apple Silicon), 6/6 on 2026-06-12 including the real constrained-decoding loop. The
   experimental GPU/Metal pass (`continue-on-error`) shows the runner's paravirtual Metal
   device DOES run inference (delegate kernels initialize, chat/streaming/constrained pass
   on backend=gpu).
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
     sampler**, via `mac-gpu-cli-probe.yml` (manual workflow that runs Google's own
     litert_lm_main on the runner). Four passes, same runner/model:
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
   Real-hardware Metal validation (the "mac test kit": console sample published for
   osx-arm64 + natives + instructions) is now unblocked and still worthwhile.
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
6. **Optional**: binding coverage push per the "C API coverage" section above (start with the
   high-value group: history restore, extra context, clone, cache dir, speculative decoding,
   multimodal); `android-x64` for emulators; Desktop meta-package;
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
  loop IS exercised weekly in CI on win-x64 and osx-arm64 (`model-tests.yml` matrix). Android is NOT affected
  (tools validated on physical device, CPU and GPU; upstream #1859 looks like a
  custom-model issue, discarded).
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
