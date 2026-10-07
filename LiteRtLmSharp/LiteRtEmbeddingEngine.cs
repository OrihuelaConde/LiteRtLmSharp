using System.Text;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>
/// An embedding engine: loads an embedding model (such as EmbeddingGemma 2) and turns text into vectors for
/// semantic search, retrieval-augmented generation, clustering or classification.
/// </summary>
/// <remarks>
/// <para>
/// An embedding engine is independent of <see cref="LiteRtEngine"/>: it does not count toward the
/// one-live-engine rule, so an app can keep a chat engine and an embedding engine loaded at the same time.
/// </para>
/// <para>
/// Calls on one embedding engine are serialized internally, so it is safe to share across threads; a call
/// waits for the one in progress. <see cref="Dispose"/> also waits for it before releasing the native
/// engine. The runtime does not apply a model's task instructions: prepend them to the text yourself (see
/// <c>docs/embeddings.md</c>). Requires native LiteRT-LM v0.18.0+ (<c>c/embedding_engine.h</c>).
/// </para>
/// </remarks>
public sealed class LiteRtEmbeddingEngine : IDisposable
{
    private readonly EmbeddingEngineHandle _engine;
    // Serializes native calls on this engine and orders Dispose after the call in progress. Never disposed:
    // a SemaphoreSlim holds no unmanaged resource unless its wait handle is requested, and disposing it would
    // strand callers already waiting on it.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;

    static LiteRtEmbeddingEngine() => NativeLibraryResolver.Initialize();

    private LiteRtEmbeddingEngine(EmbeddingEngineHandle engine, int? dimension)
    {
        _engine = engine;
        Dimension = dimension;
    }

    /// <summary>
    /// Gets the length of the vectors the model produces before any truncation by
    /// <see cref="LiteRtEmbeddingOptions.OutputDimensions"/>, or <c>null</c> when the model file does not
    /// declare it or its metadata could not be read (see <see cref="LiteRtModelInfo.Read"/>).
    /// </summary>
    public int? Dimension { get; }

    /// <summary>Loads an embedding model and creates the engine.</summary>
    /// <param name="options">The model path, backend and engine settings.</param>
    /// <returns>The loaded embedding engine. Dispose it when done.</returns>
    /// <exception cref="ArgumentException">The model path is empty, the file does not exist, the file is a
    /// language model (load those with <see cref="LiteRtEngine.Load"/>), or the cache directory does not
    /// exist.</exception>
    /// <exception cref="LiteRtException">Native engine creation failed; the message carries the runtime's
    /// reason (for example, a file that is not a model, or a backend the model does not support).</exception>
    public static LiteRtEmbeddingEngine Load(LiteRtEmbeddingEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.ModelPath))
            throw new ArgumentException("LiteRtEmbeddingEngineOptions.ModelPath must be set.", nameof(options));
        if (!File.Exists(options.ModelPath))
            throw new ArgumentException($"Model file not found: {options.ModelPath}", nameof(options));
        // The runtime rejects a missing cache directory too, but deep in a call trace; name it up front.
        if (options is { MinInputLength: { } min, MaxInputLength: { } max } && min > max)
            throw new ArgumentException(
                $"MinInputLength ({min}) is greater than MaxInputLength ({max}).", nameof(options));
        if (options.Cache.NativeValue is { } dir && dir[0] != ':' && !System.IO.Directory.Exists(dir))
            throw new ArgumentException(
                $"Cache directory not found: {dir}. The embedding engine does not create it: create it first, or " +
                "use LiteRtCache.Default (next to the model file).", nameof(options));

        // The metadata names the model kind up front, so passing a chat model gets a clear error instead of a
        // native one; a file whose metadata cannot be read still gets the engine's own verdict below.
        LiteRtModelInfo? info = null;
        try { info = LiteRtModelInfo.Read(options.ModelPath); }
        catch (LiteRtException) { }
        if (info is { ModelType: LiteRtModelType.LanguageModel })
            throw new ArgumentException(
                $"'{options.ModelPath}' is a language model: load it with LiteRtEngine.Load. LiteRtEmbeddingEngine " +
                "needs an embedding model, such as EmbeddingGemma 2.", nameof(options));

        NativeError.Clear();
        nint settingsPtr = LiteRtLmNative.litert_lm_embedding_engine_settings_create(
            options.ModelPath, options.Backend.Value, null, null);
        if (settingsPtr == nint.Zero)
            throw NativeError.Exception("litert_lm_embedding_engine_settings_create returned null.");

        using var settings = new EmbeddingEngineSettingsHandle(settingsPtr);
        if (options.Cache.NativeValue is { } cacheDir)
            LiteRtLmNative.litert_lm_embedding_engine_settings_set_cache_dir(settings.Ptr, cacheDir);
        if (options.NumThreads is { } numThreads)
            LiteRtLmNative.litert_lm_embedding_engine_settings_set_num_threads(settings.Ptr, numThreads);
        if (options.ActivationDataType is { } activationType)
            LiteRtLmNative.litert_lm_embedding_engine_settings_set_activation_data_type(settings.Ptr, (int)activationType);
        if (options.MaxInputLength is { } maxInput)
            LiteRtLmNative.litert_lm_embedding_engine_settings_set_max_input_length(settings.Ptr, maxInput);
        if (options.MinInputLength is { } minInput)
            LiteRtLmNative.litert_lm_embedding_engine_settings_set_min_input_length(settings.Ptr, minInput);

        NativeError.Clear();
        nint enginePtr = LiteRtLmNative.litert_lm_embedding_engine_create(settings.Ptr);
        if (enginePtr == nint.Zero)
            throw NativeError.Exception(
                "litert_lm_embedding_engine_create returned null.",
                fallbackHint: "The runtime reported no reason (see the native stderr). Check that the file is an " +
                "embedding model and that the backend is one it supports.");

        return new LiteRtEmbeddingEngine(new EmbeddingEngineHandle(enginePtr), info?.EmbeddingDimension);
    }

    /// <summary>Computes the embedding of <paramref name="text"/>.</summary>
    /// <param name="text">The text to embed, with any task instruction the model expects already prepended.</param>
    /// <param name="options">Per-call options; <c>null</c> uses the runtime defaults (normalized, full length).</param>
    /// <returns>The embedding vector.</returns>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="LiteRtException">The native computation failed; the message carries the runtime's reason.</exception>
    public float[] Embed(string text, LiteRtEmbeddingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return EmbedCore(text, options);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Computes the embeddings of several texts in one native call.</summary>
    /// <remarks>The runtime embeds the texts one after another, so a batch saves per-call overhead but not
    /// compute time. If any text fails (for example, a text too long for the loaded signatures), the whole
    /// call fails.</remarks>
    /// <param name="texts">The texts to embed, each with any task instruction already prepended.</param>
    /// <param name="options">Per-call options applied to every text; <c>null</c> uses the runtime defaults.</param>
    /// <returns>One vector per text, in the same order. Empty when <paramref name="texts"/> is empty.</returns>
    /// <exception cref="ArgumentException">An element of <paramref name="texts"/> is <c>null</c>.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="LiteRtException">The native computation failed; the message carries the runtime's reason.</exception>
    public float[][] EmbedBatch(IReadOnlyList<string> texts, LiteRtEmbeddingOptions? options = null)
    {
        ValidateTexts(texts);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (texts.Count == 0)
            return [];
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return EmbedBatchCore(texts, options);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Computes the embedding of <paramref name="text"/> on the thread pool.</summary>
    /// <param name="text">The text to embed, with any task instruction the model expects already prepended.</param>
    /// <param name="options">Per-call options; <c>null</c> uses the runtime defaults (normalized, full length).</param>
    /// <param name="cancellationToken">Cancels the wait for a call in progress; a computation that has started
    /// runs to completion (the native layer cannot interrupt it).</param>
    /// <returns>The embedding vector.</returns>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="LiteRtException">The native computation failed; the message carries the runtime's reason.</exception>
    public async Task<float[]> EmbedAsync(
        string text, LiteRtEmbeddingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => EmbedCore(text, options), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Computes the embeddings of several texts in one native call, on the thread pool.</summary>
    /// <remarks>See <see cref="EmbedBatch"/>: the texts are embedded one after another, and one failing text
    /// fails the call.</remarks>
    /// <param name="texts">The texts to embed, each with any task instruction already prepended.</param>
    /// <param name="options">Per-call options applied to every text; <c>null</c> uses the runtime defaults.</param>
    /// <param name="cancellationToken">Cancels the wait for a call in progress; a computation that has started
    /// runs to completion.</param>
    /// <returns>One vector per text, in the same order. Empty when <paramref name="texts"/> is empty.</returns>
    /// <exception cref="ArgumentException">An element of <paramref name="texts"/> is <c>null</c>.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="LiteRtException">The native computation failed; the message carries the runtime's reason.</exception>
    public async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts, LiteRtEmbeddingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateTexts(texts);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (texts.Count == 0)
            return [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => EmbedBatchCore(texts, options), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the native engine after any call in progress completes.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _gate.Wait();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _engine.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateTexts(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        for (int i = 0; i < texts.Count; i++)
        {
            if (texts[i] is null)
                throw new ArgumentException($"texts[{i}] is null.", nameof(texts));
        }
    }

    private unsafe float[] EmbedCore(string text, LiteRtEmbeddingOptions? options)
    {
        using EmbeddingOptionsHandle? nativeOptions = BuildOptions(options);
        using InputDataHandle input = CreateTextInput(text);
        nint inputPtr = input.Ptr;
        NativeError.Clear();
        nint responsePtr = LiteRtLmNative.litert_lm_embedding_engine_compute_embedding(
            _engine.Ptr, &inputPtr, 1, nativeOptions?.Ptr ?? nint.Zero);
        if (responsePtr == nint.Zero)
            throw NativeError.Exception("litert_lm_embedding_engine_compute_embedding returned null.");
        using var response = new EmbeddingResponseHandle(responsePtr);
        return ReadVector(response.Ptr);
    }

    private unsafe float[][] EmbedBatchCore(IReadOnlyList<string> texts, LiteRtEmbeddingOptions? options)
    {
        using EmbeddingOptionsHandle? nativeOptions = BuildOptions(options);
        var inputs = new InputDataHandle[texts.Count];
        try
        {
            for (int i = 0; i < texts.Count; i++)
                inputs[i] = CreateTextInput(texts[i]);

            // One input item per request: inputs_batch[i] points at the single-element array {inputs[i]}.
            nint[] items = new nint[texts.Count];
            for (int i = 0; i < texts.Count; i++)
                items[i] = inputs[i].Ptr;
            nuint[] counts = new nuint[texts.Count];
            Array.Fill(counts, (nuint)1);
            nint[] requests = new nint[texts.Count];

            nint responsesPtr;
            fixed (nint* itemsPtr = items)
            fixed (nint* requestsPtr = requests)
            fixed (nuint* countsPtr = counts)
            {
                for (int i = 0; i < texts.Count; i++)
                    requestsPtr[i] = (nint)(itemsPtr + i);
                NativeError.Clear();
                responsesPtr = LiteRtLmNative.litert_lm_embedding_engine_compute_embedding_batch(
                    _engine.Ptr, (nint**)requestsPtr, countsPtr, (nuint)texts.Count, nativeOptions?.Ptr ?? nint.Zero);
            }
            if (responsesPtr == nint.Zero)
                throw NativeError.Exception("litert_lm_embedding_engine_compute_embedding_batch returned null.");

            using var responses = new EmbeddingResponsesHandle(responsesPtr);
            nuint count = LiteRtLmNative.litert_lm_embedding_responses_get_size(responses.Ptr);
            if (count != (nuint)texts.Count)
                throw new LiteRtException(
                    $"litert_lm_embedding_engine_compute_embedding_batch returned {count} embeddings for {texts.Count} texts.");
            var vectors = new float[texts.Count][];
            for (int i = 0; i < texts.Count; i++)
            {
                // Owned by the collection: read it, never delete it.
                nint responsePtr = LiteRtLmNative.litert_lm_embedding_responses_get_at(responses.Ptr, (nuint)i);
                vectors[i] = responsePtr == nint.Zero ? [] : ReadVector(responsePtr);
            }
            return vectors;
        }
        finally
        {
            foreach (InputDataHandle? input in inputs)
                input?.Dispose();
        }
    }

    private static unsafe float[] ReadVector(nint response)
    {
        nuint size = LiteRtLmNative.litert_lm_embedding_response_get_size(response);
        float* values = LiteRtLmNative.litert_lm_embedding_response_get_values(response);
        if (size == 0 || values is null)
            return [];
        // The values belong to the response; copy them out before it is deleted.
        return new ReadOnlySpan<float>(values, checked((int)size)).ToArray();
    }

    private static unsafe InputDataHandle CreateTextInput(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        nint ptr;
        fixed (byte* p = utf8)
        {
            NativeError.Clear();
            // The runtime copies the bytes, so the managed buffer can go right after the call.
            ptr = LiteRtLmNative.litert_lm_input_data_create(LiteRtLmInputDataType.Text, p, (nuint)utf8.Length);
        }
        if (ptr == nint.Zero)
            throw NativeError.Exception("litert_lm_input_data_create returned null.");
        return new InputDataHandle(ptr);
    }

    private static EmbeddingOptionsHandle? BuildOptions(LiteRtEmbeddingOptions? options)
    {
        if (options is null)
            return null;
        NativeError.Clear();
        nint ptr = LiteRtLmNative.litert_lm_embedding_options_create();
        if (ptr == nint.Zero)
            throw NativeError.Exception("litert_lm_embedding_options_create returned null.");
        var handle = new EmbeddingOptionsHandle(ptr);
        if (options.Normalize is { } normalize)
            LiteRtLmNative.litert_lm_embedding_options_set_normalize(ptr, normalize);
        if (options.InsertSpecialTokens is { } insert)
            LiteRtLmNative.litert_lm_embedding_options_set_insert_special_tokens(ptr, insert);
        if (options.OverflowStrategy is { } overflow)
            LiteRtLmNative.litert_lm_embedding_options_set_input_overflow_strategy(ptr, (int)overflow);
        if (options.OutputDimensions is { } dimensions)
            LiteRtLmNative.litert_lm_embedding_options_set_output_size(ptr, dimensions);
        return handle;
    }
}

/// <summary>Options for loading a <see cref="LiteRtEmbeddingEngine"/>.</summary>
public sealed record LiteRtEmbeddingEngineOptions
{
    /// <summary>Gets the path to the embedding model's <c>.litertlm</c> file. Must be set.</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>Gets the backend to run the model on. Defaults to <see cref="LiteRtBackend.Cpu"/>.</summary>
    /// <remarks>On the GPU backend the vectors differ slightly from the CPU ones (cosine similarity above
    /// 0.99 between the two in our measurement), so index and query on the same backend.</remarks>
    public LiteRtBackend Backend { get; init; } = LiteRtBackend.Cpu;

    /// <summary>
    /// Gets where the engine keeps its compiled-artifact cache. Defaults to <see cref="LiteRtCache.Default"/>
    /// (next to the model file). A <see cref="LiteRtCache.Directory"/> must already exist: the runtime does
    /// not create it, so <see cref="LiteRtEmbeddingEngine.Load"/> throws <see cref="ArgumentException"/>.
    /// </summary>
    public LiteRtCache Cache { get; init; }

    private readonly int? _numThreads;

    /// <summary>Gets the number of CPU threads, or <c>null</c> (default) for the engine default. CPU backend only.</summary>
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

    /// <summary>
    /// Gets the activation precision. Defaults to <see cref="LiteRtActivationDataType.Float32"/>, like
    /// <see cref="LiteRtEngineOptions.ActivationDataType"/>: EmbeddingGemma's activations exceed the float16
    /// range, so its model card advises against float16, which is the runtime's fallback on the GPU backend.
    /// The CPU backend runs float32 either way. Float16 measured no faster on a desktop GPU but about twice
    /// as fast per sentence on a phone GPU (Adreno 650), with vectors slightly further from the CPU ones. Set
    /// <see cref="LiteRtActivationDataType.Float16"/> to make that trade, or <c>null</c> to let the runtime
    /// choose (the model's preferred type, else float16 on GPU).
    /// </summary>
    public LiteRtActivationDataType? ActivationDataType { get; init; } = LiteRtActivationDataType.Float32;

    private readonly int? _maxInputLength;

    /// <summary>
    /// Gets the longest input, in tokens, the engine prepares for, or <c>null</c> (default) for the limit the
    /// model declares (EmbeddingGemma 2: 1024). The engine loads the smallest of the model's input
    /// signatures that holds this many tokens, plus the shorter ones (see
    /// <see cref="LiteRtModelInfo.EmbeddingInputLengths"/>; EmbeddingGemma 2 has 128 to 8192), so texts up to
    /// that signature's length are accepted (1500 loads the 2,048 signature). A longer text fails unless
    /// <see cref="LiteRtEmbeddingOptions.OverflowStrategy"/> truncates or chunks it. A value above the
    /// longest signature makes <see cref="LiteRtEmbeddingEngine.Load"/> fail. Longer signatures need more
    /// memory.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? MaxInputLength
    {
        get => _maxInputLength;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _maxInputLength = value;
        }
    }

    private readonly int? _minInputLength;

    /// <summary>Gets the shortest input signature, in tokens, the engine loads, or <c>null</c> (default) for
    /// the model's own minimum. Signatures shorter than this are not loaded, which saves memory but pads
    /// short texts to a longer signature. It must not exceed the effective maximum:
    /// <see cref="MaxInputLength"/> when set (checked by <see cref="LiteRtEmbeddingEngine.Load"/>), else the
    /// limit the model declares (1024 for EmbeddingGemma 2; the runtime rejects a larger minimum).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? MinInputLength
    {
        get => _minInputLength;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegative(v);
            _minInputLength = value;
        }
    }
}

/// <summary>Per-call options for <see cref="LiteRtEmbeddingEngine"/>. Unset values use the runtime defaults.</summary>
public sealed record LiteRtEmbeddingOptions
{
    /// <summary>Gets a value indicating whether the vector is L2-normalized, or <c>null</c> for the runtime
    /// default (<c>true</c>). Normalized vectors make cosine similarity a dot product.</summary>
    public bool? Normalize { get; init; }

    private readonly int? _outputDimensions;

    /// <summary>
    /// Gets the length to truncate the vector to, or <c>null</c> (default) for the model's full length.
    /// Models trained with Matryoshka representation learning keep most of their quality when truncated
    /// to their trained sizes (EmbeddingGemma 2: 768, 512, 256 or 128); the runtime normalizes after
    /// truncating. A value above the model's length fails the call with
    /// <see cref="LiteRtStatusCode.InvalidArgument"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int? OutputDimensions
    {
        get => _outputDimensions;
        init
        {
            if (value is { } v)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(v);
            _outputDimensions = value;
        }
    }

    /// <summary>Gets a value indicating whether the runtime inserts the model's special tokens (such as BOS
    /// and EOS) around the input, or <c>null</c> for the runtime default (<c>true</c>).</summary>
    public bool? InsertSpecialTokens { get; init; }

    /// <summary>Gets how the runtime handles a text longer than the largest loaded signature (see
    /// <see cref="LiteRtEmbeddingEngineOptions.MaxInputLength"/>), or <c>null</c> for the runtime default,
    /// <see cref="LiteRtInputOverflowStrategy.Error"/> (the call fails with
    /// <see cref="LiteRtStatusCode.InvalidArgument"/>).</summary>
    public LiteRtInputOverflowStrategy? OverflowStrategy { get; init; }
}

/// <summary>How the embedding engine handles a text longer than its largest loaded input signature (see
/// <see cref="LiteRtEmbeddingEngineOptions.MaxInputLength"/>).</summary>
public enum LiteRtInputOverflowStrategy
{
    /// <summary>Split the text into chunks that fit, embed each and average the vectors.</summary>
    ChunkAndAverage = 0,

    /// <summary>Embed only the first part of the text that fits.</summary>
    Truncate = 1,

    /// <summary>Fail the call with <see cref="LiteRtStatusCode.InvalidArgument"/>. The runtime default.</summary>
    Error = 2,
}
