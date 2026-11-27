# Changelog

All notable changes to **LiteRtLmSharp** are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The package version is independent of the
LiteRT-LM native version it wraps (see the compatibility table in the [README](README.md)); the
managed `LiteRtLmSharp` package and every `LiteRtLmSharp.runtime.<rid>` package share one version and
are published together.

## [Unreleased]

This release moves the native runtime from LiteRT-LM v0.18.0 to v0.20.0, adds raw prefill-and-decode sessions
with experimental checkpoints, and adds experimental model downloads and updates. No public member of 1.3.0 is
removed or changed, but some runtime behaviour changes can break code that relied on 1.3.0: see **Changed**.
The engine-usage changes come from a contribution by @DimQ1
([#15](https://github.com/OrihuelaConde/LiteRtLmSharp/pull/15)), who measured where the engine-usage API fell
short for an application that shares one model between live translation, chat and background jobs.

Experimental members carry an `[Experimental]` attribute with a diagnostic ID. They can change or be removed in a
later release. The C# compiler reports each use as an error until you suppress that ID, for example with
`#pragma warning disable LITERTLM001` in the file or `<NoWarn>$(NoWarn);LITERTLM001</NoWarn>` in the project.

### Added

- **Raw sessions.** `LiteRtEngine.CreateSession` returns a `LiteRtSession`, which runs the model on text that you
  write. Use one when you need the exact text in the context, for example to continue a reply that was cut.
  `Prefill` and `PrefillAsync` add text to the context, and `Decode`, `DecodeAsync` and `DecodeStreamingAsync`
  generate a reply. A session applies no chat template, keeps no message history, takes text only, and parses no
  tool calls or reasoning.
  - `LiteRtSessionOptions` sets the `Sampler` and the `MaxOutputTokens` cap for the session's lifetime.
  - `LiteRtSession.State` returns a `LiteRtSessionState` (`Fresh`, `Prefilled`, `Decoded`, `ContextFull`,
    `Faulted` or `Cancelled`) that tells what the session accepts next. `Dispose` frees the session.
  - A session refuses a second call while one runs, instead of waiting. On a model whose start token is a
    single token, such as Gemma 4, it also refuses a first prefill that starts with that token, which the runtime
    adds itself.
  - Sessions and conversations can share one engine. `CreateSession` waits for a generation in flight on the
    engine.

  See the [Sessions guide](https://orihuelaconde.github.io/LiteRtLmSharp/sessions.html).
- **Experimental session checkpoints (`LITERTLM001`).** `LiteRtSession.SaveCheckpoint` returns a
  `LiteRtSessionCheckpoint`, `LiteRtSession.RewindTo` returns the session to it, and `LiteRtSession.Reset` clears
  the context. With them you can try a turn, drop it, and try another. `RewindTo` and `Reset` also continue a
  session whose context filled (`ContextFull`) or whose prefill failed (`Faulted`). They're experimental because
  they were measured with one model (gemma-4 E2B), on Windows x64 with the CPU and GPU backends and, in part, on an
  Android x86_64 emulator: they can change or be removed in a later release, and using them requires suppressing
  `LITERTLM001`. See
  [Experimental: checkpoints, rewind, and reset](https://orihuelaconde.github.io/LiteRtLmSharp/sessions.html#experimental-checkpoints-rewind-and-reset).
- **Experimental model downloads and updates (`LITERTLM003`).** `LiteRtModelSync.UpdateAsync` downloads a
  `.litertlm` file to a path, or brings a local copy up to date with a newer remote file by reusing the sections
  that did not change. The calls need no engine.
  - `LiteRtModelSync.CreatePlanAsync` returns a `LiteRtModelSyncPlan` (`IsUpToDate`, `BytesToDownload`,
    `BytesToReuse`, `TotalRemoteSize`) without writing anything.
  - `LiteRtModelSyncSource.FromHttp` reads the remote file with HTTP range requests.
  - `LiteRtModelSyncOptions.Progress` receives the running total of bytes received, and `UpdateAsync` returns a
    `LiteRtModelUpdateResult` with `BytesReceived`.

  A section is reused only when both files record section hashes. None of the published model files that were
  checked records them, so an update of those files downloads the whole file. Every ModelSync type is
  experimental: it can change or be removed in a later release, and using it requires suppressing `LITERTLM003`.
  See [Download and update models](https://orihuelaconde.github.io/LiteRtLmSharp/model-sync.html),
  which also covers deleting the compiled cache after you replace a model file.
- **Several chat engines in one process.** `LiteRtEngineOptions.AllowMultipleEngines` lets an engine stay
  alive next to other chat engines. A load next to live engines succeeds only when the new engine and every
  live one opted in, so code that loads an engine without the setting keeps the one-engine guarantee.
  `LiteRtEngine.LiveEngineCount` reports how many chat engines are alive. Engines only run in parallel on a desktop
  CPU (measured with LiteRT-LM v0.18.0), and each one holds its own memory. On Windows, a gemma-4 E2B engine
  commits about 2.3 GB of private memory on CPU and about 4.1 GB on GPU right after it loads (LiteRT-LM v0.20.0,
  Float32, GPU: NVIDIA RTX 3080). The API reference for `AllowMultipleEngines` lists the measurements.
- **`LiteRtConversation.TokenCountCached`**, a token count that never waits. The native `TokenCount` reader
  goes through the engine, so it waits behind a generation in flight on any conversation of that engine. The
  cache is refreshed after every completed send, and it and `IsContextFull` can be read from another thread
  while a send is in flight.
- **`LiteRtConversation.IsCancelled`** tells whether a send on the conversation was cut short (a cancelled
  send, an abandoned stream, or `CancelProcess()` during a send), after which the runtime answers `CANCELLED`
  to every later send. A stream that failed during generation counts too, and so does a cancel that lands as the
  reply ends.
- **Keyed chat clients.** `AddKeyedLiteRtChatClient` registers several models side by side, for example one per
  agent. Keys with equal options share one engine. See
  [Register several models](https://orihuelaconde.github.io/LiteRtLmSharp/extensions-ai.html#register-several-models).
- **`LiteRtConversation.LastReplyReachedContextLimit`** tells whether the overflow guard capped the last reply
  and the reply used the whole budget. Unlike `IsContextFull`, it does not mean that the next send fails: after a
  reply with thinking, the runtime removes the thinking from the context, so the conversation can continue. A
  send that the binding rejects leaves the value as it was. `LiteRtChatClient` uses it to report
  `ChatFinishReason.Length`.
- **`LiteRtBenchmarkInfo.LastSendPrefillTokenCount`** reports the prefill of the last send's own message. After
  a reply with thinking, the runtime records its refill of the context as a second prefill turn of the same send,
  so `LastPrefillTokenCount` and `LastPrefillTokensPerSecond` then describe that refill, not the message.
  `UsageDetails.InputTokenCount` in `LiteRtLmSharp.Extensions.AI` reads the new value.

### Changed

- **Native binaries are LiteRT-LM v0.20.0** (from v0.18.0), still Google's official prebuilts. Each runtime
  package carries Google's library for its platform, unchanged apart from the name; the win-x64 package also
  carries the DirectX Shader Compiler pair (`dxcompiler.dll`, `dxil.dll`) for the GPU backend. The C API moved
  to version 1.1.0: every function that can fail returns a status code and writes its result through out
  pointers, and the binding follows the new signatures. A failure the runtime reports carries that status in
  `LiteRtException.StatusCode`, with the runtime's reason.

  Exception messages from native failures read `<function> failed: CODE: reason.`, where 1.3.0 messages read
  `<function> returned null: …` or `… failed with code N.`. On win-x64, `LiteRtModelInfo.Read` on a model file
  that exists but can't be opened, for example one that another handle holds locked, reports `Unknown`
  (`Failed to open`), where 1.3.0 reported `Internal`.

  A conversation that leaves
  `LiteRtConversationOptions.EnableSpeculativeDecoding` unset keeps the engine setting: the runtime does this
  itself, so the binding no longer passes the setting to such a conversation (win-x64, CPU and GPU,
  gemma-4 E2B).

  The binding checks the C API version the first time it uses the native library. It refuses a library that
  doesn't implement C API 1.1 or a later 1.x version, with a `LiteRtException` that names the versions and the
  runtime package to install. A v0.18.0 library next to this build fails with that message instead of crashing.

  Behaviour changes that can affect existing code:
  - **`Greedy` and `TopK` decode as named on the CPU and GPU backends.** In 1.3.0, the CPU backend failed the
    first send of a `Greedy` or `TopK` conversation with `UNIMPLEMENTED: Sampler type: N`, and the GPU backend
    ran every type as TopP. LiteRT-LM v0.20.0 decodes `Greedy` as argmax and `TopK` as top-k on both backends,
    also in the first conversation on a fresh CPU engine (measured on win-x64 with gemma-4 E2B, and on CPU also
    with Phi-4-mini and Ministral 3 3B). On the GPU this changes the output of existing `Greedy` and `TopK`
    callers, which got sampled text before.
  - **Speculative decoding follows the sampler.** With speculative decoding turned on, a sampled reply follows
    the conversation's seed: seeds 7, 8 and 9 gave three different replies on CPU and on GPU. On CPU, 1.3.0 gave
    one text for all three seeds. An argmax reply with speculative decoding equals the plain argmax reply byte
    for byte on CPU; on the GPU it differed from the plain argmax reply in 2 of 2 processes. Measured on win-x64
    with gemma-4 E2B, speculative decoding turned on per conversation (GPU: WebGPU on Direct3D 12, NVIDIA
    RTX 3080, Float32).
  - **The token count leaves a reply's thinking out as soon as the reply ends.** A reply with thinking that the
    overflow guard cuts at the limit leaves room in the context: `IsContextFull` stays `false`, the next send
    works, and `LiteRtChatClient` still reports `ChatFinishReason.Length` for that turn. A clone taken after
    such a reply holds the count without the thinking. A stream or a blocking send of a reply with thinking
    returns only after the runtime has refilled the context. `UsageDetails.TotalTokenCount` in
    `LiteRtLmSharp.Extensions.AI` reads this count, so after a reply with thinking it can be lower than
    `InputTokenCount` plus `OutputTokenCount`, which counts the thinking.

    The runtime leaves the thinking out whatever `LiteRtConversationOptions.FilterThinkingFromKvCache` says:
    its native default drops channel content, and the binding sends the setting only for `true`, so `false`
    doesn't keep the thinking in the context (CPU, gemma-4 E2B). The 1.3.0 docs described the filter as off
    unless the option was set.
  - **A send ends with a native token-count read that can wait for other generations on the engine.** A
    stream that runs to its end now waits for the runtime to finish the turn before its loop ends, as a
    blocking send does. If that wait fails, the conversation counts as cancelled (`IsCancelled`). Every
    completed send, blocking or streamed, then reads the native token count to refresh `TokenCountCached`,
    also with the overflow guard off; 1.3.0 read it only after a send for which the guard set the output cap.
    Like `TokenCount`, that read goes through the engine, so it can wait behind a generation in flight on
    another conversation of the engine.
  - **`SendRaw` takes one message JSON object per call.** A JSON array, which 1.3.0 answered, fails with
    `INVALID_ARGUMENT: Message must be a JSON object.`, and the conversation stays usable.
  - **A message that doesn't fit the remaining context fails with `RESOURCE_EXHAUSTED`**, where 1.3.0 failed it
    with `FAILED_PRECONDITION`. You can reach it with sends that the overflow guard doesn't measure (media and
    extra context) and on engines without `MaxNumTokens`. A prefill that lands in the last free entries of the
    context fails with `FAILED_PRECONDITION`. After either failure, every later send of that conversation fails
    with `INTERNAL: Task failed with state: 7`; 1.3.0 did the same after a message that didn't fit. With the
    overflow guard off, no crash was seen (win-x64, CPU and GPU, gemma-4 E2B; 17 runs on the GPU). The 1.3.0
    docs said that an overflow the guard doesn't stop damages native memory and crashes the process later.
  - **Under `EnableConstrainedDecoding` without tools, a text reply can end at its first line break.** 1.3.0
    continued such a reply to the output cap. It depends on the machine: with gemma-4 E2B, a count with one
    number per line ended at its first line break on some machines and ran to the cap on others, including two
    win-x64 machines that differed. The same count on one line ran normally.
  - **A text LoRA adapter applies to its own conversation only.** 1.3.0 applied an adapter to the whole engine.
    A conversation without an adapter now answers normally next to one with an adapter, and the
    `No LoRA ID is set` failure does not occur (measured on CPU with the upstream LoRA test bundle).
  - **`LiteRtModelInfo.MaxContextTokens` reports a language model's default context size.** It reads the
    context size the file's metadata sets or, when the metadata sets none, the runtime's default of 4,096,
    capped by the size of a static model graph, so a declared 4,096 and the default read the same. The
    LiteRT-LM v0.18.0 C API that 1.3.0 wraps defined the value as the size of a static model graph, or the
    largest size a dynamic model accepts. Three model files report a 4,096-token context (win-x64):
    - `gemma-4-E2B-it`, dynamic context: 1.3.0 read 32,003. The file also declares CPU and GPU for vision.
      The value is not a hard maximum for this model: gemma-4 E2B accepted 19,533 tokens with `MaxNumTokens`
      20000 (win-x64 CPU, overflow guard off).
    - `gemma-4-E4B-it`, dynamic context: the 1.3.0 docs listed its context as not declared.
    - `Phi-4-mini-instruct`, static context: the 1.3.0 docs listed its context as not declared.
  - **`LiteRtModelInfo.Read` no longer copies the vision sections into memory.** While it reads
    `gemma-4-E2B-it`, the process's peak memory rises by about 10 MB (win-x64, LiteRT-LM v0.20.0). The 1.3.0
    docs gave about 0.8 GB, measured with LiteRT-LM v0.18.0, and advised calling `Read` on a phone before
    loading engines; that advice no longer applies.
  - **On Windows, `LiteRtModelInfo.Read` can pass a non-ASCII model path as an alias.** v0.20.0 opens the path
    twice with two encodings, so on Windows with a system code page other than UTF-8, `Read` passes a model path
    with non-ASCII characters to the runtime as its 8.3 short name or, on a volume without short names, as a
    temporary DOS device name.
  - **Values outside a declared enum fail.** A value of `LiteRtConstraintProvider`, `LiteRtSamplerType` or
    `LiteRtInputOverflowStrategy` that only a cast can produce fails with a `LiteRtException`
    (`InvalidArgument`) from the runtime. Such a `LiteRtActivationDataType` value makes `LiteRtEngine.Load` and
    `LiteRtEmbeddingEngine.Load` fail the same way.
  - **`LiteRtBenchmarkInfo.TotalInitTimeSeconds` is the engine creation time only.** LiteRT-LM v0.18.0 summed
    every initialization phase in it.
  - **Concurrent sends on one engine.** On the GPU, two blocking sends at once on two conversations of one engine
    gave correct replies in 20 of 20 rounds, where 1.3.0 gave a wrong reply in 20 of 20. Two streams at once gave
    the same replies as each one alone in 20 of 20 rounds. On CPU, the runtime ran concurrent sends one after
    another (720 sends, 0 mismatches). Measured on win-x64 with gemma-4 E2B through the core API (GPU: WebGPU on
    Direct3D 12, NVIDIA RTX 3080, Float32). Chat clients over one engine run one generation at a time (see
    "Chat clients over one engine take turns" below).
  - **Repeating a processed message on a GPU clone.** On the GPU, a clone's first send that repeats a message the
    conversation had already processed, after another conversation generated, gave a correct reply in 100 of 100
    runs (60 direct clones, 40 through the stateful chat client). With v0.18.0, the same sequence gave a wrong
    reply in 25 of 25. The upstream issue,
    [LiteRT-LM #4394](https://github.com/google-ai-edge/LiteRT-LM/issues/4394), is still open: it did not
    reproduce with v0.20.0 on the GPU measured (win-x64, WebGPU on Direct3D 12, NVIDIA RTX 3080, gemma-4 E2B,
    Float32). Other GPUs and Float16 activations were not measured.
  - **The first load after the upgrade rewrites the compiled cache.** LiteRT-LM v0.18.0 and v0.20.0 name cache
    files the same way, after the model file's name, modification time and size, and each version rewrites
    the files that the other wrote, once. On the GPU, the program cache file grows instead of being replaced.
    Measured on win-x64, CPU and GPU, with Qwen3-0.6B.
- **A per-send `EnableThinking = false` on a later send of a Gemma 4 conversation answers without thinking, for
  that send only.** In 1.3.0, a per-send `false` took effect only on the send right after the first reply with
  thinking, and then stayed for the rest of the conversation. Gemma 4 switches thinking on in the system turn,
  which the runtime prefills with the conversation's first send, so the first send's mode stays for the
  conversation. For a later send with `LiteRtSendOptions.EnableThinking = false`, the binding also suppresses
  the token that opens the thinking channel, for that send; later sends follow the conversation's mode again.
  The opposite has no effect: a per-send `true` after a first send without thinking gives no thinking. On the
  first send of a conversation, a per-send `true` turns thinking on. Measured on win-x64 with gemma-4 E2B on
  CPU and GPU (LiteRT-LM v0.20.0).
- **`IsContextFull` reads the cached token count** (see `TokenCountCached`), so it never waits behind a
  generation on another conversation. It is exact after each completed send.
- **`LiteRtConversation.Clone()` throws `InvalidOperationException` for a conversation whose send was cut
  short** or whose stream failed: the runtime would return an empty clone that also answers `CANCELLED`. To
  continue after a cancellation, take the clone before the send. The
  [conversation-state guide](https://orihuelaconde.github.io/LiteRtLmSharp/conversation-state.html#continue-after-a-cancellation) shows how.
- **A cancelled or failed stateful send no longer ends the conversation.** Before, `LiteRtChatClient` disposed the
  conversation and its `ConversationId` stopped working. Now it goes back to a checkpoint taken before the turn: the
  id keeps working and the whole turn, tool calls included, is discarded, so send the message again. Agent Framework
  relies on this, because it keeps a session's id after a failed run. Some cases still end the conversation,
  including its first turn and conversations with constrained decoding. On conversations with constrained decoding,
  the new `LiteRtStatefulConversationOptions.CheckpointConstrainedConversations` keeps the id after the first turn,
  at a cost of about a second per turn (measured with LiteRT-LM v0.18.0). See [Cancelled and failed sends](https://orihuelaconde.github.io/LiteRtLmSharp/extensions-ai.html#cancelled-and-failed-sends).
- **A stateful stream you stop reading after the reply ended keeps its turn.** Before, any stream not read to its
  end ended the conversation. In the stateful mode, a reply's tool calls now reach the stream together when the
  reply ends, after any text in it, so stopping at them keeps the turn.
- **Each Semantic Kernel `serviceId` gets its own chat client and, from options, its own engine.** If your service
  ids use different options, set `AllowMultipleEngines` on each of them, or the first use of the second service
  throws `InvalidOperationException`. Before, every `serviceId` wrapped the one client and engine registered without
  a key, so a second model silently ran on the first engine. Equal options still share one engine, and a
  `LiteRtEngine` you registered without a key before the `serviceId` still serves them. A `serviceId` also
  no longer inherits the model id, template and stateful settings of an earlier registration, or goes through an
  `IChatClient` registered without a key. See
  [Register several models](https://orihuelaconde.github.io/LiteRtLmSharp/semantic-kernel.html#register-several-models).
- **A Semantic Kernel chat service registered from options loads its engine on its first call**, with or without a
  `serviceId`. Before, resolving `IChatCompletionService` loaded the engine, so a bad model path or backend failed
  there; now it fails at the first call. To load the engine at registration, and fail there, set `eager`.
- **A conflicting chat registration throws.** Registering a chat client again, with or without a Semantic Kernel
  `serviceId`, with a different engine or different options throws `InvalidOperationException`. Before, the second
  registration was silently ignored. The same applies to a key registered with the new `AddKeyedLiteRtChatClient`.
  This includes a registration without a key after a `serviceId`, because the first `serviceId`
  is also registered without a key, so register the one without a key first. The same engine or equal options again
  still does nothing.
- **`LiteRtSamplerParams.Temperature` must be finite.** An infinite temperature now throws
  `ArgumentOutOfRangeException`, like NaN and negative values. The LiteRT-LM v0.20.0 C API requires a finite
  temperature.
- **A chat request without tools does not ask for constrained decoding.** `LiteRtChatClient` and the Semantic
  Kernel connector used to pass `EnableConstrainedDecoding` to the runtime even when the request offered no
  tools. With LiteRT-LM v0.20.0 and the flag set, a text reply to a request without tools ended at its first
  line break (measured with gemma-4 E2B; the cause is not known), so the flag now applies only to requests
  with tools.
- **Chat clients over one engine take turns.** Each `LiteRtChatClient` used to serialize only its own calls, so two
  clients over one engine could put two generations in flight on it. Now they wait for each other, so the clients
  over one engine run one generation at a time. An open stream counts until you read it to the end or dispose it,
  so calling another client over the same engine from inside the loop that reads the stream waits forever.

### Removed

- **The fallbacks for native libraries that lack an entry point.** The C API version check refuses such a
  library before the first call, so no call throws `EntryPointNotFoundException`.

### Fixed

- **Native handles stay alive for the whole call.** `LiteRtEngine.Tokenize`, `Detokenize`, `GetStartToken` and
  `GetStopTokens`, and `LiteRtConversation.CancelProcess`, hold a reference on the native handle for the call: a
  `Dispose` on another thread, or finalization, no longer deletes the native engine or conversation while the
  call uses it. The call either completes or throws `ObjectDisposedException`. `LiteRtConversation.RenderMessage`,
  `RenderMessageRaw` and `RenderPreface` keep the conversation reachable until the rendered string is copied.
- **A rejected first message no longer leaves a conversation behind.** In the stateful mode of
  `LiteRtChatClient`, a request without a `ConversationId` whose first message the context overflow guard rejects
  no longer leaves its new conversation in the store. The caller never received that conversation's id.
- **`LiteRtModelInfo.DefaultSampler` with values that aren't finite.** It is `null` when the model file declares
  a sampler with an infinite top-p or temperature, as it already was for NaN.
- **Paths with non-ASCII characters on Windows are documented.** A cache folder whose path has non-ASCII
  characters makes `LiteRtEngine.Load` fail, and a model path with non-ASCII characters fails `Load` on the GPU
  and loads on the CPU without a usable compiled cache (measured with ANSI code page 1252; the same with
  v0.18.0). The docs didn't mention it. See
  [Model and cache paths on Windows](https://orihuelaconde.github.io/LiteRtLmSharp/engine-tuning.html#model-and-cache-paths-on-windows).
- **The license notices include Dawn.** `THIRD-PARTY-NOTICES.md` reproduces the license of Dawn, which includes
  Tint, and names SPIRV-Tools, for the win-x64, linux-x64, linux-arm64 and osx-arm64 packages. The notices that
  LiteRT-LM v0.20.0 ships in its iOS framework, from which `THIRD_PARTY_NOTICES.litert-lm.txt` is built, no
  longer list them.
- **Disposing an engine before its conversations no longer frees the native engine under them.** Each
  conversation now keeps its engine's native object alive, so the native engine and its one-engine slot are
  released when the last conversation is disposed. Before, a conversation disposed or finalized after its
  engine deleted its native session through a freed engine. Until that last conversation is disposed,
  `LiveEngineCount` counts the engine and, by default, `LiteRtEngine.Load` refuses another one, so an app that
  switches models has to dispose its conversations first, as the docs always asked.
- **`History` is documented as prefilled by the conversation's first send**, not when the conversation is
  created: right after `CreateConversation`, `TokenCount` still reads 0.
- **Interleaving conversations.** With LiteRT-LM v0.20.0, interleaving turns between several live conversations
  of one engine keeps each conversation's state, and its replies stay correct (win-x64, CPU and GPU). The README
  now notes that with LiteRtLmSharp 1.1.x and older (LiteRT-LM v0.13.1 and v0.14.0), interleaving two
  conversations of one engine loses the suspended conversation's state.
- **Stateful-mode memory.** On a model with a dynamic context such as gemma-4 E2B, a live conversation's KV
  state grows with the tokens it has used (about 36 MB for 2,300 tokens on a desktop CPU, measured with
  LiteRT-LM v0.18.0). A model with a static context keeps a full-size cache per live conversation, so keep few
  live conversations with such a model. On the GPU, running out of memory this way did not fail cleanly. With
  Ministral 3 3B (static 4,096-token context) on a 10 GB GPU and live conversations each filled to about 2,300
  tokens, the fourth conversation exhausted the GPU memory and the process ended with exit code `0xC0000409`, in
  2 of 2 runs (LiteRT-LM v0.20.0, win-x64, NVIDIA RTX 3080).
- **The documentation describes what LiteRT-LM v0.20.0 does** in these cases, measured on win-x64 with
  gemma-4 E2B unless the item says otherwise:
  - `LiteRtConversation.GetBenchmarkInfo()` returns `null` only when benchmarking is off. Before the first send,
    it returns an info with zero prefill and decode turns.
  - `LiteRtEngine.Detokenize` returns plain text with ordinary spaces, not the SentencePiece `▁` word marker.
  - The runtime detects the media format from the bytes. PNG and JPEG images and MP3, WAV and FLAC audio decode;
    WEBP images and OGG Vorbis audio failed to decode (CPU).
  - On the GPU, `LiteRtEngineOptions.ActivationDataType` `Int16` and `Int8` produced unusable text (NVIDIA
    RTX 3080); the docs said the GPU ran them as F16. On CPU the setting had no effect.
  - `LiteRtAttachment.ImageFile` works on Android too: it answered like the inline bytes on an x86_64 emulator
    (API 35, CPU). The docs said file-path attachments were desktop only. `AudioFile` was not measured on
    Android.
  - The Android packages run on Mono (.NET for Android), not on CoreCLR as the docs said (checked on an x86_64
    emulator).
  - The stateless `LiteRtChatClient` and the Semantic Kernel connector send the media of earlier user turns too;
    the docs said they sent only the last message's. With an image on the first turn, the model recalled it in a
    later turn (chat client: CPU and GPU; Semantic Kernel connector: CPU). Audio on earlier turns was not
    measured.
  - On a text-only model, setting `VisionBackend` or `AudioBackend` makes `CreateConversation` fail with
    `NotFound` (`tf_lite_vision_encoder not found in the model`, or the same message for the audio encoder);
    the engine itself loads. The docs said the setting had no effect there. Measured on CPU with
    Phi-4-mini-instruct.
  - The runtime uses a `MaxNumTokens` above the model's default; the docs said it ignored such a value. With
    gemma-4 E2B on CPU, whose limit is 4,096 when no value is set, 20000 was used as set and 40000 gave a limit
    of 32,000. The overflow guard measures against the value you set, so with 40000 a send can reach the
    runtime's `RESOURCE_EXHAUSTED`, after which every send of that conversation fails.
  - A small `MaxNumTokens` makes engine creation or sends fail with an exception. No blocking send returned an
    empty reply, which the docs said could happen:
    - On CPU, 100 failed at engine creation, and 512 loaded but failed every text send with
      `Failed to allocate tensors`.
    - On the GPU, 512 accepted a 427-token first message, and 100 failed every send.
  - `LiteRtEngine.SetMinLogLevel(3)`, called before the first engine, also hides the runtime's early `I0000`
    lines; `W0000` and `WARNING` lines remain (CPU). The docs said the level couldn't hide them.
  - With a cache directory set, the vision and audio encoder caches also go to that directory, not next to the
    model (vision on CPU and GPU, audio on CPU). The drafter's cache file is named
    `<model file>_<mtime>_<size>.mtp_drafter.xnnpack_cache`, with nothing after `xnnpack_cache` (CPU). The docs
    said the cache directory didn't reach the encoders, and gave the drafter's file name a suffix. See
    [Delete the compiled cache after you replace a model](https://orihuelaconde.github.io/LiteRtLmSharp/model-sync.html#delete-the-compiled-cache-after-you-replace-a-model).
  - `LiteRtCache.InMemory` works on Windows with the CPU backend: the engine loads and answers, and no cache
    file is written. The same CPU result was seen with LiteRT-LM v0.18.0 (Qwen3-0.6B). On the GPU, engine
    creation fails with `UNKNOWN: Failed to modify graph with delegate`. The docs said the in-memory cache
    couldn't be used on Windows.
  - On the GPU, Ministral 3 3B (q4) gave the same results with `Float16` and `Float32` activations: both passed
    the same 43 checks of a precision test (NVIDIA RTX 3080). The docs said the `Float16` errors reproduce on
    that model. gemma-4 E2B with `Float16` still gets arithmetic wrong on the GPU: it writes 3052 for
    1024 + 2048.
  - Tool names in camelCase did not lower call accuracy: `get_weather` and `getWeather` each gave 10 correct
    calls out of 10 (CPU). The docs said accuracy suffers with names that aren't snake_case.
  - A win-x64 GPU process that loads and disposes many engines sometimes exited without an error with LiteRT-LM
    v0.18.0, which the 1.3.0 docs listed as a known issue. It did not occur in 12 full GPU runs of the test
    suite with v0.20.0 (NVIDIA RTX 3080).

## [1.3.0] — 2026-10-12

### Changed

- **Native binaries are LiteRT-LM v0.18.0** (from v0.16.0), still Google's official C API prebuilts.
  The C API grew from 144 to 212 functions with no removals and no signature changes, so every
  existing call keeps its ABI. Behaviour differences you may notice:
  - **Linux needs no extra system package for the CPU backend.** The v0.18.0 libraries have no hard
    dependency on the Vulkan loader, so `libvulkan1` is no longer a prerequisite (1.2.0's library did
    not load without it). The GPU backend still runs on Vulkan and needs the GPU's Vulkan driver and
    the loader.
  - **`LiteRtEngineOptions.EnableYnnpack = true` now fails engine creation off linux-arm64.** Only the
    linux-arm64 library carries the YNNPACK kernels; the others reject the flag with
    `LiteRtStatusCode.Unimplemented` (v0.16.0 ignored it). The exception names the setting.
  - **Image bytes are decoded before anything else.** Bytes that are not an image fail with the
    runtime's decoding reason (`INVALID_ARGUMENT: Failed to decode image…`) instead of a setup error.
  - **GPU caches written by an older runtime are rebuilt automatically** on the first load (one slower
    load); nothing to clean up by hand.
  - **A visual token budget above the per-image maximum fails image sends.** The runtime checks
    `VisualTokenBudget` against the engine's per-image maximum (`MaxVisionTokensPerImage`, or the
    model's own: 280 on the Gemma 4 E-series) and fails the send with `LiteRtStatusCode.InvalidArgument`
    (v0.16.0 clamped it). The binding attaches the budget only to sends that carry an image, so text-only
    sends are unaffected.
  - **A cache directory that does not exist fails engine creation.** v0.18.0 checks a
    `LiteRtCache.Directory` path when the engine loads and fails with `LiteRtStatusCode.InvalidArgument`
    ("Cache directory does not exist or is not writable"); v0.16.0 did not check it then. Create the
    directory first, or use `LiteRtCache.Default`.
  - **Speculative decoding on a model without a drafter fails.** The 1.2.0 documentation said
    `EnableSpeculativeDecoding` did nothing on such a model; with v0.18.0, engine creation fails
    (Ministral 3: `NOT_FOUND: tf_lite_mtp_drafter not found in the model`) and the exception names the
    setting. `LiteRtModelInfo.SupportsSpeculativeDecoding` tells before loading.
- **`LiteRtEngineOptions.ActivationDataType` defaults to `Float32`** (it was `null`, the runtime's choice:
  F16 for the text executor on GPU). F16 corrupts structured output (digits, dates, JSON) on the GPUs we
  measured, and on a desktop GPU it is no faster: gemma-4-E2B on an RTX 3080 decodes 113 tok/s with F16
  and 117 with F32, and F32 commits about 0.3 to 0.6 GB more memory. The CPU backend ignores the setting.
  On a Moto G100 (Adreno 650) F32 decodes about 5% slower, doubles the time to first token and uses about
  150 MB more RAM. Set `Float16` to opt back in (after checking your outputs), or `null` for the runtime's
  choice.
- The documentation of `VisualTokenBudget` (conversation and per send) now describes what the runtime
  does: it is a **per-image** budget, and the runtime scales each image down to about the budget in
  tokens. On gemma-4-E2B-it a budget of 70 brings an image from 260 tokens to 68.

### Added

- **Embeddings.** `LiteRtEmbeddingEngine` loads an embedding model, such as EmbeddingGemma 2, and turns
  text into vectors for semantic search, retrieval-augmented generation, clustering or classification:
  `Embed`, `EmbedBatch` and their async versions, with Matryoshka truncation
  (`LiteRtEmbeddingOptions.OutputDimensions`), normalization and a choice of what happens to texts longer
  than the loaded input signatures (`OverflowStrategy`). An embedding engine does not count toward the
  one-live-engine rule, so a chat model and an embedding model can stay loaded together. Calls on one
  embedding engine are serialized, so it is safe to share across threads. Activations default to
  float32 (EmbeddingGemma's model card advises against float16, the runtime's GPU fallback). See the
  [Embeddings guide](https://orihuelaconde.github.io/LiteRtLmSharp/embeddings.html) for the task
  instructions EmbeddingGemma 2 expects and for measurements.
- **`IEmbeddingGenerator<string, Embedding<float>>`** in `LiteRtLmSharp.Extensions.AI`:
  `LiteRtEmbeddingGenerator`, `LiteRtEmbeddingGenerationOptions` and
  `services.AddLiteRtEmbeddingGenerator(...)`, which coexists with `AddLiteRtChatClient`. In
  `LiteRtLmSharp.SemanticKernel`, `IKernelBuilder.AddLiteRtEmbeddingGenerator(...)` registers the same
  generator for Semantic Kernel's vector stores (keyed when you pass a `serviceId`).
- **Model metadata without loading.** `LiteRtModelInfo.Read(path)` reports what a `.litertlm` file
  declares: model type, context size, input modalities, backends per modality, speculative-decoding
  support, vision token sizes, default sampler and, for embedding models, the vector length and input
  lengths. `LiteRtEmbeddingEngine.Load` uses it to reject a language model with a clear message.
- **Runtime packages for linux-arm64 and android-x64**: `LiteRtLmSharp.runtime.linux-arm64` (the CI
  model suite runs on GitHub's arm64 Linux runner) and `LiteRtLmSharp.runtime.android-x64` (the x86_64
  Android emulator; use the CPU backend there). The MAUI sample's APK carries both Android ABIs.
- **Native failure reasons.** A failed native call now reports the runtime's own reason, read from
  LiteRT-LM's thread-local error state: `litert_lm_engine_create returned null: INVALID_ARGUMENT:
  Invalid magic number or failed to read` instead of a bare "returned null" plus a list of guesses.
  `LiteRtException.StatusCode` exposes the status (`LiteRtStatusCode`, the canonical absl codes);
  it is `null` when the runtime reported nothing or the binding detected the failure itself. Streamed
  replies that fail carry the same status and reason. A multi-line native call trace reads as one line:
  the reason, then where the runtime raised it. With the native log silenced
  (`LiteRtEngine.SetMinLogLevel` above 5) LiteRT drops some reasons; the status remains.
- `LiteRtEngineOptions.MaxVisionTokensPerImage` — an upper bound on the vision tokens one image may
  expand to (the engine loads the vision signatures up to it, rounded up to the next signature). Pair a
  cap below the model's default image size with a `VisualTokenBudget` at or below it. A cap above the
  model's largest signature (280 on Gemma 4) fails engine creation.
- `LiteRtEngineOptions.EnableMetalResidencySet` — keeps model weights and allocations resident in GPU
  memory through Apple's `MTLResidencySet` API (Apple GPU backend only; ignored elsewhere).
- `LiteRtConversationOptions.EnableSpeculativeDecoding` — per-conversation speculative decoding that
  overrides the engine setting: `true` on an engine loaded without it loads the MTP drafter lazily on
  the conversation's first send, `false` turns it off for one conversation. Unset keeps the engine
  setting, also after another conversation loaded the drafter (the runtime would otherwise keep using
  it). The `Microsoft.Extensions.AI` and Semantic Kernel conversation-options templates carry it too.

### Fixed

- XML documentation that had gone stale: the disk-cache workaround for speculative decoding on the
  desktop GPU backend is gone (fixed upstream in LiteRT-LM v0.14.0).
- **The LoRA documentation now describes what the runtime does.** A text adapter applies to the whole
  engine, not to the conversation that names it: after a conversation with `LoraPath` has generated,
  conversations without an adapter on the same engine generate with it too, and while a conversation
  with an adapter exists, a send on one without it fails with `LiteRtStatusCode.Internal`. Reload the
  engine to switch adapters. On the published gemma-4 bundles, which have no LoRA slots, an adapter is
  accepted and has no effect (1.2.0 said it failed fast). Tests pin these behaviors.
- A failed send on an engine loaded with `MaxNumTokens` below 1024 no longer blames the small context
  when the runtime reports an unrelated reason.

## [1.2.0] — 2026-09-05

### Changed

- **Native binaries are now Google's official LiteRT-LM v0.16.0 C API prebuilts** (from the
  self-built v0.14.0 set of 1.1.1 to upstream's `litert_lm_c_api-0.1.0.zip` and
  `CLiteRTLM.xcframework.zip`, repackaged by `native-release.yml`, which verifies every upstream
  asset against GitHub's sha256 digests and inspects each library before publishing). Each runtime
  package now ships one monolithic library with the constraint provider, the GPU accelerators and
  the GPU samplers embedded. What changes for consumers: **linux-x64 needs the system Vulkan loader** (`libvulkan1`
  or your distribution's equivalent — the library does not load without it, and the resolver's
  load-failure message says so); **win-x64 no longer needs the VC++ Redistributable** (static CRT)
  and keeps the DirectX Shader Compiler pair (`dxcompiler.dll`, `dxil.dll`) that the GPU backend
  requires; the **Android package is a single `libLiteRtLm.so`** whose embedded OpenCL sampler keeps
  sampling on the GPU (no separate sampler library to load, so LiteRT-LM#2211 no longer applies); the
  **iOS package carries the official `CLiteRTLM.xcframework`** unchanged (device + simulator slices,
  CPU backend) and the resolver loads `Frameworks/CLiteRTLM.framework/CLiteRTLM`.
- **Native ABI: the C API grew 109 → 144 functions since 1.1.1 with no removals**; the only signature
  changes formalize two `int` parameters into enums with identical values (`set_activation_data_type`,
  `set_min_log_level`), so every previously bound function keeps its ABI. The **streaming callback ABI
  did change** upstream — the runtime now passes an opaque stream-chunk object read through getters
  instead of `(text, is_final, error_msg)` parameters — and the binding's internal callback was
  rewritten to match; the managed streaming API and chunk semantics (including the max-tokens and
  cancellation error strings) are unchanged. Because of that change, this release's managed assembly
  requires the same-version `LiteRtLmSharp.runtime.*` packages and is not compatible with the 1.1.x
  (v0.14.0) runtime packages.
- **`EnableConstrainedDecoding` works on linux-x64.** The temporary `PlatformNotSupportedException`
  guard is gone: the official build embeds the constraint provider, and tool calling with constrained
  decoding passes the same end-to-end test on every OS (LiteRT-LM#2149 no longer applies).
- **Text LoRA is applied.** With a LoRA-enabled bundle and a matching adapter,
  `LiteRtConversationOptions.LoraPath` changes generation (the v0.14.0 runtime of 1.1.x rejected every
  text adapter with "Lora is not supported"); the test that pinned that stub now asserts the adapter
  takes effect. The published gemma-4 bundles carry no LoRA slots (LiteRT-LM#3173), so an
  adapter still fails fast on them.
- A send that fails on an engine loaded with `MaxNumTokens` below 1024 now names the likely cause:
  the native executor accepts a smaller limit, but prefill breaks once an input spans more than the
  smallest work group (1024 is the largest prefill signature of the published gemma conversions).
  The limit is not queryable through the C API, so this is guidance on failure rather than up-front
  validation; the `MaxNumTokens` documentation carries the same floor.
- **KV overflow guard recalibrated to the runtime's new prefill planning.** The native executor now plans
  prefill in fixed work groups taken from the model's prefill signatures and rejects any send with
  fewer free KV entries than the smallest signature (~128 in the current gemma conversions) — a
  clean native failure where the v0.14.0 runtime of 1.1.x corrupted memory. The guard's context-full
  threshold reserves that minimum (so `IsContextFull` / `ChatFinishReason.Length` signal up to 112
  tokens earlier than before and a conversation's usable context shrinks accordingly), the
  message-fit check accounts for the work-group rounding (an input's plan consumes whole signature
  lengths), and a `MaxNumTokens` below the minimum work group is rejected with a message naming the
  real problem. The typed `LiteRtContextOverflowException` and same-turn `IsContextFull` contract
  are unchanged. Note the reserve constant matches the current gemma `.litertlm` conversions; a
  model with a larger smallest signature can still fail a send natively (with a plain
  `LiteRtException`) slightly before the typed rejection.
- **`LiteRtConversation.Clone()` now requires a conversation that has advanced at least once** and
  throws `InvalidOperationException` (naming the fix) when `TokenCount` is 0. The native clone
  duplicates the *prefilled* state and creating a conversation prefills nothing, so cloning a
  never-sent conversation produced a branch that silently continued whatever conversation ran last
  on the engine (found by a downstream consumer as "a conversation gets invalidated when another
  runs on the engine"; narrowed with a binding-only repro on LiteRT-LM v0.16.0, CPU
  and GPU). The first clone of an unsent base happened to work, which is why the pattern looked
  valid. Send one real turn on the base, then clone: that sequence is stable across any
  interleaving. The MEAI fork (`LiteRtConversationBranching.ForkAsync`) is unaffected: a forkable
  id only exists after a send.

### Added

- `LiteRtEngineOptions.EnableYnnpack` — opts the CPU backend into the experimental YNNPACK delegate
  (native v0.16.0+). Upstream ships its kernels in the linux-arm64 builds; the other official
  prebuilts accept the flag and run unchanged.
- MEAI and Semantic Kernel surface for the two new logit processors that had no typed option:
  `LiteRtChatOptions.NoRepeatNgramSize` / `.SuppressTokens` (bag keys `no_repeat_ngram_size` /
  `suppress_tokens`, so a plain `ChatOptions` works too) and `LiteRtPromptExecutionSettings.NoRepeatNgramSize`
  / `.SuppressTokens`, mapped onto `LiteRtSendOptions.NoRepeatNgram` / `.SuppressTokens` on every send.
- **Per-send decoding controls** on `LiteRtSendOptions` (all require native v0.15.0+, and apply to
  one send's generated output):
  - `RepetitionPenalties` — multiplicative (HuggingFace-style) repetition penalty plus subtractive
    (OpenAI-style) presence/frequency penalties, with a configurable recent-tokens window.
  - `NoRepeatNgram` — bans repeating n-grams of a configured size during decode.
  - `SuppressTokens` — token ids whose logits are forced to `-inf` on every decode step.
  - `EnableThinking` / `ThinkingTokenBudget` — per-send reasoning-mode override and budget.
  - `Constraint` — a regex or JSON-Schema output constraint (see the constraint provider below).
- `LiteRtConversationOptions.ThinkingTokenBudget` — caps how many tokens the model spends on its
  thinking block (enforced token-by-token natively), independent of `MaxOutputTokens`.
- `LiteRtConversationOptions.PromptTemplate` — overrides the model's chat template with a custom
  Jinja template string.
- **Custom constrained decoding (LlGuidance)**: `LiteRtConversationOptions.ConstraintProvider` +
  `LiteRtConstraint.FromRegex(...)` / `FromJsonSchema(...)` per send force the output to match a
  regular expression or conform to a JSON Schema. Mutually exclusive with the tool-calling
  `EnableConstrainedDecoding` path. The LlGuidance engine is compiled into the native library from
  source, so it does not depend on the platform prebuilt that keeps tool-calling constrained
  decoding blocked on linux-x64.
- Engine options: `GpuDecodeStepsPerSync` and `GpuWaitForWeightUploads` (Artisan GPU backend), and
  `UseRingbuffersLocalAttention` (ringbuffer KV cache for local-attention layers — lower memory on
  long contexts).
- `Microsoft.Extensions.AI` connector: `ChatOptions.FrequencyPenalty` / `PresencePenalty` now map to
  the native penalties, and a JSON-schema `ChatOptions.ResponseFormat` is now **enforced during
  sampling** via LlGuidance — the reply is guaranteed to be a conforming JSON document (previously
  the schema only influenced the prompt). Constraints and tools are mutually exclusive (the schema
  masks the tool-call format), so a request carrying both throws `ArgumentException`; in stateful
  mode the schema format must be present on the conversation's first call (or the
  conversation-options template must set `ConstraintProvider`) — a schema-only continuation on a
  provider-less conversation is rejected in MEAI vocabulary and leaves the conversation resumable.
- Semantic Kernel connector: `LiteRtPromptExecutionSettings.PresencePenalty` / `FrequencyPenalty`
  (the standard `presence_penalty` / `frequency_penalty` keys, read by SK's adapter).
- **Multiple live stateful conversations** (`Microsoft.Extensions.AI` connector): the stateful mode's
  live-conversation cache is no longer hard-limited to one —
  `LiteRtStatefulConversationOptions.MaxLiveConversations` (LRU cap, default 8) is public again.
  LiteRT-LM v0.15.0 fixed the upstream state loss that forced the limit: a suspended conversation now
  keeps its own turns when another conversation advances (verified by an interleaved-recall canary test
  and the full multi-conversation suite; the limitation shipped guarded since 1.1.0).
- **Conversation forking** (`Microsoft.Extensions.AI` connector): `LiteRtConversationBranching` —
  obtained via `GetService(typeof(LiteRtConversationBranching))` on a stateful client — forks a live
  conversation into an independent branch that shares the parent's prefilled context (native KV-cache
  clone, no re-prefill) and diverges from there. Implemented and validated since 2026-07-10, held
  internal until the native runtime could preserve suspended-conversation state (v0.15.0+).

## [1.1.1] — 2026-07-18

Managed-only fix. The `LiteRtLmSharp.runtime.<rid>` packages bump to 1.1.1 per the shared-version
policy but contain the same LiteRT-LM v0.14.0 natives as 1.1.0 — a 1.1.0 runtime package alongside
the 1.1.1 base package also works.

### Fixed

- **GPU engine creation failed for NuGet consumers on Windows** — a framework-dependent app
  consuming `LiteRtLmSharp` + `LiteRtLmSharp.runtime.win-x64` with `Backend = Gpu` got
  `LiteRtException: litert_lm_engine_create returned null` (native stderr: `Failed to create
  engine: INTERNAL`, zero WebGPU/Dawn init lines). The NuGet layout puts the natives only under
  `runtimes/win-x64/native/`; the resolver loaded the main `LiteRtLm.dll` from there (and its
  static PE imports followed via the altered search path — which is why CPU worked), but the
  engine loads its accelerators **dynamically at runtime by base name**
  (`libLiteRtWebGpuAccelerator.dll` from libLiteRt, `libLiteRtTopKWebGpuSampler.dll` from
  LiteRtLm, and `dxcompiler.dll`/`dxil.dll` from Dawn's shader compiler), and that native
  `LoadLibrary` searches the exe directory + PATH — never the `runtimes/` subfolder. The resolver
  now pre-loads the companion DLLs by absolute path on Windows too (as it already did on
  Linux/macOS), so every later by-name load resolves via the OS loader's already-loaded-module
  short-circuit, regardless of the search flags the native code uses. The byte-identical
  non-`lib`-prefixed twin DLLs the package ships are skipped (nothing references them; loading
  both would map a second copy of Dawn and its process-global GPU state). The preload applies only
  to the `runtimes/` layout: natives placed flat next to the executable already resolve via the
  native search order (exe directory first), and sweeping the app output would load unrelated
  DLLs. Side effect: CPU-only Windows consumers now map the GPU stack (Dawn, DXC) at first use
  of the library — a startup time/working-set cost, not a behavior change; Dawn touches no GPU
  until an adapter is requested, and load failures are ignored as before. ProjectReference consumers never saw the
  bug because the repo build copies the natives flat next to the app.

## [1.1.0] — 2026-07-17

Requires **LiteRT-LM v0.14.0** native binaries (the `LiteRtLmSharp.runtime.<rid>` packages bump to
match). The managed surface is source-compatible with 1.0.0 — all changes below are additive.

### Added

- **KV overflow guard** — the native runtime does not police `LiteRtEngineOptions.MaxNumTokens`: a send
  that grows the conversation past it writes beyond the allocated KV cache and corrupts native memory
  (typically a deferred `0xC0000005`/`0xC0000374` process crash on a later call — reachable from a long
  function-calling loop in the stateful mode). When the engine is loaded with an explicit `MaxNumTokens`,
  every send now measures its real prefill cost (render + tokenize, no inference), clamps the reply's
  decode budget to the remaining context via the per-send output cap, and throws the new
  `LiteRtContextOverflowException` (with `TokenCount`/`MaxNumTokens`) once the conversation is full or the
  message cannot fit — instead of crashing the process. `LiteRtException` is now unsealed so the new
  exception derives from it (existing `catch (LiteRtException)` handlers keep working). In the
  Extensions.AI stateful mode the full conversation is evicted on throw, like a cancelled one. The
  measurement is best-effort: messages with media, sends carrying `SendRaw`'s per-send `extraContext`,
  and natives that cannot render/tokenize keep only the already-full hard stop (their prefill cost is not
  measurable managed-side — leave headroom under the limit on those paths). In the stateful mode, a
  "message doesn't fit" rejection happens before any native work and keeps the live conversation
  resumable (retry the id with a shorter message); only a "context is full" rejection evicts it. Without
  an explicit `MaxNumTokens` the limit is internal to the engine (the C API has no getter) and behavior
  is unchanged. Measured overhead (gemma-4-E2B, win-x64 CPU): ~0.2 ms per mid-conversation send, ~4 ms on
  the first send of a conversation carrying a ~1.4k-token preface — the render + tokenize run no
  inference, and the token count is read off the native result without copying the id array.

  The guard also signals **in the same turn** that a reply hit the wall, so callers never depend on the
  next call's exception to learn the conversation is over: the new **`LiteRtConversation.IsContextFull`**
  is `true` exactly when the next send would throw — a clamped reply is detected exactly, from the guard's
  own counts (tolerating measurement drift up to the safety margin either way), with a `TokenCount`
  threshold as the backstop that also covers unmeasured media sends. Check it right after a send, or after
  a stream completes; it shares its predicate with the guard's hard stop, so the two can never disagree. The
  Extensions.AI client maps it to **`ChatFinishReason.Length`** on the clamped response (and on the final
  update of a clamped stream). `Length` deliberately wins over `ToolCalls` there: even a complete tool
  call cannot be continued on a full conversation — sending its results would throw — so `Length` is the
  cue to wind the thread down. Note that `FunctionInvokingChatClient` loops on function-call content
  regardless of finish reason, so an unattended tool loop still ends in the (clean, evicting)
  `LiteRtContextOverflowException`; the signal is for callers who look.

- **CPU thread counts** — `LiteRtEngineOptions.NumThreads` and `AudioNumThreads` set the CPU text /
  audio executor thread counts (`null` = engine default). CPU-backend knobs: no-op on non-CPU backends,
  and the audio one only applies when an audio executor is configured. Non-positive values are rejected.
- **LoRA surface** — engine-level `LiteRtEngineOptions.LoraRank`, `SupportedLoraRanks`, `AudioLoraRank`,
  `SupportedAudioLoraRanks`, plus per-conversation `LiteRtConversationOptions.LoraPath` /
  `AudioLoraPath` (the adapter file is opened when the conversation is created, so a bad path fails fast
  with `LiteRtException`). Requires a LoRA-enabled model; supported-ranks are only honored on the GPU
  (Artisan) backend. **The TEXT LoRA path is stubbed in the LiteRT-LM v0.14.0 runtime**: loading a valid
  text adapter succeeds at conversation creation, but the first generation fails upstream with
  `Lora is not supported` (tracked internally by Google). The audio LoRA path is wired upstream. Our surface
  is ready either way — the adapter loads and, where the runtime stubs generation, the failure is surfaced
  coherently as a `LiteRtException`.
- **`LiteRtConversation.RenderPreface()`** — renders the templated conversation preface (system message
  + tools + history) to a string without sending, complementing `RenderMessage`. Pair with
  `LiteRtEngine.Tokenize` to measure the preamble's token cost.
- **Per-send output cap** — `LiteRtSendOptions.MaxOutputTokens` overrides the conversation-level
  `MaxOutputTokens` for a single send (maps to the v0.14.0 `optional_args_set_max_output_tokens`; the
  runtime resolves the per-send value over the session value).
- **Tool-call streaming** — `LiteRtConversationOptions.StreamToolCalls` streams the raw text of a tool
  call while the model generates it: `SendStreamingAsync` yields the new
  `LiteRtStreamChunkKind.ToolCallDelta` chunks (incremental, unparsed progress fragments) before the
  usual complete `ToolCall` chunk. Default off — without it a tool-call block keeps today's behavior
  (silence until the parsed call arrives whole). Progress display only; act on the final parsed chunk.
- **`.NET AI connectors` — opt-in stateful conversations (MEAI `ConversationId` contract)** —
  `LiteRtChatClient` can now keep the live native `LiteRtConversation` alive between calls instead of
  rebuilding it from the full message list each turn. Opt in with a new `LiteRtStatefulConversationOptions`
  on the constructor and on both `AddLiteRtChatClient(...)` and Semantic Kernel's `AddLiteRtChatCompletion(...)`
  overloads (an appended optional parameter; `null` = today's stateless behavior, byte-identical). When
  enabled the client implements Microsoft.Extensions.AI's canonical stateful-provider contract: the first
  call (no `ChatOptions.ConversationId`) returns a `ChatResponse.ConversationId`, and setting that id on the
  next request sends only the not-yet-seen messages (typically just the new user turn) to resume the live
  conversation with no history re-prefill, so each turn costs only its own tokens. Because a non-null
  `ConversationId` makes `FunctionInvokingChatClient` (`UseFunctionInvocation()`) send only the new
  function-result message(s) on its next iteration, multi-round tool loops become incremental automatically.
  The conversation's session settings (sampler, thinking, tools, constrained decoding, system message,
  template values) are fixed when it is created; on a continuation those per-request knobs are ignored (only
  the per-send `MaxOutputTokens` still applies) and a system message throws. This mode keeps a **single** live
  conversation: starting a new conversation (a call without a `ConversationId`) replaces the previous one, and
  resuming a replaced (or never-issued) id throws `ArgumentException`. Multi-live-conversation support is held
  back by a current LiteRT-LM runtime limitation (a suspended conversation's state is not preserved when
  another advances), so there is no knob to raise the limit. Disposing the client disposes the live
  conversation. `ChatResponse.Usage.TotalTokenCount` now reflects the conversation's cumulative total across
  the stateful thread. See `docs/extensions-ai.md`.
- **`.NET AI connectors` — per-client conversation-options template** — `LiteRtChatClient` takes an
  optional `LiteRtConversationOptions optionsTemplate`, and the DI registrations expose it too
  (`AddLiteRtChatClient(...)` and Semantic Kernel's `AddLiteRtChatCompletion(...)`). It surfaces the
  conversation-level settings MEAI's `ChatOptions` cannot express — `SystemMessage`, `LoraPath` /
  `AudioLoraPath`, `StreamToolCalls`, `VisualTokenBudget`, `FilterThinkingFromKvCache`, `ExtraContext`,
  and a session-default `MaxOutputTokens` — applied to every call. Per-call `ChatOptions` values still
  win where MEAI supplies them (sampler, thinking, constrained decoding, tools, system message); the
  template's `SystemMessage` is used only when the request carries no system message (never two system
  turns). When the template enables `StreamToolCalls`, streaming now surfaces each tool-call delta as a
  content-less `ChatResponseUpdate` carrying the raw fragment under `AdditionalProperties`
  (`litertlm.tool_call_delta`) — opt-in by construction, invisible otherwise. A template that carries
  `History` / `HistoryJson` is rejected at registration (history is per-call).

### Fixed

- **`LiteRtConversationOptions.SystemMessage` was silently ignored** — the binding wrapped the text in
  a bare `{"type":"text",...}` part object; the native side JSON-parses that into the message content
  and the chat template drops object-valued content, so the system turn rendered empty
  (`<|turn>system\n<turn|>`) and the model never saw the prompt. The system text is now sent as a
  content-parts **array**, which the template renders. Diagnosed and verified with the new
  `RenderPreface()` (the system prompt now appears in the templated preface). Workaround on older
  versions: pass the system prompt as a leading `LiteRtMessage.System` history message instead
  (the path `LiteRtChatClient` uses, which was never affected). **Migration note:** if you used that
  History workaround while ALSO leaving `SystemMessage` set, remove one of the two — both now render,
  which yields two system turns (the long-documented behavior of setting the system prompt in both
  places; check with `RenderPreface()`).

### Changed

- **GPU + speculative decoding no longer needs the disk cache off** — the upstream shared
  weight-cache mmap failure ([LiteRT-LM#2572](https://github.com/google-ai-edge/LiteRT-LM/issues/2572))
  is fixed in the v0.14.0 natives and re-verified end to end (engine create, generation, and cache
  readback across reloads on win-x64 WebGPU). The samples' automatic `Cache = LiteRtCache.Disabled`
  workaround for that combination is removed; `docs/speculative-decoding.md` keeps the v0.13.1
  behavior as a historical record.
- **`.NET AI connector` maps `ChatOptions.MaxOutputTokens` per-send (internal)** — the
  Microsoft.Extensions.AI `LiteRtChatClient` now maps the MEAI per-request `MaxOutputTokens` to the
  v0.14.0 per-send cap (`LiteRtSendOptions.MaxOutputTokens`) instead of the conversation-level session
  config. Behavior-neutral (the client already creates a fresh conversation per call), a more faithful
  match for MEAI's per-request semantics, and it avoids allocating a session config when the output cap
  is the only option set. Multimodal-safe: a multimodal engine forces the session config into existence
  regardless, so the encoder still loads.
- **Sampler params migration (internal)** — v0.14.0 replaced the by-value `LiteRtLmSamplerParams` struct
  with an opaque builder API (`sampler_params_create` + `set_top_k/top_p/temperature/seed`, copied into
  the session config then deleted). The binding was rewired to the builder; no public surface change.
  The native enum dropped its `unspecified` member — the public `LiteRtSamplerType.Unspecified` is
  retained (at `0`, no break) and now means what its docs always said: no sampler parameters are sent
  and the executor's internal default sampling applies (the same effective behavior as v0.13.1, where
  the unspecified type made the native sampler factory defer to the executor).

## [1.0.0] — 2026-07-02

The first stable release: everything shipped in the `0.1.0-preview` line plus the changes below.
Breaking changes for preview consumers are listed under **Changed**. Built against
**LiteRT-LM v0.13.1** (same native binaries as preview.2/preview.3).

### Added

- **.NET AI ecosystem integration (two optional companion packages).** An on-device model can now be plugged
  into the .NET AI stack:
  - **`LiteRtLmSharp.Extensions.AI`** exposes the model as a `Microsoft.Extensions.AI.IChatClient`
    (`LiteRtChatClient`) — the provider-agnostic chat abstraction that underpins both Semantic Kernel and the
    Microsoft Agent Framework, so this one package makes the model usable from **Agent Framework**
    (`new ChatClientAgent(client, …)`), **plain Microsoft.Extensions.AI** (the middleware pipeline, DI), and
    Semantic Kernel. Blocking + streaming, with `AddLiteRtChatClient(engine | options)` DI registration.
    Reasoning ("thinking") is surfaced as `TextReasoningContent` (excluded from `ChatResponse.Text`), and a
    truncated answer (the reasoning consuming the output budget) is flagged with a `Length` finish reason.
    `ChatResponse.Usage` is populated: `TotalTokenCount` always (free), with the `InputTokenCount`/`OutputTokenCount`
    split when the engine is loaded with `EnableBenchmark = true` (a note on `response.AdditionalProperties` says
    so when it is off). Depends only on lightweight abstractions — `Microsoft.Extensions.AI.Abstractions` and
    `Microsoft.Extensions.DependencyInjection.Abstractions` (+ the core `LiteRtLmSharp` package).
  - **`LiteRtLmSharp.SemanticKernel`** adds the model to [Semantic Kernel](https://learn.microsoft.com/semantic-kernel/overview/)
    as an `IChatCompletionService` via `builder.AddLiteRtChatCompletion(engine | options)`. It is a thin layer
    over the `IChatClient` (exposed through SK's `AsChatCompletionService` adapter), with a
    `LiteRtPromptExecutionSettings` (temperature / top-p / top-k / max-tokens / seed / thinking / constrained
    decoding) whose knobs flow through SK's `PromptExecutionSettings → ChatOptions` conversion. Depends on
    `LiteRtLmSharp.Extensions.AI` + `Microsoft.SemanticKernel.Abstractions` + `Microsoft.Extensions.AI`.
  - **Function calling** is supported on both connectors: function tools in `ChatOptions.Tools` are passed to
    the model and the model's tool calls surface as `FunctionCallContent`, so MEAI's `UseFunctionInvocation()`
    and Semantic Kernel's `FunctionChoiceBehavior` auto-invoke your functions and feed the results back. (The SK
    connector wraps the client with MEAI's function-invocation middleware, since SK's `AsChatCompletionService`
    adapter does not run the auto-invoke loop itself.) Opt-in `EnableConstrainedDecoding` (off by default; blocked
    on linux-x64) makes small models emit valid tool-call arguments. `ChatOptions.ToolMode` /
    `FunctionChoiceBehavior` is honored: `None` offers no tools; `RequireAny`/`RequireSpecific` are best-effort
    (the connector instructs the model and narrows the offered tools, but the on-device decoder can't be *forced*
    like a cloud `tool_choice: required`).
  - **Multimodal** (image / audio) is supported on both connectors: image/audio content on the final user
    message — a MEAI `DataContent` (inline bytes) or file-path `UriContent`, or a Semantic Kernel
    `ImageContent` / `AudioContent` (which the SK adapter forwards as `DataContent`) — is sent to the model as
    an attachment. Requires the engine loaded with the matching modality (`LiteRtEngineOptions.VisionBackend` /
    `AudioBackend`) on a multimodal model; only the triggering turn's media is sent (the stateless connector
    restores prior turns as text).
  - Both connectors are **stateless** (a fresh `LiteRtConversation` is rebuilt from the supplied history each
    call, prior turns replayed through prefill) and serialize calls (one live engine per process). Embeddings
    remain unavailable while the C API exposes none (v0.13.1). A console sample is in
    [`samples/SemanticKernel`](samples/SemanticKernel) (prompt function, streaming, multi-turn chat, function
    calling); guides: [docs/extensions-ai.md](docs/extensions-ai.md) and [docs/semantic-kernel.md](docs/semantic-kernel.md).
    Validated end-to-end on win-x64 CPU and GPU/WebGPU with gemma-4-E2B-it; mapping/settings/registration logic is
    unit-tested model-free in CI, with gated model-backed tests (chat blocking/streaming, multi-turn history replay,
    reasoning + truncation, function calling for both MEAI and SK, and image/audio attachments) under `LITERTLM_TEST_MODEL`.
- **Multi-turn multimodal history.** A restored conversation history can carry image/audio:
  `LiteRtMessage` gains an `Attachments` property and a `User(text, attachments)` factory,
  `LiteRtMessage.Serialize`/`Deserialize` round-trip media content parts, and history attachments are
  re-encoded through prefill — so a thread can refer back to an image/audio sent in an earlier turn.
  The MEAI / Semantic Kernel connectors restore prior-turn `DataContent` / file-`UriContent` media the
  same way.
- **`LiteRtBackend.Npu`** plus **`LiteRtBackend.Custom(string)`** — NPU support and a passthrough escape
  hatch for custom native builds exposing additional backends.
- **XML documentation now ships** in all three packages (`GenerateDocumentationFile`), so the public API
  appears in consumers' IntelliSense (the hand-written `///` docs were previously not emitted).
- **Cancellation.** `LiteRtConversation.CancelProcess()` cancels any in-flight generation from any
  thread (parity with the Kotlin/JS bindings' `cancelProcess`/`cancel` — the native cancel reaches the
  execution manager, so it cuts blocking sends short too, not just streams). The new awaitable
  `SendAsync(text[, attachments], ct)` and `SendToolResultsAsync(results, ct)` wrap the blocking send
  with that mechanism: cancelling the token cancels the native inference mid-generation and the task
  faults with `OperationCanceledException`. After a cancelled send, dispose the conversation and
  continue on a fresh one (restoring `History` if needed): sending again on a cancelled conversation
  hangs inside the native runtime — reproduced with Google's own binaries at v0.13.1 — while the engine
  itself is unaffected. The `IChatClient` connector's `GetResponseAsync` now honors its
  `CancellationToken` mid-generation as well (its stateless one-conversation-per-call design already
  matches the dispose-after-cancel contract).
- **Actionable native-load errors.** When the native library cannot be resolved, the
  `DllNotFoundException` now says what to do instead of a bare "Unable to load DLL": a missing binary
  names the `LiteRtLmSharp.runtime.<rid>` package for the process's RID (and the searched paths), while a
  binary that is present but fails to load points at the usual system prerequisites (VC++ Redistributable
  on Windows, missing system libraries on Linux, architecture mismatch).

### Changed (breaking — update `0.1.0-preview` consumers)

Surface clean-ups for the first stable release; source-breaking for preview consumers.

- **One send family on `LiteRtConversation`.** `Send(text[, attachments[, options]])` returning
  `LiteRtResponse` is the single blocking entry point: the string-returning
  `SendMessage(text[, attachments])` convenience is removed — read the answer from `Send(...).Text`
  (tool calls and the reasoning trace live on the same response, so nothing is silently dropped).
  `SendMessageStreamingAsync` is renamed to `SendStreamingAsync` and `SendMessageRaw` to `SendRaw`, so
  the whole family shares the `Send` stem. `attachments` is now uniformly nullable (null/empty =
  text-only) across the blocking, async and streaming variants, and every send accepts an optional
  **`LiteRtSendOptions`** — per-send settings (today `VisualTokenBudget`, overriding the
  conversation-level value) with room for the per-send knobs future native versions add.
- **`LiteRtEngineOptions.ModelPath` is no longer `required`** (it must still be set —
  `LiteRtEngine.Load` throws when it is empty). This leaves room for alternate model sources (e.g. the
  file-descriptor load newer native versions expose) to land as sibling properties without breaking.
- **Sampler types renamed** to match the `LiteRt` prefix every other public type uses:
  `SamplerType` → `LiteRtSamplerType`, `SamplerParams` → `LiteRtSamplerParams`, and
  `LiteRtSamplerParams.Type` → `.Strategy`. The knobs now validate on assignment (`TopK` positive,
  `TopP` in [0, 1], `Temperature` non-negative, NaN rejected). The defaults — `TopP`, 40, 0.95, 1.0,
  seed 0 — are the same values Google's official bindings fill in, so a partial configuration behaves
  identically across the LiteRT-LM ecosystem; seed 0 means sampling is **deterministic by default**
  (identical context reproduces the identical reply) — pass e.g. `Random.Shared.Next()` for varied
  output.
- **Backends are a typed `LiteRtBackend`** smart enum (`Cpu` / `Gpu` / `Npu` / `Custom(string)` /
  `Parse(string)`) instead of magic strings. `LiteRtEngineOptions.Backend` is now `LiteRtBackend` (was
  `string`), and `VisionBackend` / `AudioBackend` are `LiteRtBackend?` (were `string?`; `null` still = off).
- **Cache configuration is a typed `LiteRtCache`** (`Default` / `Disabled` / `InMemory` / `Directory(path)`).
  The `LiteRtEngineOptions.CacheDir` string and the `CacheDisabled` / `CacheInMemory` string sentinels are
  replaced by `LiteRtEngineOptions.Cache`.
- **Public enums carry explicit numeric values** (`LiteRtMessageRole`, `LiteRtAttachmentKind`,
  `LiteRtStreamChunkKind`) so inserting a member can't silently renumber the others.
- **Construction-time validation tightened** where a bad value previously surfaced as an opaque native
  failure: `LiteRtAttachment.Image/Audio` reject empty bytes, `LiteRtCache.Directory` rejects paths
  starting with `':'` (reserved for the engine's special cache tokens), and `LiteRtBackend.Parse`
  whitespace-normalizes custom values so `Parse(" xpu ")` equals `Parse("xpu")`.
- **`LiteRtMessage.Deserialize` rejects unknown roles.** A message whose role is not
  `system`/`user`/`model` (or `assistant`)/`tool` now throws `ArgumentException` instead of being
  silently parsed as a user turn — on the recommended persistence path, failing loudly beats
  misattributing who said what. Content parts of unknown types are still skipped (documented).

### Fixed

- **Streaming teardown no longer deadlocks on early exit.** Abandoning `SendStreamingAsync`
  mid-generation — cancelling the token, or simply `break`ing out of the `await foreach` — could hang
  forever: the teardown awaited the channel's completion without draining the chunks the consumer never
  read, and a channel only completes once its buffer is empty. The teardown now cancels the native
  inference and drains the channel, so early exits return promptly.
- **Blocking-send lifetime edge case.** A caller whose last use of a conversation was a blocking send
  could, in principle, have the wrapper garbage-collected while the native call was still running — the
  handle's finalizer would then delete the native conversation mid-generation. The blocking send now
  pins the wrapper for the duration of the call.
- **One-engine-alive counter leak.** The process-wide "single live engine" slot is now released when the
  native engine is destroyed — on `Dispose` *and* on finalization — so an engine that is garbage-collected
  without `Dispose` no longer leaves the slot stuck (previously the process could never load another model).

## [0.1.0-preview.3] — 2026-06-20

Built against **LiteRT-LM v0.13.1** (same native binaries as preview.2 — this release is binding
and CI work).

### Added

- **Token counting (tokenizer surface).** `LiteRtEngine.Tokenize(text)` and `Detokenize(ids)` run the
  model's own tokenizer with no inference (exact prompt budgeting against `MaxNumTokens`), and
  `GetStartToken()` / `GetStopTokens()` expose the model's configured start (BOS) / stop (EOS) tokens as
  `LiteRtTokenUnion` values, each a literal string or a token-id sequence. Binds 16 more C-API functions
  (now 61 of 89). Validated on win-x64 CPU with gemma-4-E2B-it.
- **Prompt rendering.** `LiteRtConversation.RenderMessage(text)` (and raw `RenderMessageRaw(json)`)
  returns the exact templated prompt a message would produce, without sending it (the KV cache is
  untouched). Pairs with the tokenizer to measure a turn's real token cost, chat template included; the
  Console sample's tokenizer demo shows the raw-vs-templated token contrast.
- **Engine tuning settings.** `LiteRtEngineOptions.PrefillChunkSize` (CPU/dynamic models),
  `ParallelFileSectionLoading` (parallel `.litertlm` load, on by default), and `ActivationDataType`
  (`LiteRtActivationDataType`: F32 / F16 / I16 / I8) for activation precision.
- **Synthetic benchmark.** `LiteRtEngineOptions.BenchmarkPrefillTokens` / `BenchmarkDecodeTokens` run a
  content-independent throughput benchmark — the prompt is padded/truncated to the prefill count and
  decode runs exactly the decode count, so `GetBenchmarkInfo` reports timings at fixed token counts (the
  reply is not a real answer; setting either also enables benchmark mode).

### Fixed

- **Multimodal works from a plain conversation.** A conversation created with `CreateConversation()`
  (no options) on an engine loaded with `VisionBackend` / `AudioBackend` can now send image/audio
  attachments. The vision/audio executor only loads when the conversation carries a session config; the
  binding now attaches one automatically when the engine is multimodal. Previously a bare conversation
  failed with "Vision executor should not be null" unless you also set `MaxOutputTokens` or a sampler.
  This also corrects earlier guidance that blamed a too-small `MaxNumTokens`: the real cause was the
  missing session config; `MaxNumTokens` only needs room for the media's tokens (~256 for an image).

### Changed

- **Clearer multimodal setup error.** When an image or audio send still fails because the engine cannot
  process it (model not multimodal, `VisionBackend` / `AudioBackend` not set, or `MaxNumTokens` cannot
  hold the media), the binding now throws a `LiteRtException` naming those causes, instead of the bare
  native "Vision/Audio executor should not be null". Applies to both the blocking and streaming paths.

### Testing & CI

_Repository changes, not part of the shipped library._

- **Model-backed tests now run on every push/PR** across linux-x64 / win-x64 / osx-arm64 (CPU, plus
  macOS GPU), instead of weekly: `model-tests.yml` became a reusable workflow that `ci.yml` calls, and
  the no-model build matrix also gained macOS. `model-tests.yml` stays dispatchable for a single OS on
  demand.

## [0.1.0-preview.2] — 2026-06-17

Built against **LiteRT-LM v0.13.1** (same native binaries as preview.1 — this release is binding,
sample and CI work).

### Added

- **Multimodal messages (image / audio).** Attach images or audio clips to a turn with
  `LiteRtAttachment` (`Image`/`ImageFile`/`Audio`/`AudioFile`) via new `Send`, `SendMessage` and
  `SendMessageStreamingAsync` overloads. Enable the encoders with `LiteRtEngineOptions.VisionBackend` /
  `AudioBackend`; bound `LiteRtConversationOptions.VisualTokenBudget` and `LiteRtEngineOptions.MaxNumImages`.
- **Conversation restore.** `LiteRtConversationOptions.History` / `HistoryJson` re-prefill prior turns;
  `LiteRtMessage` with `Serialize`/`Deserialize` and `LiteRtResponse.ToMessage()` for a caller-owned
  persist/reload round-trip.
- **Conversation clone.** `LiteRtConversation.Clone()` forks a conversation from a copy of its prefilled
  KV-cache state.
- **Reasoning ("thinking") mode.** `LiteRtConversationOptions.EnableThinking`, `ExtraContext` and
  `FilterThinkingFromKvCache`; streamed chunks are tagged answer / thinking / tool-call
  (`LiteRtStreamChunkKind`), and `LiteRtResponse.Thinking` / `Channels` expose the reasoning trace.
- **Speculative decoding.** `LiteRtEngineOptions.EnableSpeculativeDecoding` (MTP drafter).
- **Benchmark API.** `LiteRtEngineOptions.EnableBenchmark` + `LiteRtConversation.GetBenchmarkInfo()`
  (prefill / decode tokens-per-second, time-to-first-token, init time).
- **Engine cache directory control.** `LiteRtEngineOptions.CacheDir` with `CacheDisabled` / `CacheInMemory`
  sentinels.

### Changed

- **MAUI sample:** tools page, in-app model/backend switching, reasoning and speculative toggles,
  image/audio attach buttons with a modality indicator, and a context/benchmark gauge. **Console sample:**
  streaming, tools, speculative and thinking flags.

### Fixed

- **linux-x64 constrained decoding no longer crashes the process.** `EnableConstrainedDecoding = true`
  now throws a clear `PlatformNotSupportedException` because the upstream prebuilt constraint provider
  shipped with LiteRT-LM v0.13.1 returns broken constraints
  ([LiteRT-LM#2149](https://github.com/google-ai-edge/LiteRT-LM/issues/2149)). Tools still work with
  constrained decoding off. The guard is removed once upstream ships a fixed binary.

### Testing & CI

_Repository changes, not part of the shipped library._

- **macOS (osx-arm64) validated in CI** on CPU and GPU (the WebGPU / Dawn→Metal delegate); the GPU pass
  is a required check.
- **Weekly model-tests matrix** across linux-x64 / win-x64 / osx-arm64 covering blocking chat, streaming,
  tool calling (with and without constrained decoding), a speculative-decoding A/B benchmark, and
  multimodal image/audio.

### Known issues

- Multimodal needs a sufficient context window: set `LiteRtEngineOptions.MaxNumTokens` to **4096 or
  more**. A small window (e.g. 2048) cannot fit the image's vision tokens and the first image send fails
  with "Vision executor should not be null".
- The Gemma 4 audio sub-model is **CPU-only** — `AudioBackend = "gpu"` fails engine creation ("Audio
  backend constraint mismatch. Model requires one of [cpu]") on every platform; run audio on CPU. Vision
  runs on GPU.
- Speculative decoding does not speed up decode on desktop (CPU regresses; desktop WebGPU is neutral and
  requires `CacheDir = CacheDisabled` to load —
  [LiteRT-LM#2572](https://github.com/google-ai-edge/LiteRT-LM/issues/2572)); the win is on accelerators.
- The GPU TopK sampler falls back to CPU sampling on Windows/macOS
  ([LiteRT-LM#2073](https://github.com/google-ai-edge/LiteRT-LM/issues/2073)) — output is correct and the
  perf impact is negligible.

## [0.1.0-preview.1] — 2026-06-11

First public release on nuget.org (renamed from the internal *LiteLMSharp*). Built against
**LiteRT-LM v0.13.1**.

### Added

- Chat: blocking and per-token streaming with cancellation.
- Function calling / tools (constrained decoding, Gemma special-token sanitization).
- System prompt, sampler parameters, max output tokens, and KV-cache token count.
- Engine reload after `Dispose` — switch model or backend without restarting the process.
- AOT/trim-friendly interop (`[LibraryImport]`, `[UnmanagedCallersOnly]`, no reflection).
- LLamaSharp-style distribution: a pure managed `LiteRtLmSharp` package plus per-RID
  `LiteRtLmSharp.runtime.<rid>` native packages (win-x64, linux-x64, android-arm64, osx-arm64), all
  sharing one version, published via nuget.org Trusted Publishing (OIDC).
- Console and .NET MAUI samples: model download/management, chat, and tools.
