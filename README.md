# LiteLMSharp

.NET 10 bindings for [Google's LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) — on-device LLM
inference (e.g. Gemma) for any .NET app, including MAUI. P/Invoke over LiteRT-LM's C API, with native
binaries distributed per-RID as NuGet packages (LLamaSharp-style).

> Status: **preview**. Desktop **win-x64** and **linux-x64** working (chat, streaming, function calling).
> Versions track LiteRT-LM release tags (currently **v0.13.1**). macOS + mobile (Android/iOS) are planned.

## Quick start

```xml
<PackageReference Include="LiteLMSharp" Version="0.13.1-preview.1" />
<PackageReference Include="LiteLMSharp.runtime.win-x64" Version="0.13.1-preview.1" />
<!-- or LiteLMSharp.runtime.linux-x64 -->
```

```csharp
using LiteLMSharp;

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

See [`samples/Console`](samples/Console) for a runnable demo (`--tools` for the function-calling loop).

## Important notes

- **One engine per process.** LiteRT-LM's native environment initializes once; a second
  `LiteRtEngine.Load` throws. Create multiple conversations from a single engine.
- **`MaxNumTokens`** is the total context window (prompt + response, across turns). Use >= 1024;
  too small can make blocking generation return nothing.
- **Conversations are not thread-safe** — serialize calls per conversation.
- **win-x64** needs the Microsoft Visual C++ Redistributable (the native DLLs import `VCRUNTIME140`).

## Building from source

Native binaries are **not** committed. To run the sample/tests locally, restore them into
`runtimes/<rid>/native/`:

```powershell
pwsh scripts/restore-natives.ps1
```

(The repo is currently private; `restore-natives.ps1` needs `gh` or a token to pull our release.
Alternatively download the assets from the `native-v0.13.1` release manually.)

Then `dotnet build` and `dotnet test`. To run the model/tools tests, set `LITERTLM_TEST_MODEL`
(and `LITERTLM_TEST_TOOLS=1`) to a `.litertlm` file.

## How it's built (CI)

- [`build-native.yml`](.github/workflows/build-native.yml) — builds `libLiteRtLm.so` / `LiteRtLm.dll`
  from a pinned LiteRT-LM tag via [`native/patch_c_api.sh`](native/patch_c_api.sh) (adds the shared-lib
  target upstream lacks — issue #2154), publishes a `native-<tag>` release.
- [`pack-nuget.yml`](.github/workflows/pack-nuget.yml) — packs the managed + per-RID runtime packages.
- [`ci.yml`](.github/workflows/ci.yml) — build + tests on Linux/Windows.

More detail in [`docs/`](docs): [native ABI](docs/native-abi.md), [native build](docs/fase2-native-build.md),
[packaging](docs/packaging.md).

## License

Apache-2.0. LiteRT-LM and its binaries are (c) Google, also Apache-2.0.
