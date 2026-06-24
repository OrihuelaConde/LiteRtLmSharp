using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;

namespace LiteRtLmSharp.SemanticKernel;

/// <summary>
/// Convenience <see cref="PromptExecutionSettings"/> for the LiteRtLmSharp Semantic Kernel connector. The
/// connector exposes the on-device model through Semantic Kernel's <c>IChatClient</c> adapter
/// (<c>AsChatCompletionService</c>), which converts a <see cref="PromptExecutionSettings"/> into a
/// Microsoft.Extensions.AI <c>ChatOptions</c> using well-known <see cref="PromptExecutionSettings.ExtensionData"/>
/// keys. So this type simply stores its knobs in <c>ExtensionData</c> under those keys — they flow straight
/// through to the engine's sampler. You can equally pass a plain <see cref="PromptExecutionSettings"/> whose
/// <c>ExtensionData</c> holds the same keys (e.g. from a prompt template's YAML).
/// </summary>
public sealed class LiteRtPromptExecutionSettings : PromptExecutionSettings
{
    /// <summary>Sampling temperature (<c>"temperature"</c>). Higher = more random.</summary>
    [JsonIgnore]
    public float? Temperature { get => GetSingle("temperature"); set => Set("temperature", value); }

    /// <summary>Nucleus (top-p) cutoff (<c>"top_p"</c>).</summary>
    [JsonIgnore]
    public float? TopP { get => GetSingle("top_p"); set => Set("top_p", value); }

    /// <summary>Top-k cutoff (<c>"top_k"</c>).</summary>
    [JsonIgnore]
    public int? TopK { get => GetInt32("top_k"); set => Set("top_k", value); }

    /// <summary>Maximum tokens to generate for the reply (<c>"max_tokens"</c>).</summary>
    [JsonIgnore]
    public int? MaxTokens { get => GetInt32("max_tokens"); set => Set("max_tokens", value); }

    /// <summary>Sampler RNG seed for reproducible output (<c>"seed"</c>).</summary>
    [JsonIgnore]
    public int? Seed { get => GetInt32("seed"); set => Set("seed", value); }

    /// <summary>Toggle the model's reasoning ("thinking") mode (<c>"enable_thinking"</c>). The reasoning trace
    /// is not included in the returned content — it only changes how the model answers.</summary>
    [JsonIgnore]
    public bool? EnableThinking { get => GetBoolean("enable_thinking"); set => Set("enable_thinking", value); }

    /// <summary>Force schema-constrained output so function-call arguments parse reliably
    /// (<c>"enable_constrained_decoding"</c>) — recommended when using tools. Off by default; only meaningful
    /// with a <see cref="PromptExecutionSettings.FunctionChoiceBehavior"/>. Tools still work without it.</summary>
    [JsonIgnore]
    public bool? EnableConstrainedDecoding { get => GetBoolean("enable_constrained_decoding"); set => Set("enable_constrained_decoding", value); }

    // ── ExtensionData backing ─────────────────────────────────────────────────────────────────────────
    // The knobs live in ExtensionData (the typed properties are [JsonIgnore] so only the lower-case keys
    // serialize). Semantic Kernel's IChatClient adapter reads these keys to build the MEAI ChatOptions, and
    // the LiteRtChatClient maps that ChatOptions onto the engine's sampler. Getters tolerate both the boxed
    // values we store and the JsonElement values that arrive when settings are deserialized from JSON/YAML.

    private void Set(string key, object? value)
    {
        ExtensionData ??= new Dictionary<string, object>();
        if (value is null)
            ExtensionData.Remove(key);
        else
            ExtensionData[key] = value;
    }

    private bool TryGet(string key, out object? value)
    {
        value = null;
        return ExtensionData is { } data && data.TryGetValue(key, out value);
    }

    private float? GetSingle(string key) => TryGet(key, out object? v) ? ToSingle(v) : null;
    private int? GetInt32(string key) => TryGet(key, out object? v) ? ToInt32(v) : null;
    private bool? GetBoolean(string key) => TryGet(key, out object? v) ? ToBoolean(v) : null;

    private static float? ToSingle(object? v) => v switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        long l => l,
        string s when float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out float r) => r,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetSingle(),
        JsonElement { ValueKind: JsonValueKind.String } e when float.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, out float r) => r,
        _ => null,
    };

    private static int? ToInt32(object? v) => v switch
    {
        int i => i,
        long l => (int)l,
        float f => (int)f,
        double d => (int)d,
        string s when int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out int r) => r,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetInt32(),
        JsonElement { ValueKind: JsonValueKind.String } e when int.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, out int r) => r,
        _ => null,
    };

    private static bool? ToBoolean(object? v) => v switch
    {
        bool b => b,
        string s when bool.TryParse(s, out bool r) => r,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.String } e when bool.TryParse(e.GetString(), out bool r) => r,
        _ => null,
    };
}
