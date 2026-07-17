# NuGet packaging and consumption

LLamaSharp-style model: **one pure managed package + per-RID native runtime packages**.

| Package | Contents | TFM |
|---|---|---|
| `LiteRtLmSharp` | Managed assembly only (`lib/net10.0/LiteRtLmSharp.dll`). No natives. | net10.0 |
| `LiteRtLmSharp.Extensions.AI` | `IChatClient` connector (Microsoft.Extensions.AI). Depends on `LiteRtLmSharp` (same version). | net10.0 |
| `LiteRtLmSharp.SemanticKernel` | `IChatCompletionService` connector, built on `LiteRtLmSharp.Extensions.AI` (same version). | net10.0 |
| `LiteRtLmSharp.runtime.win-x64` | `runtimes/win-x64/native/*.dll` (LiteRtLm + companions + DXC). No lib. | (native-only) |
| `LiteRtLmSharp.runtime.linux-x64` | `runtimes/linux-x64/native/*.so`. No lib. | (native-only) |
| `LiteRtLmSharp.runtime.android-arm64` | `runtimes/android-arm64/native/*.so` (packed into the APK as `lib/arm64-v8a/`). | (native-only) |
| `LiteRtLmSharp.runtime.osx-arm64` | `runtimes/osx-arm64/native/*.dylib` (Apple Silicon). | (native-only) |
| `LiteRtLmSharp.runtime.ios-arm64` | Dynamic `.framework` xcframeworks injected via `NativeReference` (buildTransitive `.targets`); device-arm64. No lib. | (native-only) |

> **iOS (`ios-arm64`).** Unlike desktop, a nupkg
> `runtimes/` folder is not auto-embedded on iOS; the natives ship as an **xcframework consumed
> via `NativeReference Kind=Framework`** from a `buildTransitive` `.targets` conditioned on the
> `net10.0-ios` TFM — `libLiteRtLm.dylib` + the prebuilt companions wrapped as dynamic
> `.framework`s, embedded and code-signed into the app bundle and resolved at runtime by
> `NativeLibraryResolver` (see the iOS linking decision in `docs/roadmap.md`). Device-arm64 only
> (the companions have no simulator slice). Build/link is validated in CI
> (`ios-package-check.yml`); on-device runtime is pending hardware validation (see
> `docs/roadmap.md`). Every package ships `README.md`, `LICENSE.txt`, `NOTICE` and
> `THIRD-PARTY-NOTICES.md` at its root.

**All packages share one version per release** (enforced by the single `Version` in
`Directory.Build.props`) and that version is **independent of the LiteRT-LM native version**,
which is pinned via `LiteRtLmVersion` and surfaced in the package release notes and the README
compatibility table. Install the managed and runtime packages with the same version number.

## Consumption (including MAUI)

```xml
<PackageReference Include="LiteRtLmSharp" Version="1.1.0" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="1.1.0" />
<!-- and/or linux-x64 / android-arm64 / osx-arm64, per target -->
```

The SDK copies `runtimes/<rid>/native/*` into the consumer's output; `NativeLibraryResolver`
resolves them. The managed package does **not** depend on any runtime package (each consumer
picks its RID, like LLamaSharp).

> Validated end-to-end: managed + runtime.win-x64 from a local feed → a consumer app resolved
> the natives and ran inference.

## Producing the packages

1. **Native build** (`build-native.yml`) pinned to a tag (`v0.14.0`, the current pin) with
   `publish_release` → creates the `native-v0.14.0` release with the `litertlm-*.tar.gz` assets.
2. **Pack** (`pack-nuget.yml`): downloads those assets, lays them into
   `runtimes/<rid>/native/`, runs `dotnet pack` with the given `package_version`, uploads the
   `.nupkg`s as an artifact, and (optionally, `push=true`) publishes to nuget.org via
   **Trusted Publishing** (the GitHub OIDC token is exchanged for a short-lived API key with
   `NuGet/login` — no stored secret).

Locally: `dotnet pack LiteRtLmSharp/LiteRtLmSharp.csproj -c Release -o .nupkgs` (managed) and
the projects under `packaging/` (these need the natives already present in
`runtimes/<rid>/native/`).

## Notes / pending

- **MSVC runtime**: `LiteRtLm.dll` (win-x64) imports `MSVCP140/VCRUNTIME140*`; it relies on the
  VC++ Redistributable being installed on the user's machine. Documented as a prerequisite in
  the README; shipping it is a future packaging decision.
- Possible future RIDs: `linux-arm64`, `android-x64` (emulators).
- Optional future: a `LiteRtLmSharp.Backend.Desktop` meta-package depending on the win/linux
  runtime packages.
- Android GPU: consumers must declare `<uses-native-library>` in their manifest (see the README
  and docs/android.md).
