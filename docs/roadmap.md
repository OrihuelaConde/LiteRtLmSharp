# Project status and roadmap

Last updated: 2026-06-11. Source of truth for "what's done and what's pending".

## Status per platform

| Platform | Native binaries (CI) | NuGet package | Runtime validation |
|---|---|---|---|
| win-x64 | ✅ | ✅ | ✅ (local + CI) |
| linux-x64 | ✅ | ✅ | ✅ (CI, real model load) |
| android-arm64 | ✅ | ✅ | ✅ physical device (Adreno 650): CPU and GPU |
| osx-arm64 | ✅ | ✅ | ⏳ built in CI, not yet validated on hardware |
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
| Multimodal (image/audio), embeddings, tokenize/detokenize, benchmark API | 🔜 roadmap |

Known constraints (documented in the README): one engine ALIVE at a time (reloading after
`Dispose` works — verified on win-x64 cpu→cpu and cpu→gpu; this is Edge Gallery's pattern for
switching model/backend without restarting); conversations are not thread-safe; `MaxNumTokens`
is the total context window; VC++ Redistributable required on win-x64; Android GPU requires
`<uses-native-library>` in the app manifest.

## Actionable next steps (suggested order)

1. ✅ ~~Android GPU sampling~~: verified on a physical device — the patched samplers load (no
   CPU-sampling fallback) and output is correct. Roadmap follow-up: expose
   `EnableSpeculativeDecoding` in `LiteRtEngineOptions` (the C API exists, default off) — per
   #2211 that is what unlocks the ~3× decode speedup with the MTP drafter.
2. **macOS validation**: prepare a "mac test kit" (console sample published for osx-arm64 +
   natives + instructions) for testing on Apple Silicon hardware.
3. **Public release** (in progress): ✅ rename to `LiteRtLmSharp` (naming guidance asked in
   #2535; no objection pattern — Rust/Flutter community bindings use equivalent names) →
   make the repo public → publish `0.1.0-preview.1` to nuget.org → reserve the ID prefix.
   Legal is ready (Apache-2.0 + NOTICE + THIRD-PARTY-NOTICES + disclaimers in packages/README).
4. **Upstream reports**: (a) ✅ posted to #1881 — Vulkan/Dawn FP16 shaders fail on older Adreno
   and the engine emits silent garbage instead of an error/fallback; (b) dropped — the
   `uses-native-library` requirement is documented in upstream's Kotlin getting-started docs;
   (c) ✅ posted to #2211 — bionic ignores `RTLD_NOLOAD|RTLD_GLOBAL` flag promotion, so there is
   no consumer-side workaround for the missing `DT_NEEDED`.
5. **iOS app phase**: Apple Developer Program → xcframework + `.targets` NativeReference →
   MAUI `net10.0-ios` app → CI signing → TestFlight.
6. **Optional**: multimodal/embeddings API; `android-x64` for emulators; Desktop meta-package;
   CONTRIBUTING + issue templates; scheduled smoke-test workflow that consumes the published
   packages from nuget.org; PR upstream to be listed among the language bindings (planned right
   after the nuget.org release).

## Watchlist (re-check periodically)

- **[LiteRT-LM#2211](https://github.com/google-ai-edge/LiteRT-LM/issues/2211)** — GPU samplers
  missing `DT_NEEDED` (our patchelf is the workaround). If Google ships fixed prebuilts or a
  fix, **drop the patchelf** from the android job. Also watch the related #2241, #1860 and the
  OpenCL bug #1850 (`Invalid command queue` — did not reproduce on our Adreno 650 test device,
  but hits other Adreno GPUs).
- **[LiteRT-LM#2535](https://github.com/google-ai-edge/LiteRT-LM/issues/2535)** — our naming
  issue. Close the loop there once the packages are published.
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
- Desktop links `libLiteRt` as a separate shared lib (`litert_link_capi_so`); Android/macOS/iOS
  link it statically.
- Separate solutions: `LiteRtLmSharp.slnx` (lib+tests+packaging, bare SDK, CI) and
  `samples/LiteRtLmSharp.Samples.slnx` (console + MAUI, needs workloads).
