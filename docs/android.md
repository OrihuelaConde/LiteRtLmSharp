# Android

Goal: run LiteRtLmSharp on `net10.0-android` / MAUI (RID `android-arm64` for devices, `android-x64`
for the x86_64 emulator).

## Key finding that simplifies everything
A **`net10.0` library is consumable from `net10.0-android` apps** → **the managed package does
NOT need multi-targeting**. Android only needs the native `.so` binaries and a per-RID runtime
package. The API surface (`[LibraryImport]`, `NativeLibrary`, `[UnmanagedCallersOnly]`,
P/Invoke) works on .NET Android (CoreCLR).

## Pieces (status)

- **Native binary** (`native-release.yml`): ✅ Google's official `liblitert-lm.so` for
  `android_arm64` and `android_x86_64`, each shipped as a single `libLiteRtLm.so` with the
  OpenCL/WebGPU accelerators, the GPU samplers and the constraint provider embedded (16 KB page
  alignment, the Google Play requirement, is asserted by the workflow). The v0.18.0 libraries require
  Android 7.0 (API 24) or later. Until v0.15.0 it was built here with Bazel and shipped
  next to upstream's companion `.so` files; the sections below that mention companions or the
  sampler patch describe that era and are kept as the diagnostic record.
- **Runtime packages** `LiteRtLmSharp.runtime.android-arm64` and `LiteRtLmSharp.runtime.android-x64`:
  ✅. .NET Android packs `runtimes/<rid>/native/*.so` into the APK (under `lib/arm64-v8a/` and
  `lib/x86_64/`).
- **pack-nuget.yml**: ✅ includes android.
- **Managed**: no changes (net10.0).

## Native loading on Android
P/Invoke `"LiteRtLm"` → the runtime loads `libLiteRtLm.so` from the app's native-libs dir. The
`NativeLibraryResolver` finds no `runtimes/.../native` on disk (on Android they live inside the
APK) and falls back to the default `NativeLibrary.TryLoad("LiteRtLm")` → resolves. There are no
companion libraries any more: accelerators, samplers and the constraint provider are inside the
one file. The vendor `libOpenCL.so` is still `dlopen`'d at runtime and needs the manifest
declaration described below; the sampler factory tries `libLiteRtTopKOpenClSampler.so` first and
then uses its embedded copy, so the logcat line `OpenCL sampler not available, falling back to
statically linked C API` is expected on every GPU run (sampling stays on the GPU).

## Consumption (MAUI / .NET Android)
```xml
<PackageReference Include="LiteRtLmSharp" Version="1.2.0" />
<PackageReference Include="LiteRtLmSharp.runtime.android-arm64" Version="1.2.0" />
<!-- Only to run on the x86_64 Android emulator (CPU backend): -->
<PackageReference Include="LiteRtLmSharp.runtime.android-x64" Version="1.2.0" />
```
The `.litertlm` model (~2.5 GB for E2B) is **not packed** into the APK: download it to app
storage on first run and pass its path to `LiteRtEngine.Load`.

## Validation status
1. ✅ (self-built era) `build-native.yml` android green (the runner's NDK sufficed; dynamic-list
   applies; symbols OK). Since v0.16.0: `native-release.yml` repackages the official library.
2. ✅ `pack-nuget.yml` produces `LiteRtLmSharp.runtime.android-arm64`.
3. ✅ **Validated on a physical device** (Moto G100, Android 12): model load, chat, streaming —
   **CPU and GPU** (see the GPU diagnosis below). Sample app in `samples/Maui`.
4. ✅ `android-x64` (x86_64 emulator): the official library ships as `LiteRtLmSharp.runtime.android-x64`,
   and the MAUI sample's Release APK carries both ABIs (`lib/arm64-v8a/` and `lib/x86_64/`, 58.5 MB in
   total). Emulators expose no OpenCL GPU, so the emulator runs the CPU backend. Validated on an API 35
   x86_64 emulator (2026-10-06, 8 cores, WHPX acceleration) with an app built from the packed packages:
   gemma-4-E2B chats on CPU at 14.8 tok/s decode, and EmbeddingGemma 2 returns the same vectors as on
   desktop. Copy models into the app's own external files folder after the app has created it: a folder
   created by `adb` belongs to the shell user, and the app cannot read it.
5. ✅ Re-tested on device with the patched samplers: **the patchelf works** (device==local
   checksums; zero `sampler_factory` warnings → GPU sampling active; correct output). No
   perceptible speed gain yet: the big jump (~3×, #2211) additionally requires speculative
   decoding, now exposed as `LiteRtEngineOptions.EnableSpeculativeDecoding`
   (`litert_lm_engine_settings_set_enable_speculative_decoding`). ✅ Measured on this device
   (Adreno 650, OpenCL, 2026-06-16): also neutral (~1.01×, 14.1 vs 13.9 tok/s). logcat confirms MTP
   runs correctly on GPU (drafter compiles on OpenCL, GPU sampler active, no fallback), but draft
   acceptance is only ~32% — too low to beat the drafter overhead on this older GPU. Same story on
   desktop (CPU regresses; WebGPU needs the cache off and still doesn't speed up). A newer flagship
   GPU is the remaining thing to try. See [speculative-decoding.md](speculative-decoding.md).
6. ✅ **Official v0.16.0 prebuilt validated on the same device (2026-09-05)**: the MAUI sample with
   the single official `libLiteRtLm.so` loads gemma-4 E2B on GPU (OpenCL picked; 15.7 tok/s decode
   warm, TTFT 0.7 s), on CPU (12.2 tok/s), and the Tools tab answers both demo tools with
   constrained decoding; APK 60 MB vs 78 MB with the self-built set. The embedded OpenCL sampler
   keeps sampling on the GPU (the factory falls back to its statically linked copy, never to CPU).
   For the record, the self-built v0.15.0 candidate (never published) failed on GPU on this very device: upstream's v0.15.0
   prebuilt `libLiteRtTopKOpenClSampler.so` exported 4 of the 7 functions the v0.15.0 engine
   requires (`CanHandleInput` missing), the WebGPU sampler then failed with `NOT_FOUND` and
   generation aborted — [LiteRT-LM#3135](https://github.com/google-ai-edge/LiteRT-LM/issues/3135),
   reported by the Unity binding. v0.14.0 was fine (4 exports sufficed) and the v0.16.0 prebuilt
   exports all 7; nothing self-built at v0.15.0 was ever published.

7. ✅ **LiteRT-LM v0.18.0 validated on the same device (2026-10-06)** with an app built from the packed
   1.3.0 packages (`LiteRtLmSharp.runtime.android-arm64`, both ABIs in one APK), gemma-4-E2B, medians of
   3 runs:

   | | Load | Decode | Time to first token | RAM (RSS) |
   |---|---|---|---|---|
   | CPU | 8.9 s | 12.9 tok/s | 1.03 s | peak 1.66 GB |
   | GPU, F32 (the default) | 10.3 s (cache present) | 14.1 to 14.8 tok/s | 0.57 s | about 1.8 GB |
   | GPU, F16 | 39 s (first load, cache built) | 15.2 tok/s | 0.31 s | about 1.65 GB |

   Every configuration answered correctly and passed digit, date and counting fidelity checks. Model
   metadata and EmbeddingGemma 2 work too: one sentence takes 504 ms on CPU, 188 ms on GPU with F32 and
   103 ms with F16, with a cosine similarity to the CPU vector of 0.9995 (F32) and 0.9962 (F16). Chat and
   embedding engines also run together: with gemma-4-E2B on GPU (about 1.8 GB), the embedding engine adds
   about 120 MB on CPU or 260 MB on GPU, and a retrieve-then-answer flow (with an embedding computed while
   the reply streams) completed with about 0.4 to 0.5 GB of device memory still available.

## Risks
- Vendor GPU drivers (see the diagnosis below): older Adreno Vulkan drivers break Dawn's shaders,
  and OpenCL must be reachable through the manifest declaration.
- Model size/memory on low-RAM devices.
- Activation precision on mobile GPUs. Since 1.3.0 the binding asks for F32 activations by default
  (`LiteRtEngineOptions.ActivationDataType`), because the runtime's F16 default corrupts structured
  output on the desktop GPUs we measured. On the Moto G100 F32 costs about 5% of decode speed, doubles the
  time to first token and uses about 150 MB more RAM (item 7). If a phone runs too slowly or out of memory
  on GPU, try `ActivationDataType = LiteRtActivationDataType.Float16` and check your outputs.

## Android GPU — full diagnosis (validated on device, 2026-06-10)

Test device: Moto G100 (Snapdragon 870 / Adreno 650, Android 12 / API 31). Initial symptom:
**CPU fine, GPU returned low-ID garbage tokens** (`<unused*>`, `<bos>`, `<unk>`).

### Causal chain (every link verified with logcat/binaries)
1. **Android 12+ requires `<uses-native-library>`**: without declaring `libOpenCL.so` in the
   manifest, the OpenCL `dlopen` fails *silently* (the loader only allows declared vendor libs).
2. Without OpenCL, the registry picks **`libLiteRtGpuAccelerator.so`, which is
   Dawn/WebGPU→Vulkan** (verified via strings: dawn×78, wgpu×41; `libLiteRtOpenClAccelerator.so`
   is pure CL).
3. The Adreno 650's 2021 Vulkan driver **cannot compile Dawn's shaders**
   (`AdrenoVK: Shader compilation failed — "Unknown floating point rounding mode"`) and the
   engine **emits garbage logits instead of an error/fallback** → low-ID tokens.

### Fix (verified working)
Declare in `AndroidManifest.xml` (same set as Google's official Gallery app):
```xml
<uses-native-library android:name="libvndksupport.so" android:required="false" />
<uses-native-library android:name="libOpenCL.so" android:required="false" />
<uses-native-library android:name="libcdsprpc.so" android:required="false" />
<uses-native-library android:name="libedgetpu_litert.so" android:required="false" />
```
With this, logcat shows `tflite: Loaded OpenCL library with dlopen` and **the registry prefers
OpenCL over Dawn on its own** (measured with the self-built 7-`.so` set; the official monolith
behaves the same) → correct text on GPU.
Expected profile: slower GPU init (weight upload + CL kernel compilation, ~17 s on the test
device), faster decode than CPU.

### Historical: TopK samplers failed to load → patchelf (self-built era, v0.13.1 → v0.15.0)
> Resolved by the official v0.16.0 prebuilt, which embeds the samplers; there is no patchelf and no
> companion sampler any more. Kept for the record.

`dlopen failed: cannot locate symbol "LiteRtCreateEnvironment"` — Google's prebuilt samplers
lack `DT_NEEDED libLiteRtLm.so` (upstream **LiteRT-LM#2211**; flutter_gemma fixed it the same
way in their #270). The fallback is graceful: CPU sampling, GPU matmuls. Per #2211 the
fallback costs ~3× decode on models with an MTP drafter section (gemma-4-E2B has one).
- **No consumer-side workaround**: we tested `dlopen(RTLD_NOLOAD|RTLD_GLOBAL)` on device and
  bionic ignores the flag promotion (flags are fixed at first load).
- **Fix applied**: `patchelf --add-needed libLiteRtLm.so` in the android job of
  `build-native.yml`. #2211 caveat: some linkers (Tensor G2) reject patched ELFs — graceful
  failure mode (CPU sampling, same as without the patch). ✅ Re-tested on device with the
  patched binaries: GPU sampling active.

### Ecosystem (same problem in other projects)
- flutter_gemma [#214](https://github.com/DenisovAV/flutter_gemma/issues/214) (GPU garbage on
  an A55) and [#270](https://github.com/DenisovAV/flutter_gemma/issues/270) (samplers
  DT_NEEDED).
- Gallery [#910](https://github.com/google-ai-edge/gallery/issues/910), #934, #431 (GPU broken
  on certain devices even in Google's own app).
- Upstream: [LiteRT-LM#1850](https://github.com/google-ai-edge/LiteRT-LM/issues/1850)
  (`clEnqueueNDRangeKernel - Invalid command queue` on some Adreno — did not reproduce on our
  test device).

### Upstream reports (filed)
- Silent-garbage angle: posted to
  [LiteRT-LM#1881](https://github.com/google-ai-edge/LiteRT-LM/issues/1881) — Dawn generates
  FP16 shaders without checking the `shaderFloat16` capability; on mobile Adreno via
  Dawn/Vulkan this yields silent garbage instead of an error.
- `DT_NEEDED`/bionic angle: posted to
  [LiteRT-LM#2211](https://github.com/google-ai-edge/LiteRT-LM/issues/2211) — no consumer-side
  fix exists because bionic ignores `RTLD_NOLOAD|RTLD_GLOBAL` promotion; patchelf at build
  time is the only lever.
- The `<uses-native-library>` requirement is documented in upstream's Kotlin getting-started
  guide, so no separate report was filed for it.
