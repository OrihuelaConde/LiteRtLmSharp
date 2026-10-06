using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// A Microsoft.Extensions.AI <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> over a LiteRtLmSharp
/// <see cref="LiteRtEmbeddingEngine"/>, for the .NET AI ecosystem: vector stores, Semantic Kernel and any code
/// written against the MEAI abstractions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EmbeddingGenerationOptions.Dimensions"/> maps to
/// <see cref="LiteRtEmbeddingOptions.OutputDimensions"/> (Matryoshka truncation, normalized by the runtime);
/// the knobs MEAI does not type live on <see cref="LiteRtEmbeddingGenerationOptions"/>. Per-call values
/// override the generator's default options.
/// </para>
/// <para>
/// The runtime does not apply a model's task instructions: prepend them to each value (EmbeddingGemma 2
/// distinguishes queries from documents; see <c>docs/embeddings.md</c>). Large inputs are embedded in batches
/// of 32 texts. The generator does not own the engine: dispose the engine after the generator.
/// </para>
/// </remarks>
public sealed class LiteRtEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    // Bounds the native batch (and its temporary memory) when a caller passes a large collection.
    internal const int BatchSize = 32;

    private readonly LiteRtEmbeddingEngine _engine;
    private readonly LiteRtEmbeddingOptions? _defaultOptions;
    private readonly EmbeddingGeneratorMetadata _metadata;

    /// <summary>Initializes a new instance of the <see cref="LiteRtEmbeddingGenerator"/> class over an embedding
    /// engine <b>you own</b> (dispose it after the generator).</summary>
    /// <param name="engine">The loaded embedding engine. Must outlive this generator.</param>
    /// <param name="modelId">Identifier surfaced as the metadata's default model id and on each embedding. Optional.</param>
    /// <param name="defaultOptions">Options applied to every call unless the call overrides them. Optional.</param>
    public LiteRtEmbeddingGenerator(LiteRtEmbeddingEngine engine, string? modelId = null, LiteRtEmbeddingOptions? defaultOptions = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _defaultOptions = defaultOptions;
        _metadata = new EmbeddingGeneratorMetadata("litert-lm", null, modelId, defaultOptions?.OutputDimensions ?? engine.Dimension);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A value is <c>null</c>, or an option has an invalid value.</exception>
    /// <exception cref="LiteRtException">The native computation failed; the message carries the runtime's reason.</exception>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        string[] texts = values as string[] ?? [.. values];
        LiteRtEmbeddingOptions? nativeOptions = LiteRtEmbeddingMapping.ToEmbeddingOptions(options, _defaultOptions);
        string? modelId = options?.ModelId ?? _metadata.DefaultModelId;

        var result = new GeneratedEmbeddings<Embedding<float>>(texts.Length);
        for (int start = 0; start < texts.Length; start += BatchSize)
        {
            string[] chunk = texts[start..Math.Min(texts.Length, start + BatchSize)];
            float[][] vectors = await _engine.EmbedBatchAsync(chunk, nativeOptions, cancellationToken).ConfigureAwait(false);
            DateTimeOffset created = DateTimeOffset.UtcNow;
            foreach (float[] vector in vectors)
                result.Add(new Embedding<float>(vector) { ModelId = modelId, CreatedAt = created });
        }
        return result;
    }

    /// <summary>Resolves a provider-specific service: the <see cref="EmbeddingGeneratorMetadata"/>, the
    /// underlying <see cref="LiteRtEmbeddingEngine"/>, or the generator itself. Keyed lookups return <c>null</c>.</summary>
    /// <param name="serviceType">The type of the requested service.</param>
    /// <param name="serviceKey">An optional key; any non-null key returns <c>null</c>.</param>
    /// <returns>The service, or <c>null</c> when none matches.</returns>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return
            serviceKey is not null ? null :
            serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata :
            serviceType == typeof(LiteRtEmbeddingEngine) ? _engine :
            serviceType.IsInstanceOfType(this) ? this :
            null;
    }

    /// <summary>Disposes the generator. The engine is not disposed here: it belongs to whoever created it (you,
    /// for this constructor; the container, when registered from <see cref="LiteRtEmbeddingEngineOptions"/>).</summary>
    public void Dispose() { }
}

/// <summary>
/// <see cref="EmbeddingGenerationOptions"/> with the LiteRtLmSharp knobs MEAI's typed options do not cover. Each
/// property is stored in <see cref="EmbeddingGenerationOptions.AdditionalProperties"/>, so it survives middleware
/// that clones the options; setting the same keys on a plain options object works identically.
/// </summary>
public sealed class LiteRtEmbeddingGenerationOptions : EmbeddingGenerationOptions
{
    /// <summary>Gets or sets a value indicating whether the vector is L2-normalized; <c>null</c> keeps the
    /// generator's default (normalized). Backed by the <c>normalize</c> key.</summary>
    [JsonIgnore]
    public bool? Normalize
    {
        get => LiteRtEmbeddingMapping.GetBool(this, LiteRtEmbeddingMapping.NormalizeKey);
        set => Set(LiteRtEmbeddingMapping.NormalizeKey, value);
    }

    /// <summary>Gets or sets a value indicating whether the runtime inserts the model's special tokens;
    /// <c>null</c> keeps the generator's default (inserted). Backed by the <c>insert_special_tokens</c> key.</summary>
    [JsonIgnore]
    public bool? InsertSpecialTokens
    {
        get => LiteRtEmbeddingMapping.GetBool(this, LiteRtEmbeddingMapping.InsertSpecialTokensKey);
        set => Set(LiteRtEmbeddingMapping.InsertSpecialTokensKey, value);
    }

    /// <summary>Gets or sets how a text longer than the model's largest loaded input is handled; <c>null</c>
    /// keeps the generator's default (the runtime fails the call). Backed by the <c>overflow_strategy</c> key.</summary>
    [JsonIgnore]
    public LiteRtInputOverflowStrategy? OverflowStrategy
    {
        get => LiteRtEmbeddingMapping.GetOverflowStrategy(this);
        set => Set(LiteRtEmbeddingMapping.OverflowStrategyKey, value);
    }

    private void Set(string key, object? value)
    {
        if (value is null)
            AdditionalProperties?.Remove(key);
        else
            (AdditionalProperties ??= [])[key] = value;
    }
}

/// <summary>Maps MEAI embedding options onto <see cref="LiteRtEmbeddingOptions"/>.</summary>
internal static class LiteRtEmbeddingMapping
{
    internal const string NormalizeKey = "normalize";
    internal const string InsertSpecialTokensKey = "insert_special_tokens";
    internal const string OverflowStrategyKey = "overflow_strategy";

    /// <summary>Merges per-call options over the generator defaults; <c>null</c> when neither sets anything.</summary>
    /// <exception cref="ArgumentException">An option has an invalid value.</exception>
    internal static LiteRtEmbeddingOptions? ToEmbeddingOptions(EmbeddingGenerationOptions? options, LiteRtEmbeddingOptions? defaults)
    {
        if (options is null)
            return defaults;
        LiteRtEmbeddingOptions merged = defaults ?? new LiteRtEmbeddingOptions();
        // MEAI's own setter already rejects a non-positive Dimensions.
        if (options.Dimensions is { } dimensions)
            merged = merged with { OutputDimensions = dimensions };
        if (GetBool(options, NormalizeKey) is { } normalize)
            merged = merged with { Normalize = normalize };
        if (GetBool(options, InsertSpecialTokensKey) is { } insert)
            merged = merged with { InsertSpecialTokens = insert };
        if (GetOverflowStrategy(options) is { } overflow)
            merged = merged with { OverflowStrategy = overflow };
        return merged == new LiteRtEmbeddingOptions() && defaults is null ? null : merged;
    }

    internal static bool? GetBool(EmbeddingGenerationOptions options, string key)
        => options.AdditionalProperties?.TryGetValue(key, out object? value) == true ? LiteRtChatMapping.AsBool(value) : null;

    /// <summary>Reads the overflow strategy from the enum, its name (case-insensitive) or its numeric value, so
    /// options deserialized from JSON or configuration work too.</summary>
    /// <exception cref="ArgumentException">The value names no strategy.</exception>
    internal static LiteRtInputOverflowStrategy? GetOverflowStrategy(EmbeddingGenerationOptions options)
    {
        if (options.AdditionalProperties?.TryGetValue(OverflowStrategyKey, out object? value) != true || value is null)
            return null;
        LiteRtInputOverflowStrategy? strategy = value switch
        {
            LiteRtInputOverflowStrategy s => s,
            string text when Enum.TryParse(text, ignoreCase: true, out LiteRtInputOverflowStrategy parsed) => parsed,
            IConvertible number when value is not string && TryToInt(number, out int n) => (LiteRtInputOverflowStrategy)n,
            _ => null,
        };
        if (strategy is not { } result || !Enum.IsDefined(result))
            throw new ArgumentException(
                $"'{value}' is not an embedding overflow strategy (ChunkAndAverage, Truncate or Error).", nameof(options));
        return result;
    }

    private static bool TryToInt(IConvertible value, out int result)
    {
        try
        {
            result = value.ToInt32(CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            result = 0;
            return false;
        }
    }
}
