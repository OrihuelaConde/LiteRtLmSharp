# NuGet packaging and consumption

LLamaSharp-style model: **one pure managed package + per-RID native runtime packages**.

| Package | Contents | TFM |
|---|---|---|
| `LiteRtLmSharp` | Managed assembly only (`lib/net10.0/LiteRtLmSharp.dll`). No natives. | net10.0 |
| `LiteRtLmSharp.Extensions.AI` | `IChatClient` connector (Microsoft.Extensions.AI). Depends on `LiteRtLmSharp` (same version). | net10.0 |
| `LiteRtLmSharp.SemanticKernel` | `IChatCompletionService` connector, built on `LiteRtLmSharp.Extensions.AI` (same version). | net10.0 |
| `LiteRtLmSharp.runtime.win-x64` | `runtimes/win-x64/native/`: the official `LiteRtLm.dll` (static CRT) + the DirectX Shader Compiler runtime (`dxcompiler.dll`, `dxil.dll`) the GPU backend needs. No lib. | (native-only) |
| `LiteRtLmSharp.runtime.linux-x64` | `runtimes/linux-x64/native/libLiteRtLm.so` (one official library; needs the system Vulkan loader, `libvulkan1`). No lib. | (native-only) |
| `LiteRtLmSharp.runtime.android-arm64` | `runtimes/android-arm64/native/libLiteRtLm.so` (one official library, accelerators and samplers embedded; packed into the APK as `lib/arm64-v8a/`). | (native-only) |
| `LiteRtLmSharp.runtime.osx-arm64` | `runtimes/osx-arm64/native/libLiteRtLm.dylib` (one official library, Apple Silicon). | (native-only) |
| `LiteRtLmSharp.runtime.ios-arm64` | Google's official `CLiteRTLM.xcframework` (device + simulator slices) injected via `NativeReference` (buildTransitive `.targets`); CPU backend. No lib. | (native-only) |

> **iOS (`ios-arm64`).** Unlike desktop, a nupkg `runtimes/` folder is not auto-embedded on iOS;
> the native library ships as Google's official **`CLiteRTLM.xcframework`, consumed via
> `NativeReference Kind=Framework`** from a `buildTransitive` `.targets` conditioned on the
> `net10.0-ios` TFM — embedded and code-signed into the app bundle and loaded at runtime by
> `NativeLibraryResolver` as `Frameworks/CLiteRTLM.framework/CLiteRTLM` (see the iOS linking
> decision in `docs/roadmap.md`). The xcframework carries device and simulator slices. GPU (Metal)
> needs companion libraries the xcframework only references by name and the package does not
> ship, so the CPU backend is the supported path. Build/link is validated in CI
> (`ios-package-check.yml`, which also asserts the package layout); on-device runtime is pending
> hardware validation (see `docs/roadmap.md`). Every package ships `README.md`, `LICENSE.txt`,
> `NOTICE` and `THIRD-PARTY-NOTICES.md` at its root; the runtime packages add upstream's
> `THIRD_PARTY_NOTICES.litert-lm.txt` for the binaries they carry.

**All packages share one version per release** (enforced by the single `Version` in
`Directory.Build.props`) and that version is **independent of the LiteRT-LM native version**,
which is pinned via `LiteRtLmVersion` and surfaced in the package release notes and the README
compatibility table. Install the managed and runtime packages with the same version number.

## Consumption (including MAUI)

```xml
<PackageReference Include="LiteRtLmSharp" Version="1.2.0" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="1.2.0" />
<!-- and/or linux-x64 / android-arm64 / osx-arm64, per target -->
```

The SDK copies `runtimes/<rid>/native/*` into the consumer's output; `NativeLibraryResolver`
resolves them. The managed package does **not** depend on any runtime package (each consumer
picks its RID, like LLamaSharp).

> Validated end-to-end: managed + runtime.win-x64 from a local feed → a consumer app resolved
> the natives and ran inference.

## Producing the packages

1. **Native release** (`native-release.yml`) pinned to the upstream tag (`v0.16.0`, the current pin)
   with `publish_release` → downloads Google's official prebuilts, verifies them against the upstream
   release digests, inspects each library and publishes the `native-v0.16.0` release with the
   `litertlm-*.tar.gz` assets (plus upstream's `THIRD_PARTY_NOTICES.litert-lm.txt`).
2. **Pack** (`pack-nuget.yml`): downloads those assets, lays them into
   `runtimes/<rid>/native/`, runs `dotnet pack` with the given `package_version`, uploads the
   `.nupkg`s as an artifact, and (optionally, `push=true`) publishes to nuget.org via
   **Trusted Publishing** (the GitHub OIDC token is exchanged for a short-lived API key with
   `NuGet/login` — no stored secret).

Locally: `dotnet pack LiteRtLmSharp/LiteRtLmSharp.csproj -c Release -o .nupkgs` (managed) and
the projects under `packaging/` (these need the natives already present in
`runtimes/<rid>/native/`).

## Notes / pending

- **Vulkan loader on Linux**: the official `libLiteRtLm.so` hard-depends on `libvulkan.so.1`; consumers
  install `libvulkan1` (documented in the README; the resolver's load-failure message names it).
- **No VC++ Redistributable on Windows** since 1.2.0: the official `LiteRtLm.dll` links the CRT statically.
- Possible future RIDs: `linux-arm64`, `android-x64` (emulators).
- Optional future: a `LiteRtLmSharp.Backend.Desktop` meta-package depending on the win/linux
  runtime packages.
- Android GPU: consumers must declare `<uses-native-library>` in their manifest (see the README
  and docs/android.md).
