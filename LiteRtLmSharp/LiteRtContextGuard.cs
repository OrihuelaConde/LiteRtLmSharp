namespace LiteRtLmSharp;

/// <summary>
/// The arithmetic behind the KV-cache overflow guard (see <see cref="LiteRtContextOverflowException"/>),
/// kept native-free so the thresholds are unit-testable. <see cref="LiteRtConversation"/> supplies the
/// live numbers (token count, measured prefill cost) and applies the returned decode budget as the
/// per-send output cap.
/// </summary>
internal static class LiteRtContextGuard
{
    /// <summary>
    /// Tokens reserved out of every decode budget to absorb the small accounting drift between our
    /// measurement (render + tokenize of the templated text) and what the runtime actually prefills
    /// (BOS/system tokens the template layer adds around it). Overshooting the KV cache by even a few
    /// tokens corrupts native memory, so the budget errs low.
    /// </summary>
    internal const int SafetyMargin = 16;

    /// <summary>
    /// Whether a conversation's context is effectively full: within the <see cref="SafetyMargin"/> of the
    /// limit (a clamped send lands here by construction — its decode budget stops at the margin) or past it.
    /// This is THE full/not-full predicate, shared by the pre-send hard stop and the public
    /// <see cref="LiteRtConversation.IsContextFull"/> signal so they can never disagree: the signal is true
    /// exactly when the next send would throw. False when the limit is unknown (≤ 0, guard off).
    /// </summary>
    internal static bool IsContextFull(int tokenCount, int maxNumTokens)
        => maxNumTokens > 0 && tokenCount >= maxNumTokens - SafetyMargin;

    /// <summary>Throws when the conversation's context is full (see <see cref="IsContextFull"/>) — the
    /// state B9 showed crashes the native runtime on the next send. No-op when the limit is unknown (≤ 0).</summary>
    internal static void ThrowIfContextFull(int tokenCount, int maxNumTokens)
    {
        if (IsContextFull(tokenCount, maxNumTokens))
            ThrowContextFull(tokenCount, maxNumTokens);
    }

    /// <summary>Throws the context-full rejection unconditionally — for callers that already know the
    /// conversation is full (e.g. a latched ceiling detection) regardless of the threshold.</summary>
    internal static void ThrowContextFull(int tokenCount, int maxNumTokens)
        => throw new LiteRtContextOverflowException(
            $"The conversation's context is full: its KV cache holds {tokenCount} tokens against " +
            $"MaxNumTokens = {maxNumTokens} (the guard treats the last {SafetyMargin} tokens as reserved). " +
            "Sending more would write past the allocated cache and corrupt the native runtime (typically a " +
            "deferred 0xC0000005 crash). The previous reply may have been truncated to fit — " +
            "LiteRtConversation.IsContextFull signals this state as soon as it is reached. Dispose this " +
            "conversation and start a fresh one — restore a trimmed LiteRtConversationOptions.History if " +
            "the thread must continue — or reload the engine with a larger LiteRtEngineOptions.MaxNumTokens.",
            tokenCount, maxNumTokens);

    /// <summary>
    /// The decode budget (max output tokens) that keeps a send inside the KV cache, after the message's
    /// measured prefill cost and the <see cref="SafetyMargin"/>. Throws when the message itself does not
    /// fit (no room left to decode even one token).
    /// </summary>
    internal static int DecodeBudget(int tokenCount, int maxNumTokens, int inputTokens)
    {
        int budget = maxNumTokens - tokenCount - inputTokens - SafetyMargin;
        if (budget < 1)
            throw new LiteRtContextOverflowException(
                $"This message does not fit the conversation's remaining context: the KV cache holds " +
                $"{tokenCount} of MaxNumTokens = {maxNumTokens} tokens and the message's templated prefill " +
                $"measures {inputTokens} tokens, leaving no room to decode a reply. Sending it would " +
                "overflow the cache and corrupt the native runtime. Send a shorter message, dispose this " +
                "conversation and start a fresh one (restore a trimmed LiteRtConversationOptions.History " +
                "if needed), or reload the engine with a larger LiteRtEngineOptions.MaxNumTokens.",
                tokenCount, maxNumTokens);
        return budget;
    }

    /// <summary>
    /// The effective per-send output cap: the caller's own cap when it already fits the
    /// <paramref name="decodeBudget"/>, otherwise the budget. <paramref name="callerCap"/> ≤ 0 means
    /// "no cap requested", which the budget replaces.
    /// </summary>
    internal static int EffectiveOutputCap(int decodeBudget, int callerCap)
        => callerCap > 0 && callerCap <= decodeBudget ? callerCap : decodeBudget;
}
