using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;

namespace LiteRtLmSharp;

/// <summary>
/// The source <see cref="LiteRtModelSyncSource.FromHttp"/> creates: it serves each range the runtime asks for
/// with an HTTP <c>GET</c> and a closed <c>Range</c> header, and with more <c>GET</c>s for the rest of the range
/// when an answer carries only a part of it.
/// </summary>
/// <remarks>
/// <para>A 206 must carry a byte <c>Content-Range</c> that starts at the requested offset. A 206 with fewer bytes
/// than asked that ends before the end of the file, which RFC 9110 allows, is followed by a request for the rest,
/// within the same fetch. An answer that ends at the end of the file, before the end of the range, serves the
/// bytes the file has; the bridge accepts that only for the first fetch of a call and fails a later one with
/// <c>DATA_LOSS</c>. A 200 answers a
/// request at byte 0 with its first bytes, because the server ignored <c>Range</c>; a 200 to the first request of
/// a range that starts after byte 0 reports "ranges unsupported", which makes the runtime fall back to a full
/// download for any fetch after the header. A 200 to the request for the rest of a range, after a partial answer,
/// fails the call with <c>UNKNOWN</c>. Every copy stops at the requested length, because the runtime accepts a
/// whole-file write past it and leaves a longer, corrupt file (measured on LiteRT-LM v0.20.0, win-x64).</para>
/// <para><see cref="ForCall"/> returns an instance with its own entity tag pin, so one source serves calls one
/// after another and at the same time.</para>
/// </remarks>
[Experimental(ExperimentalApis.ModelSync, UrlFormat = ExperimentalApis.ModelSyncUrl)]
internal sealed class ModelSyncHttpSource : LiteRtModelSyncSource
{
    /// <summary>How long a fetch waits for the response headers, and for each read of the body.</summary>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private const int BufferSize = 64 * 1024;

    private static readonly Lazy<HttpClient> s_defaultClient = new(CreateDefaultClient);

    private readonly HttpClient? _httpClient;
    private readonly ETagPin? _pin;

    internal ModelSyncHttpSource(Uri uri, HttpClient? httpClient, TimeSpan idleTimeout)
        : this(uri, httpClient, idleTimeout, pin: null)
    {
    }

    private ModelSyncHttpSource(Uri uri, HttpClient? httpClient, TimeSpan idleTimeout, ETagPin? pin)
    {
        Uri = uri;
        _httpClient = httpClient;
        IdleTimeout = idleTimeout;
        _pin = pin;
    }

    /// <summary>The remote file.</summary>
    internal Uri Uri { get; }

    /// <summary>The caller's client, or the binding's shared client when the caller gave none.</summary>
    internal HttpClient Client => _httpClient ?? s_defaultClient.Value;

    /// <summary>Whether <see cref="Client"/> is the binding's shared client.</summary>
    internal bool UsesDefaultClient => _httpClient is null;

    /// <summary>How long a fetch waits for the response headers, and for each read of the body, before it fails
    /// with a <see cref="TimeoutException"/>.</summary>
    internal TimeSpan IdleTimeout { get; }

    /// <summary>The strong entity tag the call pinned, or <c>null</c>. Always <c>null</c> outside a call.</summary>
    internal string? PinnedETag => _pin?.Tag;

    /// <summary>Returns an instance for one call, with an empty entity tag pin.</summary>
    internal override LiteRtModelSyncSource ForCall() => new ModelSyncHttpSource(Uri, _httpClient, IdleTimeout, new ETagPin());

    /// <summary>The client for <c>FromHttp</c> without one: the platform's default handler, and no overall
    /// timeout, because a model download can take hours. The idle timeout bounds each wait instead.</summary>
    private static HttpClient CreateDefaultClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    internal override async ValueTask<bool> ReadRangeAsync(
        long offset, long length, Stream destination, CancellationToken cancellationToken)
    {
        long last = offset + length - 1;
        long next = offset;
        // Set after a 206 that carried less than asked and did not say where the file ends.
        bool endUnknown = false;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Uri);
            request.Headers.Range = new RangeHeaderValue(next, last);

            using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            // The request for the rest of a range, after an answer that did not say where the file ends: 416 says
            // that the file ended where that answer did, so the source has served all the file has.
            if (status == 416 && endUnknown)
                return true;
            if (status is not (200 or 206))
                throw new HttpRequestException(
                    $"The remote answered {status} ({response.ReasonPhrase}) to the request for bytes {next}-{last}.",
                    null, response.StatusCode);

            // The pin comes before the 200 rule: a file that changed fails the call instead of starting a full
            // download of the new version.
            CheckETag(response);

            long? expected;
            long? fileLength = null;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                if (range is not { From: { } from, To: { } to } || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
                    throw new ModelSyncSourceException(LiteRtStatusCode.DataLoss,
                        $"The remote answered 206 to the request for bytes {next}-{last} without a byte range in its Content-Range header.");
                if (from != next || to < from)
                    throw new ModelSyncSourceException(LiteRtStatusCode.DataLoss,
                        $"The remote answered the request for bytes {next}-{last} with bytes {from}-{to}.");
                // From the smaller end, so that an end such as long.MaxValue cannot overflow the count.
                expected = Math.Min(last, to) - next + 1;
                fileLength = range.Length;
            }
            else if (next == offset)
            {
                // 200: the server ignored Range and sends the file from byte 0.
                if (offset > 0)
                    return false;
                expected = response.Content.Headers.ContentLength is { } contentLength ? Math.Min(length, contentLength) : null;
            }
            else
            {
                // A 200 after a short 206: the server stopped honouring Range in the middle of the range.
                throw new HttpRequestException(
                    $"The remote answered 200 to the request for the rest of bytes {offset}-{last}, from byte {next}, " +
                    "after a partial answer.", null, response.StatusCode);
            }

            long want = expected ?? last - next + 1;
            long copied = await CopyAsync(response, destination, want, cancellationToken).ConfigureAwait(false);
            if (expected is { } count && copied < count)
                throw new IOException(
                    $"The response to the request for bytes {next}-{last} ended after {copied} of {count} bytes.");
            next += copied;
            // A 206 may carry less than asked (RFC 9110, section 15.3.7). The source stops at the end of the file;
            // otherwise it asks for the rest. When the remote does not say where the file ends, the source asks
            // too, and a 416 to that request means the file ended there. A range that ends short at the end of the
            // file is complete only for the first fetch of a call: the bridge fails a later one, because the
            // runtime would keep a truncated file without an error.
            if (next > last || response.StatusCode != HttpStatusCode.PartialContent || (fileLength is { } size && next >= size))
                return true;
            // An answer that moved nothing would repeat the same request without end.
            if (copied == 0)
                throw new IOException($"The response to the request for bytes {next}-{last} carried no bytes.");
            endUnknown = fileLength is null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(IdleTimeout);
        try
        {
            return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The remote sent no response for {Seconds(IdleTimeout)}.");
        }
    }

    /// <summary>Copies at most <paramref name="length"/> bytes of the body, never more than the runtime asked
    /// for, and returns the count. Only the reads from the remote are timed: a wait for the runtime to take the
    /// bytes is not idleness of the remote.</summary>
    private async Task<long> CopyAsync(HttpResponseMessage response, Stream destination, long length, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Stream body;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idle.CancelAfter(IdleTimeout);
                try
                {
                    body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"The remote sent no data for {Seconds(IdleTimeout)}.");
                }
            }

            long copied = 0;
            while (copied < length)
            {
                int want = (int)Math.Min(BufferSize, length - copied);
                int read;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    idle.CancelAfter(IdleTimeout);
                    try
                    {
                        read = await body.ReadAsync(buffer.AsMemory(0, want), idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"The remote sent no data for {Seconds(IdleTimeout)}.");
                    }
                }
                if (read == 0)
                    break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
            }
            return copied;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void CheckETag(HttpResponseMessage response)
    {
        if (_pin is null || response.Headers.ETag is not { IsWeak: false, Tag: { } tag })
            return;
        string? pinned = Interlocked.CompareExchange(ref _pin.Tag, tag, null);
        if (pinned is not null && !string.Equals(pinned, tag, StringComparison.Ordinal))
            throw new ModelSyncSourceException(LiteRtStatusCode.Aborted,
                $"The remote file changed during the sync: its ETag was {pinned} and is now {tag}.");
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds >= 1
        ? $"{span.TotalSeconds:0.###} seconds"
        : $"{span.TotalMilliseconds:0} milliseconds";

    private sealed class ETagPin
    {
        public string? Tag;
    }
}
