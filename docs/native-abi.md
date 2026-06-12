# LiteRT-LM C ABI — reference for the .NET binding

> Source of truth: [`c/engine.h`](https://github.com/google-ai-edge/LiteRT-LM/blob/main/c/engine.h)
> in the official repo. This document summarizes the ABI as **verified** against the real binary.

> **Current state (v0.13.1, self-built binaries):** the findings about the community binary
> `0.12.0-a` and the interim commit `032334d8` (`conversation_config_create` crash, missing
> `get_token_count`, blocking `send_message` returning null, streaming segfault) are
> **HISTORICAL** — all resolved by compiling our own binaries from the `v0.13.1` tag with the
> matching header. Today config/system-prompt/sampler, tools, streaming and token count work on
> all 5 platforms. The notes below are kept as a diagnostic record.
>
> Caveat (2026-06-12): "all 5 platforms" for **tools** was validated by hand on win-x64/Android;
> on desktop Linux the tools + constrained-decoding path had never actually run in CI (regular CI
> has no model), and a Linux Mint user reported the process dying silently with tools enabled —
> matches upstream [LiteRT-LM#2149](https://github.com/google-ai-edge/LiteRT-LM/issues/2149)
> (C-API shared lib segfaults/hangs in decode on Ubuntu 24.04; static CLI works). The scheduled
> `model-tests.yml` workflow now exercises streaming + tools on linux-x64 with gemma-4-E2B-it.

## Viability summary (verified)

- The `c/engine.h` header declares a **flat C API** (`extern "C"`) with **opaque pointers** —
  ideal for P/Invoke. ~89 functions, `litert_lm_` prefix.
- Exported on Windows via `__declspec(dllexport)`; on Linux/macOS via `visibility("default")`.
- **Verified against the binary**: `LiteRtLm.dll` (flutter_gemma prebuilt, tag `native-v0.12.0-a`)
  exports **89 `litert_lm_*` functions** in its export table (confirmed with `dumpbin /exports`).
  → P/Invoke against these binaries was viable from day one, without building from source.

## Origin of the prebuilt binaries (PoC phase)

flutter_gemma publishes LiteRT-LM natives as GitHub Release assets on **its own repo**:

- Base: `https://github.com/DenisovAV/flutter_gemma/releases/download/native-v<version>/`
- Version used during the PoC: `0.12.0-a` (tag `native-v0.12.0-a`).
- Relevant desktop assets:
  - `litertlm-windows_x86_64.tar.gz` (sha256 `b7264091c05001ef84e53761dfee331f761e3a2362b36b28ab2ce39666400d76`)
  - `litertlm-linux_x86_64.tar.gz` (sha256 `930296b010ecc316c6b6fc4ed1c722b275b4064b59b5aad8ff7b858e9149c0d7`)
- Main lib: **`LiteRtLm.dll`** (Win) / **`libLiteRtLm.so`** (Linux) → P/Invoke name: `LiteRtLm`.

### Required companions (Windows x64)
`LiteRtLm.dll` resolves the `lib`-prefixed copies via PE imports (at LoadLibrary time):
`libLiteRt.dll`, `libGemmaModelConstraintProvider.dll`, `libLiteRtTopKWebGpuSampler.dll`,
`libLiteRtWebGpuAccelerator.dll`, plus the DXC runtime (`dxcompiler.dll`, `dxil.dll`) and the
optional Intel NPU set (`LiteRtDispatch.dll`, `openvino*.dll`, `tbb*.dll`). **Every `.dll` in
the tarball must ship together in the output directory.** The CPU backend works without the
NPU part.

> License note: those binaries are Apache-2.0 (LiteRT-LM) repackaged by flutter_gemma. For
> production we build our own (see docs/native-build.md) and/or will consume the official
> target from #2154.

## Minimal flow (Conversation API — high level, recommended)

Handles chat templates internally; mirrors the Gemini Chat APIs via JSON.

```c
// 1. Settings
LiteRtLmEngineSettings* s = litert_lm_engine_settings_create(model_path, "cpu", NULL, NULL);
litert_lm_engine_settings_set_max_num_tokens(s, 512);          // optional
// 2. Engine (heavy, owns the weights)
LiteRtLmEngine* e = litert_lm_engine_create(s);
// 3. Conversation (NULL config = defaults)
LiteRtLmConversation* c = litert_lm_conversation_create(e, NULL);
// 4a. Blocking send
LiteRtLmJsonResponse* r = litert_lm_conversation_send_message(c, msg_json, NULL, NULL);
const char* out_json = litert_lm_json_response_get_string(r);  // string owned by r
// 4b. ...or streaming (callback on a background thread)
litert_lm_conversation_send_message_stream(c, msg_json, NULL, NULL, cb, user_data);
// 5. Release in reverse order
litert_lm_json_response_delete(r);
litert_lm_conversation_delete(c);
litert_lm_engine_delete(e);
litert_lm_engine_settings_delete(s);
```

### JSON contract (verified in `c/engine_test.cc`)

- **User message** (`message_json`):
  ```json
  {"role": "user", "content": [{"type": "text", "text": "Hello"}]}
  ```
- **Response** (`litert_lm_json_response_get_string`): same shape; the text lives at
  `response["content"][0]["text"]`.
- **System message** (for `litert_lm_conversation_config_set_system_message`, content is an
  object, not an array):
  ```json
  {"type":"text","text":"You are a helpful assistant."}
  ```

### Streaming callback
```c
typedef void (*LiteRtLmStreamCallback)(void* callback_data, const char* chunk,
                                       bool is_final, const char* error_msg);
```
- `chunk`: text fragment (valid only during the call → copy it). `error_msg`: NULL on success.
- `is_final`: true on the last chunk → signal completion. Invoked from a background thread.

## .NET marshalling conventions

- `const char*` strings = **UTF-8** → `StringMarshalling.Utf8` in `[LibraryImport]`.
- C `bool` = 1-byte bool → `[MarshalAs(UnmanagedType.U1)]` / `byte`.
- Opaque pointers → one `SafeHandle` per type; release with its `*_delete`.
- x64 has a single calling convention; declare Cdecl explicitly
  (`[UnmanagedCallConv(Cdecl)]`) for portability.
- Callback: use `[UnmanagedCallersOnly(Cdecl)]` + a `GCHandle` in `callback_data`
  (AOT-friendly, no delegate marshalling).

## Key header types/structs
- `LiteRtLmSamplerParams { LiteRtLmSamplerType type; int32 top_k; float top_p; float temperature; int32 seed; }`
- `LiteRtLmSamplerType`: 0 Unspecified, 1 TopK, 2 TopP, 3 Greedy.
- `LiteRtLmInputData { LiteRtLmInputDataType type; const void* data; size_t size; }` (multimodal; text=UTF-8).
- `LiteRtLmInputDataType`: Text, Image, ImageEnd, Audio, AudioEnd.

## Empirical findings on the prebuilt `native-v0.12.0-a` binary (VERIFIED at runtime)

Tested with `gemma-4-E2B-it.litertlm` (CPU/XNNPACK) from .NET:

1. **Generation works end-to-end** (blocking and streaming). Engine loads in ~0.2 s (mmap).
2. **Streaming chunks are full JSON objects per token**, not plain text:
   `{"role":"assistant","content":[{"type":"text","text":"1"}]}`. → **every** chunk must be
   parsed for `content[0].text` (not just the final blocking-path response).
3. **`litert_lm_conversation_config_create` triggers an AccessViolation (0xC0000005)** in this
   binary, despite being in the export table (ordinal 28). It is **version skew**: the header
   came from `main` (~0.13+) while the binary was 0.12.0-a. → Workaround at the time: create
   conversations with a **NULL config** (`litert_lm_conversation_create(engine, NULL)`).
4. **Blocking `litert_lm_conversation_send_message` returned NULL** in some conditions where
   **streaming worked**. → The **streaming path was the robust one** in this binary.
5. **`litert_lm_conversation_get_token_count` is NOT in this binary** (throws
   `EntryPointNotFoundException`); it was added upstream after 0.12.0-a.
6. **`MaxNumTokens` is the TOTAL context window** (KV cache = prompt + response, **accumulated
   across turns**). If small (e.g. 1024) a long answer fills it and later turns **overflow and
   degenerate into incoherent text** (observed symptom: answer cut mid-word, then garbage like
   "Laptop"). Raising it to 4096 fixed a coherent multi-turn chat. Not a binding bug; it's LLM
   context management. For production: expose/manage history and, when the binary allows it,
   cap per-turn output via `session_config_set_max_output_tokens`.

**Sync lesson:** the header and the binary must come from the **same LiteRT-LM tag**. The skew
explains (3) and (4); building from a pinned tag eliminates it.

## Tool / function calling (verified wire format)

The C API exposes tools via the conversation config (requires a skew-free binary):
`litert_lm_conversation_config_set_tools(config, tools_json)` +
`litert_lm_conversation_config_set_enable_constrained_decoding(config, true)`.

- **Tool definition** (OpenAI/Gemini FunctionDeclaration style, from `c/engine_test.cc`):
  ```json
  [{"type":"function","function":{"name":"get_current_weather","description":"...",
    "parameters":{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}}}]
  ```
- **Tool-call response** (what `send_message` returns; Gemma 4 / FunctionGemma docs):
  ```json
  {"role":"assistant","tool_calls":[{"type":"function",
    "function":{"name":"get_current_weather","arguments":{"location":"Tokyo"}}}]}
  ```
- **Tool-result message** (sent back via `send_message`):
  ```json
  {"role":"tool","content":[{"name":"get_current_weather","response":{"temperature":15}}]}
  ```

Wrapper surface: `LiteRtTool`, `LiteRtConversationOptions.Tools` + `EnableConstrainedDecoding`,
`conv.Send(text) → LiteRtResponse` (`.Text` or `.ToolCalls`), `conv.SendToolResults(...)`, and
`conv.SendMessageRaw(json)` as an escape hatch. The parser is tolerant (function.arguments as
object or string; fallback to top-level name/args) and always exposes `RawJson`.

**VALIDATED end-to-end** with our own binary + `gemma-4-E2B-it` (CPU): define tool → model
emits tool-call → execute → re-inject → correct final answer. `conversation_config_create`
no longer crashes (matched header+binary).

> Gemma template quirk: with constrained decoding the arguments arrive with `<|"|>` tokens as
> quotes (`<|"|>Tokyo<|"|>`). The parser **sanitizes** them (`StripControlTokens`/`CleanJson`)
> → `"Tokyo"`.

### Streaming: regression in `032334d8`, RESOLVED in `v0.13.1`
`SendMessageStreamingAsync` segfaulted (exit 139) with the interim-commit binary `032334d8` —
the native decode thread crashed BEFORE the first callback (rc=0; `[cb:enter]` never reached).
It was not managed code (worked on `0.12.0-a`), not the WebGPU sampler (#2073), and not
`litert_link_capi_so` (present in both builds): it was a regression in that commit.
**Building from the `v0.13.1` release tag fixes it.** Verified: streaming OK, tools OK,
`get_token_count` now exported (89 funcs), and the streaming→tools sequence in one process
(which used to segfault) passes. Test suite 4/4 on v0.13.1.

> Lesson: pin to a **release tag**, never an arbitrary commit — more stable and it is the sync
> target with Google. `build-native.yml` uses `v0.13.1` by default.

## Desktop GPU backend (WebGPU) — expected behavior

- The `"gpu"` backend on desktop uses **native WebGPU (Dawn)**, NOT a browser. It is a portable
  GPU layer mapping to: **Direct3D 12 on Windows**, Vulkan on Linux, Metal on macOS.
  (On Android: OpenCL/Vulkan.)
- Verified: with `Backend="gpu"` the log selects the discrete GPU (e.g.
  `NVIDIA RTX 3080, backend=Direct3D 12`) and runs the transformer layers on GPU
  (`delegate_webgpu.cc`, `delegate_kernel.cc`). Enabling companions (already shipped):
  `LiteRtWebGpuAccelerator.dll` + `dxcompiler.dll`/`dxil.dll` (DirectX Shader Compiler).
- Seeing "Created TensorFlow Lite XNNPACK delegate for CPU" alongside is normal: non-GPU ops +
  mmap'd embeddings run on CPU (mixed delegation). The bulk (matmuls) runs on GPU. Init is
  slower than CPU (~1.6 s vs ~0.2 s) due to weight upload and kernel compilation.

### GPU sampler falls back to CPU on Windows/macOS — upstream bug (#2073), NOT the binding
- Symptom: `Could not load symbol LiteRtTopKWebGpuSampler_UpdateConfig` →
  `Falling back to CPU sampling`.
- Cause (verified with `dumpbin /exports`): the Windows `LiteRtTopKWebGpuSampler.dll` exports
  **only 3 of 7** functions (`_Create`, `_Destroy`, `_SampleToIdAndScoreBuffer`); missing
  `_UpdateConfig` etc. That is
  [issue #2073](https://github.com/google-ai-edge/LiteRT-LM/issues/2073) (Linux/Android ship
  all 7).
- The fallback message mentions `.so` / `LD_LIBRARY_PATH` / `prebuilt/`: it is a
  **non-localized, Linux-centric log string**; on Windows the equivalent file is the `.dll` we
  already ship. It does not actually try to load a `.so`.
- Impact: **sampling** (token selection) runs on CPU; the **matmuls stay on GPU**. Sampling is
  tiny compared to the matmuls → negligible performance impact. Output is correct either way.
- Definitive fix: our own build or a new prebuilt once #2073 is resolved upstream.

> Note: `I0000 …` logs show up despite `SetMinLogLevel(3)` because they are emitted **before**
> `absl::InitializeLog()` (straight to STDERR); our log level cannot silence them.

## Official shared-library status
- Today the C API ships only as a Bazel `cc_library` (`:engine`, `:engine_cpu`) and
  `add_litertlm_library(... STATIC)` in CMake → there is **no** official shared-lib target.
  Tracking: issue #2154 / PR #2155.
- PoC mitigation was consuming flutter_gemma's `LiteRtLm.dll`/`.so` (verified). Production:
  our own build (docs/native-build.md).
