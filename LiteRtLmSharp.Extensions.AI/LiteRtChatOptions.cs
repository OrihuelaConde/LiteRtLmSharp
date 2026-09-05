using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// <see cref="ChatOptions"/> with the LiteRtLmSharp-specific request knobs that MEAI's typed options do not
/// cover. Mirrors the Semantic Kernel connector's <c>LiteRtPromptExecutionSettings</c>: a strongly-typed
/// options subtype whose extra knobs are stored in the underlying bag (<see cref="ChatOptions.AdditionalProperties"/>),
/// so they flow through the chat client and survive MEAI middleware that clones the options.
/// </summary>
/// <remarks>
/// <see cref="ChatOptions"/> already exposes <see cref="ChatOptions.Temperature"/>, <see cref="ChatOptions.TopP"/>,
/// <see cref="ChatOptions.TopK"/>, <see cref="ChatOptions.MaxOutputTokens"/>, <see cref="ChatOptions.Seed"/>,
/// <see cref="ChatOptions.FrequencyPenalty"/> and <see cref="ChatOptions.PresencePenalty"/>, so this subtype only
/// adds the knobs MEAI lacks. Setting the same keys directly on a plain <see cref="ChatOptions.AdditionalProperties"/>
/// works identically; this type is just the discoverable form.
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
        get => AdditionalProperties?.TryGetValue("enable_thinking", out object? v) == true && LiteRtChatMapping.AsBool(v) == true;
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
        get => AdditionalProperties?.TryGetValue("enable_constrained_decoding", out object? v) == true && LiteRtChatMapping.AsBool(v) == true;
        set => (AdditionalProperties ??= new AdditionalPropertiesDictionary())["enable_constrained_decoding"] = value;
    }

    /// <summary>
    /// Bans the reply from repeating any n-gram of this many tokens that it already produced during the same
    /// generation (the native no-repeat-ngram ban, tracked over the whole reply). <c>null</c> (default) = off.
    /// Useful against a small model echoing a prompt template verbatim. Requires native LiteRT-LM v0.15.0+.
    /// </summary>
    /// <remarks>
    /// Backed by the <c>no_repeat_ngram_size</c> key in <see cref="ChatOptions.AdditionalProperties"/>; maps to
    /// <see cref="LiteRtSendOptions.NoRepeatNgram"/> with the default window (the full reply). Setting
    /// <c>null</c> removes the key. A zero or negative size is rejected when the send options are built.
    /// </remarks>
    [JsonIgnore]
    public int? NoRepeatNgramSize
    {
        get => LiteRtChatMapping.GetInt32Property(this, "no_repeat_ngram_size");
        set => SetOrRemove("no_repeat_ngram_size", value);
    }

    /// <summary>
    /// Token ids the reply may never emit: each listed id's logit is forced to <c>-inf</c> on every decode
    /// step. Find ids with <see cref="LiteRtEngine.Tokenize"/>; most words tokenize differently with and
    /// without a leading space, so ban both forms. <c>null</c>/empty (default) = none. Requires native
    /// LiteRT-LM v0.15.0+.
    /// </summary>
    /// <remarks>
    /// Backed by the <c>suppress_tokens</c> key in <see cref="ChatOptions.AdditionalProperties"/> (stored as an
    /// <c>int[]</c>; a JSON array of numbers or a comma-separated string is accepted when the options come from
    /// JSON/YAML); maps to <see cref="LiteRtSendOptions.SuppressTokens"/>. Setting <c>null</c> removes the key.
    /// A malformed value reads as <c>null</c> here; negative ids and non-integer entries are rejected when
    /// the send options are built.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<int>? SuppressTokens
    {
        get => LiteRtChatMapping.TryGetInt32ListProperty(this, "suppress_tokens");
        set => SetOrRemove("suppress_tokens", value is null ? null : value.ToArray());
    }

    private void SetOrRemove(string key, object? value)
    {
        if (value is null)
        {
            AdditionalProperties?.Remove(key);
            return;
        }
        (AdditionalProperties ??= new AdditionalPropertiesDictionary())[key] = value;
    }
}
