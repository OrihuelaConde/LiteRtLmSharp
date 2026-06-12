# Contributing to LiteRtLmSharp

Thanks for your interest! Issues and pull requests are welcome. This is an unofficial,
community-maintained binding — see the [README](README.md#license-and-trademarks) for the
trademark disclaimer.

## Where to go

- **Bug in the binding** → open a [bug report](https://github.com/OrihuelaConde/LiteRtLmSharp/issues/new?template=bug_report.yml).
- **Feature idea / API change** → open a [feature request](https://github.com/OrihuelaConde/LiteRtLmSharp/issues/new?template=feature_request.yml)
  **before** writing code, so we can agree on the approach.
- **Questions and help** → [GitHub Discussions](https://github.com/OrihuelaConde/LiteRtLmSharp/discussions).
- **Engine-level problems** (generation quality, crashes inside the native library, model
  support) usually belong upstream in
  [google-ai-edge/LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM/issues) — this project
  wraps its C API at pinned release tags. If you are not sure where the problem lives, open an
  issue here and we will help triage.

For anything beyond a small fix, please open an issue first. It avoids wasted work on both sides.

## Development setup

Prerequisites: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and
[PowerShell 7+](https://github.com/PowerShell/PowerShell) (`pwsh`, used by the scripts — works
on Windows, Linux and macOS).

```powershell
git clone https://github.com/OrihuelaConde/LiteRtLmSharp.git
cd LiteRtLmSharp

# Native binaries are NOT committed. Restore them from the `native-v*` GitHub release
# into runtimes/<rid>/native/ (plain HTTPS, no auth needed):
pwsh scripts/restore-natives.ps1          # current platform only
# pwsh scripts/restore-natives.ps1 -All   # every RID
# pwsh scripts/restore-natives.ps1 -Rid android-arm64

dotnet build LiteRtLmSharp.slnx
dotnet test
```

`LiteRtLmSharp.slnx` (library + tests + packaging) builds with the bare SDK. The samples live in
their own solution, `samples/LiteRtLmSharp.Samples.slnx`; the MAUI sample in it needs the MAUI
workloads (`dotnet workload install maui`).

### Model-dependent tests

Tests that load a real model are opt-in via environment variables; without them they are
skipped:

- `LITERTLM_TEST_MODEL` — path to a `.litertlm` file (e.g. from
  [huggingface.co/litert-community](https://huggingface.co/litert-community)).
- `LITERTLM_TEST_TOOLS=1` — also run the function-calling tests.

## Repository layout

| Path | What it is |
|---|---|
| `LiteRtLmSharp/` | The managed library (source-generated P/Invoke over the LiteRT-LM C API) |
| `LiteRtLmSharp.Tests/` | Tests (interop + opt-in model tests) |
| `packaging/` | Per-RID `LiteRtLmSharp.runtime.<rid>` package projects |
| `native/` | Patch script that adds the shared-library target upstream lacks |
| `runtimes/` | Native binaries restored locally by `restore-natives.ps1` (never committed) |
| `samples/` | Console and MAUI sample apps (separate solution) |
| `scripts/` | Dev scripts |
| `docs/` | [Roadmap](docs/roadmap.md) (status source of truth), [native ABI](docs/native-abi.md), [native build](docs/native-build.md), [packaging](docs/packaging.md), [Android notes](docs/android.md) |

## Guidelines

- **Keep the library AOT- and trim-compatible.** Interop uses source-generated P/Invoke
  (`[LibraryImport]`) and `[UnmanagedCallersOnly]` callbacks — no `[DllImport]` with runtime
  marshalling, no reflection-based code paths.
- **Never commit native binaries or model files.** Natives are built in CI from pinned
  LiteRT-LM tags ([docs/native-build.md](docs/native-build.md)).
- **Don't bump package versions in PRs.** Versioning is handled at release time
  (policy in [docs/roadmap.md](docs/roadmap.md)).
- **Match the existing code style** (file-scoped namespaces, nullable-aware code, naming as in
  the surrounding files). Add or update tests where it makes sense — CI runs build + tests on
  Linux and Windows for every PR.
- **Update the README/docs** when you change user-visible behavior.

## License

By contributing you agree that your contributions are licensed under the project's
[Apache-2.0 license](LICENSE.txt).
