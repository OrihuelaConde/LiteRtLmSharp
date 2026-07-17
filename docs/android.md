# Android

Goal: run LiteRtLmSharp on `net10.0-android` / MAUI (RID `android-arm64`).

## Key finding that simplifies everything
A **`net10.0` library is consumable from `net10.0-android` apps** → **the managed package does
NOT need multi-targeting**. Android only needs the native `.so` binaries and a per-RID runtime
package. The API surface (`[LibraryImport]`, `NativeLibrary`, `[UnmanagedCallersOnly]`,
P/Invoke) works on .NET Android (CoreCLR).

## Pieces (status)

- **Native build** (`build-native.yml`, `build-android-arm64` job): ✅
  - `bazelisk build -c opt --strip=always --config=android_arm64 --linkopt=-Wl,-z,max-page-size=16384 //c:libLiteRtLm.dylib`
  - **No `litert_link_capi_so`**: on Android there is no separate `libLiteRt.so` in
    `prebuilt/` → LiteRt's C API is **linked statically** into `libLiteRtLm.so` (and exported
    via the dynamic-list, which covers `@platforms//os:android` after the patch fix).
  - **16 KB page size**: Google Play requirement.
  - Companions from `prebuilt/android_arm64/`: `libLiteRtGpuAccelerator.so`,
    `libLiteRtOpenClAccelerator.so`, `libLiteRtTopKOpenClSampler.so`,
    `libGemmaModelConstraintProvider.so` (+WebGPU). NO `libLiteRt.so`.
- **Runtime package** `LiteRtLmSharp.runtime.android-arm64`: ✅. .NET Android packs
  `runtimes/android-arm64/native/*.so` into the APK (under `lib/arm64-v8a/`).
- **pack-nuget.yml**: ✅ includes android.
- **Managed**: no changes (net10.0).

## Native loading on Android
P/Invoke `"LiteRtLm"` → the runtime loads `libLiteRtLm.so` from the app's native-libs dir. The
`NativeLibraryResolver` finds no `runtimes/.../native` on disk (on Android they live inside the
APK) and falls back to the default `NativeLibrary.TryLoad("LiteRtLm")` → resolves. Companions
resolve through Android's linker namespace (same app dir) and the accelerators via
`RTLD_DEFAULT` (`LiteRt*` symbols exported by the dynamic-list). The resolver's RTLD_GLOBAL
preload does not apply (no candidate dir) and is not needed: `libLiteRtLm.so` has no
`NEEDED libLiteRt.so` (static).

## Consumption (MAUI / .NET Android)
```xml
<PackageReference Include="LiteRtLmSharp" Version="1.1.0" />
<PackageReference Include="LiteRtLmSharp.runtime.android-arm64" Version="1.1.0" />
```
The `.litertlm` model (~2.5 GB for E2B) is **not packed** into the APK: download it to app
storage on first run and pass its path to `LiteRtEngine.Load`.

## Validation status
1. ✅ `build-native.yml` android green (the runner's NDK sufficed; dynamic-list applies;
   symbols OK).
2. ✅ `pack-nuget.yml` produces `LiteRtLmSharp.runtime.android-arm64`.
3. ✅ **Validated on a physical device** (Moto G100, Android 12): model load, chat, streaming —
   **CPU and GPU** (see the GPU diagnosis below). Sample app in `samples/Maui`.
4. `android-x64` (emulators) deferred — testing happens on physical devices (the upstream
   `android_x86_64` prebuilt exists if ever needed).
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

## Risks
- Runner NDK version vs r28b+.
- Accelerator resolution without a separate `libLiteRt.so` (GPU needed validation; CPU was
  expected to work) — both verified on device.
- Model size/memory on low-RAM devices.

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
OpenCL over Dawn on its own** (with the full 7-`.so` set present) → correct text on GPU.
Expected profile: slower GPU init (weight upload + CL kernel compilation, ~17 s on the test
device), faster decode than CPU.

### Additional finding: TopK samplers fail to load → patchelf applied in CI
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
