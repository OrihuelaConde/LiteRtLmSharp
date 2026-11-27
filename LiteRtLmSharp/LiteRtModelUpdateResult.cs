using System.Diagnostics.CodeAnalysis;

namespace LiteRtLmSharp;

/// <summary>Describes a completed <see cref="LiteRtModelSync.UpdateAsync"/>.</summary>
/// <remarks>This type is experimental (diagnostic <c>LITERTLM003</c>) and can change or be removed in a minor
/// release.</remarks>
[Experimental(ExperimentalApis.ModelSync, UrlFormat = ExperimentalApis.ModelSyncUrl)]
public sealed class LiteRtModelUpdateResult
{
    internal LiteRtModelUpdateResult(long bytesReceived) => BytesReceived = bytesReceived;

    /// <summary>Gets the number of bytes received from the source, header fetches and any full-download fallback
    /// included.</summary>
    /// <remarks>
    /// <para>The runtime reads the remote header with a fetch of the first 16 KiB of the file, then the rest of a
    /// longer header. A download of the whole file from a source that serves byte ranges then fetches the file
    /// from the end of the header, so a first download receives <c>min(16 KiB, size)</c>, plus the rest of a
    /// longer header, plus <c>size - header size</c>: for example 144,224 bytes for a 129,024-byte file with a
    /// 1,184-byte header. When the remote cannot serve a range after byte 0, the runtime fetches the whole file
    /// from byte 0 instead, and the update receives <c>min(16 KiB, size) + size</c>. Measured on LiteRT-LM
    /// v0.20.0, win-x64.</para>
    /// <para>An update of a file that was already up to date receives the header fetch only.</para>
    /// </remarks>
    public long BytesReceived { get; }
}
