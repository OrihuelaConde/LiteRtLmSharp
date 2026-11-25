using System.Diagnostics.CodeAnalysis;

namespace LiteRtLmSharp;

/// <summary>Provides the bytes of a remote <c>.litertlm</c> file to <see cref="LiteRtModelSync"/>.</summary>
/// <remarks>Only the binding creates sources; the type has no public constructor.</remarks>
[Experimental(ExperimentalApis.ModelSync, UrlFormat = ExperimentalApis.ModelSyncUrl)]
public abstract class LiteRtModelSyncSource
{
    internal LiteRtModelSyncSource() { }

    /// <summary>Returns the instance that serves one model sync call. A source that keeps state for the length
    /// of a call, such as the version of the remote file it first saw, returns a new instance, so one source can
    /// serve several calls, one after another or at the same time.</summary>
    internal virtual LiteRtModelSyncSource ForCall() => this;

    /// <summary>
    /// Writes the bytes <c>[offset, offset + length)</c> of the remote file to <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// <para>Returns <c>true</c> after writing the range. For the first header fetch the runtime asks for more
    /// bytes than a small file has; writing the bytes that exist and returning <c>true</c> is correct there.</para>
    /// <para>Returns <c>false</c>, with nothing written, when the remote cannot serve a range that starts after
    /// byte 0: the runtime then asks for a longer range and, at last, for the whole file from byte 0. Returning
    /// <c>false</c> after writing, or for a range at byte 0, fails the call.</para>
    /// <para>Writing past <paramref name="length"/> fails the call with <see cref="LiteRtStatusCode.DataLoss"/>
    /// and none of the extra bytes reach the runtime. A failure is an exception; the call maps it to a status
    /// (see <see cref="LiteRtModelSync"/>). <paramref name="cancellationToken"/> fires when the caller cancels and
    /// when the fetch ends for another reason; a later write to <paramref name="destination"/> throws
    /// <see cref="ObjectDisposedException"/>.</para>
    /// </remarks>
    internal abstract ValueTask<bool> ReadRangeAsync(
        long offset, long length, Stream destination, CancellationToken cancellationToken);
}
