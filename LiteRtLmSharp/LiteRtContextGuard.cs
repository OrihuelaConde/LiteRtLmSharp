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
    /// State entries a send's <b>prefill</b> needs free to be accepted by the v0.15.0+ native
    /// executor: it plans the prefill in fixed work groups taken from the model's prefill signatures
    /// and rejects the send outright (<c>FAILED_PRECONDITION: Chosen prefill work group size exceeds
    /// available state entries</c>, litert_compiled_model_executor_utils.cc
    /// <c>GetOptimizedPrefillWorkGroups</c>) when fewer entries remain than the smallest signature
    /// length — regardless of how short the message actually is. 128 is the smallest prefill
    /// signature in the current gemma <c>.litertlm</c> conversions (observed live: 99 remaining
    /// entries rejected). A model with a smaller signature just gets the typed rejection slightly
    /// early; pre-v0.15.0 natives (no such check) simply keep the stronger guard.
    /// </summary>
    internal const int MinPrefillReserve = 128;

    /// <summary>
    /// The largest prefill signature of the published gemma <c>.litertlm</c> conversions. A
    /// <c>MaxNumTokens</c> below it is accepted by the native loader but breaks prefill for inputs longer
    /// than the smallest work group (verified on v0.15.0 and v0.16.0 with a raw C API ladder). The C API
    /// exposes no signature query, so the binding cannot validate this up front — it names the cause when
    /// a send fails on such an engine (<see cref="IsBelowLargestKnownPrefillSignature"/>).
    /// </summary>
    internal const int LargestKnownPrefillSignature = 1024;

    /// <summary>Whether an explicit <paramref name="maxNumTokens"/> (0 = unknown) sits below
    /// <see cref="LargestKnownPrefillSignature"/>.</summary>
    internal static bool IsBelowLargestKnownPrefillSignature(int maxNumTokens)
        => maxNumTokens > 0 && maxNumTokens < LargestKnownPrefillSignature;

    /// <summary>
    /// Whether a conversation's context is effectively full: fewer than <see cref="MinPrefillReserve"/>
    /// entries remain, so the v0.15.0+ executor would reject any next send's prefill (and a clamped
    /// send lands here by construction — its decode budget stops at the <see cref="SafetyMargin"/>,
    /// well inside the reserve). This is THE full/not-full predicate, shared by the pre-send hard stop
    /// and the public <see cref="LiteRtConversation.IsContextFull"/> signal so they can never
    /// disagree: the signal is true exactly when the next send would throw. False when the limit is
    /// unknown (≤ 0, guard off).
    /// </summary>
    internal static bool IsContextFull(int tokenCount, int maxNumTokens)
        => maxNumTokens > 0 && tokenCount > maxNumTokens - MinPrefillReserve;

    /// <summary>Throws when the conversation's context is full (see <see cref="IsContextFull"/>) — the
    /// state shown in the field to crash the native runtime on the next send. No-op when the limit is unknown (≤ 0).</summary>
    internal static void ThrowIfContextFull(int tokenCount, int maxNumTokens)
    {
        if (IsContextFull(tokenCount, maxNumTokens))
            ThrowContextFull(tokenCount, maxNumTokens);
    }

    /// <summary>Throws the context-full rejection unconditionally — for callers that already know the
    /// conversation is full (e.g. a latched ceiling detection) regardless of the threshold.</summary>
    internal static void ThrowContextFull(int tokenCount, int maxNumTokens)
    {
        // Degenerate configuration: a limit below the minimum prefill work group makes EVERY send
        // impossible — a fresh conversation is "full" at zero tokens. The generic message below
        // ("its KV cache holds 0 tokens against MaxNumTokens = 100") would read as nonsense; name
        // the real problem instead.
        if (maxNumTokens < MinPrefillReserve)
            throw new LiteRtContextOverflowException(
                $"MaxNumTokens = {maxNumTokens} is below the native executor's minimum prefill work group " +
                $"(~{MinPrefillReserve} tokens for the current model conversions): no message can ever be " +
                "prefilled into a context this small, so every send would be rejected. Reload the engine " +
                $"with MaxNumTokens >= {MinPrefillReserve} (in practice, comfortably larger).",
                tokenCount, maxNumTokens);
        throw new LiteRtContextOverflowException(
            $"The conversation's context is full: its KV cache holds {tokenCount} tokens against " +
            $"MaxNumTokens = {maxNumTokens}, and the native executor plans prefill in fixed work groups " +
            $"(the guard treats the last {MinPrefillReserve} tokens as unusable for a new send — fewer free " +
            "entries than the model's smallest prefill signature make the runtime reject any send; on " +
            "pre-v0.15.0 natives an overflow corrupted the KV cache outright). The previous reply may have " +
            "been truncated to fit — LiteRtConversation.IsContextFull signals this state as soon as it is " +
            "reached. Dispose this conversation and start a fresh one — restore a trimmed " +
            "LiteRtConversationOptions.History if the thread must continue — or reload the engine with a " +
            "larger LiteRtEngineOptions.MaxNumTokens.",
            tokenCount, maxNumTokens);
    }

    /// <summary>
    /// The decode budget (max output tokens) that keeps a send inside the KV cache, after the message's
    /// measured prefill cost and the <see cref="SafetyMargin"/>. Throws when the message itself does not
    /// fit: either its prefill PLAN does not fit the remaining entries (the v0.15.0+ executor plans
    /// prefill in fixed work groups from the model's prefill signatures, so a plan consumes the input
    /// rounded UP to whole signature lengths — an input of 180 tokens against 200 remaining entries is
    /// rejected natively because it plans as two 128-entry groups; modeled here as
    /// ceil(input / <see cref="MinPrefillReserve"/>) × <see cref="MinPrefillReserve"/>), or no room is
    /// left to decode even one token.
    /// </summary>
    internal static int DecodeBudget(int tokenCount, int maxNumTokens, int inputTokens)
    {
        // Work-group granularity: the native planner debits FULL signature lengths per chunk
        // (litert_compiled_model_executor_utils.cc GetOptimizedPrefillWorkGroups — a partial chunk
        // still consumes its whole cur_seq_len, and an uncoverable tail is a hard error). Without
        // this check a send in the band remaining ∈ (reserve, 2×reserve) with input > reserve
        // passes the flat budget below but is rejected natively with a raw, untyped error.
        int remaining = maxNumTokens - tokenCount;
        long plannedEntries = (long)(inputTokens + MinPrefillReserve - 1) / MinPrefillReserve * MinPrefillReserve;
        int budget = maxNumTokens - tokenCount - inputTokens - SafetyMargin;
        if (plannedEntries > remaining || budget < 1)
            throw new LiteRtContextOverflowException(
                $"This message does not fit the conversation's remaining context: the KV cache holds " +
                $"{tokenCount} of MaxNumTokens = {maxNumTokens} tokens and the message's templated prefill " +
                $"measures {inputTokens} tokens (planned as ~{plannedEntries} entries — the native executor " +
                $"prefills in fixed work groups of the model's signature lengths), leaving no room to " +
                "decode a reply. Send a shorter message, dispose this conversation and start a fresh one " +
                "(restore a trimmed LiteRtConversationOptions.History if needed), or reload the engine " +
                "with a larger LiteRtEngineOptions.MaxNumTokens.",
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
