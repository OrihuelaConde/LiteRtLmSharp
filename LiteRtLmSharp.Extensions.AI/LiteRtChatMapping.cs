using Microsoft.Extensions.AI;

namespace LiteRtLmSharp.Extensions.AI;

/// <summary>
/// Maps Microsoft.Extensions.AI chat types onto the stateless LiteRtLmSharp conversation model. MEAI passes
/// the full message list every call; LiteRtLmSharp conversations are stateful, so each call rebuilds a fresh
/// one: every message except the last becomes restored <see cref="LiteRtConversationOptions.History"/>
/// (replayed through prefill) and the final user turn is the message actually sent to trigger generation.
/// </summary>
internal static class LiteRtChatMapping
{
    /// <summary>
    /// Splits <paramref name="messages"/> into the prior messages to restore and the final user text to
    /// send. The list must be non-empty and end with a <see cref="ChatRole.User"/> message.
    /// </summary>
    public static (IReadOnlyList<LiteRtMessage> History, string UserText) Split(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (list.Count == 0)
            throw new ArgumentException("The message list is empty; add at least one user message.", nameof(messages));

        ChatMessage last = list[list.Count - 1];
        if (last.Role != ChatRole.User)
            throw new ArgumentException(
                $"The message list must end with a user message (the last message's role was '{last.Role.Value}'). " +
                "Automatic tool/assistant continuations (function calling) are not wired into this chat client yet " +
                "— that integration is planned. Drive function calling through the native LiteRtLmSharp tools API " +
                "in the meantime.",
                nameof(messages));

        var history = new List<LiteRtMessage>(list.Count - 1);
        for (int i = 0; i < list.Count - 1; i++)
            history.Add(ToMessage(list[i]));

        return (history, last.Text ?? string.Empty);
    }

    /// <summary>Maps one MEAI message to a <see cref="LiteRtMessage"/>. System/User/Assistant only.</summary>
    private static LiteRtMessage ToMessage(ChatMessage message)
    {
        string text = message.Text ?? string.Empty;

        if (message.Role == ChatRole.System) return LiteRtMessage.System(text);
        if (message.Role == ChatRole.User) return LiteRtMessage.User(text);
        if (message.Role == ChatRole.Assistant) return LiteRtMessage.Model(text);

        throw new NotSupportedException(
            $"Chat message role '{message.Role.Value}' is not handled by the LiteRtLmSharp chat client yet. " +
            "Roles handled today: system, user, assistant. Tool messages arrive with function calling, which is " +
            "planned but not yet bridged — use the native LiteRtLmSharp tools API in the meantime.");
    }

    /// <summary>
    /// Builds the <see cref="LiteRtConversationOptions"/> for a request, or <c>null</c> when there is nothing
    /// to set (no history and no options) so a plain conversation is created.
    /// </summary>
    public static LiteRtConversationOptions? ToConversationOptions(IReadOnlyList<LiteRtMessage> history, ChatOptions? options)
    {
        SamplerParams? sampler = ToSampler(options);
        bool? enableThinking = GetEnableThinking(options);
        int maxOutput = options?.MaxOutputTokens ?? 0;

        if (history.Count == 0 && sampler is null && maxOutput == 0 && enableThinking is null)
            return null;

        return new LiteRtConversationOptions
        {
            History = history.Count > 0 ? history : null,
            Sampler = sampler,
            MaxOutputTokens = maxOutput,
            EnableThinking = enableThinking,
        };
    }

    /// <summary>Maps the sampler knobs from <see cref="ChatOptions"/>, or <c>null</c> when none are set.</summary>
    private static SamplerParams? ToSampler(ChatOptions? o)
    {
        if (o is null || (o.Temperature is null && o.TopP is null && o.TopK is null && o.Seed is null))
            return null;

        var defaults = new SamplerParams();
        return new SamplerParams
        {
            Type = SamplerType.TopP,
            TopK = o.TopK ?? defaults.TopK,
            TopP = o.TopP ?? defaults.TopP,
            Temperature = o.Temperature ?? defaults.Temperature,
            // ChatOptions.Seed is a long?; LiteRtLmSharp's sampler seed is an int.
            Seed = o.Seed is { } seed ? unchecked((int)seed) : defaults.Seed,
        };
    }

    /// <summary>Reads the optional <c>enable_thinking</c> flag from <see cref="ChatOptions.AdditionalProperties"/>.</summary>
    private static bool? GetEnableThinking(ChatOptions? o)
    {
        if (o?.AdditionalProperties is { } props && props.TryGetValue("enable_thinking", out object? v))
        {
            if (v is bool b) return b;
            if (v is string s && bool.TryParse(s, out bool parsed)) return parsed;
        }
        return null;
    }
}
