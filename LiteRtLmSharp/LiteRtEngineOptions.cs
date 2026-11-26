namespace LiteRtLmSharp;

/// <summary>Options for creating a <see cref="LiteRtEngine"/>.</summary>
public sealed record LiteRtEngineOptions
{
    /// <summary>Path to the <c>.litertlm</c> (or <c>.task</c>) model file. Must be set —
    /// <see cref="LiteRtEngine.Load"/> throws <see cref="System.ArgumentException"/> when it is empty.
    /// (Deliberately not <c>required</c>: the native API has other model sources — e.g. loading from a file
    /// descriptor, which this binding does not expose — that would be added here as sibling properties.)</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>Backend to run the model on. Defaults to <see cref="LiteRtBackend.Cpu"/>. Use
    /// <see cref="LiteRtBackend.Gpu"/>, <see cref="LiteRtBackend.Npu"/>, or
    /// <see cref="LiteRtBackend.Custom"/> for a backend exposed by your own native build.</summary>
    public LiteRtBackend Backend { get; init; } = LiteRtBackend.Cpu;

    /// <summary>
    /// Backend for the vision encoder, enabling <b>image</b> input. <c>null</c> (default) leaves vision
    /// unconfigured — image attachments will not work. Requires a multimodal model (e.g. the Gemma 4
    /// E-series); on a text-only model the engine loads, but creating a conversation fails.
    /// </summary>
    /// <remarks>
    /// <para>Leave this <c>null</c> on a text-only model. With a backend set,
    /// <see cref="LiteRtEngine.CreateConversation"/> fails with <c>NOT_FOUND</c> ("tf_lite_vision_encoder not
    /// found in the model"), measured on LiteRT-LM v0.20.0 with Phi-4-mini-instruct (win-x64, CPU).</para>
    /// <para>Maps to the <c>vision_backend_str</c> parameter of the C API <c>engine_settings_create</c>.
    /// May differ from <see cref="Backend"/> (e.g. main on <see cref="LiteRtBackend.Gpu"/>, vision on
    /// <see cref="LiteRtBackend.Cpu"/>). Pair with image attachments via
    /// <see cref="LiteRtAttachment.Image(System.ReadOnlySpan{byte})"/> and tune the image prefill budget
    /// with <see cref="LiteRtConversationOptions.VisualTokenBudget"/>.</para>
    /// </remarks>
    public LiteRtBackend? VisionBackend { get; init; }

    /// <summary>
    /// Backend for the audio encoder, enabling <b>audio</b> input. <c>null</c> (default) leaves audio
    /// unconfigured — audio attachments will not work. Requires a model with audio support (e.g. the
    /// Gemma 4 E-series); on a model without it the engine loads, but creating a conversation fails.
    /// </summary>
    /// <remarks>
    /// <para>Leave this <c>null</c> on a text-only model. With a backend set,
    /// <see cref="LiteRtEngine.CreateConversation"/> fails with <c>NOT_FOUND</c> ("tf_lite_audio_encoder_hw not
    /// found in the model"), measured on LiteRT-LM v0.20.0 with Phi-4-mini-instruct (win-x64, CPU).</para>
    /// <para>A model may constrain which backend its audio encoder accepts. The Gemma 4 audio sub-model
    /// requires <b>CPU</b>: passing <see cref="LiteRtBackend.Gpu"/> makes <c>litert_lm_engine_create</c>
    /// fail with "Audio backend constraint mismatch. Model requires one of [cpu]" even when
    /// <see cref="Backend"/> is GPU — on any platform, not a win-x64 quirk. Use
    /// <see cref="LiteRtBackend.Cpu"/> for such models; the vision encoder has no such constraint and
    /// runs on GPU. Maps to the <c>audio_backend_str</c> parameter of the C API
    /// <c>engine_settings_create</c>.</para>
    /// </remarks>
    public LiteRtBackend? AudioBackend { get; init; }

    /// <summary>
    /// The total context window in tokens: prompt + generated replies, accumulated across every turn of
    /// a conversation. 0 = engine default.
    /// </summary>
    /// <remarks>
    /// This is the hard ceiling for a conversation's KV cache. Size it for the longest exchange you
    /// expect — the system prompt, any restored <see cref="LiteRtConversationOptions.History"/>, and each
    /// user turn and reply all count against it and accumulate. When the running total
    /// (<see cref="LiteRtConversation.TokenCount"/>) reaches the limit, the reply stops there and later sends
    /// fail (see below), so manage the conversation before then: trim history, cap replies with
    /// <see cref="LiteRtConversationOptions.MaxOutputTokens"/>, or start a fresh conversation. A larger
    /// window costs more memory and a slower prefill, so prefer the smallest that fits your use case. For
    /// <b>multimodal</b> input leave room for the media on top of the text — an image expands to roughly
    /// 256 vision tokens — for which 4096 is a comfortable starting point.
    /// <para><b>Setting this also arms the binding's KV overflow guard.</b> Without it, the runtime stops a
    /// reply at the limit and, when the context stays full, rejects later sends; a send whose prefill does not
    /// fit, or lands in the last free entries, leaves the conversation failing every later send. The process
    /// did not crash (measured on LiteRT-LM v0.20.0 with gemma-4 E2B on win-x64, CPU and GPU). With an explicit
    /// value here, the send methods instead clamp each reply to the remaining context and throw
    /// <see cref="LiteRtContextOverflowException"/> when the conversation is full. <b>The clamp cannot
    /// cover sends whose prefill cost is unmeasurable managed-side</b> — media attachments and
    /// <c>SendRaw</c>'s per-send <c>extraContext</c> get only the conversation-full check, so keep extra
    /// headroom under the limit on those paths (an image is roughly 256 tokens). With 0 the engine uses its
    /// own default limit and the guard stays off: the binding does not read that default (the C API reports
    /// it only in the benchmark info of an engine in benchmark mode).</para>
    /// <para><b>A large value can be capped by the runtime.</b> The guard is calibrated against the value set
    /// here, so where the runtime caps it, the guard does not protect the conversation: a send that does not
    /// fit reaches the runtime, which rejects it with <c>RESOURCE_EXHAUSTED</c>, and the conversation then
    /// fails every later send. Measured on LiteRT-LM v0.20.0 with gemma-4 E2B (win-x64, CPU):</para>
    /// <list type="bullet">
    /// <item><description>No value: the limit was 4,096 tokens.</description></item>
    /// <item><description>20,000: used as set.</description></item>
    /// <item><description>40,000: capped at 32,000.</description></item>
    /// </list>
    /// <para><b>Keep the value at or above the model's largest prefill signature</b> (1024 for the
    /// published gemma conversions). The native loader accepts some smaller limits, and what then fails
    /// depends on the value and the backend. Measured on LiteRT-LM v0.20.0 with gemma-4 E2B (win-x64):</para>
    /// <list type="bullet">
    /// <item><description>100: engine creation fails on CPU; on GPU the engine loads and every send
    /// fails.</description></item>
    /// <item><description>512: on CPU the engine loads and every text send fails with "Failed to allocate
    /// tensors"; on GPU a 427-token first message was accepted.</description></item>
    /// <item><description>256 and 384 on CPU, with <see cref="VisionBackend"/> set to CPU and the cache
    /// disabled: engine creation fails with "Failed to allocate tensors".</description></item>
    /// <item><description>1000: a 707-token message was accepted on CPU and GPU. A 987-token message was
    /// rejected by the guard with <see cref="LiteRtContextOverflowException"/> before it reached the
    /// runtime.</description></item>
    /// </list>
    /// <para>When a send fails with "Failed to allocate tensors" on an engine loaded with a limit below 1024,
    /// the exception names the small limit as the likely cause. The C API exposes no way to query the
    /// signatures, so the binding cannot validate the value up front. In the cases above, every failure raised
    /// an exception; no blocking send returned an empty reply. Other values below 1024 were not
    /// measured.</para>
    /// </remarks>
    public int MaxNumTokens { get; init; }

    /// <summary>
    /// Maximum number of images the engine accepts per turn. 0 = engine default. <b>On the standard
    /// binaries this setting has no effect</b> — see the remarks for the case where it applies. To bound
    /// how much of the context window images consume, use
    /// <see cref="LiteRtConversationOptions.VisualTokenBudget"/> instead.
    /// </summary>
    /// <remarks>
    /// The native library compiles several engine implementations into one binary and picks one per
    /// backend. This value always reaches the engine settings, but only the <i>legacy TFLite</i>
    /// implementation reads it; the standard binaries select the modern CompiledModel engines for
    /// CPU/GPU, which ignore it. It matters only when a custom native build routes your backend
    /// (e.g. one targeted via <see cref="LiteRtBackend.Custom"/>) through the legacy engine. Kept for
    /// parity with the reference Kotlin binding's <c>maxNumImages</c>. Maps to
    /// <c>engine_settings_set_max_num_images</c>.
    /// </remarks>
    public int MaxNumImages { get; init; }

    private readonly int? _maxVisionTokensPerImage;

    /// <summary>
    /// Upper bound on the vision tokens one image may expand to, which limits the vision encoder
    /// signatures the engine loads (up to the cap, rounded up to the next signature). <c>null</c> (default)
    /// leaves every signature of the model available.
    /// </summary>
    /// <remarks>
    /// It caps the image size; it does not choose it. Images still expand to the model's default size
    /// (about 256 tokens for the Gemma 4 E-series) unless a visual token budget asks for less, so pair a cap
    /// below that default with <see cref="LiteRtConversationOptions.VisualTokenBudget"/> (or
    /// <see cref="LiteRtSendOptions.VisualTokenBudget"/>) at or below the cap. Without one, an image send
    /// fails with <see cref="LiteRtStatusCode.InvalidArgument"/> ("No signature found…"), and an image send
    /// whose budget exceeds the cap fails the same way ("Visual token budget (…) cannot be larger than the
    /// engine's max vision tokens per image (…)"). Left <c>null</c>, the engine checks the budget against the
    /// model's own maximum instead (<see cref="LiteRtModelInfo.MaxVisionTokenBudget"/>, 280 on the Gemma 4
    /// E-series). It applies only when <see cref="VisionBackend"/> is set and the model declares a per-image
    /// token count. A cap above the model's largest signature (280 on the Gemma 4 E-series) fails
    /// <see cref="LiteRtEngine.Load"/> with <see cref="LiteRtStatusCode.InvalidArgument"/>. Maps to
    /// <c>engine_settings_set_max_vision_tokens_per_image</c>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MaxVisionTokensPerImage
    {
        get => _maxVisionTokensPerImage;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _maxVisionTokensPerImage = value;
        }
    }

    /// <summary>
    /// Where the engine keeps its compiled-artifact cache (GPU shaders / converted weights), which
    /// speeds up subsequent loads. Defaults to <see cref="LiteRtCache.Default"/> (written next to the
    /// model file). Use <see cref="LiteRtCache.Disabled"/>, <see cref="LiteRtCache.InMemory"/>, or
    /// <see cref="LiteRtCache.Directory"/> for an explicit path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cache files are named after the model file (its name, modification time and size), not after the
    /// native runtime version, so LiteRT-LM v0.18.0 and v0.20.0 give them the same names. A model file loaded
    /// under a new name in a new process gets a new cache file, and the old one stays on disk. When v0.20.0
    /// loads a cache that v0.18.0 wrote, or the reverse, the runtime rewrites the files once, on CPU and GPU.
    /// On GPU, the program cache file grows instead of being replaced. Measured on win-x64 with Qwen3-0.6B.
    /// </para>
    /// <para>
    /// Within one process, the cache key of a path is fixed at its first load: after the file at that path
    /// was replaced, an embedding engine in the same process returned stale results (LiteRT-LM v0.20.0,
    /// win-x64, CPU, EmbeddingGemma 2).
    /// </para>
    /// <para>
    /// A <see cref="LiteRtCache.Directory"/>
    /// must exist: the runtime does not create it, and since native v0.18.0 <see cref="LiteRtEngine.Load"/>
    /// fails with <see cref="LiteRtStatusCode.InvalidArgument"/> when it is missing.
    /// </para>
    /// </remarks>
    public LiteRtCache Cache { get; init; }

    /// <summary>
    /// Allow this engine to stay alive next to other chat engines in the process. Default <c>false</c>:
    /// <see cref="LiteRtEngine.Load"/> throws <see cref="System.InvalidOperationException"/> while another chat
    /// engine is alive. A <see cref="LiteRtEmbeddingEngine"/> never counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set it on every engine that should coexist: a load next to live engines succeeds only when the engine
    /// being loaded and every live engine opted in. A component that loads an engine without this setting
    /// therefore keeps the one-engine guarantee it was written for. <see cref="LiteRtEngine.LiveEngineCount"/>
    /// reports how many chat engines are alive.
    /// </para>
    /// <para>
    /// Measured on LiteRT-LM v0.18.0 with gemma-4 E2B and E4B on win-x64 (CPU with up to four engines, GPU
    /// with up to three) and linux-x64 (CPU, up to four): the engines load, generate at the same time and
    /// dispose in any order, with replies byte-identical to a single engine. That includes engines loaded
    /// concurrently from two threads (with the compiled-model cache already warm), mixed CPU and GPU engines, a
    /// vision engine next to a text engine, and speculative decoding. With LiteRT-LM v0.20.0 on win-x64
    /// (gemma-4 E2B), each of these layouts also gave replies identical to a single engine. On the Android
    /// x86_64 emulator (API 35, CPU, gemma-4 E2B, LiteRT-LM v0.20.0) three engines stayed loaded at once and
    /// each replied in turn. Physical Android devices, Apple platforms and linux-arm64 were not measured.
    /// </para>
    /// <para>
    /// Engines run in parallel only on a desktop CPU. On a GPU, and on the Android emulator, a second engine
    /// adds no throughput: two replies generated at the same time take longer than one after the other
    /// (measured with LiteRT-LM v0.18.0).
    /// </para>
    /// <para>
    /// Each engine holds its own memory. Check free memory (on Windows, the commit limit) before loading
    /// another engine. The table lists the measured figures; memory with several engines generating at the
    /// same time was not measured.
    /// </para>
    /// <list type="table">
    /// <listheader><term>Measurement</term><description>Memory</description></listheader>
    /// <item><term>gemma-4 E2B engine on Windows, right after loading (LiteRT-LM v0.20.0, Float32; GPU on an
    /// RTX 3080)</term>
    /// <description>Committed (private) memory: about 2.3 GB on CPU and about 4.1 GB on GPU.</description></item>
    /// <item><term>CPU engine on Windows, right after loading (measured with LiteRT-LM v0.18.0)</term>
    /// <description>The working set grows by about 0.35 GB, because the weights are
    /// memory-mapped.</description></item>
    /// <item><term>Extra CPU engine on linux-x64 (measured with LiteRT-LM v0.18.0)</term>
    /// <description>0.2 to 0.4 GB each.</description></item>
    /// <item><term>One engine alone on win-x64, after one short reply (measured with LiteRT-LM
    /// v0.18.0)</term>
    /// <description>1.3 GB.</description></item>
    /// <item><term>Android x86_64 emulator (gemma-4 E2B, CPU, LiteRT-LM v0.20.0)</term>
    /// <description>Each extra engine added 252 to 257 MiB of proportional memory (PSS); one engine reached
    /// 1,402 to 1,403 MiB resident (RSS) after one reply.</description></item>
    /// <item><term>Ten load and dispose cycles next to a live engine on win-x64 (measured with LiteRT-LM
    /// v0.18.0)</term>
    /// <description>Private memory ended about 90 MB higher on CPU and 215 to 250 MB higher on GPU, moving in
    /// both directions, so this does not show steady growth.</description></item>
    /// </list>
    /// <para>
    /// With LiteRT-LM v0.20.0 (win-x64, RTX 3080), none of 12 full GPU test runs that load and dispose many
    /// engines in one process ended with the process exiting without an error. With LiteRT-LM v0.18.0 on
    /// win-x64 GPU, such runs ended that way a few times.
    /// </para>
    /// <para>
    /// The one-engine rule was added in an earlier release built on self-built LiteRT-LM v0.13.1 libraries,
    /// after a second live engine was reported to hang in the native layer. That hang was never reproduced, and
    /// the official libraries this package ships showed none in the measurements above, which is why the rule
    /// can be lifted per engine.
    /// </para>
    /// </remarks>
    public bool AllowMultipleEngines { get; init; }

    /// <summary>
    /// Enable speculative decoding: the model drafts several tokens ahead with a small
    /// Multi-Token-Prediction (MTP) drafter and the main model verifies them in one step. It pays off
    /// only where verification is cheap relative to the drafter (memory-bound accelerators, high
    /// acceptance); measure it before turning it on.
    /// </summary>
    /// <remarks>
    /// Requires a <c>.litertlm</c> that ships an MTP drafter (e.g. the Gemma 4 E2B/E4B/12B builds;
    /// <see cref="LiteRtModelInfo.SupportsSpeculativeDecoding"/> tells before loading). On a model without
    /// one, engine creation fails with <see cref="LiteRtException"/> (Ministral 3 reports NOT_FOUND
    /// "tf_lite_mtp_drafter not found in the model"). The setting is fixed at engine creation;
    /// <see cref="LiteRtConversationOptions.EnableSpeculativeDecoding"/> overrides it per conversation.
    /// Pair with <see cref="EnableBenchmark"/> to measure the effect (see
    /// <see cref="LiteRtConversation.GetBenchmarkInfo"/>).
    /// <para>
    /// Measured with gemma-4-E2B on LiteRT-LM v0.18.0 (see <c>docs/speculative-decoding.md</c>): it
    /// slows decoding down on desktop <b>CPU</b> (0.78×) and on the desktop <b>WebGPU</b> GPU backend
    /// (0.52× on an RTX 3080 with the default float32 activations), with the default disk cache in both
    /// cases. On an Adreno 650 phone GPU it made no difference (measured in June 2026 on LiteRT-LM
    /// v0.13.1).
    /// </para>
    /// <para>
    /// Sampling with speculative decoding, measured on LiteRT-LM v0.20.0 with gemma-4 E2B on win-x64:
    /// </para>
    /// <list type="bullet">
    /// <item><description>CPU (turned on per conversation): a sampled reply follows the conversation's seed
    /// (seeds 7, 8 and 9 give three different replies), and an argmax reply equals the plain argmax reply. For
    /// one seed, the sampled speculative reply differs from the plain one.</description></item>
    /// <item><description>GPU (Float32): sampled replies also follow the seed. Seeds 7, 8 and 9 give three
    /// replies, and the same three in each of two processes. An argmax reply with speculative decoding on
    /// differs from the argmax reply with it off on the same engine, in 2 of 2 processes.</description></item>
    /// </list>
    /// </remarks>
    public bool EnableSpeculativeDecoding { get; init; }

    /// <summary>
    /// Enable benchmark instrumentation so <see cref="LiteRtConversation.GetBenchmarkInfo"/>
    /// returns prefill/decode tokens-per-second, time-to-first-token and init time. Fixed at
    /// engine creation; the overhead is timing bookkeeping only.
    /// </summary>
    public bool EnableBenchmark { get; init; }

    /// <summary>
    /// Whether the engine loads the <c>.litertlm</c> file's sections in parallel during startup.
    /// <c>null</c> (default) leaves the engine default (on). When on, the tokenizer section is parsed on
    /// a background thread while the model is built, shortening cold-start init; set <c>false</c> to load
    /// it serially on the calling thread (single-threaded environments, or to avoid the brief concurrent
    /// init peak, at the cost of a slower start). Maps to
    /// <c>engine_settings_set_parallel_file_section_loading</c>. See <c>docs/engine-tuning.md</c>.
    /// </summary>
    public bool? ParallelFileSectionLoading { get; init; }

    /// <summary>
    /// Activation tensor precision. Defaults to <see cref="LiteRtActivationDataType.Float32"/>.
    /// <b>Only the GPU backend honors this</b>; on CPU it is a <b>no-op</b>.
    /// </summary>
    /// <remarks>
    /// The runtime's own GPU default for the text executor is F16, which corrupts structured output
    /// (digits, dates, JSON) on the GPUs we measured, with no speed gain on a desktop GPU: gemma-4-E2B on an
    /// RTX 3080 decodes 113 tok/s with F16 and 117 with F32, and F32 commits about 0.3 to 0.6 GB more
    /// (measured with LiteRT-LM v0.18.0). So the binding asks for F32. Set
    /// <see cref="LiteRtActivationDataType.Float16"/> to trade precision for activation memory (for example on
    /// a mobile GPU, after checking your outputs), or <c>null</c> to let
    /// the runtime choose: the precision the model file declares for its text model (F16 for the Gemma 4
    /// E-series), else F16 on GPU. The setting reaches the text executor only: the vision and audio encoders
    /// run at the precision the model file declares for them (F16 for the Gemma 4 E-series vision encoder),
    /// else F32. <see cref="LiteRtActivationDataType.Int16"/> and
    /// <see cref="LiteRtActivationDataType.Int8"/> are accepted, but on GPU both produced unusable text;
    /// on CPU, Int8 gave the same replies as F16 and F32 (LiteRT-LM v0.20.0, gemma-4 E2B, win-x64). Maps to
    /// <c>engine_settings_set_activation_data_type</c>. See <c>docs/engine-tuning.md</c>.
    /// </remarks>
    public LiteRtActivationDataType? ActivationDataType { get; init; } = LiteRtActivationDataType.Float32;

    /// <summary>
    /// Maximum prompt tokens prefilled per step. 0 (default) = no chunking (the whole prompt is
    /// prefilled at once). <b>CPU + dynamic models only</b> — ignored on GPU and on static models. A
    /// smaller chunk lowers peak memory during prefill and allows more timely cancellation of a long
    /// prompt, at the cost of more prefill iterations (potentially slower). Maps to
    /// <c>engine_settings_set_prefill_chunk_size</c>. See <c>docs/engine-tuning.md</c>.
    /// </summary>
    public int PrefillChunkSize { get; init; }

    /// <summary>
    /// Synthetic prefill token count for benchmarking. 0 (default) = off (normal inference).
    /// </summary>
    /// <remarks>
    /// When &gt; 0 the engine runs a <b>synthetic benchmark</b> instead of answering: the real prompt is
    /// truncated or padded to exactly this many tokens for prefill, and decoding runs exactly
    /// <see cref="BenchmarkDecodeTokens"/> tokens (ignoring the stop token). So
    /// <see cref="LiteRtConversation.GetBenchmarkInfo"/> reports prefill/decode throughput at FIXED token
    /// counts — independent of the prompt — which is useful for device throughput benchmarking and for
    /// measuring the effect of the tuning settings reproducibly. The reply text is <b>not</b> a real
    /// answer. Setting either this or <see cref="BenchmarkDecodeTokens"/> also turns benchmark mode on
    /// (the same switch as <see cref="EnableBenchmark"/>). Use a dedicated engine instance — do not reuse
    /// it for real chat. Verified observable through the Conversation API on win-x64 CPU (the default
    /// engine reads these during prefill/decode). Maps to <c>engine_settings_set_num_prefill_tokens</c>.
    /// </remarks>
    public int BenchmarkPrefillTokens { get; init; }

    /// <summary>
    /// Synthetic decode token count for benchmarking — the decode half of
    /// <see cref="BenchmarkPrefillTokens"/>. 0 (default) = off. See <see cref="BenchmarkPrefillTokens"/>
    /// for the full behavior. Maps to <c>engine_settings_set_num_decode_tokens</c>.
    /// </summary>
    public int BenchmarkDecodeTokens { get; init; }

    private readonly int? _numThreads;

    /// <summary>
    /// Number of CPU threads for the <b>text</b> executor. <c>null</c> (default) leaves the engine
    /// default. <b>CPU-backend only</b>: the native setter reads the executor's <c>CpuConfig</c> and is a
    /// no-op when the text backend is not CPU (verified in engine.cc <c>set_num_threads</c>). Maps to
    /// <c>engine_settings_set_num_threads</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? NumThreads
    {
        get => _numThreads;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _numThreads = value;
        }
    }

    private readonly int? _audioNumThreads;

    /// <summary>
    /// Number of CPU threads for the <b>audio</b> executor. <c>null</c> (default) leaves the engine
    /// default. Only applies when an audio executor is configured (<see cref="AudioBackend"/> set), and
    /// only reaches the audio backend's CPU compilation options — a no-op otherwise (verified in
    /// engine.cc <c>set_audio_num_threads</c> → audio_executor CPU options). Maps to
    /// <c>engine_settings_set_audio_num_threads</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? AudioNumThreads
    {
        get => _audioNumThreads;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _audioNumThreads = value;
        }
    }

    private readonly int? _loraRank;

    /// <summary>
    /// LoRA rank for the <b>text</b> executor. <c>null</c> (default) leaves the engine default (LoRA
    /// disabled). Requires a LoRA-enabled model and a matching adapter passed per conversation via
    /// <see cref="LiteRtConversationOptions.LoraPath"/>. Maps to <c>engine_settings_set_lora_rank</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The adapter applies to the conversation that sets <see cref="LiteRtConversationOptions.LoraPath"/> only
    /// (LiteRT-LM v0.20.0, measured on CPU with upstream's LoRA test bundle): after it has generated, a
    /// conversation without an adapter on the same engine generates the base model's text, and while it exists, a
    /// conversation without an adapter still answers, blocking or streamed. LiteRT-LM v0.18.0 applied the adapter
    /// to the whole engine instead.
    /// </para>
    /// <para>
    /// The published Gemma 4 bundles carry no LoRA slots (google-ai-edge/LiteRT-LM#3173): an adapter is
    /// accepted and has no effect on them. The GPU backend is not validated (the test bundle does not load
    /// on it).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? LoraRank
    {
        get => _loraRank;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _loraRank = value;
        }
    }

    private readonly IReadOnlyList<int>? _supportedLoraRanks;

    /// <summary>
    /// The set of LoRA ranks the <b>text</b> executor should support (for switching adapters of different
    /// ranks). <c>null</c> (default) leaves the engine default. <b>Only honored on the GPU (Artisan)
    /// backend</b>: on other backends the native layer accepts and silently ignores it (verified in
    /// <c>llm_executor_settings.h SetSupportedLoraRanks</c>). Every element must be positive and the list
    /// non-empty. Maps to <c>engine_settings_set_supported_lora_ranks</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Any element is zero or negative.</exception>
    public IReadOnlyList<int>? SupportedLoraRanks
    {
        get => _supportedLoraRanks;
        init
        {
            ValidateRanks(value);
            _supportedLoraRanks = value;
        }
    }

    private readonly int? _audioLoraRank;

    /// <summary>
    /// LoRA rank for the <b>audio</b> executor. <c>null</c> (default) leaves the engine default. Only
    /// applies when an audio executor is configured (<see cref="AudioBackend"/> set). Maps to
    /// <c>engine_settings_set_audio_lora_rank</c>. Unlike the text path, audio LoRA has not been validated
    /// against a real adapter in this binding.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? AudioLoraRank
    {
        get => _audioLoraRank;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _audioLoraRank = value;
        }
    }

    private readonly IReadOnlyList<int>? _supportedAudioLoraRanks;

    /// <summary>
    /// The set of LoRA ranks the <b>audio</b> executor should support. <c>null</c> (default) leaves the
    /// engine default. Requires an audio executor (<see cref="AudioBackend"/> set); see
    /// <see cref="SupportedLoraRanks"/> for the GPU-backend caveat. Every element must be positive and the
    /// list non-empty. Maps to <c>engine_settings_set_supported_audio_lora_ranks</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Any element is zero or negative.</exception>
    public IReadOnlyList<int>? SupportedAudioLoraRanks
    {
        get => _supportedAudioLoraRanks;
        init
        {
            ValidateRanks(value);
            _supportedAudioLoraRanks = value;
        }
    }

    private readonly int? _gpuDecodeStepsPerSync;

    /// <summary>
    /// Decode steps the GPU runs between host syncs. <c>null</c> (default) = engine default. Only
    /// honored by the Artisan GPU backend (the mobile GPU stack); the desktop WebGPU backend ignores
    /// it.
    /// </summary>
    /// <remarks>Maps to <c>engine_settings_set_gpu_decode_steps_per_sync</c>. Larger values reduce
    /// sync overhead but coarsen streaming/cancellation granularity.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? GpuDecodeStepsPerSync
    {
        get => _gpuDecodeStepsPerSync;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _gpuDecodeStepsPerSync = value;
        }
    }

    /// <summary>
    /// Whether engine load blocks until GPU weight uploads complete. <c>null</c> (default) = engine
    /// default. Only honored by the Artisan GPU backend; the desktop WebGPU backend ignores it.
    /// </summary>
    /// <remarks>Maps to <c>engine_settings_set_gpu_wait_for_weight_uploads</c>.</remarks>
    public bool? GpuWaitForWeightUploads { get; init; }

    /// <summary>
    /// Lets the experimental YNNPACK delegate take the CPU operations it supports before XNNPACK.
    /// <c>null</c> (default) = engine default (off). CPU backend, linux-arm64 only.
    /// </summary>
    /// <remarks>
    /// Maps to <c>engine_settings_set_enable_ynnpack</c>. Of the official libraries, only linux-arm64 carries
    /// the YNNPACK kernels. Elsewhere, <c>true</c> makes <see cref="LiteRtEngine.Load"/> fail with
    /// <see cref="LiteRtStatusCode.Unimplemented"/> (native v0.18.0, and v0.20.0 measured on win-x64 CPU with gemma-4 E2B; v0.16.0
    /// ignored the flag). Measure any
    /// speed-up on the target device rather than assume one.
    /// </remarks>
    public bool? EnableYnnpack { get; init; }

    /// <summary>
    /// Store the KV cache of local-attention layers in ringbuffers sized to what those layers
    /// actually need, instead of allocating the full context length — lower memory for long
    /// contexts, at the cost of instant rewinding. <c>null</c> (default) = off. Backend-agnostic in
    /// interface but currently only the Artisan GPU backend implements it; unsupported
    /// models/backends log a native warning and ignore it.
    /// </summary>
    /// <remarks>Maps to <c>engine_settings_set_use_ringbuffers_local_attention</c> (the knob the JS
    /// API exposes as <c>use_autosized_ringbuffers</c>).</remarks>
    public bool? UseRingbuffersLocalAttention { get; init; }

    /// <summary>
    /// Whether the GPU backend keeps the model weights and allocations resident in GPU memory through
    /// Apple's <c>MTLResidencySet</c> API, which prevents them from being paged out and lowers allocation
    /// overhead. <c>null</c> (default) leaves the engine default. Apple platforms with the Metal GPU
    /// backend only; other platforms and backends ignore it.
    /// </summary>
    /// <remarks>Maps to <c>engine_settings_set_gpu_enable_metal_residency_set</c>. The desktop macOS GPU
    /// backend runs on WebGPU over Metal; measure the effect on the target device rather than assume
    /// one.</remarks>
    public bool? EnableMetalResidencySet { get; init; }

    private static void ValidateRanks(IReadOnlyList<int>? ranks)
    {
        if (ranks is null)
            return;
        if (ranks.Count == 0)
            throw new ArgumentException("The supported LoRA ranks list must not be empty (use null to leave it unset).", nameof(ranks));
        foreach (int r in ranks)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(r);
    }
}

/// <summary>
/// Activation tensor precision for <see cref="LiteRtEngineOptions.ActivationDataType"/>, mirroring
/// upstream's <c>ActivationDataType</c> enum. Only the GPU backend honors it, and only <see cref="Float32"/>
/// and <see cref="Float16"/> gave usable text there (see
/// <see cref="LiteRtEngineOptions.ActivationDataType"/>).
/// </summary>
public enum LiteRtActivationDataType
{
    /// <summary>32-bit float: full precision, more activation memory on GPU. The binding's default.</summary>
    Float32 = 0,

    /// <summary>16-bit float: half the activation memory on GPU at lower precision. The runtime's own GPU
    /// default for the text executor; see <see cref="LiteRtEngineOptions.ActivationDataType"/>.</summary>
    Float16 = 1,

    /// <summary>16-bit integer — present for parity with the native enum. On GPU it produced unusable text
    /// (LiteRT-LM v0.20.0, gemma-4 E2B, win-x64).</summary>
    Int16 = 2,

    /// <summary>8-bit integer — present for parity with the native enum. On GPU it produced unusable text,
    /// and on CPU it had no effect (LiteRT-LM v0.20.0, gemma-4 E2B, win-x64).</summary>
    Int8 = 3,
}

/// <summary>
/// Per-conversation options: system prompt, sampler, output limit, and tools (function calling).
/// Requires native binaries version-matched to the bindings (the official builds from this repo's
/// releases are); see <c>docs/native-abi.md</c> for the ABI history.
/// </summary>
public sealed record LiteRtConversationOptions
{
    /// <summary>Optional system prompt applied to the conversation.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>Optional sampler parameters. Null = engine default.</summary>
    /// <remarks>
    /// Each explicit strategy works as named on the CPU and GPU backends: <see cref="LiteRtSamplerType.Greedy"/>
    /// decodes argmax and <see cref="LiteRtSamplerType.TopK"/> samples among the top-k tokens. Each
    /// conversation's parameters take effect, also on an engine where other
    /// conversations already generated. <see cref="LiteRtSamplerParams"/> lists what was measured, also with
    /// speculative decoding.
    /// </remarks>
    public LiteRtSamplerParams? Sampler { get; init; }

    /// <summary>Maximum output tokens per response. 0 = engine default.</summary>
    public int MaxOutputTokens { get; init; }

    /// <summary>Tools the model may call (function calling). Null/empty = no tools.</summary>
    public IReadOnlyList<LiteRtTool>? Tools { get; init; }

    /// <summary>
    /// Conversation history to restore (or a few-shot preface) — the prior turns are prefilled into the
    /// KV cache by the conversation's first send (creating the conversation prefills nothing), so the model
    /// continues as if they had just happened. Build the list with <see cref="LiteRtMessage"/> factories and capture assistant turns
    /// with <see cref="LiteRtResponse.ToMessage"/>; persist and reload via
    /// <see cref="LiteRtMessage.Serialize"/> / <see cref="LiteRtMessage.Deserialize"/>.
    /// </summary>
    /// <remarks>
    /// The C API has no history getter, so the round-trip is caller-owned: record each turn yourself.
    /// Restoring is a <i>replay through prefill</i>, not a zero-cost snapshot — it costs a prefill of the
    /// history and counts against <see cref="LiteRtEngineOptions.MaxNumTokens"/>. Keep the system prompt
    /// in <see cref="SystemMessage"/> OR as a leading <see cref="LiteRtMessageRole.System"/> message, not
    /// both (the native side prepends <see cref="SystemMessage"/> before the history). Takes precedence
    /// over <see cref="HistoryJson"/> when it is non-empty. Maps to the C API
    /// <c>conversation_config_set_messages</c>.
    /// </remarks>
    public IReadOnlyList<LiteRtMessage>? History { get; init; }

    /// <summary>
    /// Raw escape hatch for <see cref="History"/>: the messages as a JSON <b>array</b> string (the format
    /// <see cref="LiteRtMessage.Serialize"/> produces). Use it to pass persisted history verbatim without
    /// re-parsing into typed messages, or to include content the typed model does not cover yet (e.g.
    /// image/audio parts). Validated as a JSON array when the conversation is created — throws
    /// <see cref="ArgumentException"/> otherwise. Ignored when <see cref="History"/> is non-empty.
    /// </summary>
    public string? HistoryJson { get; init; }

    /// <summary>
    /// Force the model to emit valid (schema-constrained) output. Strongly recommended when
    /// <see cref="Tools"/> are set so tool-call arguments parse reliably.
    /// </summary>
    /// <remarks>
    /// The official LiteRT-LM prebuilts embed the constraint provider in the native library (from v0.16.0;
    /// tool calls with it measured on LiteRT-LM v0.20.0, gemma-4 E2B, win-x64, CPU and GPU). Releases before 1.2.0 threw
    /// <see cref="PlatformNotSupportedException"/> on linux-x64, where the separately shipped
    /// provider crashed the process (google-ai-edge/LiteRT-LM#2149); that guard is gone. Tools also
    /// work with this left <c>false</c>; arguments are then not grammar-constrained.
    /// </remarks>
    public bool EnableConstrainedDecoding { get; init; }

    /// <summary>
    /// Toggles the model's reasoning ("thinking") mode by setting <c>enable_thinking</c> in the
    /// conversation's <see cref="ExtraContext"/>. <c>null</c> (default) leaves it unset so the
    /// model uses its own default; <c>true</c>/<c>false</c> force reasoning on/off.
    /// </summary>
    /// <remarks>
    /// This is the canonical use of extra context for the Gemma reasoning builds: the chat
    /// template branches on <c>{% if enable_thinking %}</c>. On a model whose template does not
    /// reference the key it is a harmless no-op. The runtime keeps the (often long) reasoning out of the KV
    /// cache on later turns by default (see <see cref="FilterThinkingFromKvCache"/>). When both this and
    /// <see cref="ExtraContext"/> set <c>enable_thinking</c>, this flag wins. Gemma 4's template applies the
    /// switch in the system turn, which the runtime prefills with the first send, so the mode of the first send
    /// stays for the conversation (LiteRT-LM v0.20.0, gemma-4 E2B, CPU and GPU). Only a per-send
    /// <see cref="LiteRtSendOptions.EnableThinking"/> <c>false</c> turns thinking off for a later send; see there for
    /// the sends that were measured.
    /// </remarks>
    public bool? EnableThinking { get; init; }

    private readonly int? _thinkingTokenBudget;

    /// <summary>
    /// Caps how many tokens the model may spend on its thinking block per reply: the decode loop
    /// tracks the model's thinking start/end token ids and cuts the block off at the budget.
    /// <c>null</c> (default) = no cap; <c>-1</c> = explicitly infinite; <c>0</c> is treated by the
    /// native layer as "no budget" (NOT "no thinking"). <b>Setting a budget with
    /// <see cref="EnableThinking"/> unset turns thinking ON</b> (a budget only makes sense with a
    /// thinking block; the native thinking config defaults to enabled) — set
    /// <c>EnableThinking = false</c> explicitly if you want a budget configured but thinking off.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="MaxOutputTokens"/> — which caps the whole reply and starves the answer when
    /// thinking runs long — this bounds only the reasoning share. Maps to the C API thinking config
    /// (<c>litert_lm_thinking_config_*</c>). The per-send
    /// <see cref="LiteRtSendOptions.ThinkingTokenBudget"/> overrides this for one send.
    /// </remarks>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is negative and not -1.</exception>
    public int? ThinkingTokenBudget
    {
        get => _thinkingTokenBudget;
        init
        {
            if (value is { } v && v < -1)
                throw new ArgumentOutOfRangeException(nameof(value), v, "ThinkingTokenBudget must be positive, 0, or -1 (infinite).");
            _thinkingTokenBudget = value;
        }
    }

    /// <summary>
    /// Overrides the chat prompt template for this conversation with a custom Jinja template string.
    /// <c>null</c> (default) = the template the model file (or engine) provides. For advanced use —
    /// a template that does not match what the model was trained on degrades output quality.
    /// </summary>
    /// <remarks>Maps to the C API <c>conversation_config_set_prompt_template</c>.
    /// Inspect the result with <see cref="LiteRtConversation.RenderMessage"/> /
    /// <see cref="LiteRtConversation.RenderPreface"/> before relying on it.</remarks>
    public string? PromptTemplate { get; init; }

    /// <summary>
    /// Enables <b>custom</b> constrained decoding for this conversation: with a provider set, each
    /// send may carry a <see cref="LiteRtSendOptions.Constraint"/> (regex / JSON Schema) that the
    /// provider enforces during sampling. <c>null</c> (default) = no custom constraints.
    /// </summary>
    /// <remarks>
    /// The binding does not combine it with <see cref="EnableConstrainedDecoding"/> (the tool-calling
    /// constrained-decoding path): <see cref="LiteRtEngine.CreateConversation"/> throws
    /// <see cref="ArgumentException"/> when both are set. The runtime itself accepted both on one
    /// conversation (LiteRT-LM v0.20.0, gemma-4 E2B, win-x64, CPU), but whether both took effect together was
    /// not measured. A constrained send can keep a tool call from happening: on a conversation with
    /// <see cref="Tools"/>, a send with a regex <see cref="LiteRtSendOptions.Constraint"/> returned text and no
    /// tool call (one send measured; see that property). Maps to the C API
    /// <c>conversation_config_set_constraint_provider</c>. The LlGuidance provider, like the tool-calling one,
    /// is embedded in the native library.
    /// </remarks>
    public LiteRtConstraintProvider? ConstraintProvider { get; init; }

    /// <summary>
    /// Extra context merged into the conversation preface and passed to the prompt-template
    /// renderer — a raw JSON <b>object</b> string, e.g. <c>{"user_name":"Alice"}</c>. Template
    /// variables are referenced Jinja-style (<c>{{ user_name }}</c>). Null/empty = none.
    /// </summary>
    /// <remarks>
    /// For the common <c>enable_thinking</c> toggle prefer the typed <see cref="EnableThinking"/>;
    /// this is the general escape hatch for arbitrary template variables. Must be a JSON object —
    /// <see cref="LiteRtEngine.CreateConversation"/> throws <see cref="ArgumentException"/> otherwise
    /// (validated when the conversation is created, not when this property is set). Maps to the C API
    /// <c>conversation_config_set_extra_context</c>.
    /// </remarks>
    public string? ExtraContext { get; init; }

    /// <summary>
    /// Drop channel content — in practice the thinking channel — from the KV cache, so a long
    /// reasoning block does not eat into the context window on subsequent turns. Only meaningful alongside
    /// <see cref="EnableThinking"/>.
    /// </summary>
    /// <remarks>The runtime already drops channel content when this is left <c>false</c>: the native default
    /// is on, and the binding sends the setting only for <c>true</c>, so <c>false</c> cannot turn the filter
    /// off (LiteRT-LM v0.20.0; measured on CPU, gemma-4 E2B: the token count after a reply with thinking leaves
    /// the thinking out with this unset). Maps to the C API
    /// <c>conversation_config_set_filter_channel_content_from_kv_cache</c>.</remarks>
    public bool FilterThinkingFromKvCache { get; init; }

    /// <summary>
    /// Stream the raw text of a tool call <b>while the model is generating it</b>. Default <c>false</c>:
    /// during a tool-call block the stream goes silent until the block completes and the parsed
    /// <see cref="LiteRtStreamChunkKind.ToolCall"/> chunk arrives whole. With this on,
    /// <see cref="LiteRtConversation.SendStreamingAsync(string, System.Threading.CancellationToken)"/>
    /// additionally yields <see cref="LiteRtStreamChunkKind.ToolCallDelta"/> chunks — incremental,
    /// unparsed fragments of the tool-call text — as they are produced, followed by the usual complete
    /// <see cref="LiteRtStreamChunkKind.ToolCall"/> chunk. Use the deltas for progress display only;
    /// act on the final parsed chunk. Only meaningful when <see cref="Tools"/> are set.
    /// </summary>
    /// <remarks>
    /// Maps to the C API <c>conversation_config_set_stream_tool_calls</c>. The deltas do not affect the
    /// blocking <see cref="LiteRtConversation.Send(string)"/> path.
    /// </remarks>
    public bool StreamToolCalls { get; init; }

    /// <summary>
    /// Tokens each <b>image</b> attachment may expand to. 0 (default) = the model's default size. The
    /// runtime scales each image down to about the budget in tokens (never above the image's default
    /// size), so a lower budget leaves more of the context window for text at the cost of image detail.
    /// Only sends that carry an image use it.
    /// </summary>
    /// <remarks>
    /// The Gemma 4 E-series bundles carry vision signatures of 70, 140 and 280 tokens and default to
    /// about 256 tokens per image. Measured on gemma-4-E2B-it, one image costs 260 tokens by default, and
    /// 68, 104 and 200 tokens with budgets of 70, 100 and 200. The budget must not exceed the engine's
    /// per-image maximum: <see cref="LiteRtEngineOptions.MaxVisionTokensPerImage"/> when the engine sets
    /// it, else the model's own (<see cref="LiteRtModelInfo.MaxVisionTokenBudget"/>, 280 on the Gemma 4
    /// E-series). Above it, image sends fail with <see cref="LiteRtStatusCode.InvalidArgument"/>; the
    /// binding attaches the budget only to sends that carry an image, so text-only sends are unaffected.
    /// Applied per send via the C API <c>conversation_optional_args_set_visual_token_budget</c>;
    /// <see cref="LiteRtSendOptions.VisualTokenBudget"/> overrides it for one send.
    /// </remarks>
    public int VisualTokenBudget { get; init; }

    /// <summary>
    /// Path to a <b>text</b> LoRA adapter (weights file) to apply to this conversation. <c>null</c>
    /// (default) = no adapter. Requires a LoRA-enabled model, and the engine loaded with a matching
    /// <see cref="LiteRtEngineOptions.LoraRank"/>.
    /// </summary>
    /// <remarks>
    /// The native side opens the file when the conversation is created, so a missing or unreadable path
    /// fails fast with <see cref="LiteRtException"/> at <see cref="LiteRtEngine.CreateConversation"/>,
    /// not silently at first send. The adapter applies to this conversation only: see
    /// <see cref="LiteRtEngineOptions.LoraRank"/> for what was measured and for the published Gemma 4 bundles,
    /// which accept an adapter without effect (google-ai-edge/LiteRT-LM#3173).
    /// Maps to the C API <c>session_config_set_lora_path</c>.
    /// </remarks>
    public string? LoraPath { get; init; }

    /// <summary>
    /// Path to an <b>audio</b> LoRA adapter (weights file) to apply to this conversation. <c>null</c>
    /// (default) = no adapter. Requires an audio-capable, LoRA-enabled model and a matching
    /// <see cref="LiteRtEngineOptions.AudioLoraRank"/>. Opened at conversation creation, so a bad path fails
    /// fast like <see cref="LoraPath"/>. Not validated against a real audio adapter in this binding. Maps
    /// to the C API <c>session_config_set_audio_lora_path</c>.
    /// </summary>
    public string? AudioLoraPath { get; init; }

    /// <summary>
    /// Speculative decoding for this conversation, overriding
    /// <see cref="LiteRtEngineOptions.EnableSpeculativeDecoding"/>. <c>null</c> (default) uses the engine
    /// setting.
    /// </summary>
    /// <remarks>
    /// <c>true</c> on an engine loaded without speculative decoding makes the runtime load the model's
    /// Multi-Token-Prediction (MTP) drafter lazily, on this conversation's first send, so you can enable it
    /// only where it pays off. <c>false</c> turns it off for this conversation even when the engine enables
    /// it. <c>null</c> keeps the engine setting also after another conversation on the engine loaded the
    /// drafter with <c>true</c> (measured on LiteRT-LM v0.20.0, win-x64, CPU and GPU). Requires a model that
    /// ships an MTP drafter (such as the Gemma 4 E-series): on a model without one, <c>true</c> makes the
    /// first send fail with <see cref="LiteRtException"/>. Maps to the C API
    /// <c>session_config_set_enable_speculative_decoding</c>. With speculative decoding on, a sampled reply
    /// follows the conversation's seed (see <see cref="LiteRtEngineOptions.EnableSpeculativeDecoding"/>).
    /// </remarks>
    public bool? EnableSpeculativeDecoding { get; init; }
}
