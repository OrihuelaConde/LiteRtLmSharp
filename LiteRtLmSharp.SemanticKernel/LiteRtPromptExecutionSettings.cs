using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;

namespace LiteRtLmSharp.SemanticKernel;

/// <summary>
/// Typed <see cref="PromptExecutionSettings"/> for the LiteRtLmSharp Semantic Kernel connectors. Set
/// any subset; unset values fall back to the engine/model defaults. Pass an instance directly to
/// <c>GetChatMessageContentsAsync</c> / <c>kernel.InvokePromptAsync</c>, or let the connector recover
/// these from a generic <see cref="PromptExecutionSettings"/> (e.g. one declared in a prompt template's
/// YAML/JSON) via <see cref="FromExecutionSettings"/> — the JSON keys are the snake_case names below.
/// </summary>
public sealed class LiteRtPromptExecutionSettings : PromptExecutionSettings
{
    /// <summary>Sampling temperature. Higher = more random. Maps to <see cref="SamplerParams.Temperature"/>.</summary>
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>Nucleus (top-p) cutoff. Maps to <see cref="SamplerParams.TopP"/>; selects the TopP sampler.</summary>
    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    /// <summary>Top-k cutoff. Maps to <see cref="SamplerParams.TopK"/>.</summary>
    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    /// <summary>Maximum tokens to generate for the reply. Maps to
    /// <see cref="LiteRtConversationOptions.MaxOutputTokens"/>. Null = engine default.</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>Sampler RNG seed for reproducible output. Maps to <see cref="SamplerParams.Seed"/>.</summary>
    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    /// <summary>
    /// Toggle the model's reasoning ("thinking") mode (Gemma reasoning builds). Maps to
    /// <see cref="LiteRtConversationOptions.EnableThinking"/>. The reasoning trace is intentionally NOT
    /// included in the returned content — it changes how the model answers, the trace stays internal.
    /// </summary>
    [JsonPropertyName("enable_thinking")]
    public bool? EnableThinking { get; set; }

    /// <summary>
    /// System prompt for <b>text generation</b> (<see cref="LiteRtTextGenerationService"/>), which has no
    /// chat roles. Ignored by the chat-completion service — there the system prompt travels as a
    /// <c>System</c>-role message in the <c>ChatHistory</c>.
    /// </summary>
    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; set; }

    // Reused for the FromExecutionSettings round-trip. Case-insensitive so PascalCase ExtensionData keys
    // (e.g. "MaxTokens") still bind in addition to the snake_case JSON names above.
    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Returns <paramref name="settings"/> as a <see cref="LiteRtPromptExecutionSettings"/>: the instance
    /// itself when it already is one, a fresh default when it is <c>null</c>, otherwise a typed copy
    /// recovered by serializing the generic settings (whose <c>[JsonExtensionData]</c> surfaces keys such
    /// as <c>temperature</c> / <c>max_tokens</c>) and deserializing into this type. Mirrors the pattern
    /// the official Semantic Kernel connectors use to accept settings declared in prompt templates.
    /// </summary>
    public static LiteRtPromptExecutionSettings FromExecutionSettings(PromptExecutionSettings? settings)
    {
        switch (settings)
        {
            case null:
                return new LiteRtPromptExecutionSettings();
            case LiteRtPromptExecutionSettings typed:
                return typed;
            default:
                string json = JsonSerializer.Serialize(settings, s_serializerOptions);
                return JsonSerializer.Deserialize<LiteRtPromptExecutionSettings>(json, s_serializerOptions)
                       ?? new LiteRtPromptExecutionSettings();
        }
    }

    /// <summary>
    /// Deep-copies these settings, preserving the typed sampler/output knobs. Overriding the base
    /// <see cref="PromptExecutionSettings.Clone"/> is required: the base implementation builds a plain
    /// <see cref="PromptExecutionSettings"/> and would drop the typed properties below (so the clone would
    /// yield a null sampler) when Semantic Kernel clones settings internally — e.g. those declared in a
    /// prompt-template config.
    /// </summary>
    public override PromptExecutionSettings Clone() => new LiteRtPromptExecutionSettings
    {
        ModelId = ModelId,
        ServiceId = ServiceId,
        FunctionChoiceBehavior = FunctionChoiceBehavior,
        ExtensionData = ExtensionData is not null ? new Dictionary<string, object>(ExtensionData) : null,
        Temperature = Temperature,
        TopP = TopP,
        TopK = TopK,
        MaxTokens = MaxTokens,
        Seed = Seed,
        EnableThinking = EnableThinking,
        SystemPrompt = SystemPrompt,
    };

    /// <summary>
    /// Builds the <see cref="SamplerParams"/> for these settings, or <c>null</c> when none of the sampler
    /// knobs were set (so the engine default sampler is used). Any set knob selects the TopP sampler and
    /// fills the unset knobs from <see cref="SamplerParams"/>' own defaults.
    /// </summary>
    internal SamplerParams? ToSamplerParams()
    {
        if (Temperature is null && TopP is null && TopK is null && Seed is null)
            return null;

        var defaults = new SamplerParams();
        return new SamplerParams
        {
            Type = global::LiteRtLmSharp.SamplerType.TopP,
            TopK = TopK ?? defaults.TopK,
            TopP = TopP ?? defaults.TopP,
            Temperature = Temperature ?? defaults.Temperature,
            Seed = Seed ?? defaults.Seed,
        };
    }
}
