# LiteRtLmSharp MAUI sample

On-device LLM chat app: download/manage Gemma 4 `.litertlm` models and chat with streaming,
all running locally via LiteRtLmSharp.

- **Models tab** — catalog of Gemma 4 models from `litert-community` (Hugging Face): download with
  progress + resume, delete, pick CPU/GPU and load. Loading while another model is active swaps
  the engine in-place (no app restart) — same pattern as Google's Edge Gallery.
- **Chat tab** — streaming chat with Stop (cancellation), context-usage gauge, New conversation.
- **Tools tab** — function-calling demo: the model can call real device APIs (battery status,
  device info via MAUI essentials) and a mock weather service. Each question runs in a fresh
  conversation, so Chat and Tools never share context.

Targets today: **Android** (physical arm64 device) and **Windows**. iOS/macOS arrive with the Apple
phase (the csproj documents how to add the TFMs).

## Prerequisites

Native binaries restored at `runtimes/<rid>/native/` (the samples reference the library by
project so they always exercise the current source; consumers should use the NuGet packages
instead — see the repo README):

- Android: extract `litertlm-android_arm64.tar.gz` from the `native-v0.13.1` GitHub release into
  `runtimes/android-arm64/native/`
- Windows: `litertlm-windows_x86_64.tar.gz` into `runtimes/win-x64/native/`

```
pwsh scripts/restore-natives.ps1 -Rid android-arm64   # and/or -Rid win-x64
```

## Run on an Android device

Physical **arm64** device (we only ship arm64 binaries), Android 7.0+ (API 24), ideally 8 GB+ RAM
for Gemma 4 E2B. Enable USB debugging, plug in, then:

```
cd samples/Maui
dotnet build -f net10.0-android -t:Run
```

(or F5 in Visual Studio with the device selected). In the app: Models → Download E2B (~2.5 GB,
resumable) → Load → CPU → chat.

## Run on Windows

```
cd samples/Maui
dotnet build -f net10.0-windows10.0.19041.0 -t:Run
```

## Notes

- **One engine alive at a time**: switching model or backend disposes every conversation and the
  engine, then loads the new one (`EngineService.LoadAsync` → `UnloadAsync`). Pages release their
  conversations via the `EngineService.Unloading` event before the engine goes away.
- Models are stored in the app's private data dir (`FileSystem.AppDataDirectory/models`); deleting
  the app deletes the models.
- First token after a long prompt can take a while on phone CPUs — that's the prefill, not a hang.
