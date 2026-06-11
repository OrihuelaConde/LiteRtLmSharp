# LiteRtLmSharp

[![CI](https://github.com/OrihuelaConde/LiteRtLmSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/OrihuelaConde/LiteRtLmSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/LiteRtLmSharp)](https://www.nuget.org/packages/LiteRtLmSharp)

.NET 10 bindings for [Google's LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) — on-device LLM
inference (e.g. Gemma) for any .NET app, including MAUI. P/Invoke over LiteRT-LM's C API, with native
binaries distributed per-RID as NuGet packages (LLamaSharp-style).

> Status: **preview**. Chat, token streaming and function calling working.
>
> | Platform | Native binaries | NuGet package | Runtime-validated |
> |---|---|---|---|
> | win-x64 | ✅ | ✅ | ✅ |
> | linux-x64 | ✅ | ✅ | ✅ (CI) |
> | android-arm64 | ✅ | ✅ | ✅ (device, CPU & GPU) |
> | osx-arm64 | ✅ | ✅ | ⏳ |
> | ios-arm64 | ✅ | ⏳ (needs xcframework packaging) | ⏳ |
>
> See the [roadmap](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/roadmap.md)
> for the full status and pending work.

## Quick start

```xml
<PackageReference Include="LiteRtLmSharp" Version="0.1.0-preview.1" />
<PackageReference Include="LiteRtLmSharp.runtime.win-x64" Version="0.1.0-preview.1" />
<!-- or LiteRtLmSharp.runtime.linux-x64 / android-arm64 / osx-arm64, per target -->
```

Install the managed package plus the runtime package for your platform, **always with the same
version number**. Which LiteRT-LM native build each release wraps:

| LiteRtLmSharp | LiteRT-LM native |
|---|---|
| 0.1.0-preview.1 | v0.13.1 |

```csharp
using LiteRtLmSharp;

using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",   // from huggingface.co/litert-community
    Backend = "cpu",                          // or "gpu" (WebGPU -> D3D12/Vulkan/Metal)
    MaxNumTokens = 4096,                       // total context window
});

using var chat = engine.CreateConversation();

// Blocking:
Console.WriteLine(chat.SendMessage("Hello!"));

// Streaming:
await foreach (var chunk in chat.SendMessageStreamingAsync("Tell me a joke"))
    Console.Write(chunk);
```

### Function calling

```csharp
using var chat = engine.CreateConversation(new LiteRtConversationOptions
{
    Tools = [ new LiteRtTool("get_weather", "Get weather for a city",
        """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""") ],
    EnableConstrainedDecoding = true,
});

var r = chat.Send("Weather in Tokyo?");
if (r.IsToolCall)
{
    var results = r.ToolCalls.Select(c => new LiteRtToolResult(c.Name, RunTool(c)));
    Console.WriteLine(chat.SendToolResults(results).Text);
}
```

See [`samples/Console`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/samples/Console)
for a runnable demo (`--tools` for the function-calling loop) and
[`samples/Maui`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/samples/Maui) for a
full Android/Windows chat app with model download, streaming and tools.

## Why .NET 10 only?

.NET 10 is the current LTS (released November 2025; .NET 8 reaches end of support in November
2026). Targeting it exclusively lets the binding use the modern interop stack as designed —
source-generated P/Invoke (`[LibraryImport]`) and `[UnmanagedCallersOnly]` callbacks with no
runtime marshalling code, which is what makes the library AOT- and trim-compatible — and a
single `net10.0` TFM is directly consumable from `net10.0-android`/`-ios`/`-windows` MAUI apps
without multi-targeting.

## Important notes

- **One engine alive at a time.** Loading a second engine while one is alive throws (it would
  hang in the native layer). To switch model or backend, dispose the conversations and the
  engine, then `LiteRtEngine.Load` again — same pattern as Google's Edge Gallery.
- **`MaxNumTokens`** is the total context window (prompt + response, across turns). Use >= 1024;
  too small can make blocking generation return nothing.
- **Conversations are not thread-safe** — serialize calls per conversation.
- **win-x64** needs the Microsoft Visual C++ Redistributable (the native DLLs import `VCRUNTIME140`).
- **Android GPU needs manifest declarations.** Android 12+ only grants access to vendor native
  libraries declared via `<uses-native-library>`; without `libOpenCL.so` the engine silently picks a
  Vulkan path that produces garbage on older Adreno drivers. Copy the `<uses-native-library>` block
  from [the MAUI sample's AndroidManifest](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/samples/Maui/Platforms/Android/AndroidManifest.xml).
  Full diagnosis in [docs/android.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/android.md).

## Building from source

Native binaries are **not** committed. To run the sample/tests locally, restore them into
`runtimes/<rid>/native/` from the `native-v*` GitHub release:

```powershell
pwsh scripts/restore-natives.ps1
```

(`-All` restores every RID, `-Rid android-arm64` a specific one; needs the `gh` CLI.)

Then `dotnet build LiteRtLmSharp.slnx` and `dotnet test` (library + tests + packaging; bare .NET SDK
is enough). The samples have their own solution, `samples/LiteRtLmSharp.Samples.slnx` — the MAUI
sample in it needs the MAUI workloads (`dotnet workload install maui`). To run the model/tools
tests, set `LITERTLM_TEST_MODEL` (and `LITERTLM_TEST_TOOLS=1`) to a `.litertlm` file.

## How it's built (CI)

- [`build-native.yml`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/.github/workflows/build-native.yml)
  — builds `libLiteRtLm.so` / `LiteRtLm.dll` from a pinned LiteRT-LM tag via
  [`native/patch_c_api.sh`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/native/patch_c_api.sh)
  (adds the shared-lib target upstream lacks — issue #2154), publishes a `native-<tag>` release.
- [`pack-nuget.yml`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/.github/workflows/pack-nuget.yml)
  — packs the managed + per-RID runtime packages.
- [`ci.yml`](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/.github/workflows/ci.yml)
  — build + tests on Linux/Windows.

More detail in [`docs/`](https://github.com/OrihuelaConde/LiteRtLmSharp/tree/master/docs):
[native ABI](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/native-abi.md),
[native build](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/native-build.md),
[packaging](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/packaging.md),
[Android](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/android.md).

## Contributing

Issues and PRs are welcome. To get a working dev setup: clone, `pwsh scripts/restore-natives.ps1`,
`dotnet build LiteRtLmSharp.slnx`, `dotnet test`. Please open an issue first for anything beyond
a small fix.

## License and trademarks

Apache-2.0 (see [LICENSE.txt](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/LICENSE.txt),
[NOTICE](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/NOTICE) and
[THIRD-PARTY-NOTICES.md](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/THIRD-PARTY-NOTICES.md)).

This is an unofficial, community-maintained project. It is **not affiliated with, sponsored,
or endorsed by Google**. LiteRT, LiteRT-LM and Gemma are trademarks of Google LLC. The native
binaries are built from [LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) source
(Apache-2.0) at pinned release tags.
