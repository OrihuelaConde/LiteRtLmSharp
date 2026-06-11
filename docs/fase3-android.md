# Fase 3 — Android

Objetivo: correr LiteRtLmSharp en `net10.0-android` / MAUI (RID `android-arm64`).

## Hallazgo clave que simplifica todo
Una librería **`net10.0` es consumible por apps `net10.0-android`** → **el paquete managed NO necesita
multitarget**. Android solo necesita el binario nativo `.so` y un runtime-package por-RID. La API
(`[LibraryImport]`, `NativeLibrary`, `[UnmanagedCallersOnly]`, P/Invoke) funciona en .NET Android (CoreCLR).

## Piezas (estado)

- **Native build** (`build-native.yml`, job `build-android-arm64`): ✅ agregado, **falta correrlo en CI**.
  - `bazelisk build -c opt --strip=always --config=android_arm64 --linkopt=-Wl,-z,max-page-size=16384 //c:libLiteRtLm.dylib`
  - **Sin `litert_link_capi_so`**: en Android NO hay `libLiteRt.so` separado en `prebuilt/` → la C API de
    LiteRt se **linkea estática** en `libLiteRtLm.so` (y se exporta vía el dynamic-list, que ya cubre
    `@platforms//os:android` tras el fix del parche).
  - **page-size 16 KB**: requisito de Google Play.
  - Companions desde `prebuilt/android_arm64/`: `libLiteRtGpuAccelerator.so`, `libLiteRtOpenClAccelerator.so`,
    `libLiteRtTopKOpenClSampler.so`, `libGemmaModelConstraintProvider.so` (+WebGPU). NO `libLiteRt.so`.
- **Runtime package** `LiteRtLmSharp.runtime.android-arm64`: ✅ agregado. `.NET Android` empaqueta
  `runtimes/android-arm64/native/*.so` en el APK (bajo `lib/arm64-v8a/`).
- **pack-nuget.yml**: ✅ actualizado para incluir android.
- **Managed**: sin cambios (net10.0).

## Carga nativa en Android
P/Invoke `"LiteRtLm"` → el runtime carga `libLiteRtLm.so` desde el dir de libs nativas del app. El
`NativeLibraryResolver` no encuentra `runtimes/.../native` en disco (en Android viven en el APK) y cae al
default `NativeLibrary.TryLoad("LiteRtLm")` → resuelve. Las companions resuelven por el namespace del
linker de Android (mismo dir del app) y los accelerators vía `RTLD_DEFAULT` (símbolos `LiteRt*` exportados
por el dynamic-list). El preload RTLD_GLOBAL del resolver no aplica (no hay dir candidato) y no hace falta:
`libLiteRtLm.so` no tiene `NEEDED libLiteRt.so` (estático).

## Consumo (MAUI / .NET Android)
```xml
<PackageReference Include="LiteRtLmSharp" Version="0.13.1-preview.1" />
<PackageReference Include="LiteRtLmSharp.runtime.android-arm64" Version="0.13.1-preview.1" />
```
El modelo `.litertlm` (~2.5 GB para E2B) **no se empaqueta** en el APK: descargarlo a almacenamiento del
app en primer arranque y pasar su ruta a `LiteRtEngine.Load`.

## Estado de validación
1. ✅ `build-native.yml` android verde (NDK del runner alcanzó; dynamic-list aplica; símbolos OK).
2. ✅ `pack-nuget.yml` genera `LiteRtLmSharp.runtime.android-arm64`.
3. ✅ **Validado en device físico** (Moto G100, Android 12): carga de modelo, chat, streaming —
   **CPU y GPU** (ver diagnóstico GPU abajo). Workload MAUI instalado; app sample en `samples/Maui`.
4. `android-x64` (emuladores) descartado por ahora — pruebas en device físico (el prebuilt
   `android_x86_64` existe upstream si algún día hace falta).
5. ✅ Re-test en device con los samplers patcheados: **el patchelf funciona en el G100**
   (checksums device==local; cero warnings de `sampler_factory` → GPU sampling activo; output
   correcto). Mejora de velocidad no perceptible: el gran salto (~3×, #2211) requiere además
   habilitar speculative decoding (`litert_lm_engine_settings_set_enable_speculative_decoding`,
   apagado por defecto y aún no expuesto en el binding — candidato a roadmap).

## Riesgos
- Versión de NDK en el runner vs r28b+.
- Resolución de accelerators sin `libLiteRt.so` separado (validar GPU; CPU debería andar).
- Tamaño/memoria del modelo en dispositivos.
- Primera iteración del build de Bazel para Android suele requerir ajustes (no testeable localmente).

## GPU en Android — diagnóstico completo (validado en device, 2026-06-10)

Device: Moto G100 (Snapdragon 870 / Adreno 650, Android 12 / API 31). Síntoma inicial: **CPU bien,
GPU devolvía tokens basura de ID bajo** (`<unused*>`, `<bos>`, `<unk>`).

### Cadena causal (cada eslabón verificado con logcat/binarios)
1. **Android 12+ exige `<uses-native-library>`**: sin declarar `libOpenCL.so` en el manifest, el
   `dlopen` de OpenCL falla *silenciosamente* (el loader solo permite libs del vendor declaradas).
2. Sin OpenCL, el registry usa **`libLiteRtGpuAccelerator.so`, que es Dawn/WebGPU→Vulkan**
   (verificado por strings: dawn×78, wgpu×41; `libLiteRtOpenClAccelerator.so` es CL puro).
3. El driver Vulkan 2021 del Adreno 650 **no compila los shaders de Dawn**
   (`AdrenoVK: Shader compilation failed — "Unknown floating point rounding mode"`) y el engine
   **emite logits basura en vez de error/fallback** → tokens de ID bajo.

### Fix (verificado funcionando)
Declarar en `AndroidManifest.xml` (mismo set que la app Gallery oficial de Google):
```xml
<uses-native-library android:name="libvndksupport.so" android:required="false" />
<uses-native-library android:name="libOpenCL.so" android:required="false" />
<uses-native-library android:name="libcdsprpc.so" android:required="false" />
<uses-native-library android:name="libedgetpu_litert.so" android:required="false" />
```
Con esto, logcat muestra `tflite: Loaded OpenCL library with dlopen` y **el registry prefiere OpenCL
sobre Dawn por sí solo** (con el set completo de 7 `.so` presente) → texto correcto en GPU.
Perfil esperado: init GPU más lenta (subida de pesos + compilación de kernels CL, ~17 s en el G100),
decode más rápido que CPU.

### Hallazgo adicional: samplers TopK no cargan → patchelf aplicado en CI
`dlopen failed: cannot locate symbol "LiteRtCreateEnvironment"` — a los samplers prebuilt de Google
les falta `DT_NEEDED libLiteRtLm.so` (upstream **LiteRT-LM#2211**; flutter lo arregló igual en su
#270). El fallback es graceful: sampling en CPU, matmuls en GPU. Según #2211, el fallback cuesta ~3×
de decode en modelos con sección MTP drafter (gemma-4-E2B la tiene).
- **No hay workaround consumer-side**: probamos `dlopen(RTLD_NOLOAD|RTLD_GLOBAL)` en device y bionic
  ignora la promoción de flags (se fijan en la primera carga).
- **Fix aplicado**: `patchelf --add-needed libLiteRtLm.so` en el job android de `build-native.yml`.
  Caveat de #2211: algunos linkers (Tensor G2) rechazan ELFs parcheados — modo de falla graceful
  (CPU sampling, como sin patch). ⏳ Pendiente re-test en device con los binarios patcheados.

### Ecosistema (mismo problema en otros proyectos)
- flutter_gemma [#214](https://github.com/DenisovAV/flutter_gemma/issues/214) (basura GPU en A55) y
  [#270](https://github.com/DenisovAV/flutter_gemma/issues/270) (samplers DT_NEEDED).
- Gallery [#910](https://github.com/google-ai-edge/gallery/issues/910), #934, #431 (GPU roto en
  ciertos devices incluso en la app de Google).
- Upstream: [LiteRT-LM#1850](https://github.com/google-ai-edge/LiteRT-LM/issues/1850)
  (`clEnqueueNDRangeKernel - Invalid command queue` en algunos Adreno — NO nos afectó en el G100).

### Material para reporte upstream (pendiente de redactar JUNTOS)
Dos ángulos: (a) **falta de fallback**: si los shaders Vulkan no compilan, el engine debería degradar
o fallar, nunca emitir basura silenciosa; (b) **documentación**: el requisito de `uses-native-library`
no está documentado para consumidores del C API / embebedores.
