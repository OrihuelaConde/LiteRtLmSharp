# Engine tuning and benchmarking

A handful of `LiteRtEngineOptions` settings trade off load time, memory, precision and speed, and let
you measure the result. They are all fixed at engine creation (`LiteRtEngine.Load`) and most are
advanced knobs — the defaults are good for typical use, so reach for these only when you have a
specific load-time, memory or throughput goal.

This guide explains what each one does, when it helps, and what it costs. Two related performance
features have their own pages: [speculative decoding](speculative-decoding.md) (the MTP drafter) and
the compiled-artifact cache (`LiteRtEngineOptions.Cache`, also the fix for speculative decoding on
the desktop WebGPU backend).

## Activation precision — `ActivationDataType`

The precision of the activation tensors during inference. `null` (default) lets each executor pick its
own default — the text executor uses **F16** on GPU; the vision and audio executors use F32.

```csharp
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    Backend = LiteRtBackend.Gpu,
    ActivationDataType = LiteRtActivationDataType.Float32,  // full precision on GPU
});
```

- **Only the GPU backend honors this, and only as F32 vs F16.** `Float32` runs activations at full
  precision — higher quality, more memory. `Float16` (the GPU default for text) uses half the
  activation memory, with a precision loss that is NOT always small (below).
- **On CPU it is a no-op** — the CPU/XNNPACK path does not read it.
- **`Int16` / `Int8` are accepted but not distinctly implemented** by the shipped executors: on GPU they
  fold into F16, on CPU they are ignored. They exist only to mirror the native enum — do not expect
  8/16-bit activation quantization from them.
- **When to set it:** choose `Float32` on GPU if you see quality/precision issues, or on a GPU whose
  driver lacks reliable FP16. Otherwise leave it unset.

### The F16 default corrupts structured output on desktop GPU — set `Float32` if you see it

If your GPU outputs show **corrupted digit sequences** (dates like `206-15-2023` or `195959-06-17`,
truncated or looping numbers), **degraded reasoning** versus the same model on CPU, or results that
**vary run-to-run at temperature 0**, the cause is very likely the default **F16 activations**, not
your prompts and not the model. This failure mode is widely reported against LiteRT-LM but hard to
find the real knob for (upstream threads blame the sampler DLL, suggest repetition penalties, or go
unanswered — see google-ai-edge/LiteRT-LM#2637, #2727, #2202, and the export-gap/sampler issues
#2073/#2080), so it is documented here with what we measured:

- On win-x64 WebGPU (RTX 3080, temp 0), a 16-check benchmark of structured extraction from free text
  FAILS 13/16 with rotating errors on default F16 — digit sequences corrupted **at emission** (before
  any post-processing), dates resolved against the wrong reference, relations between extracted
  entities inverted or attached to the wrong entity — and passes **16/16 across 3 consecutive runs
  with `Float32`**, with clean digits in the raw output and **no measurable speed cost** on that GPU
  (~32 s either way; F32 compute was not the bottleneck).
- The same F16→F32 flip reproduces on a **non-Gemma model** (Ministral-3-3B q4: 13/16 → 16/16), so it
  is a property of the GPU text executor's precision, not of one model family.
- F16 did not only corrupt digits — it degraded reasoning quality (which entity a fact belongs to,
  which date a relative expression resolves to), which is easy to misattribute to the model or the
  prompt.
- CPU is immune (the knob is GPU-only, and the CPU path never showed the corruption).

**Recommendation:** for any GPU workload where output fidelity matters more than activation memory —
structured output, function calling, dates/numbers, JSON — set
`ActivationDataType = LiteRtActivationDataType.Float32` and A/B it once on your target GPU. The cost
is activation memory (roughly double) and possibly speed on GPUs with weak F32 throughput; on desktop
discrete GPUs we measured the speed cost as nil.

## Prefill chunk size — `PrefillChunkSize`

How many prompt tokens the engine prefills per step. `0` (default) means no chunking — the whole prompt
is prefilled in one pass.

```csharp
PrefillChunkSize = 256,   // prefill 256 tokens at a time
```

- **CPU + dynamic models only.** It is read only by the CPU "dynamic" executor (models whose KV-cache
  and sequence-length tensors are dynamically sized). On GPU, or on static models, it is a no-op (the
  native layer silently ignores the call).
- **Smaller chunks lower peak memory during prefill** — intermediate activations are sized to the chunk,
  not the whole prompt — and allow **more timely cancellation** of a long prompt.
- **Trade-off:** more chunks mean more prefill iterations and per-chunk overhead, so total prefill can be
  slower. Leave it off (no chunking) for the shortest prefill latency.
- **When to set it:** a long prompt on a memory-constrained CPU device, or where you want responsive
  cancellation partway through prefill.

## Parallel file-section loading — `ParallelFileSectionLoading`

Whether the engine loads parts of the `.litertlm` file concurrently during startup. `null` (default)
leaves it on.

```csharp
ParallelFileSectionLoading = false,   // serial, single-threaded load
```

- A `.litertlm` file is a container of typed sections (tokenizer, model graph, weights, metadata). With
  this **on** (the default), the tokenizer section is parsed on a background thread while the
  model/executor is built, overlapping the two and shortening cold-start init.
- With it **off**, that load is deferred onto the calling thread and serialized after model construction
  — slower startup, but single-threaded.
- **When to set `false`:** single-threaded environments (e.g. WASM without pthreads), or to avoid the
  brief concurrent IO/thread peak during init, accepting a slower start.

## CPU thread counts: `NumThreads` / `AudioNumThreads`

How many CPU threads the text and audio executors use. `null` (default) lets the engine pick its own
count.

```csharp
NumThreads = 4,        // text executor CPU threads
AudioNumThreads = 2,   // audio executor CPU threads (only when audio is enabled)
```

- **CPU backend only.** These set the thread counts for the CPU/XNNPACK executors and are a **no-op on
  non-CPU backends** (GPU and NPU manage their own parallelism). `AudioNumThreads` applies only when an
  audio executor is configured (`AudioBackend` set); otherwise it is ignored.
- **Non-positive values are rejected**: pass a positive count, or leave it `null` for the default.
- **When to set it:** pin the engine to a subset of cores on a busy machine, or tune throughput on a
  device where the default (all cores) contends with the rest of the app. More threads help prefill and
  decode up to the memory-bandwidth limit; past that they only add scheduling contention.

## LoRA adapters: engine ranks and adapter paths

Loading a low-rank adaptation on top of a LoRA-enabled base model has two layers, both added in v0.14.0:

- **Engine-level** `LiteRtEngineOptions.LoraRank` / `SupportedLoraRanks` (and the `AudioLoraRank` /
  `SupportedAudioLoraRanks` variants) declare the adapter rank(s) the engine compiles for. The
  supported-ranks lists are only honored on the **GPU (Artisan) backend**.
- **Per-conversation** `LiteRtConversationOptions.LoraPath` / `AudioLoraPath` point at the adapter file,
  which is opened when the conversation is created, so a bad path **fails fast** with `LiteRtException`
  at `CreateConversation`, not mid-generation.

Requires a LoRA-enabled model. This path is **not yet validated end-to-end** against a real adapter (no
adapter artifact on hand), but the plumbing is in place and the failure modes surface coherently.

## Benchmarking

### Measuring real runs — `EnableBenchmark`

Turn on benchmark instrumentation and read the timings off the conversation after a turn. The overhead
is timing bookkeeping only.

```csharp
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    Backend = LiteRtBackend.Cpu,
    EnableBenchmark = true,
});
using var chat = engine.CreateConversation();
chat.Send("Write a short paragraph about the printing press.");

if (chat.GetBenchmarkInfo() is { NumDecodeTurns: > 0 } b)
    Console.WriteLine(
        $"prefill {b.LastPrefillTokensPerSecond:F1} tok/s · decode {b.LastDecodeTokensPerSecond:F1} tok/s · " +
        $"TTFT {b.TimeToFirstTokenSeconds:F2}s · init {b.TotalInitTimeSeconds:F2}s");
```

`GetBenchmarkInfo()` returns `null` when benchmarking was not enabled or no turn has completed. It
exposes the latest turn's `LastPrefillTokenCount` / `LastDecodeTokenCount`, the matching
`LastPrefillTokensPerSecond` / `LastDecodeTokensPerSecond`, plus `TimeToFirstTokenSeconds` and
`TotalInitTimeSeconds`.

### Synthetic, content-independent benchmark — `BenchmarkPrefillTokens` / `BenchmarkDecodeTokens`

To measure raw throughput at **fixed** token counts, independent of any prompt, set the synthetic token
counts. A send then runs a benchmark instead of answering:

```csharp
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = "gemma-4-E2B-it.litertlm",
    Backend = LiteRtBackend.Cpu,
    BenchmarkPrefillTokens = 512,   // prefill exactly 512 tokens
    BenchmarkDecodeTokens = 128,    // decode exactly 128 tokens
});
using var chat = engine.CreateConversation();
chat.Send("anything");              // the text is ignored — this is a benchmark run

var b = chat.GetBenchmarkInfo()!;   // reports 512 prefill / 128 decode
Console.WriteLine($"prefill {b.LastPrefillTokensPerSecond:F1} tok/s · decode {b.LastDecodeTokensPerSecond:F1} tok/s");
```

How it works: the prompt is padded or truncated to `BenchmarkPrefillTokens` for prefill, and decoding
runs exactly `BenchmarkDecodeTokens` tokens, ignoring the stop token. The measured prefill/decode rates
therefore depend only on the token counts and the engine settings — ideal for comparing devices, or for
measuring the effect of `ActivationDataType` / `PrefillChunkSize` reproducibly.

Two caveats:
- **The reply is not a real answer** — it is a synthetic run. Use a dedicated engine for benchmarking,
  not one you also chat with.
- **Setting either count turns benchmark mode on** (the same switch as `EnableBenchmark`), so
  `GetBenchmarkInfo()` works even without `EnableBenchmark`.

This is the mechanism the project's CI A/B harness uses to compare speculative decoding on and off; the
measured numbers and method are in [speculative-decoding.md](speculative-decoding.md).
