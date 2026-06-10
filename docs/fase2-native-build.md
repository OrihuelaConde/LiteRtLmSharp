# Fase 2 — Build nativo propio (CI)

Objetivo: dejar de depender de binarios de la comunidad y **compilar nosotros** la shared library del
C API de LiteRT-LM, pinneada a un ref de Google, con el **header emparejado** (lo que elimina el
ABI-skew que vimos: `litert_lm_conversation_config_create` crasheando y `get_token_count` ausente).

## Piezas

- **`native/patch_c_api.sh`** — añade a `c/BUILD` un `cc_binary(linkshared=True)` (`//c:libLiteRtLm.dylib`)
  sobre `//c:engine`, porque upstream aún no expone target de shared lib ([#2154](https://github.com/google-ai-edge/LiteRT-LM/issues/2154)).
  Genera `windows_exports.def` automáticamente desde `c/engine.h` (export list siempre en sync con el ref).
- **`.github/workflows/build-native.yml`** — compila `libLiteRtLm.so` (linux-x64) y `LiteRtLm.dll` (win-x64),
  trae los companions de aceleración desde `prebuilt/<plataforma>/` (git-lfs), baja el runtime DXC en Windows,
  sube artifacts y, opcionalmente, publica un Release `native-<ref>` en este repo.

## Cómo correrlo

GitHub → pestaña **Actions** → *Build LiteRT-LM native libraries* → **Run workflow**:
- `litertlm_version`: ref a compilar. Default: **`v0.13.1`** (siempre pinear a un **tag de release**,
  no a commits sueltos — el commit interino `032334d8` nos costó un segfault de streaming).
- `platforms`: lista separada por comas (`linux-x64,win-x64,android-arm64,macos-arm64,ios-arm64`) o
  `all`. **Construir solo plataformas nuevas** ahorra minutos (los runners macOS facturan 10x en
  repos privados); el release **acumula** assets y mergea `checksums.txt` entre runs parciales.
- `publish_release`: si se marca, publica los `.tar.gz` + checksums como Release `native-<ref>`.

Notas por plataforma:
- **Desktop (linux/win)**: `litert_link_capi_so=true` (libLiteRt separada). Windows además
  `resolve_symbols_in_exec=false`.
- **Android**: sin `litert_link_capi_so` (LiteRt estática), page-size 16 KB, y **patchelf** sobre los
  samplers TopK (`--add-needed libLiteRtLm.so`, upstream #2211).
- **macOS/iOS**: como Android (LiteRt estática; `litert_link_capi_so` dispara un select ambiguo en
  @litert); companions Metal; iOS además parchea `minos` con vtool cuando >16.

Tras publicar, apuntar `scripts/restore-natives.ps1` (`$version` y, si se quiere, la URL base) a **nuestro**
release en vez del de flutter_gemma.

## Flags clave (no tocar sin entender)

- `--define=litert_link_capi_so=true`: mantiene `libLiteRt` como shared lib **separada** (si no, se linkea
  estático y choca con el acelerador WebGPU prebuilt → doble copia de TFLite).
- Windows además: `--define=resolve_symbols_in_exec=false` (si no, externals sin resolver al linkear).
- Windows usa `win_def_file` (la `.def`); en Linux se usa `--dynamic-list` con wildcards `LiteRt*`/`litert_lm_*`.

## Sincronía con Google

Versionado: `LiteLMSharp x.y.z` ↔ tag de LiteRT-LM consumido (documentar en CHANGELOG). Un tag nuevo de
upstream → re-correr el workflow con ese tag → publicar release → bump del paquete. (Futuro: un workflow
"watcher" que abra PR al salir un tag nuevo.)

## Riesgos conocidos / qué validar en el primer run (no testeable localmente)

- **Bazel**: build de 30–90 min; depende de toolchain (clang-17 en linux, MSVC en windows) y `prebuilt/` por LFS.
- **Windows**: long paths + `--output_base=D:\b`; el build de Bazel en Windows es el más frágil.
- **Drift del patch**: si se pinnea a un ref donde `c/BUILD` cambió de forma incompatible, ajustar el patch.
- **MSVC runtime**: `LiteRtLm.dll` importa `MSVCP140/VCRUNTIME140*` — presentes con VS; para usuarios finales
  habrá que depender del VC++ Redistributable o shippearlos (decisión de empaquetado, Fase 2 NuGet).
- Tras el primer build, **regenerar/verificar** el P/Invoke contra el `c/engine.h` del ref pinneado.
