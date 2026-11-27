using System.Runtime.InteropServices;
using System.Text;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>
/// Metadata a <c>.litertlm</c> model file declares, read without loading an engine: what kind of model it
/// is, its context size, capabilities, default sampler, supported backends per modality, and embedding
/// facts. Use it to validate a file before loading it or to adapt the app to the model.
/// </summary>
/// <remarks>
/// <see cref="Read"/> opens the file, reads every value and closes it, so the object holds no native
/// resources. Values a model does not declare read as <c>false</c>, <c>0</c>, <c>null</c> or an empty
/// list, as each property states. The read is cheap: on gemma-4-E2B-it it takes about 20 ms and raises the
/// process's peak memory by about 10 MB (measured on win-x64). Requires native LiteRT-LM v0.20.0 or later
/// (C API 1.1, <c>c/model_info.h</c>).
/// </remarks>
public sealed class LiteRtModelInfo
{
    private static readonly LiteRtModality[] AllModalities =
        [LiteRtModality.Text, LiteRtModality.Vision, LiteRtModality.Audio, LiteRtModality.Video];

    static LiteRtModelInfo() => NativeLibraryResolver.Initialize();

    private LiteRtModelInfo() { }

    /// <summary>Gets whether the file is a language model or an embedding model.</summary>
    public LiteRtModelType ModelType { get; private init; }

    /// <summary>
    /// Gets the context size in tokens that the runtime reports for the file. For a language model it is the
    /// default context size the model is initialized with (the runtime reports 4,096 for gemma-4-E2B-it); when the
    /// metadata sets none, the runtime reports its own default of 4,096, capped by the size of a static model
    /// graph, so 4,096 does not show whether the file sets the size. A dynamic model (see
    /// <see cref="IsDynamicContext"/>) accepts another size through
    /// <see cref="LiteRtEngineOptions.MaxNumTokens"/>. 0 when the runtime reports no context size for the file.
    /// For an embedding model it is the longest input signature (8,192 for EmbeddingGemma 2), which an embedding engine only
    /// accepts with <see cref="LiteRtEmbeddingEngineOptions.MaxInputLength"/>: by default it stops at the
    /// limit the model declares (1,024 for EmbeddingGemma 2), which the C API does not expose.
    /// </summary>
    public int MaxContextTokens { get; private init; }

    /// <summary>Gets a value indicating whether the context size is configurable (dynamic) rather than fixed
    /// by the model graph, as the graph itself declares. For an embedding model it means the file has
    /// several input signatures.</summary>
    public bool IsDynamicContext { get; private init; }

    /// <summary>Gets a value indicating whether the model declares a reasoning ("thinking") mode. <c>false</c>
    /// when the metadata does not say.</summary>
    public bool SupportsThinking { get; private init; }

    /// <summary>Gets a value indicating whether the model declares function calling. <c>false</c> when the
    /// metadata does not say.</summary>
    public bool SupportsFunctionCalling { get; private init; }

    /// <summary>Gets a value indicating whether the file ships a Multi-Token-Prediction drafter for
    /// <see cref="LiteRtEngineOptions.EnableSpeculativeDecoding"/>.</summary>
    public bool SupportsSpeculativeDecoding { get; private init; }

    /// <summary>Gets the input modalities the file declares (text, vision, audio, video). A bundle can declare
    /// a modality whose encoder it does not ship; the engine's own check at load time is authoritative.</summary>
    public IReadOnlyList<LiteRtModality> InputModalities { get; private init; } = [];

    /// <summary>
    /// Gets the backends the file declares for each modality, in the model's priority order (the first is
    /// its default). A modality without declared backends is absent.
    /// </summary>
    /// <remarks>The engine's own check at load time is authoritative: a backend the model cannot use makes
    /// <see cref="LiteRtEngine.Load"/> fail with the runtime's reason.</remarks>
    public IReadOnlyDictionary<LiteRtModality, IReadOnlyList<LiteRtBackend>> SupportedBackends { get; private init; } =
        new Dictionary<LiteRtModality, IReadOnlyList<LiteRtBackend>>();

    /// <summary>Gets the sampler the model declares as its default, or <c>null</c> when it declares none.</summary>
    public LiteRtSamplerParams? DefaultSampler { get; private init; }

    /// <summary>Gets the largest per-image vision token budget the model supports, or <c>null</c> when it
    /// has no vision input or declares no budget. See <see cref="LiteRtConversationOptions.VisualTokenBudget"/>.</summary>
    public int? MaxVisionTokenBudget { get; private init; }

    /// <summary>Gets the per-image vision token sizes the model's vision encoder supports (for example 70,
    /// 140 and 280 on the Gemma 4 E-series), or an empty list when it has no vision input.</summary>
    public IReadOnlyList<int> VisionTokenSizes { get; private init; } = [];

    /// <summary>Gets the length of the vectors an embedding model produces, or <c>null</c> for a language
    /// model.</summary>
    public int? EmbeddingDimension { get; private init; }

    /// <summary>Gets the input lengths, in tokens, an embedding model's text encoder has signatures for, or an
    /// empty list for a language model. An embedding engine loads the ones up to
    /// <see cref="LiteRtEmbeddingEngineOptions.MaxInputLength"/>.</summary>
    public IReadOnlyList<int> EmbeddingInputLengths { get; private init; } = [];

    /// <summary>Gets the minimum LiteRT-LM runtime version the file requires, or <c>null</c> when it
    /// declares none.</summary>
    public string? MinRuntimeVersion { get; private init; }

    /// <summary>Reads the metadata of the model file at <paramref name="modelPath"/>.</summary>
    /// <param name="modelPath">The path to the <c>.litertlm</c> file.</param>
    /// <returns>The model's declared metadata.</returns>
    /// <remarks>On Windows, keep the path shorter than 260 characters: with LiteRT-LM v0.20.0, as with v0.18.0, the
    /// runtime failed to open a plain path of 301 characters ("Failed to open"), here and in
    /// <see cref="LiteRtEmbeddingEngine.Load"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="modelPath"/> is empty or the file does not exist.</exception>
    /// <exception cref="LiteRtException">The native layer could not open the file as a model, or the native
    /// library's C API version does not match this binding.</exception>
    public static LiteRtModelInfo Read(string modelPath) => ReadCore(modelPath, allowDeviceAlias: true);

    /// <summary>Reads the metadata like <see cref="Read(string)"/>; <paramref name="allowDeviceAlias"/> is
    /// <c>false</c> only to test the path that cannot open a non-ASCII path (see
    /// <see cref="NarrowPath.Acquire"/>).</summary>
    internal static unsafe LiteRtModelInfo ReadCore(string modelPath, bool allowDeviceAlias)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        if (!File.Exists(modelPath))
            throw new ArgumentException($"Model file not found: {modelPath}", nameof(modelPath));
        NativeLibraryResolver.EnsureCompatible();

        // The native reader opens the path both as UTF-8 and through the C runtime's narrow API, which on
        // Windows reads it in the ANSI code page (see NarrowPath).
        nint filePtr;
        LiteRtStatusCode status;
        bool opens;
        string? alias;
        using (NarrowPath.Lease path = NarrowPath.Acquire(modelPath, allowDeviceAlias))
        {
            opens = path.Opens;
            // Read before the lease is disposed, which forgets the alias.
            alias = path.IsDeviceAlias ? Encoding.ASCII.GetString(path.Bytes, 0, path.Bytes.Length - 1) : null;
            fixed (byte* p = path.Bytes)
                status = LiteRtLmNative.litert_lm_loaded_file_create(p, &filePtr);
            // The loaded file holds the parsed metadata, so a temporary alias can go right away.
        }
        if (status != LiteRtStatusCode.Ok)
            throw NativeError.Exception(
                status, $"litert_lm_loaded_file_create failed for '{modelPath}'.",
                hintFor: report =>
                    !opens
                        ? "On Windows the native metadata reader opens the path both as UTF-8 and in the ANSI code page, " +
                          "so a path with characters outside ASCII needs an ASCII alias, and none could be made for this " +
                          "one: move the model to a folder whose path uses only ASCII characters."
                        : alias is not null && report?.Message.Contains(alias, StringComparison.Ordinal) == true
                            ? $"The runtime's reason names {alias}, a temporary ASCII alias of '{modelPath}' that " +
                              "existed only for this call, because the native metadata reader cannot open the path's " +
                              "characters otherwise."
                            : null);

        using var file = new LoadedFileHandle(filePtr);
        nint f = file.Ptr;

        var modalities = new List<LiteRtModality>();
        var backends = new Dictionary<LiteRtModality, IReadOnlyList<LiteRtBackend>>();
        foreach (LiteRtModality modality in AllModalities)
        {
            byte supported;
            Check(LiteRtLmNative.litert_lm_loaded_file_supports_input_modality(f, (LiteRtLmModality)modality, &supported),
                nameof(LiteRtLmNative.litert_lm_loaded_file_supports_input_modality));
            if (supported != 0)
                modalities.Add(modality);
            IReadOnlyList<LiteRtBackend> list = ReadBackends(f, modality);
            if (list.Count > 0)
                backends[modality] = list;
        }

        // NOT_FOUND is the runtime's "the model does not declare it" (a file that is not an LLM has no default
        // sampler); each reads as the value 1.3.0 reported for an undeclared value.
        LiteRtLmSamplerType samplerType;
        if (!Declared(LiteRtLmNative.litert_lm_loaded_file_sampler_type(f, &samplerType),
                nameof(LiteRtLmNative.litert_lm_loaded_file_sampler_type)))
            samplerType = LiteRtLmSamplerType.Unspecified;
        float topP, temperature;
        int topK;
        if (!Declared(LiteRtLmNative.litert_lm_loaded_file_sampler_top_p(f, &topP), nameof(LiteRtLmNative.litert_lm_loaded_file_sampler_top_p)))
            topP = 0;
        if (!Declared(LiteRtLmNative.litert_lm_loaded_file_sampler_temperature(f, &temperature), nameof(LiteRtLmNative.litert_lm_loaded_file_sampler_temperature)))
            temperature = 0;
        if (!Declared(LiteRtLmNative.litert_lm_loaded_file_sampler_top_k(f, &topK), nameof(LiteRtLmNative.litert_lm_loaded_file_sampler_top_k)))
            topK = 0;
        int visionBudget;
        if (!Declared(LiteRtLmNative.litert_lm_loaded_file_max_vision_token_budget(f, &visionBudget),
                nameof(LiteRtLmNative.litert_lm_loaded_file_max_vision_token_budget)))
            visionBudget = -1;
        int dimension;
        if (!Declared(LiteRtLmNative.litert_lm_loaded_file_embedding_dimension(f, &dimension),
                nameof(LiteRtLmNative.litert_lm_loaded_file_embedding_dimension)))
            dimension = -1;
        uint maxContext;
        if (!Declared(LiteRtLmNative.litert_lm_loaded_file_max_context_tokens(f, &maxContext),
                nameof(LiteRtLmNative.litert_lm_loaded_file_max_context_tokens)))
            maxContext = 0;

        LiteRtLmModelType modelType;
        Check(LiteRtLmNative.litert_lm_loaded_file_model_type(f, &modelType), nameof(LiteRtLmNative.litert_lm_loaded_file_model_type));
        byte isDynamic, thinking, functionCalling, speculative;
        Check(LiteRtLmNative.litert_lm_loaded_file_is_dynamic_context(f, &isDynamic), nameof(LiteRtLmNative.litert_lm_loaded_file_is_dynamic_context));
        Check(LiteRtLmNative.litert_lm_loaded_file_supports_thinking(f, &thinking), nameof(LiteRtLmNative.litert_lm_loaded_file_supports_thinking));
        Check(LiteRtLmNative.litert_lm_loaded_file_supports_function_calling(f, &functionCalling),
            nameof(LiteRtLmNative.litert_lm_loaded_file_supports_function_calling));
        Check(LiteRtLmNative.litert_lm_loaded_file_has_speculative_decoding_support(f, &speculative),
            nameof(LiteRtLmNative.litert_lm_loaded_file_has_speculative_decoding_support));
        // Lent by the loaded file: copied before the handle is disposed. NULL (a success) when undeclared.
        nint version;
        Check(LiteRtLmNative.litert_lm_loaded_file_min_runtime_version(f, &version),
            nameof(LiteRtLmNative.litert_lm_loaded_file_min_runtime_version));

        return new LiteRtModelInfo
        {
            ModelType = modelType switch
            {
                LiteRtLmModelType.Llm => LiteRtModelType.LanguageModel,
                LiteRtLmModelType.Embedding => LiteRtModelType.Embedding,
                _ => LiteRtModelType.Unknown,
            },
            // A uint32 the public int saturates rather than wraps.
            MaxContextTokens = maxContext > int.MaxValue ? int.MaxValue : (int)maxContext,
            IsDynamicContext = isDynamic != 0,
            SupportsThinking = thinking != 0,
            SupportsFunctionCalling = functionCalling != 0,
            SupportsSpeculativeDecoding = speculative != 0,
            InputModalities = modalities,
            SupportedBackends = backends,
            // A sampler declared with NaN values (a malformed file) reads as undeclared rather than throwing.
            DefaultSampler = samplerType is LiteRtLmSamplerType.TopK or LiteRtLmSamplerType.TopP or LiteRtLmSamplerType.Greedy
                             && !float.IsNaN(topP) && !float.IsNaN(temperature)
                ? new LiteRtSamplerParams
                {
                    Strategy = (LiteRtSamplerType)samplerType,
                    TopK = Math.Max(1, topK),
                    TopP = Math.Clamp(topP, 0f, 1f),
                    Temperature = Math.Max(0f, temperature),
                }
                : null,
            MaxVisionTokenBudget = visionBudget > 0 ? visionBudget : null,
            VisionTokenSizes = ReadLengths(f, embedding: false),
            EmbeddingDimension = dimension > 0 ? dimension : null,
            EmbeddingInputLengths = ReadLengths(f, embedding: true),
            MinRuntimeVersion = Marshal.PtrToStringUTF8(version),
        };
    }

    // A getter whose failure 1.3.0 had no value for: every failure throws. In practice these fail only on a
    // NULL argument or an undeclared modality, which the binding never passes.
    private static void Check(LiteRtStatusCode status, string function) => NativeError.ThrowIfFailed(status, function);

    // true when the getter wrote its value; false when the model does not declare it (NOT_FOUND), with the
    // thread's message cleared; any other failure throws.
    private static bool Declared(LiteRtStatusCode status, string function)
    {
        if (status == LiteRtStatusCode.Ok)
            return true;
        if (status != LiteRtStatusCode.NotFound)
            throw NativeError.Exception(status, $"{function} failed.");
        NativeError.Clear();
        return false;
    }

    private static unsafe IReadOnlyList<int> ReadLengths(nint file, bool embedding)
    {
        string function = embedding
            ? nameof(LiteRtLmNative.litert_lm_loaded_file_embedding_signature_selection)
            : nameof(LiteRtLmNative.litert_lm_loaded_file_vision_signature_selection);
        nuint total;
        LiteRtStatusCode status = embedding
            ? LiteRtLmNative.litert_lm_loaded_file_embedding_signature_selection(file, null, 0, &total)
            : LiteRtLmNative.litert_lm_loaded_file_vision_signature_selection(file, null, 0, &total);
        if (!Declared(status, function) || total == 0)
            return [];
        var lengths = new int[checked((int)total)];
        nuint again;
        fixed (int* p = lengths)
        {
            status = embedding
                ? LiteRtLmNative.litert_lm_loaded_file_embedding_signature_selection(file, p, total, &again)
                : LiteRtLmNative.litert_lm_loaded_file_vision_signature_selection(file, p, total, &again);
        }
        if (!Declared(status, function))
            return [];
        // The second call reports the total again; only min(total, again) entries were written.
        return again >= total ? lengths : lengths[..(int)again];
    }

    private static unsafe IReadOnlyList<LiteRtBackend> ReadBackends(nint file, LiteRtModality modality)
    {
        nuint total;
        Check(LiteRtLmNative.litert_lm_loaded_file_modality_supported_backends(file, (LiteRtLmModality)modality, null, 0, &total),
            nameof(LiteRtLmNative.litert_lm_loaded_file_modality_supported_backends));
        if (total == 0)
            return [];
        var raw = new LiteRtLmBackendType[checked((int)total)];
        nuint again;
        fixed (LiteRtLmBackendType* p = raw)
        {
            Check(LiteRtLmNative.litert_lm_loaded_file_modality_supported_backends(file, (LiteRtLmModality)modality, p, total, &again),
                nameof(LiteRtLmNative.litert_lm_loaded_file_modality_supported_backends));
        }
        int count = (int)Math.Min(total, again);
        var result = new List<LiteRtBackend>(count);
        for (int i = 0; i < count; i++)
        {
            // A backend type newer than this binding has no backend name to map to: leave it out.
            LiteRtBackend? backend = raw[i] switch
            {
                LiteRtLmBackendType.Cpu => LiteRtBackend.Cpu,
                LiteRtLmBackendType.Gpu => LiteRtBackend.Gpu,
                LiteRtLmBackendType.Npu => LiteRtBackend.Npu,
                _ => null,
            };
            if (backend is { } known)
                result.Add(known);
        }
        return result;
    }
}

/// <summary>The kind of model a <c>.litertlm</c> file holds (see <see cref="LiteRtModelInfo.ModelType"/>).</summary>
public enum LiteRtModelType
{
    /// <summary>The file does not declare its type.</summary>
    Unknown = 0,

    /// <summary>A language model, loaded with <see cref="LiteRtEngine"/>.</summary>
    LanguageModel = 1,

    /// <summary>An embedding model, loaded with <see cref="LiteRtEmbeddingEngine"/>.</summary>
    Embedding = 2,
}

/// <summary>An input modality a model can accept (see <see cref="LiteRtModelInfo.InputModalities"/>).</summary>
public enum LiteRtModality
{
    /// <summary>Text.</summary>
    Text = 0,

    /// <summary>Images.</summary>
    Vision = 1,

    /// <summary>Audio.</summary>
    Audio = 2,

    /// <summary>Video.</summary>
    Video = 3,
}
