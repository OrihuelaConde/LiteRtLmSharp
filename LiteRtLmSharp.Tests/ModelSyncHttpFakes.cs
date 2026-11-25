using System.Net;
using System.Net.Http.Headers;

namespace LiteRtLmSharp.Tests;

/// <summary>One request the fake server received, and what it answered.</summary>
/// <param name="Index">The request's position, from 0.</param>
/// <param name="Range">The <c>Range</c> header as sent, or <c>null</c>.</param>
/// <param name="Status">The status code answered.</param>
internal sealed record FakeHttpRequest(int Index, string? Range, int Status)
{
    public override string ToString() => $"{Range ?? "(no range)"}:{Status}";
}

/// <summary>What the fake server answers to one request, instead of the default.</summary>
internal sealed record FakeHttpAction
{
    /// <summary>Answer this status with an empty body.</summary>
    public HttpStatusCode? Status { get; init; }

    /// <summary>Throw this exception from the handler.</summary>
    public Exception? Throw { get; init; }

    /// <summary>Answer 206 with this <c>Content-Range</c> start instead of the requested one.</summary>
    public long? ContentRangeFrom { get; init; }

    /// <summary>Answer 206 without a <c>Content-Range</c> header.</summary>
    public bool NoContentRange { get; init; }

    /// <summary>Answer 200 with the whole file, as a server that ignores <c>Range</c>.</summary>
    public bool Whole { get; init; }

    /// <summary>Bytes to add after the requested range, declared in the headers as part of the answer.</summary>
    public int ExtraBytes { get; init; }

    /// <summary>Bytes of the declared answer that the body leaves out at its end.</summary>
    public int Truncate { get; init; }

    /// <summary>After this many body bytes, the body waits for <see cref="StallGate"/> or the read's
    /// token.</summary>
    public long? StallAfter { get; init; }

    /// <summary>The task a stalled body waits for. A body that stalls without one waits for its token.</summary>
    public Task? StallGate { get; init; }

    /// <summary>A task the handler waits for, observing the request's token, before it answers.</summary>
    public Task? HeaderGate { get; init; }

    /// <summary>The entity tag of this answer instead of the server's, or <c>""</c> for none.</summary>
    public string? ETag { get; init; }
}

/// <summary>
/// An HTTP server in memory, as an <see cref="HttpMessageHandler"/>: it serves <see cref="Data"/> with 206 and
/// a <c>Content-Range</c> for each <c>Range</c> request, or 200 with the whole file when
/// <see cref="ServesRanges"/> is <c>false</c>, with the entity tag <see cref="ETag"/>. A script changes the answer
/// per request. Every request is recorded; no socket is opened.
/// </summary>
internal sealed class FakeModelServer(byte[] data) : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly List<FakeHttpRequest> _requests = [];
    private readonly List<HttpRequestMessage> _messages = [];
    private int _next;

    /// <summary>The file the server serves. A test replaces it to change the remote.</summary>
    public byte[] Data { get; set; } = data;

    /// <summary>The entity tag of every answer (for example <c>"v1"</c> or <c>W/"v1"</c>), or <c>null</c> for
    /// none.</summary>
    public string? ETag { get; set; } = "\"v1\"";

    /// <summary>Whether the server honours <c>Range</c>.</summary>
    public bool ServesRanges { get; set; } = true;

    /// <summary>Picks the answer for a request from its index; <c>null</c> answers normally.</summary>
    public Func<int, HttpRequestMessage, FakeHttpAction?>? Script { get; set; }

    /// <summary>The requests, in order.</summary>
    public IReadOnlyList<FakeHttpRequest> Requests
    {
        get { lock (_lock) return [.. _requests]; }
    }

    /// <summary>The request messages, in order.</summary>
    public IReadOnlyList<HttpRequestMessage> Messages
    {
        get { lock (_lock) return [.. _messages]; }
    }

    /// <summary>The bodies handed out, in order, so a test can see how much of each was read.</summary>
    public List<FakeBody> Bodies { get; } = [];

    /// <summary>Whether <see cref="HttpMessageHandler.Dispose()"/> ran.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Completes when a body starts to stall or a handler starts to wait on its header gate.</summary>
    public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Sequence() => string.Join(" ", Requests.Select(r => r.ToString()));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int index = Interlocked.Increment(ref _next) - 1;
        FakeHttpAction? action = Script?.Invoke(index, request);
        string? range = request.Headers.TryGetValues("Range", out var values) ? string.Join(",", values) : null;
        lock (_lock)
            _messages.Add(request);

        if (action?.HeaderGate is { } headerGate)
        {
            Stalled.TrySetResult();
            await headerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (action?.Throw is { } exception)
        {
            Record(index, range, 0);
            throw exception;
        }

        byte[] file = Data;
        HttpResponseMessage response;
        if (action?.Status is { } status)
        {
            response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]), RequestMessage = request };
        }
        else
        {
            RangeItemHeaderValue? item = request.Headers.Range?.Ranges.SingleOrDefault();
            bool partial = ServesRanges && action?.Whole != true && item is { From: { } };
            long from = partial ? item!.From!.Value : 0;
            long to = partial ? Math.Min(item!.To ?? file.Length - 1, file.Length - 1) : file.Length - 1;
            int extra = action?.ExtraBytes ?? 0;
            byte[] declared = new byte[to - from + 1 + extra];
            Array.Copy(file, from, declared, 0, to - from + 1);
            var body = new FakeBody(declared, declared.Length - (action?.Truncate ?? 0), action?.StallAfter, action?.StallGate, Stalled);
            lock (_lock)
                Bodies.Add(body);
            var content = new StreamContent(body);
            content.Headers.ContentLength = declared.Length;
            response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            };
            if (partial && action?.NoContentRange != true)
            {
                long first = action?.ContentRangeFrom ?? from;
                long last = first + declared.Length - 1;
                // A server that sends extra bytes declares a size that holds them.
                content.Headers.ContentRange = new ContentRangeHeaderValue(first, last, Math.Max(file.Length, last + 1));
            }
        }
        string? etag = action?.ETag ?? ETag;
        if (!string.IsNullOrEmpty(etag))
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        Record(index, range, (int)response.StatusCode);
        return response;
    }

    private void Record(int index, string? range, int status)
    {
        lock (_lock)
            _requests.Add(new FakeHttpRequest(index, range, status));
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>A response body that can end early or stall, and counts what was read.</summary>
internal sealed class FakeBody(byte[] data, int available, long? stallAfter, Task? stallGate, TaskCompletionSource stalled) : Stream
{
    private long _position;

    /// <summary>The bytes the reader took.</summary>
    public long BytesRead => Interlocked.Read(ref _position);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long position = Interlocked.Read(ref _position);
        if (stallAfter is { } after && position >= after)
        {
            stalled.TrySetResult();
            if (stallGate is null)
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            else
                await stallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            stallAfter = null;
        }
        long end = stallAfter is { } limit ? Math.Min(available, limit) : available;
        int count = (int)Math.Min(buffer.Length, Math.Max(0, end - position));
        if (count == 0)
            return 0;
        data.AsMemory((int)position, count).CopyTo(buffer);
        Interlocked.Add(ref _position, count);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
