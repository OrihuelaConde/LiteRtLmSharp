using System.Runtime.InteropServices;
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
/// list, as each property states. On a multimodal bundle the native reader copies the vision sections into
/// memory while it reads (gemma-4-E2B-it: about 0.8 GB for a few hundred milliseconds), so on a phone call
/// it before loading engines rather than next to them. Requires native LiteRT-LM v0.18.0+
/// (<c>c/model_info.h</c>).
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
    /// Gets the maximum context size in tokens: the fixed size of a static model, or the largest value a
    /// dynamic model accepts for <see cref="LiteRtEngineOptions.MaxNumTokens"/> (see
    /// <see cref="IsDynamicContext"/>). 0 when the file does not declare it. For an embedding model it is
    /// the longest input signature (8,192 for EmbeddingGemma 2), which an embedding engine only accepts
    /// with <see cref="LiteRtEmbeddingEngineOptions.MaxInputLength"/>: by default it stops at the limit the
    /// model declares (1,024 for EmbeddingGemma 2), which the C API does not expose.
    /// </summary>
    public int MaxContextTokens { get; private init; }

    /// <summary>Gets a value indicating whether the context size is configurable up to
    /// <see cref="MaxContextTokens"/> (dynamic) rather than fixed by the model graph. For an embedding
    /// model it means the file has several input signatures.</summary>
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
    /// <remarks>The declaration is not exhaustive: gemma-4-E2B-it declares only CPU for vision, yet its vision
    /// encoder runs on the GPU backend. A backend the model truly cannot use makes
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
    /// <exception cref="ArgumentException"><paramref name="modelPath"/> is empty or the file does not exist.</exception>
    /// <exception cref="LiteRtException">The native layer could not open the file as a model.</exception>
    public static LiteRtModelInfo Read(string modelPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        if (!File.Exists(modelPath))
            throw new ArgumentException($"Model file not found: {modelPath}", nameof(modelPath));

        // The native reader opens the file through the C runtime's narrow API, which on Windows reads the
        // path in the ANSI code page rather than UTF-8 (see NarrowPath).
        byte[] nativePath = NarrowPath.Encode(modelPath, out bool opens);
        NativeError.Clear();
        nint filePtr;
        unsafe
        {
            fixed (byte* p = nativePath)
                filePtr = LiteRtLmNative.litert_lm_loaded_file_create(p);
        }
        if (filePtr == nint.Zero)
            throw NativeError.Exception(
                $"litert_lm_loaded_file_create returned null for '{modelPath}'.",
                hint: opens ? null
                    : "On Windows the native metadata reader opens paths in the ANSI code page, which cannot spell " +
                      "this one, and the volume has no ASCII short name for it: move the model to a folder whose " +
                      "path uses only characters of the system code page.");

        using var file = new LoadedFileHandle(filePtr);
        nint f = file.Ptr;

        var modalities = new List<LiteRtModality>();
        var backends = new Dictionary<LiteRtModality, IReadOnlyList<LiteRtBackend>>();
        foreach (LiteRtModality modality in AllModalities)
        {
            if (LiteRtLmNative.litert_lm_loaded_file_supports_input_modality(f, (LiteRtLmModality)modality))
                modalities.Add(modality);
            IReadOnlyList<LiteRtBackend> list = ReadBackends(f, modality);
            if (list.Count > 0)
                backends[modality] = list;
        }

        LiteRtLmSamplerType samplerType = LiteRtLmNative.litert_lm_loaded_file_sampler_type(f);
        float topP = LiteRtLmNative.litert_lm_loaded_file_sampler_top_p(f);
        float temperature = LiteRtLmNative.litert_lm_loaded_file_sampler_temperature(f);
        int visionBudget = LiteRtLmNative.litert_lm_loaded_file_max_vision_token_budget(f);
        int dimension = LiteRtLmNative.litert_lm_loaded_file_embedding_dimension(f);
        uint maxContext = LiteRtLmNative.litert_lm_loaded_file_max_context_tokens(f);

        return new LiteRtModelInfo
        {
            ModelType = LiteRtLmNative.litert_lm_loaded_file_model_type(f) switch
            {
                LiteRtLmModelType.Llm => LiteRtModelType.LanguageModel,
                LiteRtLmModelType.Embedding => LiteRtModelType.Embedding,
                _ => LiteRtModelType.Unknown,
            },
            MaxContextTokens = maxContext > int.MaxValue ? int.MaxValue : (int)maxContext,
            IsDynamicContext = LiteRtLmNative.litert_lm_loaded_file_is_dynamic_context(f),
            SupportsThinking = LiteRtLmNative.litert_lm_loaded_file_supports_thinking(f),
            SupportsFunctionCalling = LiteRtLmNative.litert_lm_loaded_file_supports_function_calling(f),
            SupportsSpeculativeDecoding = LiteRtLmNative.litert_lm_loaded_file_has_speculative_decoding_support(f),
            InputModalities = modalities,
            SupportedBackends = backends,
            // A sampler declared with NaN values (a malformed file) reads as undeclared rather than throwing.
            DefaultSampler = samplerType is LiteRtLmSamplerType.TopK or LiteRtLmSamplerType.TopP or LiteRtLmSamplerType.Greedy
                             && !float.IsNaN(topP) && !float.IsNaN(temperature)
                ? new LiteRtSamplerParams
                {
                    Strategy = (LiteRtSamplerType)samplerType,
                    TopK = Math.Max(1, LiteRtLmNative.litert_lm_loaded_file_sampler_top_k(f)),
                    TopP = Math.Clamp(topP, 0f, 1f),
                    Temperature = Math.Max(0f, temperature),
                }
                : null,
            MaxVisionTokenBudget = visionBudget > 0 ? visionBudget : null,
            VisionTokenSizes = ReadLengths(f, embedding: false),
            EmbeddingDimension = dimension > 0 ? dimension : null,
            EmbeddingInputLengths = ReadLengths(f, embedding: true),
            MinRuntimeVersion = Marshal.PtrToStringUTF8(LiteRtLmNative.litert_lm_loaded_file_min_runtime_version(f)),
        };
    }

    private static unsafe IReadOnlyList<int> ReadLengths(nint file, bool embedding)
    {
        int count = embedding
            ? LiteRtLmNative.litert_lm_loaded_file_embedding_signature_selection(file, null, 0)
            : LiteRtLmNative.litert_lm_loaded_file_vision_signature_selection(file, null, 0);
        if (count <= 0)
            return [];
        var lengths = new int[count];
        fixed (int* p = lengths)
        {
            int written = embedding
                ? LiteRtLmNative.litert_lm_loaded_file_embedding_signature_selection(file, p, count)
                : LiteRtLmNative.litert_lm_loaded_file_vision_signature_selection(file, p, count);
            return written >= count ? lengths : lengths[..Math.Max(0, written)];
        }
    }

    private static unsafe IReadOnlyList<LiteRtBackend> ReadBackends(nint file, LiteRtModality modality)
    {
        int count = LiteRtLmNative.litert_lm_loaded_file_modality_supported_backends(file, (LiteRtLmModality)modality, null, 0);
        if (count <= 0)
            return [];
        var raw = new LiteRtLmBackendType[count];
        fixed (LiteRtLmBackendType* p = raw)
            count = Math.Min(count, LiteRtLmNative.litert_lm_loaded_file_modality_supported_backends(file, (LiteRtLmModality)modality, p, count));
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
