namespace LiteRtLmSharp;

/// <summary>The media kind carried by a <see cref="LiteRtAttachment"/>.</summary>
public enum LiteRtAttachmentKind
{
    /// <summary>An image (PNG/JPEG/… — the model decodes it; no format hint is sent).</summary>
    Image = 0,
    /// <summary>An audio clip (WAV/… — decoded by the model's audio preprocessor).</summary>
    Audio = 1,
}

/// <summary>
/// One image or audio attachment to add to a user message on a multimodal model. Attach it to a
/// turn with <see cref="LiteRtConversation.Send(string, System.Collections.Generic.IReadOnlyList{LiteRtAttachment})"/>
/// (or the streaming overload). The engine must have been loaded with the matching modality enabled
/// (<see cref="LiteRtEngineOptions.VisionBackend"/> for images, <see cref="LiteRtEngineOptions.AudioBackend"/>
/// for audio) and the model must support it (e.g. the Gemma 4 E-series handle both).
/// <para>
/// Two carriers, both mapping to the LiteRT-LM content-part wire format
/// (<c>{"type":"image"|"audio", …}</c>): inline <see cref="Bytes"/> (sent as a base64 <c>"blob"</c>),
/// or a <see cref="Path"/> on disk (sent as <c>"path"</c> and memory-mapped natively, avoiding a
/// base64 round-trip — desktop only; the path must be readable by the native process).
/// </para>
/// </summary>
public sealed class LiteRtAttachment
{
    private LiteRtAttachment(LiteRtAttachmentKind kind, byte[]? bytes, string? path)
    {
        Kind = kind;
        Bytes = bytes;
        Path = path;
    }

    /// <summary>Whether this is an image or an audio attachment.</summary>
    public LiteRtAttachmentKind Kind { get; }

    /// <summary>The inline media bytes, sent as a base64 <c>"blob"</c>; <c>null</c> when this is a
    /// <see cref="Path"/>-backed attachment.</summary>
    internal byte[]? Bytes { get; }

    /// <summary>A filesystem path the native layer memory-maps, sent as <c>"path"</c>; <c>null</c>
    /// when this is a <see cref="Bytes"/>-backed attachment.</summary>
    internal string? Path { get; }

    /// <summary>An image from in-memory bytes (PNG/JPEG/…); sent as a base64 blob. The bytes are
    /// copied, so the caller may reuse the buffer.</summary>
    public static LiteRtAttachment Image(ReadOnlySpan<byte> bytes) =>
        new(LiteRtAttachmentKind.Image, bytes.ToArray(), null);

    /// <summary>An image read from a file on disk; sent as a native-memory-mapped path (no base64).
    /// Desktop only — the path must be readable by the native process.</summary>
    public static LiteRtAttachment ImageFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new(LiteRtAttachmentKind.Image, null, path);
    }

    /// <summary>An audio clip from in-memory bytes (WAV/…); sent as a base64 blob. The bytes are
    /// copied, so the caller may reuse the buffer.</summary>
    public static LiteRtAttachment Audio(ReadOnlySpan<byte> bytes) =>
        new(LiteRtAttachmentKind.Audio, bytes.ToArray(), null);

    /// <summary>An audio clip read from a file on disk; sent as a native-memory-mapped path (no
    /// base64). Desktop only — the path must be readable by the native process.</summary>
    public static LiteRtAttachment AudioFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new(LiteRtAttachmentKind.Audio, null, path);
    }
}
