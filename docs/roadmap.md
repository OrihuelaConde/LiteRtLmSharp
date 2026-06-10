# Estado del proyecto y roadmap

Última actualización: 2026-06-10. Fuente de verdad del "qué está hecho y qué falta".

## Estado por plataforma

| Plataforma | Binarios nativos (CI) | Paquete NuGet | Validación runtime |
|---|---|---|---|
| win-x64 | ✅ | ✅ | ✅ (local + CI) |
| linux-x64 | ✅ | ✅ | ✅ (CI carga real) |
| android-arm64 | ✅ | ✅ | ✅ device (Moto G100): CPU y GPU |
| osx-arm64 | ✅ | ✅ | ⏳ colega con Mac Apple Silicon |
| ios-arm64 | ✅ | ⏳ (requiere xcframework) | ⏳ vía TestFlight |

Todo pinneado a **LiteRT-LM v0.13.1**; versión de paquetes `0.13.1-preview.1`.

## Funcionalidad del binding

| Área | Estado |
|---|---|
| Chat (bloqueante + streaming token a token, cancelación) | ✅ |
| Function calling / tools (constrained decoding, sanitización de tokens Gemma) | ✅ |
| System prompt, sampler params, max tokens, token count (medidor de contexto) | ✅ |
| AOT/trim-friendly (`[LibraryImport]`, `[UnmanagedCallersOnly]`, sin reflection) | ✅ |
| Multimodal (imagen/audio), embeddings, tokenize/detokenize, benchmark API | 🔜 roadmap |

Restricciones conocidas (documentadas en README): un engine por proceso; conversaciones no
thread-safe; `MaxNumTokens` = ventana total de contexto; VC++ Redistributable en win-x64; Android
GPU requiere `<uses-native-library>` en el manifest.

## Pendientes accionables (en orden sugerido)

1. ✅ ~~Android GPU sampling~~: verificado en el G100 — los samplers patcheados cargan (sin
   fallback a CPU sampling) y el output es correcto. Follow-up de roadmap: exponer
   `EnableSpeculativeDecoding` en `LiteRtEngineOptions` (el API C existe, default off) — es lo que
   desbloquea el ~3× de decode con el MTP drafter según #2211.
2. **Validación macOS**: preparar "mac test kit" (console sample publicado osx-arm64 + natives +
   instrucciones) para el colega con Mac Apple Silicon.
3. **Liberación** (bloqueado por respuesta de naming en #2535):
   rename (repo/namespaces/IDs `LiteRtLmSharp` o `LiteRTLM.NET`) → repo público → publicar a
   nuget.org + reservar prefijo de ID. Lo legal ya está listo (Apache-2.0 + NOTICE +
   THIRD-PARTY-NOTICES + disclaimers en paquetes/README).
4. **Reporte upstream** (redactar JUNTOS antes de publicar): (a) shaders Vulkan fallan → basura
   silenciosa sin fallback; (b) `uses-native-library` sin documentar para consumidores del C API;
   (c) aportar a #2211 el hallazgo de que bionic ignora la promoción `RTLD_NOLOAD|RTLD_GLOBAL`
   (no hay workaround consumer-side).
5. **Fase iOS app**: Apple Developer Program → xcframework + `.targets` NativeReference → app MAUI
   `net10.0-ios` → firma en CI → TestFlight al iPhone 16 Pro.
6. **Opcionales**: API multimodal/embeddings; `android-x64` para emuladores; SourceLink/símbolos;
   meta-paquete Desktop; CONTRIBUTING + templates; migrar samples a `PackageReference` cuando los
   NuGets estén publicados; PR a upstream para aparecer en su lista de bindings.

## Watchlist (revisar periódicamente)

- **[LiteRT-LM#2211](https://github.com/google-ai-edge/LiteRT-LM/issues/2211)** — samplers GPU sin
  `DT_NEEDED` (nuestro patchelf es el workaround). Si Google publica prebuilts arreglados o un fix,
  **quitar el patchelf** del job android. Atentos también a los relacionados #2241, #1860 y al bug
  OpenCL #1850 (`Invalid command queue` — no nos afectó en el G100, pero afecta otros Adreno).
- **[LiteRT-LM#2535](https://github.com/google-ai-edge/LiteRT-LM/issues/2535)** — nuestro issue de
  naming. Su respuesta destraba el rename y la liberación.
- **Tags nuevos de LiteRT-LM** — automatizado: `upstream-watch.yml` (lunes/jueves) abre un issue con
  el checklist de actualización cuando hay release nuevo.
- **flutter_gemma** — releases/issues como fuente de recetas (p.ej. su #270/#214 anticiparon
  nuestros problemas de Android GPU).

## Decisiones de arquitectura (registro)

- Wrapper P/Invoke sobre el **C API** (`c/engine.h`), nunca C++/CLI. .NET 10 exclusivo.
- Binarios **propios** desde tags de release (jamás commits sueltos — lección del segfault de
  streaming en `032334d8`), vía `native/patch_c_api.sh` + `build-native.yml` (input `platforms`
  para no recompilar lo existente; release acumula assets).
- Distribución estilo LLamaSharp: managed puro + `runtime.<rid>` por plataforma.
- Desktop linkea `libLiteRt` separada (`litert_link_capi_so`); Android/macOS/iOS la llevan estática.
- Soluciones separadas: `LiteLMSharp.slnx` (lib+tests+packaging, SDK pelado, CI) y
  `samples/LiteLMSharp.Samples.slnx` (console + MAUI, requiere workloads).
