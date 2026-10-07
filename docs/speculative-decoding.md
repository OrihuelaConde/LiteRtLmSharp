# Speculative decoding

Speculative decoding speeds up token generation by letting a small **Multi-Token-Prediction (MTP)
drafter** — shipped *inside* the `.litertlm` file — propose several tokens ahead, which the main
model then verifies in a single forward pass. When the drafter is accurate, many tokens are accepted
per main-model step, so decode throughput goes up without changing the output distribution. On the
hardware we measured it has not paid off yet: it slowed gemma-4-E2B down on desktop CPU and GPU (see
[Measured results](#measured-results)).

LiteRtLmSharp exposes it as an engine-level flag with a per-conversation override, plus the native
**benchmark API** to measure the effect.

## Requirements

- A model that ships an MTP drafter. The **Gemma 4** builds (`gemma-4-E2B-it`, `E4B`, `12B`) do, and
  `LiteRtModelInfo.SupportsSpeculativeDecoding` tells before loading. On a model without a drafter the
  flag fails: engine creation throws `LiteRtException` (Ministral 3 reports `NOT_FOUND:
  tf_lite_mtp_drafter not found in the model`), and a per-conversation `true` fails the first send.
- The engine flag is fixed at engine creation. Since native v0.18.0 a conversation can override it:
  `LiteRtConversationOptions.EnableSpeculativeDecoding = true` on an engine loaded without it makes the
  runtime load the drafter lazily on that conversation's first send, and `false` turns it off for one
  conversation of a speculative engine. The runtime keeps a lazily loaded drafter on the engine and would
  use it for every later conversation that leaves the setting unset (on CPU those decoded at 0.80× of
  the plain speed); the binding passes the engine setting to them explicitly, so unset keeps meaning the
  engine setting.
- ~~On the WebGPU GPU backend (desktop), disable the disk cache~~ — **fixed in LiteRT-LM v0.14.0**.
  On v0.13.1 the drafter's shared weight-cache file failed to open ("Access denied") on Windows and
  engine creation failed unless `Cache = LiteRtCache.Disabled` (upstream
  [#2572](https://github.com/google-ai-edge/LiteRT-LM/issues/2572); full investigation in
  [Root cause](#root-cause-the-disk-cache)). The fix is in the v0.14.0 tag and re-verified end to
  end with our v0.14.0 natives (engine create + generate + cache readback across two loads, win-x64
  WebGPU): the default disk cache is safe again, and warm loads benefit from it.

## Using it

```csharp
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    Backend = LiteRtBackend.Gpu,
    MaxNumTokens = 4096,
    EnableSpeculativeDecoding = true,                 // MTP drafter
    EnableBenchmark = true,                           // so GetBenchmarkInfo() reports timings
    // On LiteRT-LM v0.13.1 natives, speculative + WebGPU GPU also required
    // Cache = LiteRtCache.Disabled (upstream #2572); fixed in v0.14.0.
});

using var chat = engine.CreateConversation();
chat.Send("Hello!");

if (chat.GetBenchmarkInfo() is { NumDecodeTurns: > 0 } b)
    Console.WriteLine($"{b.LastDecodeTokensPerSecond:F1} tok/s decode · TTFT {b.TimeToFirstTokenSeconds:F2}s");

// Native v0.18.0+: opt one conversation out of the engine setting (or in, on an engine loaded without it).
using var plain = engine.CreateConversation(new LiteRtConversationOptions { EnableSpeculativeDecoding = false });
```

`GetBenchmarkInfo()` works after both blocking (`Send`) and streaming
(`SendStreamingAsync`) generation. It returns `null` when benchmarking was not enabled or no
turn has completed yet; it throws `EntryPointNotFoundException` on native binaries that predate the
benchmark API (the samples catch this and fall back to wall-clock timing).

### In the samples

- **Console**: `--spec` flag, or the *Switch model / backend / speculative* menu option (default
  off). The context gauge then shows `… · N tok/s decode · TTFT …`.
  ```
  LiteRtLmSharp.Sample gemma-4-E2B-it.litertlm "Tell me a joke" --spec
  ```
- **MAUI**: after picking the backend, a *Speculative decoding* prompt (shown only for MTP-capable
  models). The three tab headers show `· speculative on/off` and the gauge shows decode tok/s · TTFT.

(Until v0.14.0 both samples automatically set `Cache = LiteRtCache.Disabled` when speculative decoding
was enabled on the GPU backend; with #2572 fixed they use the default disk cache everywhere.)

## How effectiveness is measured

The model test `SpeculativeDecodingBenchmarkTests` does an A/B: it loads the engine with the drafter
**off**, runs one fixed prompt, reads decode tokens/sec from the benchmark API, disposes the engine,
then repeats with the drafter **on**. It asserts both runs produce coherent text and that the
benchmark reports decode throughput, and logs the speedup ratio (a Markdown row) for this doc.

Run it locally (the gate keeps it out of the fast unit-test runs):

```powershell
$env:LITERTLM_TEST_MODEL = "<path>\gemma-4-E2B-it.litertlm"
$env:LITERTLM_TEST_BENCH = "1"   # also set LITERTLM_TEST_BACKEND=gpu to measure GPU
dotnet test LiteRtLmSharp.Tests/LiteRtLmSharp.Tests.csproj -c Release `
  --filter "FullyQualifiedName~SpeculativeDecodingBenchmarkTests" --logger "console;verbosity=detailed"
```

In CI it runs on the CPU leg of `model-tests.yml` on each push via ci.yml (linux-x64 / linux-arm64 /
win-x64 / osx-arm64); the printed row lands in that job's console log and step summary.

> Sampler note: the CPU sampler factory only implements the **TopP** sampler (Greedy and TopK
> return *"not implemented yet"*; verified on v0.14.0 and v0.15.0). Speculative decoding preserves the output *distribution*, not the
> exact token sequence under sampling, so the A/B checks coherence + throughput rather than asserting
> byte-identical output.

## Measured results

`gemma-4-E2B-it.litertlm`, single fixed prompt, 128 max output tokens. Decode throughput is the
native benchmark API's `decode_tokens_per_sec` for the turn.

| Platform / backend | spec OFF | spec ON | speedup | Notes |
|---|---:|---:|---:|---|
| win-x64 · CPU (dev box, LiteRT-LM v0.18.0, 2026-10-06) | 33.0 tok/s | 25.8 tok/s | **0.78×** | official prebuilt; same ratio as on v0.13.1 |
| win-x64 · GPU WebGPU/D3D12, RTX 3080 (dev box, v0.18.0, 2026-10-07, F32 activations, the default since 1.3.0) | 100.3 tok/s | 51.8 tok/s | **0.52×** | official prebuilt, default disk cache, GPU sampler embedded (no CPU-sampling fallback); two runs (100.3/51.8 and 100.0/51.7) |
| win-x64 · GPU WebGPU/D3D12, RTX 3080 (dev box, v0.18.0, 2026-10-07, F16 activations, the default before 1.3.0) | 87.7 to 96.4 tok/s | 37.7 to 43.5 tok/s | **0.43× to 0.45×** | default disk cache; two runs (87.7/37.7 and 96.4/43.5) |
| win-x64 · GPU WebGPU/D3D12, RTX 3080 (dev box, v0.18.0, 2026-10-06, F32) | 91.4 tok/s | 60.5 tok/s | **0.66×** | disk cache disabled by the benchmark (a v0.13.1 workaround, obsolete since v0.14.0) |
| win-x64 · GPU WebGPU/D3D12, RTX 3080 (dev box, v0.18.0, 2026-10-06, F16) | 84.1 tok/s | 56.8 tok/s | **0.68×** | disk cache disabled by the benchmark, as above |
| win-x64 · CPU (dev box, 2026-06-15) | 29.9 tok/s | 23.4 tok/s | **0.78×** | works, but slower — see below |
| win-x64 · GPU WebGPU/D3D12, RTX 3080 (dev box, 2026-06-15) | 41.8 tok/s | 35.5 tok/s | **0.85×** | A/B both with cache off; plain GPU *with* the disk cache ≈85 tok/s |
| linux-x64 · CPU (CI ubuntu-latest, 2026-06-16) | 16.7 tok/s | 12.2 tok/s | **0.73×** | from `model-tests.yml` |
| osx-arm64 · CPU (CI macos-15, 2026-06-16) | 21.1 tok/s | 20.5 tok/s | **0.97×** | from `model-tests.yml` |
| android-arm64 · GPU OpenCL, Adreno 650 (Moto G100, 2026-06-16) | 13.9 tok/s | 14.1 tok/s | **~1.01×** | runs correctly on GPU (drafter on OpenCL, GPU sampler active, no fallback); ~32% draft acceptance, too low to beat the drafter overhead on this older GPU |

### Findings

- **CPU regresses (every platform).** The drafter + verification overhead is not amortized on CPU, so
  speculative decoding is a net loss-to-neutral for this model: **0.73×** (linux-x64 CI), **0.78×**
  (win-x64 dev box), **0.97×** (osx-arm64 CI). This matches the general result that
  speculative decoding helps memory-bandwidth-bound (accelerator) decode, not compute-bound CPU
  decode. Both outputs were coherent, full paragraphs, and the `*.mtp_drafter.xnnpack_cache_*` file
  produced alongside the model confirms the drafter was actually engaged.
- **Desktop WebGPU GPU on the official prebuilts (v0.18.0): works, still slower.** With the default disk
  cache and the GPU sampler embedded in the library (so neither factor below applies any more), the
  drafter halves the decode throughput on an RTX 3080: 100.3 → 51.8 tok/s (0.52×) with the F32
  activations the binding uses by default, 0.43× to 0.45× with F16. With the disk cache disabled, as the
  benchmark ran until October 2026, plain decoding is slower and the ratio less bad (0.66× with F32,
  0.68× with F16). The drafter's overhead, not the sampling path, dominates at this acceptance rate.
- **Desktop WebGPU GPU on v0.13.1 worked (with the cache off), but didn't help.** With
  `Cache = LiteRtCache.Disabled` the engine loads and the drafter speculates on the GPU (the CLI reports
  ~0.32 draft-acceptance on this prompt). In a fair A/B with the cache off on both legs, spec is a
  slight regression (35.5 vs 41.8 tok/s, 0.85×). Two compounding factors: our package's WebGPU
  **sampler** does not load (`LiteRtTopKWebGpuSampler_UpdateConfig` is missing → CPU-sampling
  fallback), which is expensive in the tight draft/verify loop; and disabling the disk cache itself
  costs throughput here (plain GPU *with* the cache runs ~85 tok/s). So the drafter's overhead isn't
  recovered on this desktop config.
- **A real accelerator (Adreno 650) also shows no win here.** Validated on a Moto G100 (Android 12,
  Adreno 650, OpenCL): MTP runs **correctly** — logcat confirms `enable_speculative_decoding: true`,
  the drafter compiles on the **OpenCL delegate** (its `mtp_drafter` subgraph initializes on GPU
  alongside decode/prefill/verify), and the **GPU sampler loads** (measured with the self-built set and its Android `patchelf`; the official v0.16.0 library embeds the sampler, so
  there's no CPU-sampling fallback here, unlike desktop WebGPU on v0.13.1). Yet throughput is flat: 14.1 (on)
  vs 13.9 (off) tok/s, ~1.01×. The drafter's **acceptance rate is ~32%** (399 drafted, 126 verified)
  — essentially identical to desktop (~0.317), so acceptance is **model/prompt-bound, not
  hardware-bound**. At ~32% acceptance the per-step drafter cost roughly cancels its benefit on this
  older GPU. Upstream's ≈3× (LiteRT-LM#2211) presumably needs a newer/faster accelerator (cheaper
  verification relative to the drafter) and/or a higher-acceptance workload; a recent flagship GPU is
  the remaining thing to try.

## Root cause: the disk cache

Our binding first appeared to fail GPU+MTP at engine creation. Running **Google's official `litert-lm`
CLI** (`pip install litert-lm`, v0.13.1 — the same engine version) on the same machine and model
reproduced the **identical** failure with the default `--cache disk`:

```
delegate_webgpu.cc] Failed to create litert::ml_drift::DelegateKernelLiteRt: INTERNAL:
  Could not map file ('…gemma-4-E2B-it.litertlm_…_mldrift_weight_cache.bin'): Access denied.
  serialization_weight_cache/mmap_handle.cc:147 → … → llm_litert_mtp_drafter.cc:197
```

With `--cache no` the CLI **succeeds** and the drafter runs. So it is **not a binding bug**: with MTP
the main model and the drafter share one `…_mldrift_weight_cache.bin`, and on Windows the second
consumer's `mmap` of that file fails (a file-sharing violation). Pointing the cache at a different
directory does **not** help (same shared file, same error) — only disabling the disk cache avoids it.
The CLI's `--cache no` maps to `litert_lm_engine_settings_set_cache_dir(settings, ":nocache")`, which
we now expose as `Cache = LiteRtCache.Disabled`.

Source-level (v0.13.1): `CreateCompilationOptions` in `runtime/executor/llm_executor_settings_utils.cc`
applies the `.mtp_drafter` cache suffix only on the CPU/XNNPACK branch, not the GPU/MlDrift branch, so
the main model and the drafter resolve to one `…_mldrift_weight_cache.bin` — which is why a custom
directory doesn't help. The Windows share-mode that turns the collision into "Access denied" lives in
the closed `libLiteRtWebGpuAccelerator` (inferred `ERROR_SHARING_VIOLATION`).

**Fixed in v0.14.0** (commit `4aa96a019` applies the `cache_suffix` on the GPU/MlDrift branch too, plus
the follow-up `0a6590988`; both are in the tag). Re-verified 2026-07-10 with our v0.14.0 natives on
win-x64 WebGPU: spec + default disk cache creates, generates, and reads the cache back across engine
reloads. The whole section above is kept as the v0.13.1 historical record.

### Upstream tracking (researched 2026-06-15)

- **Cache**: no dedicated issue for the Windows/WebGPU case yet —
  [#2503](https://github.com/google-ai-edge/LiteRT-LM/issues/2503) is the iOS/Metal sibling, and a
  buried comment on [#2461](https://github.com/google-ai-edge/LiteRT-LM/issues/2461) reports the exact
  trace as a **regression from v0.12.0** (without MTP, so the collision is broader than MTP). A new
  upstream issue is warranted.
- **CPU-sampling fallback**: this was [#2073](https://github.com/google-ai-edge/LiteRT-LM/issues/2073)
  (WebGPU sampler exports 3/7 C-ABI functions on macOS/Windows → CPU fallback), closed upstream on
  2026-09-22. It no longer applies: the official prebuilts the binding ships since 1.2.0 embed the GPU
  samplers in the library. Per flutter_gemma #287 the steady-state cost had been small (~3%), but it
  weighed more on the speculative draft/verify loop.

In short: the flag works on every backend we tested with the default cache (v0.13.1 needed
`Cache = LiteRtCache.Disabled` on desktop WebGPU), but on desktop CPU and GPU it slows decoding down for
gemma-4-E2B (0.78× and 0.52× on v0.18.0), and an older mobile GPU (Adreno 650) shows no win at ~32%
acceptance. Measure it on your target accelerator and workload before turning it on, and use the
per-conversation override to keep it where it pays off.
