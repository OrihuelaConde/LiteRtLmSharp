namespace LiteRtLmSharp;

/// <summary>
/// Per-send options for <see cref="LiteRtConversation.Send(string, System.Collections.Generic.IReadOnlyList{LiteRtAttachment}, LiteRtSendOptions)"/>
/// and its async/streaming variants. Everything here applies to <b>one send</b>, overriding the
/// conversation-level setting where one exists; pass <c>null</c> (the default) to use the
/// conversation's configuration unchanged.
/// </summary>
/// <remarks>
/// This is also the growth point for per-send settings future native versions add (e.g. a per-send
/// output-token cap), so they can land without changing the send signatures. Maps to the C API's
/// per-send <c>conversation_optional_args</c>.
/// </remarks>
public sealed record LiteRtSendOptions
{
    /// <summary>
    /// Budget (in tokens) that <b>image</b> attachments in this send may consume during prefill.
    /// 0 (default) = inherit <see cref="LiteRtConversationOptions.VisualTokenBudget"/> (whose own 0
    /// means the engine default). Only meaningful when the send carries image attachments.
    /// </summary>
    public int VisualTokenBudget { get; init; }

    /// <summary>
    /// Maximum output tokens for <b>this one send</b>. 0 (default) = inherit the conversation-level
    /// <see cref="LiteRtConversationOptions.MaxOutputTokens"/> (whose own 0 means the engine default).
    /// When positive it overrides that conversation-level cap for this send only.
    /// </summary>
    /// <remarks>
    /// Maps to the C API <c>conversation_optional_args_set_max_output_tokens</c>. This is the same
    /// underlying decode cap as the conversation-level setting, applied at per-send granularity: the
    /// native runtime resolves the effective cap as the per-send value when present, otherwise the
    /// session's value (session_advanced.cc). Unlike the conversation-level setting, it does not create a
    /// session config, so it never changes multimodal encoder loading.
    /// </remarks>
    public int MaxOutputTokens { get; init; }

    /// <summary>
    /// Repetition penalties (multiplicative and/or subtractive) applied to this send's decode.
    /// <c>null</c> (default) = none. Requires native LiteRT-LM v0.15.0+.
    /// </summary>
    public LiteRtRepetitionPenaltyOptions? RepetitionPenalties { get; init; }

    /// <summary>
    /// Bans this send's decode from repeating n-grams it already produced. <c>null</c> (default) =
    /// no ban. Requires native LiteRT-LM v0.15.0+.
    /// </summary>
    public LiteRtNoRepeatNgramOptions? NoRepeatNgram { get; init; }

    /// <summary>
    /// Token ids banned from this send's decode: every listed id's logit is forced to -inf on each
    /// step. <c>null</c>/empty (default) = none. Find ids with <see cref="LiteRtEngine.Tokenize"/>
    /// (note most words tokenize differently with/without a leading space). Requires native
    /// LiteRT-LM v0.15.0+.
    /// </summary>
    public IReadOnlyList<int>? SuppressTokens { get; init; }

    /// <summary>
    /// Per-send thinking override: <c>true</c>/<c>false</c> turns the model's reasoning mode on/off
    /// for this send only, overriding the conversation-level
    /// <see cref="LiteRtConversationOptions.EnableThinking"/>. <c>null</c> (default) = inherit.
    /// Requires native LiteRT-LM v0.15.0+.
    /// </summary>
    /// <remarks>
    /// Maps to the per-send thinking config (<c>conversation_optional_args_set_thinking_config</c>),
    /// which the runtime resolves ahead of the conversation-level config. An explicit
    /// <c>enable_thinking</c> in the conversation's <see cref="LiteRtConversationOptions.ExtraContext"/>
    /// still wins over both (the template merge keeps existing keys).
    /// </remarks>
    public bool? EnableThinking { get; init; }

    private readonly int? _thinkingTokenBudget;

    /// <summary>
    /// Per-send thinking token budget, overriding the conversation-level
    /// <see cref="LiteRtConversationOptions.ThinkingTokenBudget"/> for this send. <c>null</c>
    /// (default) = inherit; <c>-1</c> = explicitly infinite. Requires native LiteRT-LM v0.15.0+.
    /// </summary>
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
    /// Output constraint (regex / JSON Schema) enforced during this send's sampling. Requires the
    /// conversation to have been created with
    /// <see cref="LiteRtConversationOptions.ConstraintProvider"/> set — without a provider the send
    /// throws <see cref="LiteRtException"/>. <c>null</c> (default) = unconstrained. Requires native
    /// LiteRT-LM v0.15.0+.
    /// </summary>
    public LiteRtConstraint? Constraint { get; init; }
}
