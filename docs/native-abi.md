# LiteRT-LM C ABI — reference for the .NET binding

> Source of truth: [`c/engine.h`](https://github.com/google-ai-edge/LiteRT-LM/blob/main/c/engine.h)
> in the official repo. This document summarizes the ABI as **verified** against the real binary.

> **Current state (v0.20.0, Google's official C API prebuilts — see [native-build.md](native-build.md)):** the findings about the community binary
> `0.12.0-a` and the interim commit `032334d8` (`conversation_config_create` crash, missing
> `get_token_count`, blocking `send_message` returning null, streaming segfault) are
> **HISTORICAL** — first resolved by compiling our own binaries from the `v0.13.1` tag with the
> matching header (the self-built era, v0.13.1 → v0.15.0), and since v0.16.0 by shipping upstream's
> official prebuilts. The v0.20.0 libraries export 242 `litert_lm_*` functions on ELF and PE across
> seven public headers: `c/engine.h`, `c/conversation.h`, `c/embedding_engine.h`, `c/model_info.h`,
> `c/model_sync.h`, `c/error_reporter.h` and `c/experimental.h`. The Mach-O libraries list 243; the
> reason for the extra export is not known. The binding's coverage is tracked in [`roadmap.md`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/roadmap.md).
>
> v0.20.0 implements C API 1.1.0. Since C API 1.0.0 (LiteRT-LM v0.19.0), most functions return a
> `LiteRtLmStatusCode` and deliver their results through trailing `out_*` parameters. The stream
> callback typedef is the same as in v0.18.0, but the chunk getters (`stream_chunk_get_text`,
> `stream_chunk_is_final`, `stream_chunk_get_error`) return a status and write through out
> pointers. The C snippets on this page show the
> v0.20.0 shapes, except in the sections marked as history.
>
> Today config/system-prompt/sampler, tools, streaming and token count work on every validated platform. Speculative decoding, the benchmark API, and the engine cache-dir setting were
> bound on 2026-06-15; multimodal image/audio messages on 2026-06-17; the tokenizer surface
> (tokenize/detokenize + start/stop tokens) on 2026-06-19; and the v0.14.0 surface (LoRA, CPU thread
> counts, per-send output cap, tool-call streaming, preface rendering, plus the internal sampler-builder
> migration) on 2026-07-10 (see [`roadmap.md`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/roadmap.md) for the C-API coverage count, the
> [v0.14.0 ABI changes](#v0140-abi-changes-sampler-struct--opaque-builder) section below, and the
> [multimodal section](#multimodal-messages-image--audio--verified-wire-format) below). The notes
> below are kept as a diagnostic record.
>
> Caveat (2026-06-12, historical, resolved): with v0.20.0 the constrained tool loop passes in CI on
> linux-x64 and linux-arm64. The crash described here came with the self-built constraint provider,
> and the official prebuilts embed the provider. At the time, "all 5 platforms" for **tools** was
> validated by hand on win-x64/Android; on desktop Linux the tools + constrained-decoding path had never
> actually run in CI (regular CI has no model), and a Linux Mint user reported the process dying silently
> with tools enabled — matches upstream [LiteRT-LM#2149](https://github.com/google-ai-edge/LiteRT-LM/issues/2149)
> (C-API shared lib segfaults/hangs in decode on Ubuntu 24.04; static CLI works). The
> `model-tests.yml` workflow now exercises streaming + tools on linux-x64 with gemma-4-E2B-it (on
> each push, via ci.yml).

## Viability summary (verified)

- The `c/engine.h` header declares a **flat C API** (`extern "C"`) with **opaque pointers** —
  ideal for P/Invoke. ~89 functions at the time of the PoC (the v0.20.0 libraries export 242
  across seven headers), `litert_lm_` prefix.
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

> License note: those binaries are Apache-2.0 (LiteRT-LM) repackaged by flutter_gemma. They were
> used only during the PoC. The self-built natives replaced them, and since 1.2.0 the runtime
> packages ship Google's official prebuilts (see [native-build.md](native-build.md)).

## Minimal flow (Conversation API — high level, recommended)

Handles chat templates internally; mirrors the Gemini Chat APIs via JSON.

Every call below that can fail returns a `LiteRtLmStatusCode` (`kLiteRtLmStatusOk`, 0, on success).
The sample leaves the checks out to stay short. In your code, check each one, and on failure read
`litert_lm_get_last_error_message()` on the same thread (see
[Error reporting](#error-reporting-v0200-cerror_reporterh)).

```c
// 1. Settings
LiteRtLmEngineSettings* s = NULL;
litert_lm_engine_settings_create(model_path, "cpu", NULL, NULL, &s);
litert_lm_engine_settings_set_max_num_tokens(s, 512);          // optional
// 2. Engine (heavy, owns the weights)
LiteRtLmEngine* e = NULL;
litert_lm_engine_create(s, &e);
// 3. Conversation (NULL config = defaults)
LiteRtLmConversation* c = NULL;
litert_lm_conversation_create(e, NULL, &c);
// 4a. Blocking send
LiteRtLmJsonResponse* r = NULL;
litert_lm_conversation_send_message(c, msg_json, NULL, NULL, &r);
const char* out_json = NULL;
litert_lm_json_response_get_string(r, &out_json);              // string owned by r
// 4b. ...or streaming (callback on a library-owned thread)
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
typedef void (*LiteRtLmStreamCallback)(void* callback_data,
                                       const LiteRtLmStreamChunk* chunk);
```
- `chunk` is owned by the library and valid only during the call. Read it with
  `litert_lm_stream_chunk_get_text`, `litert_lm_stream_chunk_get_error` (NULL when the chunk carries
  no error) and `litert_lm_stream_chunk_is_final` (true on the last chunk → signal completion), and
  copy the text out before returning.
- The callback runs on a library-owned thread that the engine's streams share, so keep it short.
  It must not call LiteRT-LM functions on that engine or on its sessions and conversations, with one
  exception: while it handles a chunk that is not final, it may call `litert_lm_session_cancel_process`
  or `litert_lm_conversation_cancel_process`.

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
  (**pre-v0.14.0 shape**; v0.14.0 removed this by-value struct in favor of an opaque builder, see
  [v0.14.0 ABI changes](#v0140-abi-changes-sampler-struct--opaque-builder) below).
- `LiteRtLmSamplerType`: 0 Unspecified, 1 TopK, 2 TopP, 3 Greedy. **v0.14.0 dropped the `Unspecified`
  member** (see below); the public `LiteRtSamplerType.Unspecified` is retained at `0`. Since v0.18.0
  the header declares `kLiteRtLmSamplerTypeUnspecified = 0` again; the binding still sends no sampler
  parameters for it.
- `LiteRtLmInputData` (multimodal; text = UTF-8) is an opaque object:
  `litert_lm_input_data_create(type, data, size, &out_input_data)` copies the bytes into it, and
  `litert_lm_input_data_delete` frees it.
- `LiteRtLmInputDataType`: Text, Image, ImageEnd, Audio, AudioEnd.

## v0.14.0 ABI changes (sampler struct → opaque builder)

The v0.14.0 repin (from v0.13.1, July 2026) grew the C API from 89 to 109 functions (84 bound at the
time). The one **breaking** ABI change the binding depends on:

- **Sampler params: by-value struct → opaque builder.** v0.14.0 removed the by-value `LiteRtLmSamplerParams`
  struct (the pre-v0.14.0 shape above) and replaced it with an opaque builder: `sampler_params_create` +
  `set_top_k` / `set_top_p` / `set_temperature` / `set_seed`, copied into the session config and deleted
  immediately. The binding was rewired to the builder (**required migration**; the by-value struct is gone).
  The native enum also **dropped its `Unspecified` member**; the public `LiteRtSamplerType.Unspecified` is
  retained (value `0`) and now sends **no** sampler params at all (the executor's internal default), which
  is the same effective behavior as v0.13.1, where the unspecified type made the native sampler factory
  defer to the executor. (The old doc claim that the engine resolved `Unspecified` to `TopP` was false.)

**Version match is mandatory.** Because the binding now calls the v0.14.0 builder functions (and the other
16 newly bound entry points), it requires **version-matched v0.14.0+ native binaries**. Run it against
older natives (v0.13.1 or earlier) and the first call into a missing function throws
**`EntryPointNotFoundException`**, the same skew failure mode documented for `get_token_count` on
`0.12.0-a` above. Always restore the `native-v<tag>` release that matches the managed package
(the `LiteRtLmSharp.runtime.<rid>` package version); never mix versions.

**Since C API 1.0.0, the binding checks the version itself.** The `EntryPointNotFoundException` failure
mode no longer protects a mismatch: from C API 1.0.0 on, releases export the same function names with
other signatures, so a mismatched pair would corrupt memory instead of throwing. Right after it loads the
native library, the binding calls `litert_lm_get_api_version`. If the library does not implement C API
1.1 or a later 1.x version, the binding refuses it with a `LiteRtException` that names the package to
install.

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
   cap per-turn output via `session_config_set_max_output_tokens`. With v0.20.0 an overflow behaves
   differently. On win-x64 with gemma-4 E2B and without the binding's context guard:
   - On CPU, a reply stops at `MaxNumTokens` with an OK status, and the next send fails with
     `RESOURCE_EXHAUSTED`. A prefill that does not fit, or that lands in the last free entries, leaves
     every later send failing with `INTERNAL` (`Task failed with state: 7`).
   - On GPU (WebGPU on Direct3D 12, one NVIDIA RTX 3080, Float32 activations), the cases measured
     behaved the same.

   No crash or memory corruption was seen. See [chat.md](chat.md#token-counting-tokenize--detokenize).

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
`conv.SendRaw(json)` as an escape hatch. The parser is tolerant (function.arguments as
object or string; fallback to top-level name/args) and always exposes `RawJson`.

**VALIDATED end-to-end** with our own binary + `gemma-4-E2B-it` (CPU): define tool → model
emits tool-call → execute → re-inject → correct final answer. `conversation_config_create`
no longer crashes (matched header+binary).

> Gemma template quirk: Gemma's template marks strings with the `<|"|>` delimiter token. The parser
> **sanitizes** any delimiters that reach the arguments (`StripControlTokens`/`CleanJson`:
> `<|"|>Tokyo<|"|>` → `"Tokyo"`). With LiteRT-LM v0.20.0 and gemma-4 E2B on win-x64, none did: all 47
> raw tool calls observed had clean JSON arguments, on CPU with and without constrained decoding and on
> GPU with constrained decoding.

## Multimodal messages (image / audio) — verified wire format

Multimodal works on the **high-level Conversation API** — no need for the low-level Session/InputData
path. Two layers:

1. **Engine** — enable the encoders at
   `engine_settings_create(model, backend, vision_backend, audio_backend, &out_settings)`.
   The vision and audio args are `const char*`: `"cpu"`/`"gpu"` to enable that modality, `NULL` to leave it
   off (the documented sentinel — pass a real C# `null`, not `""`). Confirmed by upstream
   `engine_test.cc` `CreateSettingsWithVisionAndAudioBackend` (`vision="gpu"`, `audio="cpu"`).
   **A model can constrain its audio backend.** Gemma 4's audio sub-model requires **CPU**: the model's
   metadata lists CPU as its only audio backend (`LiteRtModelInfo.SupportedBackends`), so the constraint
   belongs to the model, not to a platform. With LiteRT-LM v0.20.0 on win-x64, an engine with audio on
   the GPU backend failed to load with a `LiteRtException`, which the GPU test run records as a skip, and
   the macOS GPU leg of `model-tests.yml` skips the same audio tests. With the
   self-built v0.13.1 library (2026-06-17), `audio_backend="gpu"` made `engine_create` fail with
   `INVALID_ARGUMENT: Audio backend constraint mismatch. Model requires one of [cpu] but Audio backend is
   GPU`. Run audio on CPU for such models even when the main backend is GPU; the vision encoder is
   unconstrained and runs on GPU. (Upstream's own test pairs `vision="gpu"` with `audio="cpu"`.)
   `engine_settings_set_max_num_images(settings, int)` exists but the header says it is **legacy-only**
   (the current engine path ignores it) — bound for completeness; the real per-turn knob is the visual
   token budget below.
2. **Message** — attach media as extra **content parts** in the same `message_json` the text path uses:
   ```json
   {"role":"user","content":[
     {"type":"text","text":"Describe this image: "},
     {"type":"image","blob":"<BASE64 image bytes>"}
   ]}
   ```
   Image and audio parts are interchangeable in two forms (byte-verified against
   `runtime/conversation/model_data_processor/data_utils.cc` `LoadItemData`):
   - `{"type":"image"|"audio","blob":"<base64>"}` — `blob` is **base64** (decoded with
     `absl::Base64Unescape`; a bad string → `InvalidArgumentError("Failed to decode base64 blob.")`).
     The .NET side must `Convert.ToBase64String(bytes)`.
   - `{"type":"image"|"audio","path":"/abs/path"}` — `LiteRtAttachment.ImageFile` and `AudioFile` send
     this form: the binding stores only the path and puts it in the content part. By the parser's design,
     the native side then opens the file itself (memory-mapped, `MemoryMappedFile::Create`), with no
     base64 round-trip, so the path must be readable by the native process.

     With LiteRT-LM v0.20.0, the path forms gave these results:

     | Platform | `ImageFile` | `AudioFile` (MP3) |
     |---|---|---|
     | win-x64, CPU, gemma-4 E2B | Same answers as the byte form | Same answers as the byte form |
     | Android x86_64 emulator, API 35, CPU | Same reply as the byte form, from the app's external files folder and from its internal files folder | Not measured |
     | Android arm64 device | Not measured | Not measured |

   - **No `mime_type`/`mime`/`data`/`image_url` field** exists or is read — the media kind is the `"type"`
     string alone. Content-part **order is preserved and significant** (interleave text/media as seen).
   - **Formats.** The runtime detects the format from the bytes. With LiteRT-LM v0.20.0 (win-x64, CPU,
     gemma-4 E2B), PNG and JPEG images and MP3, WAV and FLAC audio decoded with no hint. WEBP images
     failed with `Failed to decode image. Reason: unknown image type.`, and OGG Vorbis audio failed with
     `Failed to initialize miniaudio decoder, error code: -10`.
3. **Visual token budget** — optional per-send override created via
   `conversation_optional_args_create(&args)` → `conversation_optional_args_set_visual_token_budget(args, int)`
   → passed as the `optional_args` argument (the fourth) of `send_message`/`send_message_stream` →
   `conversation_optional_args_delete(args)`.
   Both sends copy every optional-args field before they return, so the args can be deleted right after
   the call. The binding frees them later, in the streaming iterator's `finally` alongside the callback
   `GCHandle`; deleting them that late is also safe.

> The v0.13.1 `c/engine_test.cc` exercises only **text** messages through the Conversation API (and
> `set_visual_token_budget(args, 100)` with a text-only message), so the image/audio JSON shape is taken
> from the **parser source** (`data_utils.cc`) and the model-specific data processors
> (`gemma4_data_processor.cc` uses an image *and* an audio preprocessor), not from the test file.

Wrapper surface: `LiteRtAttachment.Image/ImageFile/Audio/AudioFile`,
`LiteRtConversation.Send(text, attachments)` /
`SendStreamingAsync(text, attachments, ct)`, `LiteRtEngineOptions.VisionBackend`/`AudioBackend`/
`MaxNumImages`, `LiteRtConversationOptions.VisualTokenBudget`.

> **A multimodal conversation needs a conversation config.** With LiteRT-LM v0.20.0 (win-x64, CPU
> and GPU, gemma-4 E2B), the vision executor loaded for any conversation created with a conversation
> config, with or without a session config in it. A conversation created with no config (`NULL`)
> failed its image send with `INVALID_ARGUMENT: Vision executor should not be null, please
> TryLoadingVisionExecutor() first`, and the next send on it, a text send, failed with `INTERNAL`
> (`Task failed with state: 7`).
>
> The binding creates a conversation config, with a bare session config in it, whenever the engine was
> loaded with `VisionBackend`/`AudioBackend` (`LiteRtConversation.Create` is passed an
> `engineIsMultimodal` flag), so a plain `CreateConversation()` can send attachments. The conversation
> config is what the executor needs; the session config inside it is not. Conversation options such as
> `MaxOutputTokens`, a `Sampler`, a system message or tools also make the binding create a conversation
> config.
>
> **Earlier mis-diagnosis (corrected 2026-06-20).** The 2026-06-17 smoke test blamed `MaxNumTokens`
> ("use ≥ 4096; 2048 fails"). That was a confound — the failing case used a bare conversation (no session
> config) while the working case used the sample's `MaxOutputTokens` cap. A 2×2 over `{2048,4096} ×
> {default, capped}` showed `MaxNumTokens` is irrelevant: 2048 works with a session config, 4096 fails
> without one. (In 0.1.0-preview.2, the binding created no conversation config at all for a bare
> conversation, so "no session config" there also meant no conversation config. 0.1.0-preview.3 added
> the config for multimodal engines.)
>
> **Media floor.** `MaxNumTokens` must also hold the media's tokens, and with LiteRT-LM v0.20.0 the floor
> differs by backend. A 64×64 image message takes 280 tokens on gemma-4 E2B (win-x64; GPU: WebGPU on
> Direct3D 12, one NVIDIA RTX 3080, Float32 activations):
>
> | Backend | `MaxNumTokens = 256` | `384` | `1024` |
> |---|---|---|---|
> | GPU | Rejects the image (280 ≥ 256) | Accepts it | Accepts it |
> | CPU (`VisionBackend = Cpu`) | Engine creation fails | Engine creation fails | Accepts it |
>
> **Wrapped error.** When a send still fails this way (e.g. the model is not multimodal, or
> `VisionBackend`/`AudioBackend` was left unset, or `MaxNumTokens` cannot hold the media), the binding
> throws a managed `LiteRtException` naming those causes, on both the blocking and streaming send paths.
> It flags the message as media-bearing by parsing the content parts only when a send fails, so a normal
> send pays nothing.

**VALIDATED** with LiteRT-LM v0.20.0 and `gemma-4-E2B-it` on win-x64. The model-backed tests assert the
**token delta** (deterministic proof the encoder ran) and log the transcription:

| Media | Backend | Prefill, text only | Prefill with the media |
|---|---|---|---|
| A red PNG | CPU, and GPU (WebGPU on Direct3D 12, one NVIDIA RTX 3080) | 28 tokens | 288 tokens |
| A real spoken 5→0 countdown (`countdown.mp3`) | CPU | 35 tokens | 163 tokens |

Audio runs on CPU because the model's audio sub-model is CPU-constrained: its metadata lists CPU as the
only audio backend, and in the win-x64 GPU run the audio tests skipped because an engine with audio on
the GPU backend failed to load (the macOS GPU leg skips them too). (The first validation, on 2026-06-17 with a
self-built binary, started with a synthetic sine tone, which yielded a canned "I cannot process audio";
a real clip transcribes correctly, so the fixture is an embedded `countdown.mp3`.)

### Streaming: regression in `032334d8`, RESOLVED in `v0.13.1`
`SendMessageStreamingAsync` segfaulted (exit 139) with the interim-commit binary `032334d8` —
the native decode thread crashed BEFORE the first callback (rc=0; `[cb:enter]` never reached).
It was not managed code (worked on `0.12.0-a`), not the WebGPU sampler (#2073), and not
`litert_link_capi_so` (present in both builds): it was a regression in that commit.
**Building from the `v0.13.1` release tag fixes it.** Verified: streaming OK, tools OK,
`get_token_count` now exported (89 funcs), and the streaming→tools sequence in one process
(which used to segfault) passes. Test suite 4/4 on v0.13.1.

> Lesson: pin to a **release tag**, never an arbitrary commit (more stable, and it is the sync
> target with Google). `native-release.yml` defaults to the current pin (`v0.20.0`, with `source=auto`).

## Tokenizer (tokenize / detokenize / start-stop tokens) — verified

The engine exposes the model's own tokenizer, so exact token counts can be computed without running
inference (16 functions, all bound). The shape is opaque-pointer + getters, freed by the matching
`*_delete`; the `const int*` / `const char*` getters point INTO the owning object, so the .NET side
copies the data out before disposing the handle.

```c
// Each call returns a LiteRtLmStatusCode; pointer results are NULL on failure.
LiteRtLmTokenizeResult* t = NULL;
litert_lm_engine_tokenize(engine, "Hello", &t);
size_t n = 0;
litert_lm_tokenize_result_get_num_tokens(t, &n);
const int* ids = NULL;
litert_lm_tokenize_result_get_tokens(t, &ids);                  // valid while t lives
LiteRtLmDetokenizeResult* d = NULL;
litert_lm_engine_detokenize(engine, ids, n, &d);                // inverse
const char* text = NULL;
litert_lm_detokenize_result_get_string(d, &text);              // UTF-8, owned by d
litert_lm_detokenize_result_delete(d);
litert_lm_tokenize_result_delete(t);
```

- **Start (BOS) / stop (EOS) tokens.** `litert_lm_engine_get_start_token` yields a `LiteRtLmTokenUnion*`
  that the caller deletes with `litert_lm_token_union_delete` (NULL when the engine has no start token);
  `litert_lm_engine_get_stop_tokens` yields a `LiteRtLmTokenUnions*` collection. A `TokenUnion` is a
  tagged value: `litert_lm_token_union_get_type` is `kLiteRtLmTokenUnionTypeString` (read
  `..._get_string`) or `kLiteRtLmTokenUnionTypeIds` (read `..._get_ids(out_tokens, out_num)` → returns
  `kLiteRtLmStatusOk`, 0, on success). **Ownership trap:** since C API 1.0.0, the pointer that
  `litert_lm_token_unions_get_token_at` yields is borrowed from the collection. Don't pass it to
  `litert_lm_token_union_delete`: that is a double free. (Before C API 1.0.0 it was a copy that the
  caller deleted.)
- .NET surface: `LiteRtEngine.Tokenize(string) → int[]`, `Detokenize(ReadOnlySpan<int>) → string`,
  `GetStartToken() → LiteRtTokenUnion?`, `GetStopTokens() → IReadOnlyList<LiteRtTokenUnion>`
  (`LiteRtTokenUnion.Kind`/`Text`/`Ids`). **VALIDATED** on win-x64 CPU with `gemma-4-E2B-it` (2026-06-19):
  text round-trips through ids, counts are deterministic and monotone, and the model reports a non-empty
  set of stop tokens (start = id `[2]`, stops = ids `[1]`/`[50]`/`[106]`).
- **Tokenize adds no chat template, and Detokenize returns plain text.** With LiteRT-LM v0.20.0
  (win-x64, CPU, gemma-4 E2B), "The quick brown fox." tokenizes to 5 ids, with no BOS or turn markers,
  and detokenizing them gives back the same string with ordinary spaces (U+0020). The binding passes the
  native string through verbatim.

## Desktop GPU backend (WebGPU) — expected behavior

- The `"gpu"` backend on desktop uses **native WebGPU (Dawn)**, NOT a browser. It is a portable
  GPU layer mapping to: **Direct3D 12 on Windows**, Vulkan on Linux, Metal on macOS.
  (On Android: OpenCL/Vulkan.)
- Verified: with `Backend="gpu"` the log selects the discrete GPU (e.g.
  `NVIDIA RTX 3080, backend=Direct3D 12`) and runs the transformer layers on GPU
  (`delegate_webgpu.cc`, `delegate_kernel.cc`). The WebGPU accelerator is embedded in the official
  library; the only companions are `dxcompiler.dll`/`dxil.dll` (DirectX Shader Compiler, loaded lazily
  by Dawn at the first shader compile), which the win-x64 runtime package ships.
- Seeing "Created TensorFlow Lite XNNPACK delegate for CPU" alongside is normal: non-GPU ops +
  mmap'd embeddings run on CPU (mixed delegation). The bulk (matmuls) runs on GPU. Init is
  slower than CPU (~1.6 s vs ~0.2 s, measured with the community `0.12.0-a` build of LiteRT-LM) due to
  weight upload and kernel compilation.

### GPU sampler falls back to CPU on Windows/macOS — upstream bug (#2073), NOT the binding

> Historical (self-built era, v0.13.1 → v0.15.0). The official v0.16.0 monolith embeds the TopK
> samplers, so this fallback no longer occurs; an explicit TopK sampler works on desktop GPU.
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

> Note: call `LiteRtEngine.SetMinLogLevel` before the first engine is created. With LiteRT-LM v0.20.0
> (win-x64, CPU, gemma-4 E2B), level 3 then printed no `I0000 …` lines, though `W0000` and `WARNING`
> lines still appeared, and level 1000 printed nothing.

## Error reporting (v0.20.0, `c/error_reporter.h`)

Every v0.20.0 function that can fail returns its `LiteRtLmStatusCode` (`kLiteRtLmStatusOk`, 0, on
success), and the error code getter of v0.18.0, `litert_lm_get_last_error_code`, is gone. The reason
stays in errno-style, **thread-local** state. `litert_lm_get_last_error_message()` returns it as a
library-owned string, typically the full absl status text, such as `INVALID_ARGUMENT: Invalid magic
number or failed to read`. `litert_lm_clear_last_error()` resets it. The contract shapes how the
binding reads it (`NativeError`):

- The state is set **only on failure**: a later successful call does not clear it, and every failure
  overwrites it. The binding reads it only after a call that failed, so it doesn't clear it before a call.
- Failure is decided by the status code the call returns, never by the report.
- The report is read on the **same thread**, right after the failing call, and then cleared, which also
  frees the message on long-lived thread-pool threads. The blocking sends that the async APIs move to
  the thread pool move there as a whole, read included.

The binding appends the runtime's reason to `LiteRtException` (dropping the code prefix the message
repeats) and exposes the status as `LiteRtException.StatusCode` (`LiteRtStatusCode`). Errors raised
through LiteRT's status macros arrive as a multi-line call trace (one `ERROR: [path:line]` per level, the
reason, when there is one, on the last line); the binding rewrites it to one line, the reason first and
then the innermost location. Measured messages: an invalid model file reports `INVALID_ARGUMENT: Invalid
magic number or failed to read`; a missing LoRA adapter `UNKNOWN: Failed to open: <path>`; an image sent
to an engine without a vision backend `INVALID_ARGUMENT: Vision executor should not be null, please
TryLoadingVisionExecutor() first`; a missing embedding cache directory `INVALID_ARGUMENT: Cache directory
does not exist or is not writable: <dir> (at embedding_engine_settings.cc:346)`; and `EnableYnnpack` on a
library without YNNPACK kernels `UNIMPLEMENTED` with only a trace (`no details, failed at
compiled_model.cc:<line>`). A failure during a streamed reply arrives instead as the stream chunk's error
string, the same `CODE: reason` text; the binding parses the code back and builds the same exception,
with the status, the one-line reason and the same guidance. With the native log silenced (a level above
5, such as 1000), LiteRT's status macros record the status without the reason (the text is the bare
`INVALID_ARGUMENT: `); the exception then keeps the status and says why the reason is missing.

## Embedding engine (v0.20.0, `c/embedding_engine.h`) — verified

The embedding engine is a separate object from `LiteRtLmEngine`, with its own settings, options and
response types. The binding gives it its own handles (`EmbeddingEngineHandle` and friends), outside the
one-live-engine gate: a chat engine and an embedding engine coexist, verified on CPU and GPU (win-x64),
including an embedding computed on another thread while the chat engine streams.

Each function below that can fail returns a status and writes its result through a trailing `out_*`
parameter; the signatures in this list leave that parameter out.

- **Create:** `litert_lm_embedding_engine_settings_create(model_path, backend, vision_backend,
  audio_backend)`. The setters (`set_cache_dir`, `set_num_threads`, `set_activation_data_type`,
  `set_max_input_length`, `set_min_input_length`) apply before
  `litert_lm_embedding_engine_create(settings)`, and the settings can be deleted right after it. A cache
  directory that does not exist fails the create (`Cache directory does not exist or is not writable`,
  at the end of a call trace); the binding checks the directory first, to name it and point to
  `LiteRtCache.Default`.
- **Encoders:** the binding passes `NULL` for the vision and audio backends, which the C layer reads as
  "the main backend for any encoder the bundle carries". A bundle with encoders still loads them, and the
  binding cannot feed them. With LiteRT-LM v0.20.0 (win-x64, CPU), EmbeddingGemma 2 Text Vision 440M
  loaded its vision encoder for text-only use and returned the same text vectors, bit for bit, as the
  text-only 270M bundle. The 440M bundle carries no audio encoder. The 740M multimodal bundle was not
  measured.
- **Input:** `litert_lm_input_data_create(kLiteRtLmInputDataTypeText, bytes, size)` (declared in
  `c/engine.h`) copies the UTF-8 bytes into the input, so the managed buffer can be released right
  after the call; the caller deletes the input with `litert_lm_input_data_delete`.
- **One embedding:** `litert_lm_embedding_engine_compute_embedding(engine, inputs, num_inputs, options)`
  combines **all** `inputs` into **one** embedding (the multimodal case: text plus an image), so one text
  is one input. It returns a response the caller deletes; `response_get_values` points into the response
  (copy the floats out before deleting it) and `response_get_size` gives their count.
- **Batch:** `compute_embedding_batch(engine, inputs_batch, num_inputs_per_batch, batch_size, options)`
  takes, per request, an array of inputs and its length; the binding builds one single-input request per
  text. The returned collection owns its responses: `responses_get_at` returns a borrowed pointer, and
  only `responses_delete` frees them. The runtime processes the requests one after another
  (`EmbeddingEngineImpl::ComputeEmbeddingBatch` is a loop), so a batch saves call overhead but not
  compute, and the first failing request fails the call.
- **Options:** `NULL` options mean the defaults: normalized, special tokens inserted, overflow strategy
  `Error` (`kInvalidArgument` for a text longer than the loaded signatures), full output size.
  `set_output_size` truncates (Matryoshka) and the runtime normalizes after truncating.
- **Input length:** without `set_max_input_length`, the engine takes the bounds from the model's
  `EmbeddingMetadata` (`max_input_length: 1024` for EmbeddingGemma 2) and loads the smallest text
  signature that holds the maximum plus the shorter ones. Verified: the default and an explicit 1024
  accept the same 1,022 one-token words (plus BOS and EOS), and 1025 already loads the 2,048 signature.
  The C API does not expose the declared bounds.
- **Activations:** precedence is the explicit setting, then the model's preferred type, then float16 on
  GPU. CPU runs float32 by default.
- **Errors** go through the error reporter: a text over the limit under `Error` reports
  `INVALID_ARGUMENT`.

## Model metadata (v0.20.0, `c/model_info.h`) — verified

`litert_lm_loaded_file_create(path, &out_loaded_file)` opens a `.litertlm` file for capability queries
without creating an engine, and `loaded_file_delete` closes it. For a file it cannot open, the create
returns a failure status, with a report, and a `NULL` file.

Every query returns a status and writes its value through an out parameter. For what the file does not
declare, some queries return `NOT_FOUND`; the binding maps that status to the value these queries
returned before C API 1.0.0 (`false`, `0` or `-1`). The other queries succeed with `false`, `0` or
`NULL` in that case. Most files declare little (see [chat.md](chat.md#read-a-models-metadata)).

The list queries (`modality_supported_backends`, `vision_signature_selection`,
`embedding_signature_selection`) follow a two-call pattern: a `NULL` buffer yields the count, then a
buffer of that size is filled.
`LiteRtLmBackendType` starts at 1 (`Cpu = 1`, `Gpu = 2`, `Npu = 3`), unlike the backend strings of the
engine settings; the binding maps the three known values and skips any newer one.
`min_runtime_version` yields a string owned by the file, or `NULL`. The NPU queries (`npu_brand`,
`soc_name`) are not bound.

## Session API (v0.20.0, C API 1.1.0, `c/engine.h`)

A session prefills and decodes raw text on an engine, without the conversation layer's template, history, tool
parsing, or reasoning channels. The declarations are in `Native/LiteRtLmNative.Session.cs`; the session config
functions that conversations also use, and `input_data_create`/`_delete`, are declared with those features.
The public surface is `LiteRtSession` (see [sessions.md](sessions.md)).

| Function | Used by | Notes |
|---|---|---|
| `engine_create_session` | `LiteRtEngine.CreateSession` | The session holds a reference on the native engine until it is deleted. |
| `session_config_set_apply_prompt_template` | `LiteRtEngine.CreateSession` | Always `false`: the text reaches the tokenizer as given. |
| `session_run_prefill` | `Prefill`, `PrefillAsync` | One text input per call; blocking. |
| `session_run_decode_async` | `Decode`, `DecodeAsync`, `DecodeStreamingAsync` | Every decode takes this path; the final chunk settles it. |
| `session_cancel_process` | Cancel from a token, a stream left early, or `Dispose` | Called from the stream callback on a chunk that is not final. Afterwards the session is only deleted. |
| `session_delete` | `Dispose` (through the session handle) | Never from a stream callback, and always before the engine. |
| `session_save_checkpoint` | `SaveCheckpoint` (experimental) | The binding passes a NUL-terminated ASCII label that never repeats on a session; the runtime copies it. |
| `session_rewind_to_checkpoint` | `RewindTo` (experimental) | `NOT_FOUND` (`Checkpoint not found: <label>`) for a dropped checkpoint becomes `InvalidOperationException`, and the session is unchanged. |
| `session_rewind_to_step` | `Reset` (experimental) | Called only with step 0. The C API reports no current step. |
| `session_run_decode` | Declared only | Blocking decode, kept for the export check and a regression test that compares its reply with the async decode's. |
| `session_run_text_scoring` | Declared only | Not wrapped: after a save, a score of a one-token target, and a rewind, the next decode fails with `INVALID_ARGUMENT: Unprocessed token is null.` and the session is unusable (CPU and GPU). |
| `responses_delete`, `responses_get_num_candidates`, `responses_get_response_text_at`, `responses_get_score_at`, `responses_get_token_length_at` | Declared only | The responses object of the blocking decode and of scoring. |

The header's threading and lifetime rules apply: calls on one session must not overlap (the binding refuses a
second call with `InvalidOperationException`), a stream callback may call only `session_cancel_process`, and
every session is deleted before its engine. After a failed prefill the runtime returned OK for later prefills and
decodes on the session, but a streamed decode then delivered no chunk in 30 s and a blocking one returned an
empty text. The
binding refuses those calls. After a prefill whose input did not fit, a rewind or `rewind_to_step(0)` recovered
the session; after other prefill failures the binding accepts both, but that was not measured. Measured on
LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and GPU.

## Model sync (v0.20.0, `c/model_sync.h`) — verified on win-x64 and an Android x86_64 emulator

`c/model_sync.h` declares 19 functions, all added in C API version 1.1.0, and the binding declares all 19
(`Native/LiteRtLmNative.ModelSync.cs`). The exports were checked by name in the v0.20.0 library of every
packaged platform. The calls were measured on win-x64 and, in part, on an Android x86_64 emulator; on
linux-x64, linux-arm64 and osx-arm64, only the repository's CI tests run them. The public API (`LiteRtModelSync`, see
[model-sync.md](model-sync.md)) calls 15 of them:

- **Options:** `options_create`, `options_delete`, `options_set_full_download_threshold`,
  `options_set_clean`, `options_set_check_disk_space`. Every call creates options and sets all three
  values instead of passing `NULL`, pinned to the native defaults (threshold 0.80, `clean` off, disk check
  on), so a change of those defaults changes nothing in the binding.
- **Plan:** `plan_create_from_file`, `plan_delete`, `plan_is_up_to_date`, `plan_get_bytes_to_download`,
  `plan_get_bytes_to_reuse`, `plan_get_total_remote_size`, `plan_format`. The summary string belongs to the
  plan, so the binding copies it before it deletes the plan.
- **Update:** `update`.
- **Range writer:** `range_writer_write`, `range_writer_set_error`, called from the fetcher callback.

The other four, `options_set_allow_offline_fallback`, `cache_sync`, `result_delete` and
`result_get_cached_path`, belong to the cache manager. They are declared and covered by the C-level ABI
tests, with no managed caller.

Contract, as the binding relies on it:

- **Fetcher callback** (`LiteRtLmModelSyncRangeFetcherCallback`, cdecl
  `(uint64_t offset, uint64_t length, writer*, void* user_data)`): the runtime calls it synchronously on the
  thread that called `plan_create_from_file` or `update`, and never after that call returns. The writer is
  valid only during the callback, and the binding calls it only on the callback thread. No request has
  length 0, and every request is closed and bounded.
- **Requests:** the header is read with `[0, 16384)`, then the rest of a longer header. A whole-file fetch
  asks for `[header_end, size)`; only when that request is refused does the runtime ask for `[0, size)`.
- **Statuses:** the status that the callback returns is the call's status, and a value outside 1 to 16
  becomes `UNKNOWN`. `FAILED_PRECONDITION`, `UNIMPLEMENTED` and `OUT_OF_RANGE` make `update` fall back to a
  full download only for fetches after the header; returned from a header fetch, they fail the plan or the
  update. The binding returns `UNIMPLEMENTED` only for a remote that can't serve a range, never for an
  error. The runtime doesn't retry a failed fetch.
- **`set_error`:** the UTF-8 text appears in the last error after the status and the phase, as in
  `CANCELLED: Failed to fetch section payload: <text>`. `NULL` restores the default text.
- **Byte counts:** the runtime rejects short and long section writes and a short whole-file write with
  `DATA_LOSS`, but accepts a whole-file write past `length` and leaves a longer, corrupt output. The binding
  never passes the runtime a byte past `length`.
- **Verification:** the runtime verifies each downloaded section that the remote header hashes, and nothing
  for a remote without section hashes.
- **Paths (Windows):** the runtime decodes every path in the active ANSI code page (UTF-8 bytes fail on code
  page 1252), and in a process that isn't long-path aware, plain paths of 260 characters or more fail. The
  binding passes ANSI bytes, refuses characters outside the code page, and switches every path of a call to
  `\\?\` from 260 characters.
- **In place:** the runtime compares the two path strings. An up-to-date update whose output is another
  spelling of the local file fails with `INTERNAL` ("File exists"), so the binding passes the same bytes for
  both when the full paths are equal.
- **Replace (Windows):** `update` writes `OUTPUT.tmp_updating` and renames it over the output at the end.
  While the output or that temporary file is open, for example by a live engine, by a
  `litert_lm_loaded_file_create` that `LiteRtModelInfo.Read` makes, or by a second `update` of the same output
  in another process, the rename fails with `INTERNAL` ("Failed to rename temporary model to destination")
  after the download. The binding refuses an output that a live engine of the process loaded, and adds a hint
  for that reason.

## Official shared-library status
- **Upstream published official C API prebuilts with v0.16.0** (`litert_lm_c_api-<version>.zip` for
  linux/windows/macos/android, `CLiteRTLM.xcframework.zip` for iOS), and 1.2.0 ships those. The zip is
  paused (LiteRT-LM#3569): v0.17.x and v0.18.0 shipped only the Apple xcframeworks, and v0.20.0 carries
  no C API zip either. The v0.20.0 natives therefore come from the same official build in the
  `litert-lm-api` wheels on PyPI; see [native-build.md](native-build.md). The self-built target (`native/patch_c_api.sh`, v0.13.1 →
  v0.15.0) is retired.
- Before that: as of v0.14.0 upstream had its own `cc_binary litert-lm` in `c/BUILD` (the
  Python-wheel build); earlier tags shipped only the Bazel `cc_library` (`:engine`, `:engine_cpu`)
  and `add_litertlm_library(... STATIC)` in CMake, with no shared-lib target (issue #2154 / PR #2155).
  The PoC consumed flutter_gemma's `LiteRtLm.dll`/`.so`.
