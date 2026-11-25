using System.Diagnostics.CodeAnalysis;

namespace LiteRtLmSharp;

/// <summary>Options for <see cref="LiteRtModelSync.UpdateAsync"/>.</summary>
[Experimental(ExperimentalApis.ModelSync, UrlFormat = ExperimentalApis.ModelSyncUrl)]
public sealed record LiteRtModelSyncOptions
{
    /// <summary>Gets the receiver of the running total of bytes received from the source, or <c>null</c> for no
    /// reports.</summary>
    /// <remarks>
    /// <para>The total counts every byte the source delivered during the call, header fetches included, and
    /// ends at <see cref="LiteRtModelUpdateResult.BytesReceived"/> when the call succeeds.
    /// It is reported at most once per MiB and at the end of each range the runtime asks for.</para>
    /// <para><see cref="IProgress{T}.Report"/> runs on the thread that runs the native update, so the update
    /// waits while it runs. <see cref="Progress{T}"/> posts each report to the synchronization context it was
    /// created on and returns at once; a receiver that blocks stalls the download. A receiver that throws fails
    /// the update with <see cref="LiteRtStatusCode.Unknown"/>, and its exception is the
    /// <see cref="Exception.InnerException"/> of the <see cref="LiteRtException"/>.</para>
    /// </remarks>
    public IProgress<long>? Progress { get; init; }
}
