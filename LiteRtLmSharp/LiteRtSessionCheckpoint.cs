using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace LiteRtLmSharp;

/// <summary>A point in a <see cref="LiteRtSession"/> that <see cref="LiteRtSession.RewindTo"/> returns to. Create
/// one with <see cref="LiteRtSession.SaveCheckpoint"/>.</summary>
/// <remarks>
/// <para>
/// A checkpoint is valid only on the session that saved it. It records the session's context and its
/// <see cref="LiteRtSession.State"/> at the save. It holds no native resource of its own: the runtime keeps the
/// saved point inside the session, and the C API has no call that deletes one checkpoint.
/// </para>
/// <para>
/// The runtime drops a checkpoint in two cases. A <see cref="LiteRtSession.RewindTo"/> drops every checkpoint saved
/// at a later point than the one it returns to; checkpoints saved at the same point stay. A
/// <see cref="LiteRtSession.Reset"/> drops every checkpoint except those saved while the session was
/// <see cref="LiteRtSessionState.Fresh"/>. A rewind to a dropped checkpoint throws
/// <see cref="InvalidOperationException"/> and changes nothing. This behavior was measured on LiteRT-LM v0.20.0,
/// win-x64, gemma-4 E2B, CPU and GPU.
/// </para>
/// </remarks>
[Experimental(ExperimentalApis.SessionCheckpoints, UrlFormat = ExperimentalApis.SessionCheckpointsUrl)]
public sealed class LiteRtSessionCheckpoint
{
    /// <summary>The prefix of every label: ASCII, so the label bytes are the same in UTF-8 on every
    /// platform.</summary>
    internal const string LabelPrefix = "litertlmsharp-checkpoint-";

    internal LiteRtSessionCheckpoint(LiteRtSession owner, long sequence, LiteRtSessionState recorded)
    {
        Owner = owner;
        Sequence = sequence;
        Label = LabelBytes(sequence);
        Recorded = recorded;
    }

    /// <summary>The session that saved the checkpoint.</summary>
    internal LiteRtSession Owner { get; }

    /// <summary>The session's count of saves when this one was made. It never repeats on a session, also across
    /// <see cref="LiteRtSession.Reset"/>: the runtime accepts a second save under a label it holds and then
    /// rewinds that label to the later save.</summary>
    internal long Sequence { get; }

    /// <summary>The native label: ASCII, NUL-terminated. The runtime copies it during the save.</summary>
    internal byte[] Label { get; }

    /// <summary>The session state at the save: <see cref="LiteRtSessionState.Fresh"/>,
    /// <see cref="LiteRtSessionState.Prefilled"/> or <see cref="LiteRtSessionState.Decoded"/>.</summary>
    internal LiteRtSessionState Recorded { get; }

    /// <summary>The label of the save numbered <paramref name="sequence"/>, without its terminator.</summary>
    internal static string LabelText(long sequence)
        => LabelPrefix + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The NUL-terminated ASCII bytes of <see cref="LabelText"/>.</summary>
    internal static byte[] LabelBytes(long sequence)
    {
        string text = LabelText(sequence);
        byte[] bytes = new byte[text.Length + 1];
        Encoding.ASCII.GetBytes(text, bytes);
        return bytes;
    }
}
