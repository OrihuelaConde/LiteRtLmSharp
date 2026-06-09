# LiteLMSharp MAUI sample

On-device LLM chat app: download/manage Gemma 4 `.litertlm` models and chat with streaming,
all running locally via LiteLMSharp.

- **Models tab** — catalog of Gemma 4 models from `litert-community` (Hugging Face): download with
  progress + resume, delete, pick CPU/GPU and load.
- **Chat tab** — streaming chat with Stop (cancellation), context-usage gauge, New conversation.

Targets today: **Android** (physical arm64 device) and **Windows**. iOS/macOS arrive with the Apple
phase (the csproj documents how to add the TFMs).

## Prerequisites

Native binaries restored at `runtimes/<rid>/native/` (this sample uses direct references while the
NuGet packages are unpublished):

- Android: extract `litertlm-android_arm64.tar.gz` from the `native-v0.13.1` GitHub release into
  `runtimes/android-arm64/native/`
- Windows: `litertlm-windows_x86_64.tar.gz` into `runtimes/win-x64/native/`

```
gh release download native-v0.13.1 -p 'litertlm-android_arm64.tar.gz'
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

- **One model per app run**: LiteRT-LM's native environment initializes once per process; to switch
  models, restart the app (the UI tells you).
- Models are stored in the app's private data dir (`FileSystem.AppDataDirectory/models`); deleting
  the app deletes the models.
- First token after a long prompt can take a while on phone CPUs — that's the prefill, not a hang.
