using System.Diagnostics.CodeAnalysis;

namespace LiteRtLmSharp;

/// <summary>Describes what an update would download and reuse at the time the plan was made.</summary>
/// <remarks>
/// <para>This type is experimental (diagnostic <c>LITERTLM003</c>) and can change or be removed in a minor
/// release.</para>
/// <para>The runtime reuses a section of the local file only when the local file's header records a SHA-256 for
/// it and the remote header records the same one. Without section hashes on both sides an update downloads the
/// whole file and the local file is never up to date, even when it is identical to the remote one; see the
/// remarks of <see cref="LiteRtModelSync.CreatePlanAsync"/>.</para>
/// <para>The values are the runtime's (measured on LiteRT-LM v0.20.0, win-x64). The plan holds no native
/// resources.</para>
/// </remarks>
[Experimental(ExperimentalApis.ModelSync, UrlFormat = ExperimentalApis.ModelSyncUrl)]
public sealed class LiteRtModelSyncPlan
{
    private readonly string _summary;

    internal LiteRtModelSyncPlan(bool isUpToDate, long bytesToDownload, long bytesToReuse, long totalRemoteSize, string summary)
    {
        IsUpToDate = isUpToDate;
        BytesToDownload = bytesToDownload;
        BytesToReuse = bytesToReuse;
        TotalRemoteSize = totalRemoteSize;
        _summary = summary;
    }

    /// <summary>Gets a value indicating whether the local file already matches the remote file, section by
    /// section.</summary>
    /// <remarks>It is <c>false</c> when there is no local file, when either file has no section hashes, and when a
    /// local section's bytes do not match the hash its header records.</remarks>
    public bool IsUpToDate { get; }

    /// <summary>Gets the number of bytes an update would download.</summary>
    /// <remarks>
    /// <para>It includes the remote header, and is 0 when the local file is up to date. When the local file is
    /// missing, when either file has no section hashes, or when more than 80% of the section bytes changed, it is
    /// the size of the whole file, because the update then downloads the whole file.</para>
    /// <para>It is an estimate: an update also downloads a section whose local bytes do not match their hash,
    /// even when the plan counted the section as reused. The bytes an update receives from the source differ
    /// from this value, because the runtime reads the header with a fetch of the first 16 KiB of the file (see
    /// <see cref="LiteRtModelUpdateResult.BytesReceived"/>).</para>
    /// </remarks>
    public long BytesToDownload { get; }

    /// <summary>Gets the number of bytes an update would copy from the local file instead of downloading.</summary>
    /// <remarks>
    /// <para>It counts section bytes only, so <see cref="BytesToDownload"/> plus this value can be less than
    /// <see cref="TotalRemoteSize"/>: the alignment padding between sections is neither downloaded nor counted
    /// as reused.</para>
    /// <para>0 with an existing local file means the remote file has no section hashes, the local file has none,
    /// or more than 80% of the section bytes changed. The plan does not tell these cases apart: a file published
    /// without section hashes looks the same as a new version.</para>
    /// </remarks>
    public long BytesToReuse { get; }

    /// <summary>Gets the size of the remote file in bytes.</summary>
    public long TotalRemoteSize { get; }

    /// <summary>Returns the runtime's human-readable summary of the plan, for logs. Its format is not
    /// stable.</summary>
    /// <returns>The runtime's summary: the status, the mode, the sizes, and the action for each section.</returns>
    public override string ToString() => _summary;
}
