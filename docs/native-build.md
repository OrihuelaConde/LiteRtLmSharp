# Native binaries: Google's official prebuilts

Since 1.2.0 (LiteRT-LM v0.16.0) the runtime packages ship **Google's official LiteRT-LM C API
prebuilts**, unchanged apart from the file name. Each is one monolithic shared library per platform
with the LiteRT runtime, the GPU accelerators and TopK samplers, the constraint provider and LlGuidance
embedded (static CRT on Windows). Upstream distributes the same libraries through two channels:

- **The C API zip**, `litert_lm_c_api-<version>.zip`, a release asset with the libraries
  (`lib/<platform>/liblitert-lm.{so,dylib}` / `lib/windows_x86_64/bin/litert-lm.dll`), the C headers
  and the per-dependency licenses. Upstream shipped it with v0.16.0 and paused it from v0.17.0 while it
  renames functions (LiteRT-LM#3569).
- **The `litert-lm-api` wheels on PyPI**, published with every release for Windows x64, Linux x64 and
  arm64, macOS arm64 and Android arm64 and x86_64. Each wheel is a ctypes wrapper around the C API
  library (`litert_lm/liblitert-lm.{so,dylib}` / `litert_lm/litert-lm.dll`); the v0.18.0 libraries
  export every function of the v0.18.0 headers. The Windows wheel also carries the DirectX Shader
  Compiler pair, byte-identical to the one this repository pins.

The iOS framework, `CLiteRTLM.xcframework.zip` (device + simulator slices), is a release asset in both
cases.

Until v0.15.0 the natives were built here with Bazel (`build-native.yml` + `native/patch_c_api.sh`,
both in git history) and shipped next to upstream's companion `.so`/`.dll`/`.dylib` files. The
switch was decided after the v0.16.0 evaluation recorded in [`roadmap.md`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/roadmap.md): the same
model-backed suite is green on every platform, the linux-x64 tools + constrained-decoding crash is
gone, and on a real Android device the official library runs GPU, CPU and tool calling where the
self-built v0.15.0 set failed on GPU (upstream's separately shipped sampler lagged behind its own
engine, LiteRT-LM#3135).

## The workflow: `native-release.yml`

GitHub → **Actions** → *Native release (official LiteRT-LM prebuilts)* → **Run workflow**:

- `litertlm_version`: the upstream release tag. The release created here is `native-<tag>`.
- `source`: `auto` (default: the C API zip when the release carries it, else the PyPI wheels), `zip`
  (the C API zip release asset) or `pypi` (the `litert-lm-api` wheels).
- `capi_version`: with `source=zip`, the zip name suffix (`litert_lm_c_api-<capi_version>.zip`,
  `0.1.0` at v0.16.0).
- `pypi_version`: with `source=pypi`, the `litert-lm-api` version; empty takes the tag without its
  leading `v`.
- `platforms`: comma-separated
  (`linux-x64,linux-arm64,win-x64,android-arm64,android-x64,macos-arm64,ios-arm64`) or `all`; an
  unknown name fails the run. The release accumulates assets and merges `checksums.txt` across partial
  runs, and a run that packages only the iOS framework keeps the release's notes.
- `publish_release`: publish the tarballs and `THIRD_PARTY_NOTICES.litert-lm.txt` to the
  `native-<tag>` release. Unchecked, the run only packages and uploads artifacts.

`.github/scripts/fetch-capi-libs.sh` lays out either source as the zip's `lib/<platform>/` tree, so
the packaging steps are the same for both. What each job does, per platform:

| RID | Library (zip path / wheel) | Shipped as | Checks before packaging |
|---|---|---|---|
| linux-x64 | `lib/linux_x86_64/liblitert-lm.so` / `manylinux_*_x86_64` wheel | `libLiteRtLm.so` | x86-64 ELF; ≥ 140 `litert_lm_*` exports incl. the engine/stream entry points; no `libLiteRt*` companion dependency; reports whether the Vulkan loader is a hard dependency |
| linux-arm64 | `lib/linux_arm64/liblitert-lm.so` / `manylinux_*_aarch64` wheel | `libLiteRtLm.so` | AArch64 ELF; same checks as linux-x64 |
| android-arm64 | `lib/android_arm64/liblitert-lm.so` / `android_*_arm64_v8a` wheel | `libLiteRtLm.so` | AArch64 ELF; same export check; **OpenCL TopK sampler embedded** (`LiteRtTopKOpenClSampler_*`); 16 KB page alignment (Google Play) |
| android-x64 | `lib/android_x86_64/liblitert-lm.so` / `android_*_x86_64` wheel | `libLiteRtLm.so` | x86-64 ELF; same checks as android-arm64, except that a missing OpenCL sampler only warns (emulators have no OpenCL) |
| win-x64 | `lib/windows_x86_64/bin/litert-lm.dll` / `win_amd64` wheel | `LiteRtLm.dll` + `dxcompiler.dll` + `dxil.dll` | x64 image; export check; no separate LiteRt import; **no VC++ runtime import** (static CRT); the DXC zip is pinned by sha256 |
| osx-arm64 | `lib/macos_arm64/liblitert-lm.dylib` / `macosx_*_arm64` wheel | `libLiteRtLm.dylib` | arm64; export check; system frameworks only in the load commands |
| ios-arm64 | `CLiteRTLM.xcframework.zip` | `xcframeworks/CLiteRTLM.xcframework` (upstream name kept) | device slice present; export check on the device binary; logs the Metal companions the binary references by name |

Every download is verified against the digest its host publishes: the **sha256 digest GitHub records
for each release asset** (`fetch-upstream-asset.sh`; an asset without a digest is not packaged), or the
**sha256 digest PyPI lists for each wheel** (PyPI carries no provenance attestations for
`litert-lm-api`, so the digest is the integrity check). The ELF checks live in `package-elf.sh`, the
Windows ones in `inspect-pe.py`, both under `.github/scripts/`.

**Third-party notices.** Each runtime package carries `THIRD_PARTY_NOTICES.litert-lm.txt`. The
workflow takes the release's `THIRD_PARTY_NOTICES.txt` asset when there is one (v0.16.0). Releases
without it get a file built by `notices-from-xcframework.py` from the license bundle inside the
release's own `CLiteRTLM.xcframework` (the framework's LICENSE plus one license per third-party
component); the v0.16.0 asset was itself built from the Apple frameworks and covered every platform.
The `native-<tag>` release notes record both sources.

## Platform notes that matter to consumers

- **Linux — no Vulkan loader needed for the CPU backend.** The v0.18.0 libraries carry no hard
  dependency on `libvulkan.so.1` (the v0.16.0 one did: it did not load without `libvulkan1`). The GPU
  backend runs on Vulkan, so it needs the GPU's Vulkan driver and the Vulkan loader.
- **linux-arm64.** The same library built for AArch64 (glibc 2.27+, like the x64 one). CI runs the
  model-backed suite on GitHub's arm64 runner (CPU). Upstream builds its YNNPACK kernels for this
  platform (`LiteRtEngineOptions.EnableYnnpack`).
- **win-x64 — DXC.** The official library's Dawn backend requires the DirectX Shader Compiler on
  Direct3D 12 and has no FXC fallback (without `dxil.dll` the GPU engine fails to create:
  `DynamicLib.Open: dxil.dll Windows Error: 87`). The C API zip ships neither DLL, so the workflow adds
  the pair from Microsoft's DXC release (v1.9.2602, pinned by hash; the exact pair validated on an
  RTX 3080, and the same bytes the Windows wheel carries). No VC++ Redistributable is needed (static
  CRT).
- **android-arm64 — one file.** Accelerators, samplers and the constraint provider are inside
  `libLiteRtLm.so`; the sampler factory still tries `dlopen("libLiteRtTopKOpenClSampler.so")` first
  and then uses its statically linked copy, which is why the runtime logs
  `OpenCL sampler not available, falling back to statically linked C API` on every GPU run — that
  line is expected and sampling stays on the GPU (verified on a Moto G100 / Adreno 650). The
  vendor `libOpenCL.so` still needs the `<uses-native-library>` manifest entry ([android.md](android.md)).
- **android-x64.** The same library built for x86_64, for the Android emulator. Emulators expose no
  OpenCL GPU, so use the CPU backend there.
- **osx-arm64.** The dylib's own install name is `@rpath/liblitert-lm.so` (sic); irrelevant, because
  the resolver loads it by absolute path. The v0.18.0 library requires macOS 14 or later.
- **ios-arm64.** The framework keeps its upstream name (`CLiteRTLM`); the resolver loads
  `Frameworks/CLiteRTLM.framework/CLiteRTLM`. The binary references `libLiteRtMetalAccelerator.dylib`
  and `libLiteRtTopKMetalSampler.dylib` by name and the package ships neither, so iOS is CPU-only
  until a Metal companion strategy is validated on hardware.

## Staying in sync with upstream

The `upstream-watch.yml` workflow opens a checklist issue when a new LiteRT-LM release appears. For
each release:

1. Read the release notes and diff the C API headers (`c/*.h`) against the P/Invoke layer:
   signatures, not only names. The workflow's export-count floor (`MIN_EXPORTS`) catches a truncated
   or wrong-architecture binary, not a changed signature.
2. Run `native-release.yml` with the tag. The default `source=auto` takes the release's
   `litert_lm_c_api-*.zip` asset when there is one and the PyPI wheels otherwise. Inspect the run, then
   run it again with `publish_release` to create the `native-<tag>` release.
3. Bump `LiteRtLmVersion` in `Directory.Build.props`, `NATIVE_REF` in `ci.yml` and `model-tests.yml`,
   and the default in `scripts/restore-natives.ps1`.
4. Run the full model-backed suite locally (CPU and GPU) and let CI run it on every desktop leg.

Each package release records the native tag it ships (release notes + README compatibility table).

## Local restore

```powershell
pwsh scripts/restore-natives.ps1                 # current desktop OS and architecture
pwsh scripts/restore-natives.ps1 -Rid android-arm64,android-x64,ios-arm64
pwsh scripts/restore-natives.ps1 -All
```

The script downloads the release assets over plain HTTPS, verifies them against the release's
`checksums.txt`, and swaps them into `runtimes/<rid>/native/` (iOS: `runtimes/ios-arm64/xcframeworks/`).
Run it from PowerShell: launched from Git Bash, `tar` resolves to MSYS tar, which misreads the
`C:\...` destination as a remote host.
