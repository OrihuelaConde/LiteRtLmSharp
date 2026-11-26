namespace LiteRtLmSharp;

/// <summary>The media kind carried by a <see cref="LiteRtAttachment"/>.</summary>
public enum LiteRtAttachmentKind
{
    /// <summary>An image. No format hint is sent: the runtime detects the format from the bytes.</summary>
    /// <remarks>PNG and JPEG decode; WEBP fails ("Failed to decode image. Reason: unknown image type.").
    /// Measured on LiteRT-LM v0.20.0, win-x64, CPU, gemma-4 E2B.</remarks>
    Image = 0,
    /// <summary>An audio clip, decoded by the model's audio preprocessor. No format hint is sent: the runtime
    /// detects the format from the bytes.</summary>
    /// <remarks>MP3, WAV and FLAC decode; OGG Vorbis fails ("Failed to initialize miniaudio decoder").
    /// Measured on LiteRT-LM v0.20.0, win-x64, CPU, gemma-4 E2B.</remarks>
    Audio = 1,
}

/// <summary>
/// One image or audio attachment to add to a user message on a multimodal model. Attach it to a
/// turn with <see cref="LiteRtConversation.Send(string, System.Collections.Generic.IReadOnlyList{LiteRtAttachment}, LiteRtSendOptions)"/>
/// (or the streaming overload). The engine must have been loaded with the matching modality enabled
/// (<see cref="LiteRtEngineOptions.VisionBackend"/> for images, <see cref="LiteRtEngineOptions.AudioBackend"/>
/// for audio) and the model must support it (e.g. the Gemma 4 E-series handle both).
/// <para>
/// Two carriers, both mapping to the LiteRT-LM content-part wire format
/// (<c>{"type":"image"|"audio", …}</c>): inline <see cref="Bytes"/> (sent as a base64 <c>"blob"</c>),
/// or a <see cref="Path"/> on disk (sent as <c>"path"</c>: the native side opens the file itself, so
/// there is no base64 round-trip; the path must be readable by the native process). With LiteRT-LM
/// v0.20.0, a file path gave the same replies as the same media sent as inline bytes on win-x64 (CPU, image
/// and MP3 audio) and, for images, on an Android x86_64 emulator (API 35, CPU). Audio files on Android were
/// not measured.
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

    /// <summary>A filesystem path the native side opens itself, sent as <c>"path"</c>; <c>null</c>
    /// when this is a <see cref="Bytes"/>-backed attachment.</summary>
    internal string? Path { get; }

    /// <summary>An image from in-memory bytes (PNG or JPEG; see <see cref="LiteRtAttachmentKind.Image"/>);
    /// sent as a base64 blob. The bytes are copied, so the caller may reuse the buffer.</summary>
    /// <exception cref="System.ArgumentException"><paramref name="bytes"/> is empty (the engine fails
    /// on an empty blob at send time with a far less helpful error).</exception>
    public static LiteRtAttachment Image(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            throw new ArgumentException("Image bytes must not be empty.", nameof(bytes));
        return new(LiteRtAttachmentKind.Image, bytes.ToArray(), null);
    }

    /// <summary>An image read from a file on disk; sent as a path that the native side opens itself (no
    /// base64). The path must be readable by the native process.</summary>
    /// <remarks>With LiteRT-LM v0.20.0, a file path gave the same replies as inline bytes on win-x64, and on an
    /// Android x86_64 emulator (API 35, CPU) both from the app's external files folder and from a copy in its
    /// internal files folder.</remarks>
    public static LiteRtAttachment ImageFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new(LiteRtAttachmentKind.Image, null, path);
    }

    /// <summary>An audio clip from in-memory bytes (MP3, WAV or FLAC; see
    /// <see cref="LiteRtAttachmentKind.Audio"/>); sent as a base64 blob. The bytes are copied, so the caller
    /// may reuse the buffer.</summary>
    /// <exception cref="System.ArgumentException"><paramref name="bytes"/> is empty (the engine fails
    /// on an empty blob at send time with a far less helpful error).</exception>
    public static LiteRtAttachment Audio(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            throw new ArgumentException("Audio bytes must not be empty.", nameof(bytes));
        return new(LiteRtAttachmentKind.Audio, bytes.ToArray(), null);
    }

    /// <summary>An audio clip read from a file on disk; sent as a path that the native side opens itself (no
    /// base64). Not measured on Android (see <see cref="LiteRtAttachment"/>). The
    /// path must be readable by the native process.</summary>
    public static LiteRtAttachment AudioFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new(LiteRtAttachmentKind.Audio, null, path);
    }
}
