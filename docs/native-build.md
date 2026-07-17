# Native build (CI)

Goal: never depend on community-built binaries — **we compile** the LiteRT-LM C API shared
library ourselves, pinned to a Google ref, with the **matching header** (which eliminates the
ABI skew we once hit: `litert_lm_conversation_config_create` crashing and `get_token_count`
missing).

## Pieces

- **`native/patch_c_api.sh`** — adds a `cc_binary(linkshared=True)` target (`//c:libLiteRtLm.dylib`)
  over `//c:engine` to `c/BUILD`, because upstream does not expose a shared-lib target yet
  ([#2154](https://github.com/google-ai-edge/LiteRT-LM/issues/2154)). It generates
  `windows_exports.def` automatically from `c/engine.h` (the export list always stays in sync
  with the pinned ref).
- **`.github/workflows/build-native.yml`** — builds `libLiteRtLm.so` / `LiteRtLm.dll` per
  platform, pulls the acceleration companions from `prebuilt/<platform>/` (git-lfs), downloads
  the DXC runtime on Windows, uploads artifacts and, optionally, publishes a `native-<ref>`
  Release on this repo.

## How to run it

GitHub → **Actions** tab → *Build LiteRT-LM native libraries* → **Run workflow**:
- `litertlm_version`: ref to build. Default: **`v0.14.0`** (always pin to a **release tag**,
  never a loose commit — the interim commit `032334d8` cost us a streaming segfault).
- `platforms`: comma-separated list (`linux-x64,win-x64,android-arm64,macos-arm64,ios-arm64`)
  or `all`. **Build only the platforms you need** — the release **accumulates** assets and
  merges `checksums.txt` across partial runs, so there is no reason to rebuild existing ones.
- `publish_release`: when checked, publishes the `.tar.gz` files + checksums as the
  `native-<ref>` Release.

Per-platform notes:
- **Desktop (linux/win)**: `litert_link_capi_so=true` (separate libLiteRt). Windows also needs
  `resolve_symbols_in_exec=false`.
- **Android**: no `litert_link_capi_so` (LiteRt linked statically), 16 KB page size, and
  **patchelf** over the TopK samplers (`--add-needed libLiteRtLm.so`, upstream #2211).
- **macOS/iOS**: like Android (static LiteRt; `litert_link_capi_so` triggers an ambiguous
  select in @litert); Metal companions; iOS additionally patches `minos` with vtool when >16.

## Key flags (do not touch without understanding them)

- `--define=litert_link_capi_so=true`: keeps `libLiteRt` as a **separate** shared lib (without
  it, LiteRt links statically and clashes with the prebuilt WebGPU accelerator → two copies of
  TFLite).
- Windows also: `--define=resolve_symbols_in_exec=false` (otherwise unresolved externals at
  link time).
- Windows uses `win_def_file` (the generated `.def`); Linux uses `--dynamic-list` with
  `LiteRt*`/`litert_lm_*` wildcards.

## Staying in sync with upstream

A new upstream tag → re-run the workflow with that tag → publish the `native-<ref>` release →
bump `LiteRtLmVersion` (and the package version) in `Directory.Build.props`. The
`upstream-watch.yml` workflow opens a checklist issue automatically when a new LiteRT-LM
release appears. Each package release records the native tag it was built against (release
notes + README compatibility table).

## Known risks / what to validate on a first run (not testable locally)

- **Bazel**: 30–90 min builds; depends on the toolchain (clang-17 on linux, MSVC on windows)
  and `prebuilt/` via LFS.
- **Windows**: long paths + `--output_base=D:\b`; the Bazel build on Windows is the most
  fragile one.
- **Patch drift**: if pinned to a ref where `c/BUILD` changed incompatibly, adjust the patch.
- **MSVC runtime**: `LiteRtLm.dll` imports `MSVCP140/VCRUNTIME140*` — present with VS; end
  users need the VC++ Redistributable (documented in the README).
- After a first build for a new ref, **verify the P/Invoke layer** against that ref's
  `c/engine.h`.
