namespace LiteRtLmSharp;

/// <summary>
/// An immutable capture of a conversation's state: the turns it has completed, the partial reply text it
/// received before being cut short, and the settings it was created with. Feed it to
/// <see cref="LiteRtConversation.Resume(LiteRtConversationSnapshot)"/> to continue on a fresh conversation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A cancelled conversation is dead: after <see cref="LiteRtConversation.CancelProcess"/>
/// (or a cancelled <c>SendAsync</c> / abandoned stream) the native runtime answers the next send with
/// <c>CANCELLED</c> forever (LiteRT-LM v0.16.0/v0.18.0) or hangs (v0.14.0). Continuing therefore means a new
/// conversation whose <see cref="LiteRtConversationOptions.History"/> is "the completed turns plus the partial
/// reply" — the price is one prefill of that history (0.2–1.8 s measured on desktop CPU for a few hundred to
/// ~1.2 k tokens, minutes on a phone for thousands). This type carries that state so the recipe does not have
/// to be re-implemented per application.
/// </para>
/// <para>
/// <see cref="LiteRtConversation.Snapshot"/> fills <see cref="Turns"/> automatically for every send made
/// through the typed API (the recorded turns include the history the conversation was created with), and
/// captures the partial text of an abandoned streaming send. A blocking send that was cut short by
/// <see cref="LiteRtConversation.CancelProcess"/> cannot be observed by the binding (the native call never
/// returns the text), so pass that text to <see cref="LiteRtConversation.Snapshot(string?)"/>; it can also be
/// set afterwards on the snapshot record, which is a <c>record</c> with <c>init</c> members.
/// </para>
/// </remarks>
public sealed record LiteRtConversationSnapshot
{
    /// <summary>The engine the conversation belonged to. Required: a resumed conversation is created from the
    /// same engine (the settings of the captured conversation live in <see cref="Options"/>).</summary>
    public required LiteRtEngine Engine { get; init; }

    /// <summary>The completed turns, oldest first — the turn log the conversation kept, seeded with the
    /// <see cref="LiteRtConversationOptions.History"/> it was created with.</summary>
    public IReadOnlyList<LiteRtMessage> Turns { get; init; } = [];

    /// <summary>The answer text received before the conversation was cut short, or <c>null</c> when nothing
    /// partial was captured. <see cref="LiteRtConversation.Resume(LiteRtConversationSnapshot)"/> appends it as
    /// a model turn, which is what lets the model pick the reply up where it stopped instead of starting over.
    /// </summary>
    public string? PartialResponse { get; init; }

    /// <summary>The conversation's token count at snapshot time (from the non-blocking cache — see
    /// <see cref="LiteRtConversation.TokenCountCached"/>). Informational: the resumed conversation rebuilds the
    /// same context by replaying <see cref="Turns"/>.</summary>
    public int TokenCount { get; init; }

    /// <summary>The options the conversation was created with (system message, tools, sampler, output cap, …),
    /// re-applied to the resumed conversation. <c>null</c> when the conversation was created with the defaults.
    /// </summary>
    public LiteRtConversationOptions? Options { get; init; }

    /// <summary>Whether the source conversation carried an automatic session config for a multimodal engine,
    /// so the resumed one does too.</summary>
    internal bool EngineIsMultimodal { get; init; }
}
