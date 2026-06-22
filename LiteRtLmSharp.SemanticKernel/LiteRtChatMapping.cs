using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace LiteRtLmSharp.SemanticKernel;

/// <summary>
/// Maps a Semantic Kernel <see cref="ChatHistory"/> onto the stateless LiteRtLmSharp conversation model.
/// SK passes the full history every call; LiteRtLmSharp conversations are stateful, so each call rebuilds
/// a fresh one: every message except the last becomes restored <see cref="LiteRtConversationOptions.History"/>
/// (replayed through prefill) and the final user turn is the message actually sent to trigger generation.
/// </summary>
internal static class LiteRtChatMapping
{
    /// <summary>
    /// Splits <paramref name="chatHistory"/> into the prior messages to restore and the final user text to
    /// send. The history must be non-empty and end with a <see cref="AuthorRole.User"/> message.
    /// </summary>
    /// <exception cref="ArgumentException">The history is empty or does not end with a user message.</exception>
    /// <exception cref="NotSupportedException">A message uses a role the connector does not map.</exception>
    public static (IReadOnlyList<LiteRtMessage> History, string UserText) Split(ChatHistory chatHistory)
    {
        ArgumentNullException.ThrowIfNull(chatHistory);
        if (chatHistory.Count == 0)
            throw new ArgumentException(
                "The chat history is empty; add at least one user message before requesting a completion.",
                nameof(chatHistory));

        ChatMessageContent last = chatHistory[chatHistory.Count - 1];
        if (last.Role != AuthorRole.User)
            throw new ArgumentException(
                $"The chat history must end with a user message (the last message's role was " +
                $"'{last.Role.Label}'). The LiteRtLmSharp Semantic Kernel connector responds to the final " +
                "user turn. Automatic tool/assistant continuations (Semantic Kernel function calling) are " +
                "not wired into the connector yet — that integration is planned. In the meantime drive " +
                "function calling through the native LiteRtLmSharp tools API.",
                nameof(chatHistory));

        var history = new List<LiteRtMessage>(chatHistory.Count - 1);
        for (int i = 0; i < chatHistory.Count - 1; i++)
            history.Add(ToMessage(chatHistory[i]));

        return (history, last.Content ?? string.Empty);
    }

    /// <summary>Maps one SK message to a <see cref="LiteRtMessage"/>. System/User/Assistant only.</summary>
    private static LiteRtMessage ToMessage(ChatMessageContent message)
    {
        string text = message.Content ?? string.Empty;

        if (message.Role == AuthorRole.System)
            return LiteRtMessage.System(text);
        if (message.Role == AuthorRole.User)
            return LiteRtMessage.User(text);
        if (message.Role == AuthorRole.Assistant)
            return LiteRtMessage.Model(text);

        throw new NotSupportedException(
            $"Chat message role '{message.Role.Label}' is not handled by the LiteRtLmSharp Semantic " +
            "Kernel connector yet. Roles handled today: system, user, assistant. The 'tool' role arrives " +
            "with Semantic Kernel function calling, which the connector does not bridge yet (it's planned). " +
            "Until then, drive function calling through the native LiteRtLmSharp tools API.");
    }
}
