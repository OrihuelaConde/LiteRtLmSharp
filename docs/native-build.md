# Native binaries: Google's official prebuilts

Since 1.2.0 (LiteRT-LM v0.16.0) the runtime packages ship **Google's official LiteRT-LM C API
prebuilts**, unchanged apart from the file name. Upstream publishes them on every release:

- `litert_lm_c_api-<version>.zip` — one monolithic shared library per platform
  (`lib/<platform>/liblitert-lm.{so,dylib}` / `bin/litert-lm.dll`) with the LiteRT runtime, the GPU
  accelerators and TopK samplers, the constraint provider and LlGuidance embedded; static CRT on
  Windows. The zip also carries the C header and the per-dependency licenses.
- `CLiteRTLM.xcframework.zip` — the iOS framework (device + simulator slices).

Until v0.15.0 the natives were built here with Bazel (`build-native.yml` + `native/patch_c_api.sh`,
both in git history) and shipped next to upstream's companion `.so`/`.dll`/`.dylib` files. The
switch was decided after the v0.16.0 evaluation recorded in [`roadmap.md`](roadmap.md): the same
model-backed suite is green on every platform, the linux-x64 tools + constrained-decoding crash is
gone, and on a real Android device the official library runs GPU, CPU and tool calling where the
self-built v0.15.0 set failed on GPU (upstream's separately shipped sampler lagged behind its own
engine, LiteRT-LM#3135).

## The workflow: `native-release.yml`

GitHub → **Actions** → *Native release (official LiteRT-LM prebuilts)* → **Run workflow**:

- `litertlm_version`: the upstream release tag (default `v0.16.0`). The release created here is
  `native-<tag>`.
- `capi_version`: the zip name suffix (`litert_lm_c_api-<capi_version>.zip`, `0.1.0` at v0.16.0).
- `platforms`: comma-separated (`linux-x64,win-x64,android-arm64,macos-arm64,ios-arm64`) or `all`.
  The release accumulates assets and merges `checksums.txt` across partial runs.
- `publish_release`: publish the tarballs (and upstream's `THIRD_PARTY_NOTICES.litert-lm.txt`) to
  the `native-<tag>` release. Unchecked, the run only packages and uploads artifacts.

What each job does, per platform:

| RID | Source in the zip | Shipped as | Checks before packaging |
|---|---|---|---|
| linux-x64 | `lib/linux_x86_64/liblitert-lm.so` | `libLiteRtLm.so` | ≥ 140 `litert_lm_*` exports incl. the engine/stream entry points; no `libLiteRt*` companion dependency; warns if the `libvulkan.so.1` dependency ever disappears |
| android-arm64 | `lib/android_arm64/liblitert-lm.so` | `libLiteRtLm.so` | same export check; **OpenCL TopK sampler embedded** (`LiteRtTopKOpenClSampler_*`); 16 KB page alignment (Google Play) |
| win-x64 | `lib/windows_x86_64/bin/litert-lm.dll` | `LiteRtLm.dll` + `dxcompiler.dll` + `dxil.dll` | x64 image; export check; no separate LiteRt import; **no VC++ runtime import** (static CRT); the DXC zip is pinned by sha256 |
| osx-arm64 | `lib/macos_arm64/liblitert-lm.dylib` | `libLiteRtLm.dylib` | arm64; export check; system frameworks only in the load commands |
| ios-arm64 | `CLiteRTLM.xcframework.zip` | `xcframeworks/CLiteRTLM.xcframework` (upstream name kept) | device slice present; export check on the device binary; logs the Metal companions the binary references by name |

Every upstream download is verified against the **sha256 digest GitHub records for that release
asset** (`.github/scripts/fetch-upstream-asset.sh`); an asset without a digest is not packaged.
The inspection helpers live in `.github/scripts/` (`assert-elf-exports.sh`, `inspect-pe.py`).

## Platform notes that matter to consumers

- **linux-x64 — Vulkan loader.** The official library has a hard `DT_NEEDED libvulkan.so.1`: without
  the loader it does not load at all, CPU backend included. CI installs `libvulkan1`; the README lists
  it as a prerequisite and the resolver's load-failure message names it.
- **win-x64 — DXC.** The official library's Dawn backend requires the DirectX Shader Compiler on
  Direct3D 12 and has no FXC fallback (without `dxil.dll` the GPU engine fails to create:
  `DynamicLib.Open: dxil.dll Windows Error: 87`). The zip ships neither DLL, so the workflow adds
  the pair from Microsoft's DXC release (v1.9.2602, pinned by hash; the exact pair validated on an
  RTX 3080). No VC++ Redistributable is needed any more (static CRT).
- **android-arm64 — one file.** Accelerators, samplers and the constraint provider are inside
  `libLiteRtLm.so`; the sampler factory still tries `dlopen("libLiteRtTopKOpenClSampler.so")` first
  and then uses its statically linked copy, which is why the runtime logs
  `OpenCL sampler not available, falling back to statically linked C API` on every GPU run — that
  line is expected and sampling stays on the GPU (verified on a Moto G100 / Adreno 650). The
  vendor `libOpenCL.so` still needs the `<uses-native-library>` manifest entry ([android.md](android.md)).
- **osx-arm64.** The dylib's own install name is `@rpath/liblitert-lm.so` (sic); irrelevant, because
  the resolver loads it by absolute path.
- **ios-arm64.** The framework keeps its upstream name (`CLiteRTLM`); the resolver loads
  `Frameworks/CLiteRTLM.framework/CLiteRTLM`. The binary references `libLiteRtMetalAccelerator.dylib`
  and `libLiteRtTopKMetalSampler.dylib` by name and the package ships neither, so iOS is CPU-only
  until a Metal companion strategy is validated on hardware.

## Staying in sync with upstream

A new upstream release → run `native-release.yml` with that tag (and the zip's `capi_version`) →
publish the `native-<tag>` release → bump `LiteRtLmVersion` (and the package version) in
`Directory.Build.props`, `NATIVE_REF` in `ci.yml` / `model-tests.yml`, and the default in
`scripts/restore-natives.ps1` → let CI run the model-backed suite on every OS. The
`upstream-watch.yml` workflow opens a checklist issue automatically when a new LiteRT-LM release
appears. Each package release records the native tag it ships (release notes + README
compatibility table). Verify the P/Invoke layer against that release's `include/engine.h`
(shipped in the zip) — the workflow's export-count floor (`MIN_EXPORTS`) catches a truncated or
wrong-architecture binary, not a changed signature.

## Local restore

```powershell
pwsh scripts/restore-natives.ps1                 # current desktop OS
pwsh scripts/restore-natives.ps1 -Rid android-arm64,ios-arm64
pwsh scripts/restore-natives.ps1 -All
```

The script downloads the release assets over plain HTTPS, verifies them against the release's
`checksums.txt`, and swaps them into `runtimes/<rid>/native/` (iOS: `runtimes/ios-arm64/xcframeworks/`).
Run it from PowerShell: launched from Git Bash, `tar` resolves to MSYS tar, which misreads the
`C:\...` destination as a remote host.
