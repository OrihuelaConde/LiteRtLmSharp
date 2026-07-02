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
}
