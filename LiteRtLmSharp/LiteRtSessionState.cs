namespace LiteRtLmSharp;

/// <summary>Describes which calls a <see cref="LiteRtSession"/> accepts next.</summary>
/// <remarks>A session that reaches <see cref="ContextFull"/>, <see cref="Faulted"/> or <see cref="Cancelled"/>
/// accepts only <see cref="LiteRtSession.Dispose"/>. To continue, dispose it and create a new session.</remarks>
public enum LiteRtSessionState
{
    /// <summary>Holds no input. The next prefill is the session's first.</summary>
    /// <remarks>On the first prefill the runtime adds the model's start token before the text (measured on
    /// LiteRT-LM v0.20.0, win-x64, gemma-4 E2B, CPU and GPU).</remarks>
    Fresh = 0,

    /// <summary>Holds input that waits for a decode.</summary>
    Prefilled = 1,

    /// <summary>Holds a decoded reply. Prefill more text before the next decode.</summary>
    Decoded = 2,

    /// <summary>A decode stopped because the context is full. The session accepts only
    /// <see cref="LiteRtSession.Dispose"/>.</summary>
    ContextFull = 3,

    /// <summary>A native call failed. The session accepts only <see cref="LiteRtSession.Dispose"/>.</summary>
    Faulted = 4,

    /// <summary>A decode was cancelled. The session accepts only <see cref="LiteRtSession.Dispose"/>.</summary>
    Cancelled = 5,
}
