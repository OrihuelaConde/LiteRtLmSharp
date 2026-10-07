using System.Text.Json;
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
/// of 32 texts. The engine runs one model, so each embedding carries the generator's model id;
/// <see cref="EmbeddingGenerationOptions.ModelId"/> is ignored, as it is by the chat client. The generator
/// does not own the engine: dispose the engine after the generator.
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
    /// <param name="modelId">Identifier surfaced as the metadata's default model id and on each embedding, whatever
    /// the request's <see cref="EmbeddingGenerationOptions.ModelId"/> says. Optional.</param>
    /// <param name="defaultOptions">Options applied to every call unless the call overrides them. Optional.</param>
    public LiteRtEmbeddingGenerator(LiteRtEmbeddingEngine engine, string? modelId = null, LiteRtEmbeddingOptions? defaultOptions = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _defaultOptions = defaultOptions;
        _metadata = new EmbeddingGeneratorMetadata("litert-lm", null, modelId, defaultOptions?.OutputDimensions ?? engine.Dimension);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A value is <c>null</c> (checked before anything is embedded), or an
    /// option has an invalid value.</exception>
    /// <exception cref="LiteRtException">The native computation failed; the message carries the runtime's reason.</exception>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        string[] texts = values as string[] ?? [.. values];
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] is null)
                throw new ArgumentException($"values[{i}] is null.", nameof(values));
        }
        LiteRtEmbeddingOptions? nativeOptions = LiteRtEmbeddingMapping.ToEmbeddingOptions(options, _defaultOptions);
        // The engine runs one model: a request's ModelId cannot switch it, so it never relabels the vectors.
        string? modelId = _metadata.DefaultModelId;

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
    /// generator's default (normalized). Backed by the <c>normalize</c> key. A stored value that is not a
    /// boolean reads as <c>null</c> here and fails the call.</summary>
    [JsonIgnore]
    public bool? Normalize
    {
        get => LiteRtEmbeddingMapping.GetBool(this, LiteRtEmbeddingMapping.NormalizeKey);
        set => Set(LiteRtEmbeddingMapping.NormalizeKey, value);
    }

    /// <summary>Gets or sets a value indicating whether the runtime inserts the model's special tokens;
    /// <c>null</c> keeps the generator's default (inserted). Backed by the <c>insert_special_tokens</c> key. A
    /// stored value that is not a boolean reads as <c>null</c> here and fails the call.</summary>
    [JsonIgnore]
    public bool? InsertSpecialTokens
    {
        get => LiteRtEmbeddingMapping.GetBool(this, LiteRtEmbeddingMapping.InsertSpecialTokensKey);
        set => Set(LiteRtEmbeddingMapping.InsertSpecialTokensKey, value);
    }

    /// <summary>Gets or sets how a text longer than the model's largest loaded input is handled; <c>null</c>
    /// keeps the generator's default (the runtime fails the call). Backed by the <c>overflow_strategy</c> key. A
    /// stored value that names no strategy reads as <c>null</c> here and fails the call.</summary>
    [JsonIgnore]
    public LiteRtInputOverflowStrategy? OverflowStrategy
    {
        get => LiteRtEmbeddingMapping.ParseOverflowStrategy(
            AdditionalProperties?.TryGetValue(LiteRtEmbeddingMapping.OverflowStrategyKey, out object? value) == true ? value : null);
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
        if (GetBoolOrThrow(options, NormalizeKey) is { } normalize)
            merged = merged with { Normalize = normalize };
        if (GetBoolOrThrow(options, InsertSpecialTokensKey) is { } insert)
            merged = merged with { InsertSpecialTokens = insert };
        if (GetOverflowStrategy(options) is { } overflow)
            merged = merged with { OverflowStrategy = overflow };
        return merged == new LiteRtEmbeddingOptions() && defaults is null ? null : merged;
    }

    /// <summary>The getter-safe read of a boolean knob: <c>null</c> when absent or unreadable.</summary>
    internal static bool? GetBool(EmbeddingGenerationOptions options, string key)
        => options.AdditionalProperties?.TryGetValue(key, out object? value) == true ? LiteRtChatMapping.AsBool(value) : null;

    /// <summary>Reads a boolean knob for a call: <c>null</c> when absent, an error when present but unreadable,
    /// so a typo in configuration does not silently fall back to the default.</summary>
    /// <exception cref="ArgumentException">The value is present but is not a boolean.</exception>
    private static bool? GetBoolOrThrow(EmbeddingGenerationOptions options, string key)
    {
        if (options.AdditionalProperties?.TryGetValue(key, out object? value) != true || value is null)
            return null;
        return LiteRtChatMapping.AsBool(value)
            ?? throw new ArgumentException($"'{Describe(value)}' is not a valid '{key}' value (true or false).", nameof(options));
    }

    /// <summary>Reads the overflow strategy for a call from the enum, its name (case-insensitive) or its
    /// number, including the <see cref="JsonElement"/> values of options deserialized from JSON.</summary>
    /// <exception cref="ArgumentException">The value names no strategy.</exception>
    internal static LiteRtInputOverflowStrategy? GetOverflowStrategy(EmbeddingGenerationOptions options)
    {
        if (options.AdditionalProperties?.TryGetValue(OverflowStrategyKey, out object? value) != true || value is null)
            return null;
        return ParseOverflowStrategy(value)
            ?? throw new ArgumentException(
                $"'{Describe(value)}' is not an embedding overflow strategy (ChunkAndAverage, Truncate or Error).", nameof(options));
    }

    /// <summary>Parses an overflow strategy without throwing: <c>null</c> for an absent value or one that names
    /// no strategy (a boolean, a fractional number, a list of names, an undefined number).</summary>
    internal static LiteRtInputOverflowStrategy? ParseOverflowStrategy(object? value)
    {
        LiteRtInputOverflowStrategy? strategy = value switch
        {
            null or bool => null,
            LiteRtInputOverflowStrategy s => s,
            string text => ParseName(text),
            JsonElement { ValueKind: JsonValueKind.String } json => ParseName(json.GetString()),
            _ => LiteRtChatMapping.AsInt32(value) is { } n ? (LiteRtInputOverflowStrategy)n : null,
        };
        return strategy is { } result && Enum.IsDefined(result) ? result : null;
    }

    // Exact names only: Enum.TryParse would also take numbers in strings and comma-separated lists.
    private static LiteRtInputOverflowStrategy? ParseName(string? text)
    {
        string trimmed = text?.Trim() ?? "";
        foreach (LiteRtInputOverflowStrategy candidate in Enum.GetValues<LiteRtInputOverflowStrategy>())
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return LiteRtChatMapping.AsInt32(trimmed) is { } n ? (LiteRtInputOverflowStrategy)n : null;
    }

    private static string Describe(object value) => value is JsonElement json ? json.GetRawText() : value.ToString() ?? "";
}
