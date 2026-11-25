using System.Diagnostics.CodeAnalysis;

namespace LiteRtLmSharp;

/// <summary>Provides the bytes of a remote <c>.litertlm</c> file to <see cref="LiteRtModelSync"/>.</summary>
/// <remarks>
/// <para>This type is experimental (diagnostic <c>LITERTLM003</c>) and can change or be removed in a minor
/// release.</para>
/// <para>Only the binding creates sources, with <see cref="FromHttp"/>; the type has no public
/// constructor.</para>
/// </remarks>
[Experimental(ExperimentalApis.ModelSync, UrlFormat = ExperimentalApis.ModelSyncUrl)]
public abstract class LiteRtModelSyncSource
{
    internal LiteRtModelSyncSource() { }

    /// <summary>Creates a source that reads the <c>.litertlm</c> file at <paramref name="uri"/> with HTTP range
    /// requests.</summary>
    /// <param name="uri">The absolute <c>http</c> or <c>https</c> address of the file. Prefer an address that names
    /// one revision of the file, such as a Hugging Face <c>resolve/COMMIT/FILE</c> address, over one that names a
    /// branch: a branch can move to a new version during a call, which fails the call (see the remarks).</param>
    /// <param name="httpClient">The client that sends the requests, with its redirect, proxy, header and handler
    /// settings, or <c>null</c> to use a client that the binding shares between sources. The source never
    /// disposes a client you pass: keep it alive while a call uses the source.</param>
    /// <returns>The source. One source can serve several calls, one after another or at the same time.</returns>
    /// <remarks>
    /// <para>Each range the runtime asks for is one <c>GET</c> with a closed <c>Range: bytes=FIRST-LAST</c>
    /// header. The source reads the response headers first, then copies the body in chunks, and never passes the
    /// runtime more bytes than it asked for. It does not retry: a failed request fails the call.</para>
    /// <para>Responses:</para>
    /// <list type="bullet">
    /// <item><description>206: the <c>Content-Range</c> header must start at the requested byte, or the call
    /// fails with <see cref="LiteRtStatusCode.DataLoss"/>. A body that ends before the range it declares fails the
    /// call with <see cref="LiteRtStatusCode.Unavailable"/>.</description></item>
    /// <item><description>200, the whole file: the source serves a request at byte 0 from the first bytes of the
    /// body, and reports any other request as a remote that cannot serve byte ranges. The runtime then downloads
    /// the whole file from byte 0, after one refused request for a first download and two for an update that
    /// reuses sections (measured over loopback). Each refused request is still a <c>GET</c> of the whole file:
    /// the source stops reading it at once, but the server can already have sent part or all of it. A plan or an
    /// update fails with <see cref="LiteRtStatusCode.Unimplemented"/> when the remote header is longer than
    /// 16 KiB, because the runtime does not fall back for the fetches of the header.</description></item>
    /// <item><description>401: <see cref="LiteRtStatusCode.Unauthenticated"/>. 403:
    /// <see cref="LiteRtStatusCode.PermissionDenied"/>. 404 and 410: <see cref="LiteRtStatusCode.NotFound"/>.
    /// 408, 429 and 5xx, and a request that gets no response: <see cref="LiteRtStatusCode.Unavailable"/>. Any other
    /// status: <see cref="LiteRtStatusCode.Unknown"/>, never a status that would make the runtime start a full
    /// download after an error. The <see cref="HttpRequestException"/> is the
    /// <see cref="Exception.InnerException"/> of the call's exception.</description></item>
    /// </list>
    /// <para>Remote changes: the source remembers the first strong <c>ETag</c> of a call, read from the final
    /// response after any redirect. A later response of the same call with another strong <c>ETag</c> fails the
    /// call with <see cref="LiteRtStatusCode.Aborted"/>, and the output is not replaced. A weak or missing
    /// <c>ETag</c> turns the check off. The runtime verifies only the sections that the remote header hashes, so
    /// for a remote without section hashes this check is the only protection against a file assembled from two
    /// versions. The check is per call: a plan, a change of the remote, then an update with the same source
    /// works.</para>
    /// <para>Timeouts: a fetch fails with <see cref="LiteRtStatusCode.DeadlineExceeded"/> when the remote sends
    /// no response headers, or no body bytes, for 60 seconds. The client's own <see cref="HttpClient.Timeout"/>
    /// applies too. With <see cref="SocketsHttpHandler"/> on .NET 10, it bounds the wait for the response headers
    /// and not the copy of the body, so it does not cut a long download. The binding's shared client has no
    /// overall timeout.</para>
    /// <para>Credentials: with <see cref="SocketsHttpHandler"/> on .NET 10, an <c>Authorization</c> header set on
    /// <see cref="HttpClient.DefaultRequestHeaders"/> is not sent again after a redirect, to the same host or to
    /// another one, so the address a redirect leads to does not receive it.</para>
    /// <para>The <see cref="SocketsHttpHandler"/> statements above were measured on .NET 10.0.15, win-x64, against
    /// a server on the loopback address. Other handlers, such as the Android one, are not measured.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is not an absolute <c>http</c> or <c>https</c>
    /// address.</exception>
    public static LiteRtModelSyncSource FromHttp(Uri uri, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The address must be an absolute http or https address.", nameof(uri));
        return new ModelSyncHttpSource(uri, httpClient, ModelSyncHttpSource.DefaultIdleTimeout);
    }

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
