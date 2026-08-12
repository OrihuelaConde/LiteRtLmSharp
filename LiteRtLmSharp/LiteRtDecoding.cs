namespace LiteRtLmSharp;

/// <summary>
/// Repetition penalties for one send's decode, in the two industry conventions the native runtime
/// implements: a multiplicative HuggingFace-style <see cref="RepetitionPenalty"/> and subtractive
/// OpenAI-style <see cref="PresencePenalty"/>/<see cref="FrequencyPenalty"/>. All penalties look at
/// the <b>generated</b> output window of the current send only (not the prompt, not prior turns).
/// Set on <see cref="LiteRtSendOptions.RepetitionPenalties"/>. Requires native LiteRT-LM v0.15.0+.
/// </summary>
/// <remarks>Maps to the C API <c>litert_lm_repetition_penalty_config_*</c> builder; the defaults
/// below are the native defaults, so a partially configured record is faithful.</remarks>
public sealed record LiteRtRepetitionPenaltyOptions
{
    private readonly float _repetitionPenalty = 1.0f;

    /// <summary>Multiplicative repetition penalty (HuggingFace style): positive logits of tokens
    /// already seen in the window are divided by this value, negative ones multiplied. 1.0 (default)
    /// = off; must be &gt;= 1.0 (the native layer clamps lower values to 1.0 at execution time — the
    /// binding rejects them up front instead).</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is NaN or below 1.0.</exception>
    public float RepetitionPenalty
    {
        get => _repetitionPenalty;
        init
        {
            if (float.IsNaN(value) || value < 1.0f)
                throw new ArgumentOutOfRangeException(nameof(value), value, "RepetitionPenalty must be >= 1.0 (1.0 = off).");
            _repetitionPenalty = value;
        }
    }

    private readonly float _presencePenalty;

    /// <summary>Subtractive presence penalty (OpenAI style): subtracted once from a token's logit if
    /// the token appeared anywhere in the window. Positive discourages repetition, negative rewards
    /// it. Default 0 = off.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is NaN or infinite.</exception>
    public float PresencePenalty
    {
        get => _presencePenalty;
        init
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "PresencePenalty must be a finite number.");
            _presencePenalty = value;
        }
    }

    private readonly float _frequencyPenalty;

    /// <summary>Subtractive frequency penalty (OpenAI style): subtracted from a token's logit scaled
    /// by how many times the token appeared in the window. Positive discourages repetition, negative
    /// rewards it. Default 0 = off.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is NaN or infinite.</exception>
    public float FrequencyPenalty
    {
        get => _frequencyPenalty;
        init
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "FrequencyPenalty must be a finite number.");
            _frequencyPenalty = value;
        }
    }

    private readonly int _windowSize;

    /// <summary>How many of the most recent generated tokens the penalties consider. 0 (default) =
    /// the whole generation history of the send.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is negative.</exception>
    public int WindowSize
    {
        get => _windowSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _windowSize = value;
        }
    }
}

/// <summary>
/// Bans the current send's decode from repeating any n-gram of <see cref="NgramSize"/> consecutive
/// tokens it already produced: a candidate token that would complete an already-seen n-gram has its
/// logit forced to -inf. Set on <see cref="LiteRtSendOptions.NoRepeatNgram"/>. Requires native
/// LiteRT-LM v0.15.0+.
/// </summary>
/// <remarks>Maps to the C API <c>litert_lm_no_repeat_ngram_config_*</c> builder.</remarks>
public sealed record LiteRtNoRepeatNgramOptions
{
    private readonly int _ngramSize;

    /// <summary>Size of the n-grams (consecutive token sequences) banned from repeating. Must be
    /// positive — construct the record only when you want the ban (a zero/negative size is the native
    /// "disabled" encoding; in this binding simply leave <see cref="LiteRtSendOptions.NoRepeatNgram"/>
    /// null instead).</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public required int NgramSize
    {
        get => _ngramSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _ngramSize = value;
        }
    }

    private readonly int _windowSize;

    /// <summary>How many of the most recent generated tokens are tracked for repeats. 0 (default) =
    /// the whole generation history of the send. A positive value smaller than
    /// <see cref="NgramSize"/> is clamped up to it natively.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is negative.</exception>
    public int WindowSize
    {
        get => _windowSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _windowSize = value;
        }
    }
}

/// <summary>The shape of a <see cref="LiteRtConstraint"/> pattern string.</summary>
public enum LiteRtConstraintType
{
    /// <summary>A regular expression the whole output must match.</summary>
    Regex = 1,

    /// <summary>A JSON Schema the output must conform to (the output is forced to be a matching
    /// JSON document).</summary>
    JsonSchema = 2,
}

/// <summary>
/// A per-send output constraint enforced during sampling by the conversation's
/// <see cref="LiteRtConversationOptions.ConstraintProvider"/>: tokens that cannot lead to a valid
/// completion of the pattern are masked out, so the model can only emit conforming output. Set on
/// <see cref="LiteRtSendOptions.Constraint"/>; requires the conversation to have been created with
/// <see cref="LiteRtConstraintProvider.LlGuidance"/>. Requires native LiteRT-LM v0.15.0+.
/// </summary>
/// <remarks>Maps to the C API <c>litert_lm_conversation_optional_args_set_constraint</c>.</remarks>
public sealed record LiteRtConstraint
{
    private readonly LiteRtConstraintType _type;

    /// <summary>How to interpret <see cref="Pattern"/>. Must be a defined member — an out-of-range
    /// value (including the unset <c>default</c>) is rejected here rather than passed to the native
    /// layer, whose constraint compiler does not police the enum (an undefined value reaches
    /// undefined behavior in the LlGuidance bridge).</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is not
    /// <see cref="LiteRtConstraintType.Regex"/> or <see cref="LiteRtConstraintType.JsonSchema"/>.</exception>
    public required LiteRtConstraintType Type
    {
        get => _type;
        init
        {
            if (value is not (LiteRtConstraintType.Regex or LiteRtConstraintType.JsonSchema))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Type must be Regex or JsonSchema.");
            _type = value;
        }
    }

    private readonly string _pattern = null!;

    /// <summary>The constraint pattern: a regular expression or a JSON Schema document, per
    /// <see cref="Type"/>. Must be non-empty.</summary>
    /// <exception cref="System.ArgumentException">The value is null or whitespace.</exception>
    public required string Pattern
    {
        get => _pattern;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _pattern = value;
        }
    }

    /// <summary>A constraint forcing output that conforms to the given JSON Schema document.</summary>
    public static LiteRtConstraint FromJsonSchema(string schemaJson) =>
        new() { Type = LiteRtConstraintType.JsonSchema, Pattern = schemaJson };

    /// <summary>A constraint forcing output that matches the given regular expression.</summary>
    public static LiteRtConstraint FromRegex(string regex) =>
        new() { Type = LiteRtConstraintType.Regex, Pattern = regex };
}

/// <summary>
/// Provider used to compile and enforce custom <see cref="LiteRtConstraint"/>s (the "custom
/// constrained decoding" path). Distinct from <see cref="LiteRtConversationOptions.EnableConstrainedDecoding"/>,
/// which is the <b>tool-calling</b> constrained-decoding path — the native runtime supports only one
/// of the two per conversation.
/// </summary>
public enum LiteRtConstraintProvider
{
    /// <summary>The <a href="https://github.com/guidance-ai/llguidance">llguidance</a> engine
    /// (regex, JSON Schema). Compiled into the native library from source — unlike the tool-calling
    /// constraint provider, it does not depend on a platform prebuilt.</summary>
    LlGuidance = 1,
}
