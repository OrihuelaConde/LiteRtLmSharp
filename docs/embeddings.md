# Embeddings

An embedding model turns a text into a vector of numbers whose geometry follows meaning: texts about
the same thing get vectors that point the same way. With LiteRtLmSharp you compute embeddings on the
device, so semantic search, retrieval-augmented generation (RAG), clustering and classification work
offline, without sending any text to a server.

`LiteRtEmbeddingEngine` loads an embedding model such as **EmbeddingGemma 2** and embeds text.
`LiteRtEmbeddingGenerator` exposes it as a Microsoft.Extensions.AI
`IEmbeddingGenerator<string, Embedding<float>>`, which Semantic Kernel and the .NET vector stores
consume directly. `LiteRtModelInfo` reads a model file's metadata without loading it.

## Requirements

- The LiteRT-LM v0.18.0 natives (LiteRtLmSharp 1.3.0 and later). The embedding engine and the model
  metadata reader are part of the v0.18.0 C API.
- An embedding model in `.litertlm` format. This project validates
  [EmbeddingGemma 2 Text 270M](https://huggingface.co/litert-community/embeddinggemma-2-text-270m-litert-lm)
  (`embeddinggemma-2-text-270m.litertlm`, 165 MB, Apache 2.0, no sign-in needed to download). The
  same repository has variants compiled for specific NPUs (Google Tensor, Qualcomm, MediaTek, Intel);
  LiteRtLmSharp does not support NPUs, so use the plain file on CPU or GPU.
- Text input. EmbeddingGemma 2 also comes in larger bundles that embed images, audio and video; the
  binding does not expose those inputs yet. Use the text-only bundle: the larger ones load their vision
  and audio encoders anyway, which costs memory and load time for nothing.

## Quick start

```csharp
using LiteRtLmSharp;

using var engine = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
{
    ModelPath = "embeddinggemma-2-text-270m.litertlm",
});

string[] notes =
[
    "The bakery on Elm Street opens at 7 a.m. on weekdays.",
    "Remember to renew the car insurance before March.",
    "The library closes early on Sundays.",
];

// Index: one vector per note, with the document instruction in front (see "Task instructions").
float[][] index = engine.EmbedBatch(notes.Select(n => "title: none | text: " + n).ToArray());

// Search: embed the question with the query instruction and rank by similarity.
float[] query = engine.Embed("task: search result | query: When can I buy bread in the morning?");
int best = Enumerable.Range(0, index.Length).MaxBy(i => Dot(query, index[i]));
Console.WriteLine(notes[best]);   // The bakery on Elm Street opens at 7 a.m. on weekdays.

// The vectors are L2-normalized by default, so the dot product is the cosine similarity.
static float Dot(float[] a, float[] b)
{
    float sum = 0;
    for (int i = 0; i < a.Length; i++)
        sum += a[i] * b[i];
    return sum;
}
```

Store the vectors wherever you keep the app's data (a file, SQLite, a vector database) and embed only
new or changed texts. `engine.Dimension` gives the vector length (768 for EmbeddingGemma 2).

## Task instructions

EmbeddingGemma 2 is trained with a short instruction in front of each text that says what the vector is
for: a search query and the documents it searches are embedded differently. The runtime does not add
the instruction, so prepend it to every text yourself. Without it the model still works, at lower
quality.

Two official sources list different instructions:

| Use | Model card ([google/embeddinggemma-2](https://huggingface.co/google/embeddinggemma-2)) | [LiteRT-LM guide](https://developers.google.com/edge/litert-lm/embedding_models) |
|---|---|---|
| Search query | <code>task: search result &#124; query: </code> | <code>task: search query &#124; text: </code> |
| Document to search | <code>title: none &#124; text: </code> (or <code>title: {title} &#124; text: </code>) | <code>task: search result &#124; text: </code> |
| Question answering query | <code>task: question answering &#124; query: </code> | (not listed) |
| Fact checking query | <code>task: fact checking &#124; query: </code> | (not listed) |
| Code search query | <code>task: code retrieval &#124; query: </code> | (not listed) |
| Classification | <code>task: classification &#124; query: </code> | <code>task: classification &#124; text: </code> |
| Clustering | <code>task: clustering &#124; query: </code> | <code>task: clustering &#124; text: </code> |
| Sentence similarity | <code>task: sentence similarity &#124; query: </code> | <code>task: sentence similarity &#124; text: </code> |

The model card's set is also the one the model's Sentence Transformers configuration applies. On a small
check of our own (24 questions against 32 passages, 8 of them distractors that share words with a
question), both sets and no instruction at all ranked the right passage first 23 or 24 times; the model
card's set separated it from the runner-up by the widest margin (mean cosine margin 0.096, against 0.093
for the LiteRT-LM guide's set and 0.084 without instructions). Check on your own data before you
commit to one.

Whichever set you choose:

- Use the same set to index and to search. Changing the instructions, the model, the backend or
  `OutputDimensions` means embedding the stored documents again.
- Trim leading and trailing whitespace from the text before you prepend the instruction.

LiteRtLmSharp does not add the instructions for you, because the two sources disagree and the right
choice depends on the model.

## Per-call options

`Embed` and `EmbedBatch` take an optional `LiteRtEmbeddingOptions`. Unset values keep the runtime
defaults.

| Option | Default | What it does |
|---|---|---|
| `OutputDimensions` | Full length (768) | Truncates the vector to that length and, unless `Normalize` is `false`, normalizes it again. EmbeddingGemma 2 is trained for 768, 512, 256 and 128 (Matryoshka representation learning), so use one of those; 128 dimensions take 512 bytes per vector instead of 3 KB. A value above the model's length fails the call with `InvalidArgument`. |
| `Normalize` | `true` | L2-normalizes the vector, which makes the dot product equal to the cosine similarity. |
| `OverflowStrategy` | `Error` | What happens to a text longer than the loaded signatures (see [Input length](#input-length)): `Error` fails the call with `LiteRtStatusCode.InvalidArgument`, `Truncate` embeds the first part that fits, `ChunkAndAverage` embeds every chunk and averages the vectors. |
| `InsertSpecialTokens` | `true` | Adds the model's begin and end tokens around the text. Leave it on. |

In the check above, 128 dimensions ranked the right passage first 24 times out of 24, with narrower
margins than 768.

```csharp
float[] small = engine.Embed(text, new LiteRtEmbeddingOptions { OutputDimensions = 256 });
float[] whole = engine.Embed(longText, new LiteRtEmbeddingOptions { OverflowStrategy = LiteRtInputOverflowStrategy.ChunkAndAverage });
```

## Input length

An embedding model runs one of several fixed-size input signatures. EmbeddingGemma 2 has signatures for
128, 256, 512, 1,024, 2,048 and 8,192 tokens (`LiteRtModelInfo.EmbeddingInputLengths` lists them), and
the engine pads each text to the smallest loaded signature that holds it.

By default the engine loads the signatures up to the limit the model file declares, which is **1,024
tokens** for EmbeddingGemma 2, not 8,192 (`LiteRtModelInfo.MaxContextTokens` reports the longest
signature, 8,192, which only a raised `MaxInputLength` reaches). A longer text then fails, unless
`OverflowStrategy` truncates or chunks it. To embed longer texts whole, raise
`LiteRtEmbeddingEngineOptions.MaxInputLength`: the engine loads the smallest signature that holds that many
tokens, plus the shorter ones, so it accepts texts up to that signature's length (1,500 loads the 2,048
signature). A value above the longest signature makes `Load` fail. `MinInputLength` leaves out the
signatures shorter than its value; it must not exceed the effective maximum (`MaxInputLength`, or the
model's 1,024 when that is unset).

```csharp
using var engine = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
{
    ModelPath = "embeddinggemma-2-text-270m.litertlm",
    MaxInputLength = 2048,   // loads the 2,048-token signature too
});
```

Longer signatures cost memory and time. For long documents, splitting them into passages of a few
hundred tokens and embedding each passage usually retrieves better than one vector for the whole text.

## Engine options

| Option | Default | Notes |
|---|---|---|
| `Backend` | `Cpu` | `Gpu` is faster for longer texts (see [Performance](#performance)). The GPU vectors differ slightly from the CPU ones (cosine similarity 0.9994 between them in our measurement), so index and search on the same backend. |
| `ActivationDataType` | `Float32` | Like the chat engine, the embedding engine defaults to float32. The EmbeddingGemma 2 model card advises against float16 (its activations exceed the float16 range, which degrades the vectors silently), and on GPU the runtime would otherwise fall back to float16: in our measurement those vectors had a cosine similarity of 0.9965 with the CPU ones and slightly narrower ranking margins. Float32 cost no speed on a desktop GPU, but on a phone GPU float16 was about twice as fast per sentence (see [Performance](#performance)). The CPU backend runs float32 either way. Set `Float16` to make that trade, or `null` to let the runtime choose. |
| `MaxInputLength`, `MinInputLength` | The model's limits | See [Input length](#input-length). |
| `Cache` | Next to the model file | Compiled artifacts written on the first load (67 MB on CPU, 74 MB on GPU for EmbeddingGemma 2 Text 270M). A `LiteRtCache.Directory` must already exist: the runtime does not create it, so `Load` throws `ArgumentException`. |
| `NumThreads` | Runtime default | CPU only. |

## Performance

Measured with LiteRT-LM v0.18.0 and EmbeddingGemma 2 Text 270M on Windows 11, Intel Core i9-14900K (CPU
backend) and NVIDIA GeForce RTX 3080 (GPU backend, WebGPU, `Float32` activations). Medians of several runs
after a warm-up; texts carry the document instruction.

| | CPU | GPU |
|---|---|---|
| Load (cache present) | 0.15 s | 2.4 to 3.3 s |
| One sentence | 35 ms | 15 ms |
| 32 sentences, one `EmbedBatch` call | 1.2 s | 0.43 s |
| About 300 words | 150 ms | 32 ms |
| About 1,500 words (`MaxInputLength = 2048`) | 1.6 s | 0.15 s |

- **Batching saves call overhead, not compute.** The runtime embeds the texts of a batch one after
  another, so 32 texts in one `EmbedBatch` call take as long as 32 `Embed` calls. If one text in a batch
  fails (for example, a text too long under `OverflowStrategy.Error`), the whole call fails.
- **Memory on CPU** grows with the longest signature you use: the process peaked at about 0.42 GB with
  `MaxInputLength = 512`, 0.53 GB with the default 1,024, and 0.98 GB after embedding a text that
  needed the 2,048-token signature (`MaxInputLength = 2048`).
- **Memory on GPU (Windows, WebGPU)**: the working set grew by about 0.4 GB at load, but the process's
  committed memory grew by about 3.6 GB at the default limit and 7.1 GB with `MaxInputLength = 2048`.
  On a machine with a small page file, keep `MaxInputLength` as low as your texts allow.
- **On a phone** (Moto G100, Snapdragon 870 with an Adreno 650 GPU, Android 12): one sentence takes
  504 ms on CPU, 188 ms on GPU with `Float32` and 103 ms with `Float16`; the GPU vectors have a cosine
  similarity to the CPU ones of 0.9995 (`Float32`) and 0.9962 (`Float16`). Loading takes about 4 s on CPU
  and about 22 s on GPU the first time, while the GPU kernels are compiled into the cache.

## Threads, lifetime and the chat engine

- **Thread-safe.** Calls on one engine are serialized internally, so you can share it across threads; a
  call waits for the one in progress. `EmbedAsync` and `EmbedBatchAsync` run on the thread pool, and their
  cancellation token cancels only the wait: a computation that has started runs to completion.
- **Dispose** waits for the call in progress, then releases the native engine.
- **Alongside a chat engine.** An embedding engine does not count toward the one-live-engine rule of
  `LiteRtEngine`, so an app can keep a chat model and an embedding model loaded at the same time, for
  example to retrieve passages and then answer with them. Validated on CPU and GPU (win-x64), including
  embeddings computed while the chat engine streams a reply. Known issue on win-x64 GPU: the full test
  suite, which loads and disposes many engines in one process, has ended a few times with the process
  exiting without an error, once inside the coexistence test; isolated runs and the last 21 full runs
  were clean. The
  [roadmap](https://github.com/OrihuelaConde/LiteRtLmSharp/blob/master/docs/roadmap.md) tracks it. On a phone the chat model dominates memory:
  on a Moto G100 (8 GB), gemma-4-E2B on GPU takes about 1.8 GB and the embedding engine adds about 120 MB
  on CPU or 260 MB on GPU, and the same retrieve-then-answer flow ran with about 0.4 to 0.5 GB of the
  device's memory still available.
- **Model check.** `Load` reads the file's metadata first and throws `ArgumentException` when the file
  is a language model (load those with `LiteRtEngine.Load`).

## Microsoft.Extensions.AI

The `LiteRtLmSharp.Extensions.AI` package adds `LiteRtEmbeddingGenerator`, an
`IEmbeddingGenerator<string, Embedding<float>>` over an embedding engine:

```csharp
using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;

using var engine = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
{
    ModelPath = "embeddinggemma-2-text-270m.litertlm",
});
using IEmbeddingGenerator<string, Embedding<float>> generator =
    new LiteRtEmbeddingGenerator(engine, modelId: "embeddinggemma-2-text-270m");

// The same options for documents and questions: vectors of different lengths are not comparable.
var options = new EmbeddingGenerationOptions { Dimensions = 256 };

GeneratedEmbeddings<Embedding<float>> documents = await generator.GenerateAsync(
    ["title: none | text: The bakery opens at 7 a.m.", "title: none | text: Renew the car insurance."], options);

Embedding<float> question = await generator.GenerateAsync(
    "task: search result | query: When does the bakery open?", options);
```

- `EmbeddingGenerationOptions.Dimensions` maps to `OutputDimensions`. The other knobs are on
  `LiteRtEmbeddingGenerationOptions` (`Normalize`, `InsertSpecialTokens`, `OverflowStrategy`), stored in
  `AdditionalProperties` under the keys `normalize`, `insert_special_tokens` and `overflow_strategy`, so
  setting those keys on a plain `EmbeddingGenerationOptions` (for example, from configuration or JSON) works
  too. `overflow_strategy` accepts the enum, its name or its number; `normalize` and
  `insert_special_tokens` take a boolean. A value that cannot be read fails the call with
  `ArgumentException`.
- Every embedding carries the generator's model id: the engine runs one model, so a request's
  `ModelId` is ignored.
- The generator embeds large inputs in batches of 32 texts and keeps their order.
- The generator does not own the engine: dispose the engine after the generator.
- `GetService<EmbeddingGeneratorMetadata>()` reports the provider `litert-lm`, the model id and the
  vector length; `GetService<LiteRtEmbeddingEngine>()` returns the engine.

### Dependency injection

```csharp
services.AddLiteRtChatClient(new LiteRtEngineOptions { ModelPath = "gemma-4-E2B-it.litertlm" });
services.AddLiteRtEmbeddingGenerator(
    new LiteRtEmbeddingEngineOptions { ModelPath = "embeddinggemma-2-text-270m.litertlm" },
    modelId: "embeddinggemma-2-text-270m");
```

Registered from options, the container loads one shared embedding engine on first use (or at
registration with `eager: true`), owns it and disposes it once it has resolved it. The registration
coexists with `AddLiteRtChatClient`. A container holds one generator: registering again with the same
options does nothing, and with different options throws `InvalidOperationException`. To use an engine
you own, pass the engine instead of the options.

## Semantic Kernel

Semantic Kernel consumes `IEmbeddingGenerator<string, Embedding<float>>` directly (vector stores, text
search), so the `LiteRtLmSharp.SemanticKernel` package registers the same generator on the kernel
builder:

```csharp
IKernelBuilder builder = Kernel.CreateBuilder();
builder.AddLiteRtChatCompletion(new LiteRtEngineOptions { ModelPath = "gemma-4-E2B-it.litertlm" });
builder.AddLiteRtEmbeddingGenerator(
    new LiteRtEmbeddingEngineOptions { ModelPath = "embeddinggemma-2-text-270m.litertlm" },
    modelId: "embeddinggemma-2-text-270m");
Kernel kernel = builder.Build();

var generator = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
```

Pass `serviceId` to register a keyed generator. Each `serviceId` gets its own engine, so keyed
generators can run different models or backends (for example one on CPU and one on GPU).

## Read a model's metadata

`LiteRtModelInfo.Read` opens a `.litertlm` file, reads what it declares and closes it, without loading an
engine. For an embedding model:

```csharp
LiteRtModelInfo info = LiteRtModelInfo.Read("embeddinggemma-2-text-270m.litertlm");
Console.WriteLine(info.ModelType);                              // Embedding
Console.WriteLine(info.EmbeddingDimension);                     // 768
Console.WriteLine(string.Join(", ", info.EmbeddingInputLengths)); // 128, 256, 512, 1024, 2048, 8192
```

The same call describes chat models; see [Read a model's metadata](chat.md#read-a-models-metadata).

## Limitations

- Text only (see [Requirements](#requirements)).
- The C API does not report the input limit a model declares (1,024 tokens for EmbeddingGemma 2), so
  the binding cannot tell you the default. Set `MaxInputLength` when your texts need more.
- Vectors from different models, backends, instruction sets or `OutputDimensions` values are not
  comparable with each other.
