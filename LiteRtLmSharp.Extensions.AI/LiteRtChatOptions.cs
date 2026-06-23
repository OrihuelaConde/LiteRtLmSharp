using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// <see cref="ChatOptions"/> with the LiteRtLmSharp-specific request knobs that MEAI's typed options do not
/// cover. Mirrors the Semantic Kernel connector's <c>LiteRtPromptExecutionSettings</c>: a strongly-typed
/// options subtype whose extra knob is stored in the underlying bag (<see cref="ChatOptions.AdditionalProperties"/>),
/// so it flows through the chat client and survives MEAI middleware that clones the options.
/// </summary>
/// <remarks>
/// <see cref="ChatOptions"/> already exposes <see cref="ChatOptions.Temperature"/>, <see cref="ChatOptions.TopP"/>,
/// <see cref="ChatOptions.TopK"/>, <see cref="ChatOptions.MaxOutputTokens"/> and <see cref="ChatOptions.Seed"/>,
/// so this subtype only adds the one knob MEAI lacks. Setting the <c>enable_thinking</c> key directly on a plain
/// <see cref="ChatOptions.AdditionalProperties"/> works identically; this type is just the discoverable form.
/// </remarks>
public sealed class LiteRtChatOptions : ChatOptions
{
    /// <summary>
    /// Enables the model's reasoning ("thinking") mode. The reasoning trace is surfaced on the response as
    /// <see cref="TextReasoningContent"/> (excluded from <see cref="ChatResponse.Text"/>) and shares the
    /// <see cref="ChatOptions.MaxOutputTokens"/> budget with the answer — give thinking models headroom, or a
    /// small budget can leave the answer empty (signaled by <see cref="ChatFinishReason.Length"/>).
    /// </summary>
    /// <remarks>
    /// Backed by the <c>enable_thinking</c> key in <see cref="ChatOptions.AdditionalProperties"/>;
    /// <see cref="JsonIgnoreAttribute"/> keeps it from serializing twice (the backing key already round-trips).
    /// </remarks>
    [JsonIgnore]
    public bool EnableThinking
    {
        get => AdditionalProperties?.TryGetValue("enable_thinking", out object? v) == true && v is true;
        set => (AdditionalProperties ??= new AdditionalPropertiesDictionary())["enable_thinking"] = value;
    }

    /// <summary>
    /// Forces the model to emit schema-constrained output so tool-call arguments parse reliably — recommended
    /// when using tools. Off by default. Only meaningful when <see cref="ChatOptions.Tools"/> are set.
    /// </summary>
    /// <remarks>
    /// Backed by the <c>enable_constrained_decoding</c> key in <see cref="ChatOptions.AdditionalProperties"/>.
    /// Tools still work without it (arguments are simply not grammar-constrained).
    /// </remarks>
    [JsonIgnore]
    public bool EnableConstrainedDecoding
    {
        get => AdditionalProperties?.TryGetValue("enable_constrained_decoding", out object? v) == true && v is true;
        set => (AdditionalProperties ??= new AdditionalPropertiesDictionary())["enable_constrained_decoding"] = value;
    }
}
