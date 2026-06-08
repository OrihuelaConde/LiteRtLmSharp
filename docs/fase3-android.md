# Fase 3 — Android

Objetivo: correr LiteLMSharp en `net10.0-android` / MAUI (RID `android-arm64`).

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
- **Runtime package** `LiteLMSharp.runtime.android-arm64`: ✅ agregado. `.NET Android` empaqueta
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
<PackageReference Include="LiteLMSharp" Version="0.13.1-preview.1" />
<PackageReference Include="LiteLMSharp.runtime.android-arm64" Version="0.13.1-preview.1" />
```
El modelo `.litertlm` (~2.5 GB para E2B) **no se empaqueta** en el APK: descargarlo a almacenamiento del
app en primer arranque y pasar su ruta a `LiteRtEngine.Load`.

## Pendiente / a validar (necesita workload .NET Android + device/emulador)
1. **Correr `build-native.yml`** (ahora con android) y revisar logs:
   - **NDK**: LiteRT-LM pide r28b+; el runner trae uno — si es viejo, pinear NDK (p.ej. `nttld/setup-ndk`).
   - Que el dynamic-list aplique en android y que `litert_lm_*`/`LiteRt*` queden exportados.
2. **`pack-nuget.yml`** para generar `LiteLMSharp.runtime.android-arm64`.
3. **App MAUI/.NET Android de prueba** en device/emulador arm64: cargar modelo, generar, tools.
   - Memoria: E2B necesita varios GB → device con RAM suficiente (gama alta).
   - GPU en Android: OpenCL/Vulkan vía los accelerators; CPU como fallback.
4. Considerar `android-x64` (emuladores x86_64) además de arm64.

## Riesgos
- Versión de NDK en el runner vs r28b+.
- Resolución de accelerators sin `libLiteRt.so` separado (validar GPU; CPU debería andar).
- Tamaño/memoria del modelo en dispositivos.
- Primera iteración del build de Bazel para Android suele requerir ajustes (no testeable localmente).
