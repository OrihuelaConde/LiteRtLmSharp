# LiteRT-LM C ABI — referencia para el binding .NET

> Fuente de verdad: [`c/engine.h`](https://github.com/google-ai-edge/LiteRT-LM/blob/main/c/engine.h)
> del repo oficial. Este documento resume el ABI **verificado** sobre el binario real.

## Resumen de viabilidad (verificado)

- El header `c/engine.h` declara una API **C plana** (`extern "C"`) con **punteros opacos** —
  ideal para P/Invoke. ~89 funciones, prefijo `litert_lm_`.
- Export en Windows vía `__declspec(dllexport)`; en Linux/macOS vía `visibility("default")`.
- **Verificado sobre binario**: `LiteRtLm.dll` (prebuilt de flutter_gemma, tag `native-v0.12.0-a`)
  exporta **89 funciones `litert_lm_*`** en su tabla de exports (confirmado con `dumpbin /exports`).
  → El P/Invoke contra estos binarios es viable hoy, sin compilar desde source.

## Origen de los binarios prebuilt (PoC)

flutter_gemma publica los nativos de LiteRT-LM como assets de GitHub Release en **su propio repo**:

- Base: `https://github.com/DenisovAV/flutter_gemma/releases/download/native-v<version>/`
- Versión actual usada: `0.12.0-a` (tag `native-v0.12.0-a`).
- Assets relevantes para desktop:
  - `litertlm-windows_x86_64.tar.gz` (sha256 `b7264091c05001ef84e53761dfee331f761e3a2362b36b28ab2ce39666400d76`)
  - `litertlm-linux_x86_64.tar.gz` (sha256 `930296b010ecc316c6b6fc4ed1c722b275b4064b59b5aad8ff7b858e9149c0d7`)
- Lib principal: **`LiteRtLm.dll`** (Win) / **`libLiteRtLm.so`** (Linux) → nombre P/Invoke: `LiteRtLm`.

### Companions necesarios (Windows x64)
`LiteRtLm.dll` resuelve por PE imports (en LoadLibrary) las copias con prefijo `lib`:
`libLiteRt.dll`, `libGemmaModelConstraintProvider.dll`, `libLiteRtTopKWebGpuSampler.dll`,
`libLiteRtWebGpuAccelerator.dll`, más runtime DXC (`dxcompiler.dll`, `dxil.dll`) y NPU Intel opcional
(`LiteRtDispatch.dll`, `openvino*.dll`, `tbb*.dll`). **Todos los `.dll` del tarball deben ir juntos en
el directorio de salida.** El CPU backend funciona sin la parte NPU.

> Nota de licencias: estos binarios son Apache-2.0 (LiteRT-LM) re-empaquetados por flutter_gemma. Para
> producción montaremos build propio (Fase 2) y/o consumiremos el target oficial de #2154.

## Flujo mínimo (API Conversation — alto nivel, recomendado)

Maneja plantillas de chat internamente; espeja las Gemini Chat APIs vía JSON.

```c
// 1. Settings
LiteRtLmEngineSettings* s = litert_lm_engine_settings_create(model_path, "cpu", NULL, NULL);
litert_lm_engine_settings_set_max_num_tokens(s, 512);          // opcional
// 2. Engine (pesado, tiene los pesos)
LiteRtLmEngine* e = litert_lm_engine_create(s);
// 3. Conversation (config NULL = default)
LiteRtLmConversation* c = litert_lm_conversation_create(e, NULL);
// 4a. Envío bloqueante
LiteRtLmJsonResponse* r = litert_lm_conversation_send_message(c, msg_json, NULL, NULL);
const char* out_json = litert_lm_json_response_get_string(r);  // string propiedad de r
// 4b. ...o streaming (callback en hilo de fondo)
litert_lm_conversation_send_message_stream(c, msg_json, NULL, NULL, cb, user_data);
// 5. Liberar en orden inverso
litert_lm_json_response_delete(r);
litert_lm_conversation_delete(c);
litert_lm_engine_delete(e);
litert_lm_engine_settings_delete(s);
```

### Contrato JSON (verificado en `c/engine_test.cc`)

- **Mensaje de usuario** (`message_json`):
  ```json
  {"role": "user", "content": [{"type": "text", "text": "Hello"}]}
  ```
- **Respuesta** (`litert_lm_json_response_get_string`): mismo shape; el texto se lee en
  `response["content"][0]["text"]`.
- **System message** (en `litert_lm_conversation_config_set_system_message`, content es objeto no array):
  ```json
  {"type":"text","text":"You are a helpful assistant."}
  ```

### Callback de streaming
```c
typedef void (*LiteRtLmStreamCallback)(void* callback_data, const char* chunk,
                                       bool is_final, const char* error_msg);
```
- `chunk`: trozo de texto (válido solo durante la llamada → copiar). `error_msg`: NULL en éxito.
- `is_final`: true en el último chunk → señalizar fin. Se invoca desde hilo de fondo.

## Convenciones de marshalling .NET

- Strings `const char*` = **UTF-8** → `StringMarshalling.Utf8` en `[LibraryImport]`.
- `bool` C = `bool` de 1 byte → `[MarshalAs(UnmanagedType.U1)]` / `byte`.
- Punteros opacos → `SafeHandle` por tipo; liberar con su `*_delete`.
- x64 tiene convención única; declarar Cdecl explícito (`[UnmanagedCallConv(Cdecl)]`) por portabilidad.
- Callback: usar `[UnmanagedCallersOnly(Cdecl)]` + `GCHandle` en `callback_data` (AOT-friendly,
  sin marshalling de delegates).

## Tipos/structs clave del header
- `LiteRtLmSamplerParams { LiteRtLmSamplerType type; int32 top_k; float top_p; float temperature; int32 seed; }`
- `LiteRtLmSamplerType`: 0 Unspecified, 1 TopK, 2 TopP, 3 Greedy.
- `LiteRtLmInputData { LiteRtLmInputDataType type; const void* data; size_t size; }` (multimodal; texto=UTF-8).
- `LiteRtLmInputDataType`: Text, Image, ImageEnd, Audio, AudioEnd.

## Hallazgos empíricos sobre el binario prebuilt `native-v0.12.0-a` (VERIFICADO en runtime)

Probado con `gemma-4-E2B-it.litertlm` (CPU/XNNPACK) desde .NET:

1. **Generación funciona end-to-end** (bloqueante y streaming). Engine carga en ~0.2 s (mmap).
2. **Los chunks de streaming son objetos JSON completos por token**, no texto plano:
   `{"role":"assistant","content":[{"type":"text","text":"1"}]}`. → hay que parsear **cada** chunk
   con `content[0].text` (no solo la respuesta final del path bloqueante).
3. **`litert_lm_conversation_config_create` provoca AccessViolation (0xC0000005)** en este binario,
   pese a estar en la tabla de exports (ordinal 28). Es **skew de versión**: el header viene de `main`
   (~0.13+) y el binario es 0.12.0-a. → Por ahora usar conversación con **config NULL**
   (`litert_lm_conversation_create(engine, NULL)`); system message y sampler quedan deshabilitados
   hasta Fase 2 (build propio con header del mismo tag). `LiteRtConversationOptions` ya documenta esto.
4. **`litert_lm_conversation_send_message` (bloqueante) devolvió NULL** en algunas condiciones donde el
   **streaming sí funcionó**. → El path **streaming es el robusto** en este binario; tratar el bloqueante
   como best-effort.
5. **`litert_lm_conversation_get_token_count` NO está en este binario** (lanza `EntryPointNotFoundException`);
   se añadió upstream después de 0.12.0-a. El binding existe pero hay que usarlo con try/catch hasta Fase 2.
6. **`MaxNumTokens` = ventana de contexto TOTAL** (KV cache = prompt + respuesta, **acumulado entre turnos**).
   Si es pequeña (p.ej. 1024) una respuesta larga la llena y los turnos siguientes **se desbordan y degeneran
   en texto incoherente** (síntoma observado: respuesta cortada a media palabra, luego basura tipo "Laptop").
   Subir a 4096 resolvió un multi-turno coherente. No es bug del binding; es gestión de contexto del LLM.
   Para producción: exponer/gestionar historial y, cuando el binario lo permita, cap por turno vía
   `session_config_set_max_output_tokens`.

**Lección para Fase 2 / sincronía:** el header y el binario deben provenir del **mismo tag** de LiteRT-LM.
El skew explica (3) y (4); compilar nosotros desde un tag fijo lo elimina.

## Backend GPU en desktop (WebGPU) — comportamiento esperado

- El backend `"gpu"` en desktop usa **WebGPU nativo (Dawn)**, NO el navegador. Es una capa GPU portable
  que mapea a: **Direct3D 12 en Windows**, Vulkan en Linux, Metal en macOS. (En Android: OpenCL/Vulkan.)
- Verificado: con `Backend="gpu"` el log selecciona la GPU discreta (p.ej. `NVIDIA RTX 3080, backend=Direct3D 12`)
  y corre las capas del transformer en GPU (`delegate_webgpu.cc`, `delegate_kernel.cc`). Companions que lo
  habilitan (ya incluidos): `LiteRtWebGpuAccelerator.dll` + `dxcompiler.dll`/`dxil.dll` (DirectX Shader Compiler).
- Que aparezca también "Created TensorFlow Lite XNNPACK delegate for CPU" es normal: ops no-GPU + embeddings
  mmap corren en CPU (delegación mixta). El grueso (matmuls) va en GPU. Init más lento que CPU (~1.6s vs ~0.2s)
  por subir pesos a la GPU y compilar kernels.

### Sampler GPU cae a CPU en Windows/macOS — bug upstream (#2073), NO del binding
- Síntoma: `Could not load symbol LiteRtTopKWebGpuSampler_UpdateConfig` → `Falling back to CPU sampling`.
- Causa (verificado con `dumpbin /exports`): el `LiteRtTopKWebGpuSampler.dll` de Windows exporta **solo 3 de 7**
  funciones (`_Create`, `_Destroy`, `_SampleToIdAndScoreBuffer`); falta `_UpdateConfig` etc. Es el
  [issue #2073](https://github.com/google-ai-edge/LiteRT-LM/issues/2073) (Linux/Android sí traen las 7).
- El mensaje de fallback menciona `.so` / `LD_LIBRARY_PATH` / `prebuilt/`: es un **string de log sin localizar**
  (Linux-céntrico); en Windows el archivo equivalente es el `.dll` que ya enviamos. No intenta cargar `.so`.
- Impacto: el **sampling** (selección del token) corre en CPU; las **matmuls siguen en GPU**. El sampling es
  diminuto frente a las matmuls → impacto de rendimiento despreciable. Salida correcta igual.
- Fix definitivo: build propio (Fase 2) o prebuilt nuevo cuando #2073 se resuelva upstream.

> Nota: los logs `I0000 …` aparecen pese a `SetMinLogLevel(3)` porque se emiten **antes** de
> `absl::InitializeLog()` (van directo a STDERR); nuestro nivel no los puede silenciar.

## Estado del shared-library oficial
- Hoy el C API solo se ofrece como `cc_library` Bazel (`:engine`, `:engine_cpu`) y `add_litertlm_library(... STATIC)`
  en CMake → **no hay** target oficial de shared lib. Seguimiento: issue #2154 / PR #2155.
- Mitigación PoC: consumir el `LiteRtLm.dll`/`.so` de flutter_gemma (ya verificado). Producción: build propio.
