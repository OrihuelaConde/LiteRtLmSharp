namespace LiteRtLmSharp;

/// <summary>Describes which calls a <see cref="LiteRtSession"/> accepts next.</summary>
/// <remarks>A session that reaches <see cref="Cancelled"/>, or <see cref="Faulted"/> after any failure other than
/// a failed prefill, accepts only <see cref="LiteRtSession.Dispose"/>: to continue, dispose it and create a new
/// session. From <see cref="ContextFull"/>, and from <see cref="Faulted"/> after a failed prefill,
/// <see cref="LiteRtSession.RewindTo"/> and <see cref="LiteRtSession.Reset"/> recover the session.</remarks>
public enum LiteRtSessionState
{
    /// <summary>Holds no input. The next prefill is the session's first: on a new session, after
    /// <see cref="LiteRtSession.Reset"/>, or after a <see cref="LiteRtSession.RewindTo"/> a checkpoint saved while
    /// the session was Fresh.</summary>
    /// <remarks>On the first prefill the runtime adds one position before the text, which on Gemma 4 holds the
    /// start token, as the position counts indicate (measured on LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and
    /// GPU).</remarks>
    Fresh = 0,

    /// <summary>Holds input that waits for a decode.</summary>
    Prefilled = 1,

    /// <summary>Holds a decoded reply. Prefill more text before the next decode.</summary>
    Decoded = 2,

    /// <summary>A decode stopped because the context is full. The session accepts
    /// <see cref="LiteRtSession.RewindTo"/>, <see cref="LiteRtSession.Reset"/> and
    /// <see cref="LiteRtSession.Dispose"/>.</summary>
    ContextFull = 3,

    /// <summary>A native call failed. After a failed prefill the session accepts
    /// <see cref="LiteRtSession.RewindTo"/>, <see cref="LiteRtSession.Reset"/> and
    /// <see cref="LiteRtSession.Dispose"/>; after any other failure, only Dispose.</summary>
    Faulted = 4,

    /// <summary>A decode was cancelled. The session accepts only <see cref="LiteRtSession.Dispose"/>.</summary>
    Cancelled = 5,
}
