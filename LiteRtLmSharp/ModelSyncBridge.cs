using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>
/// The state of one model sync call and the native fetcher callback that serves it from a
/// <see cref="LiteRtModelSyncSource"/>.
/// </summary>
/// <remarks>
/// <para>The native call runs on one dedicated thread, and the runtime calls the fetcher synchronously on that
/// thread, never after the call returns (measured on LiteRT-LM v0.20.0, win-x64). The call object is passed as
/// <c>user_data</c> through a <see cref="GCHandle"/> that is freed right after the native call returns.</para>
/// <para>Each fetch starts the source on the thread pool and drains its bytes through a bounded channel on the
/// callback thread, so the native writer is touched only on that thread and only during the callback. Whatever
/// ends a fetch, the callback cancels the source's token, closes the destination (a late write throws
/// <see cref="ObjectDisposedException"/> inside the source) and observes the source's exception before it
/// returns. A fetch succeeds only after the source's task has completed, so no byte past the requested length
/// reaches the runtime: the runtime accepts a whole-file write past the length and leaves a longer, corrupt file
/// (measured).</para>
/// </remarks>
[Experimental(ExperimentalApis.ModelSync, UrlFormat = ExperimentalApis.ModelSyncUrl)]
internal sealed class ModelSyncCall
{
    private const long ProgressStep = 1024 * 1024;
    private const int MaxErrorBytes = 1024;

    private static int s_liveHandles;

    private readonly LiteRtModelSyncSource _source;
    private readonly CancellationToken _token;
    private readonly IProgress<long>? _progress;
    private long _bytesReceived;
    private long _lastReported;
    private int _callingThread;
    private volatile bool _returned;
    private int _fetches;
    private int _offThreadFetches;
    private int _lateFetches;

    internal ModelSyncCall(LiteRtModelSyncSource source, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        _source = source;
        _progress = progress;
        _token = cancellationToken;
    }

    /// <summary>The number of call objects whose <see cref="GCHandle"/> is allocated, process-wide.</summary>
    internal static int LiveHandles => Volatile.Read(ref s_liveHandles);

    /// <summary>Receives a report after each native call returns, for tests and diagnostics.</summary>
    internal static Action<ModelSyncCallReport>? Completed { get; set; }

    /// <summary>For tests: how long <see cref="End"/> keeps the handle allocated after the native call returns,
    /// so <see cref="ModelSyncCallReport.LateFetches"/> can see a fetcher call that comes after the return. Zero,
    /// the default, frees it at once.</summary>
    internal static TimeSpan LateCallbackWindow { get; set; }

    /// <summary>The bytes passed to the native writer so far.</summary>
    internal long BytesReceived => _bytesReceived;

    /// <summary>The exception of the fetch that failed last, or <c>null</c>. A fetch that succeeds clears it.</summary>
    internal Exception? LastFetchException { get; private set; }

    /// <summary>Whether the fetch that ran last failed for a reason the source or the bridge reported (not the
    /// native writer). The runtime then reports that status, so a hint about the runtime's own checks does not
    /// apply.</summary>
    internal bool LastFetchFailed { get; private set; }

    /// <summary>Whether the runtime's writer refused bytes of the fetch that ran last. The call then fails in the
    /// runtime's storage of the bytes, so a hint about the remote does not apply.</summary>
    internal bool LastWriterFailed { get; private set; }

    /// <summary>The callback, for the native calls.</summary>
    internal static unsafe delegate* unmanaged[Cdecl]<ulong, ulong, nint, nint, LiteRtStatusCode> Fetcher => &Fetch;

    /// <summary>Pins this object for one native call on the calling thread and returns the fetcher's
    /// <c>user_data</c>. Call <see cref="End"/> on the same thread once the native call returns, in a
    /// <c>finally</c> block.</summary>
    internal nint Begin()
    {
        _callingThread = Environment.CurrentManagedThreadId;
        _handle = GCHandle.Alloc(this);
        Interlocked.Increment(ref s_liveHandles);
        return GCHandle.ToIntPtr(_handle);
    }

    /// <summary>Frees the handle <see cref="Begin"/> allocated: the native call has returned, and the runtime calls
    /// no fetcher after that (measured).</summary>
    internal void End(LiteRtStatusCode status)
    {
        _returned = true;
        // For tests: keep the handle valid for a while, so a fetcher call after the return would be counted
        // instead of reading a freed handle.
        if (LateCallbackWindow > TimeSpan.Zero)
            Thread.Sleep(LateCallbackWindow);
        if (_handle.IsAllocated)
        {
            _handle.Free();
            Interlocked.Decrement(ref s_liveHandles);
        }
        try
        {
            Completed?.Invoke(new ModelSyncCallReport(
                status, _callingThread, Volatile.Read(ref _fetches), Volatile.Read(ref _offThreadFetches),
                Volatile.Read(ref _lateFetches), _bytesReceived));
        }
        catch
        {
            // A diagnostics receiver never changes the call's outcome.
        }
    }

    private GCHandle _handle;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static LiteRtStatusCode Fetch(ulong offset, ulong length, nint writer, nint userData)
    {
        // The whole body is guarded: an exception that leaves an [UnmanagedCallersOnly] frame ends the process.
        try
        {
            return GCHandle.FromIntPtr(userData).Target is ModelSyncCall call
                ? call.OnFetch(offset, length, writer)
                : LiteRtStatusCode.Internal;
        }
        catch
        {
            return LiteRtStatusCode.Unknown;
        }
    }

    private LiteRtStatusCode OnFetch(ulong offset, ulong length, nint writer)
    {
        Interlocked.Increment(ref _fetches);
        if (Environment.CurrentManagedThreadId != _callingThread)
            Interlocked.Increment(ref _offThreadFetches);
        if (_returned)
            Interlocked.Increment(ref _lateFetches);
        LastFetchException = null;
        LastFetchFailed = false;
        LastWriterFailed = false;

        // The runtime skips empty sections and never asks for 0 bytes (measured); this is a defence.
        if (length == 0)
            return LiteRtStatusCode.Ok;
        if (_token.IsCancellationRequested)
            return Fail(writer, LiteRtStatusCode.Cancelled, CancelledText, null);
        if (offset > long.MaxValue || length > (ulong)(long.MaxValue - (long)offset))
            return Fail(writer, LiteRtStatusCode.DataLoss,
                "The remote header declares a range past the largest file size the binding supports.", null);
        return Transfer(writer, (long)offset, (long)length);
    }

    private const string CancelledText = "Cancelled by the caller.";

    private LiteRtStatusCode Transfer(nint writer, long offset, long length)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_token);
        var destination = new ModelSyncDestination(length, linked.Token);
        Task<bool> sourceTask = Task.Run(async () =>
        {
            try
            {
                return await _source.ReadRangeAsync(offset, length, destination, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                destination.CompleteWriting();
            }
        }, CancellationToken.None);
        try
        {
            return Drain(writer, offset, length, destination, sourceTask);
        }
        finally
        {
            // The same teardown on every exit: stop the source, refuse its later writes, observe its exception.
            linked.Cancel();
            destination.Shutdown();
            if (sourceTask.IsCompleted)
            {
                _ = sourceTask.Exception;
                linked.Dispose();
            }
            else
            {
                sourceTask.ContinueWith(
                    static (task, state) =>
                    {
                        _ = task.Exception;
                        ((CancellationTokenSource)state!).Dispose();
                    },
                    linked, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private unsafe LiteRtStatusCode Drain(
        nint writer, long offset, long length, ModelSyncDestination destination, Task<bool> sourceTask)
    {
        ChannelReader<ModelSyncDestination.Chunk> reader = destination.Reader;
        while (true)
        {
            while (reader.TryRead(out ModelSyncDestination.Chunk chunk))
            {
                LiteRtStatusCode written;
                try
                {
                    fixed (byte* p = chunk.Buffer)
                        written = LiteRtLmNative.litert_lm_model_sync_range_writer_write(writer, p, (nuint)chunk.Count);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(chunk.Buffer);
                }
                // The writer's own status, with its own message: a failure of the runtime's sink, not of the source.
                if (written != LiteRtStatusCode.Ok)
                {
                    LastWriterFailed = true;
                    return written;
                }
                _bytesReceived += chunk.Count;
                if (ReportProgress(force: false) is { } progressFailure)
                    return Fail(writer, LiteRtStatusCode.Unknown, progressFailure.Message, progressFailure);
            }

            bool more;
            try
            {
                more = reader.WaitToReadAsync(_token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested)
            {
                return Fail(writer, LiteRtStatusCode.Cancelled, CancelledText, null);
            }
            if (!more)
                break;
        }

        // The channel completes in the source task's finally block, so the task is about to complete.
        bool served;
        try
        {
            served = sourceTask.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            if (destination.Overflowed)
                return Fail(writer, LiteRtStatusCode.DataLoss, OverflowText, null);
            return FailWith(writer, e);
        }
        if (destination.Overflowed)
            return Fail(writer, LiteRtStatusCode.DataLoss, OverflowText, null);
        if (!served)
        {
            // Only "nothing written for a range after byte 0" tells the runtime to fall back to a full download.
            return offset > 0 && destination.Written == 0
                ? Fail(writer, LiteRtStatusCode.Unimplemented, "The remote does not serve byte ranges.", null)
                : Fail(writer, LiteRtStatusCode.Unknown,
                    offset == 0
                        ? "The source could not serve the start of the file."
                        : "The source stopped serving a range after writing part of it.", null);
        }
        // Only the first fetch of a call, the header fetch, can be longer than the file. Every later fetch asks
        // for an exact length, one of them from byte 0, and the runtime does not count the bytes of a whole-file
        // write: a short range would leave a truncated file, unnoticed when the remote header has no section
        // hashes.
        if (Volatile.Read(ref _fetches) > 1 && destination.Written < length)
            return Fail(writer, LiteRtStatusCode.DataLoss,
                $"The remote returned fewer bytes than the range the runtime asked for: {destination.Written} of " +
                $"{length} bytes from byte {offset}.", null);
        if (ReportProgress(force: true) is { } failure)
            return Fail(writer, LiteRtStatusCode.Unknown, failure.Message, failure);
        return LiteRtStatusCode.Ok;
    }

    private const string OverflowText = "The source returned more bytes than the requested range.";

    /// <summary>Reports the running total at most once per MiB, and with <paramref name="force"/> at the end of a
    /// range. Returns the receiver's exception instead of throwing it.</summary>
    private Exception? ReportProgress(bool force)
    {
        if (_progress is null || _bytesReceived == _lastReported)
            return null;
        if (!force && _bytesReceived - _lastReported < ProgressStep)
            return null;
        _lastReported = _bytesReceived;
        try
        {
            _progress.Report(_bytesReceived);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>The status a source's exception becomes. <c>FAILED_PRECONDITION</c>, <c>UNIMPLEMENTED</c> and
    /// <c>OUT_OF_RANGE</c> are never returned for an exception: the runtime answers them from a fetch after the
    /// header with a full download.</summary>
    internal LiteRtStatusCode StatusFor(Exception e) => e switch
    {
        OperationCanceledException when _token.IsCancellationRequested => LiteRtStatusCode.Cancelled,
        ModelSyncSourceException source => source.Status,
        TimeoutException or OperationCanceledException => LiteRtStatusCode.DeadlineExceeded,
        HttpRequestException { StatusCode: { } code } => (int)code switch
        {
            401 => LiteRtStatusCode.Unauthenticated,
            403 => LiteRtStatusCode.PermissionDenied,
            404 or 410 => LiteRtStatusCode.NotFound,
            408 or 429 or >= 500 => LiteRtStatusCode.Unavailable,
            _ => LiteRtStatusCode.Unknown,
        },
        HttpRequestException => LiteRtStatusCode.Unavailable,
        IOException => LiteRtStatusCode.Unavailable,
        _ => LiteRtStatusCode.Unknown,
    };

    private LiteRtStatusCode FailWith(nint writer, Exception e)
    {
        LiteRtStatusCode status = StatusFor(e);
        return status == LiteRtStatusCode.Cancelled
            ? Fail(writer, status, CancelledText, null)
            : Fail(writer, status, e.Message, e);
    }

    private LiteRtStatusCode Fail(nint writer, LiteRtStatusCode status, string? text, Exception? exception)
    {
        LastFetchException = exception;
        LastFetchFailed = true;
        if (text is not null)
            SetError(writer, text);
        return status;
    }

    /// <summary>Records the first line of <paramref name="text"/>, at most 1 KiB of UTF-8, as the fetch's
    /// message.</summary>
    private static unsafe void SetError(nint writer, string text)
    {
        string line = text.Trim();
        int newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
            line = line[..newline];
        byte[] bytes = new byte[MaxErrorBytes + 1];
        Encoder encoder = Encoding.UTF8.GetEncoder();
        // Converts whole characters only, so the cap never splits a UTF-8 sequence.
        encoder.Convert(line, bytes.AsSpan(0, MaxErrorBytes), flush: true, out _, out int used, out _);
        bytes[used] = 0;
        fixed (byte* p = bytes)
            LiteRtLmNative.litert_lm_model_sync_range_writer_set_error(writer, p);
    }
}

/// <summary>What one model sync call observed, from the managed side.</summary>
/// <param name="Status">The status the native call returned.</param>
/// <param name="CallingThread">The managed id of the thread that made the native call.</param>
/// <param name="Fetches">The fetcher callbacks.</param>
/// <param name="OffThreadFetches">The callbacks that ran on another thread than <paramref name="CallingThread"/>.</param>
/// <param name="LateFetches">The callbacks that ran after the native call returned.</param>
/// <param name="BytesReceived">The bytes passed to the native writer.</param>
internal readonly record struct ModelSyncCallReport(
    LiteRtStatusCode Status, int CallingThread, int Fetches, int OffThreadFetches, int LateFetches, long BytesReceived);

/// <summary>
/// The write-only stream a source writes one range into. It copies the bytes into pooled 64 KiB buffers and
/// queues them, at most four, for the callback thread; a writer that is faster than the runtime waits.
/// </summary>
internal sealed class ModelSyncDestination : Stream
{
    internal const int ChunkSize = 64 * 1024;
    internal const int Capacity = 4;

    internal readonly record struct Chunk(byte[] Buffer, int Count);

    private readonly Channel<Chunk> _channel = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private readonly long _length;
    private readonly CancellationToken _token;
    private long _written;
    private long _reserved;
    private volatile bool _closed;
    private volatile bool _overflowed;

    internal ModelSyncDestination(long length, CancellationToken token)
    {
        _length = length;
        _token = token;
    }

    internal ChannelReader<Chunk> Reader => _channel.Reader;

    /// <summary>The bytes accepted so far.</summary>
    internal long Written => Interlocked.Read(ref _written);

    /// <summary>Whether the source tried to write past the requested length. None of those bytes was queued.</summary>
    internal bool Overflowed => _overflowed;

    /// <summary>Marks the end of the source's writes.</summary>
    internal void CompleteWriting() => _channel.Writer.TryComplete();

    /// <summary>Refuses every later write and returns the queued buffers to the pool.</summary>
    internal void Shutdown()
    {
        _closed = true;
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out Chunk chunk))
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_closed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckWrite(buffer.Length);
        while (!buffer.IsEmpty)
        {
            int count = Math.Min(ChunkSize, buffer.Length);
            byte[] rented = ArrayPool<byte>.Shared.Rent(ChunkSize);
            buffer[..count].CopyTo(rented);
            EnqueueAsync(new Chunk(rented, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            buffer = buffer[count..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckWrite(buffer.Length);
        while (!buffer.IsEmpty)
        {
            int count = Math.Min(ChunkSize, buffer.Length);
            byte[] rented = ArrayPool<byte>.Shared.Rent(ChunkSize);
            buffer.Span[..count].CopyTo(rented);
            await EnqueueAsync(new Chunk(rented, count), cancellationToken).ConfigureAwait(false);
            buffer = buffer[count..];
        }
    }

    /// <summary>Reserves <paramref name="count"/> bytes of the range before any of them is queued. The reservation
    /// is atomic, so writes that overlap cannot together pass the requested length.</summary>
    private void CheckWrite(int count)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        while (true)
        {
            long reserved = Interlocked.Read(ref _reserved);
            if (_overflowed || count > _length - reserved)
            {
                _overflowed = true;
                throw new InvalidOperationException("The write goes past the end of the requested range.");
            }
            if (Interlocked.CompareExchange(ref _reserved, reserved + count, reserved) == reserved)
                return;
        }
    }

    private async ValueTask EnqueueAsync(Chunk chunk, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!_channel.Writer.TryWrite(chunk))
            {
                if (_closed || !await _channel.Writer.WaitToWriteAsync(_token).ConfigureAwait(false))
                    throw new ObjectDisposedException(nameof(ModelSyncDestination));
            }
            Interlocked.Add(ref _written, chunk.Count);
        }
        catch (Exception e) when (e is OperationCanceledException or ChannelClosedException && !cancellationToken.IsCancellationRequested)
        {
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
            throw new ObjectDisposedException(nameof(ModelSyncDestination));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
            throw;
        }
    }
}

/// <summary>A source failure with a fixed status: <c>ABORTED</c> when the remote file changed during the call,
/// or <c>DATA_LOSS</c> when the remote returned other bytes than the ones asked for.</summary>
internal sealed class ModelSyncSourceException : Exception
{
    internal ModelSyncSourceException(LiteRtStatusCode status, string message) : base(message)
    {
        if (status is not (LiteRtStatusCode.Aborted or LiteRtStatusCode.DataLoss))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Only ABORTED and DATA_LOSS are allowed.");
        Status = status;
    }

    internal LiteRtStatusCode Status { get; }
}

/// <summary>
/// The files that model sync calls of this process read or write. Two updates of one output would share one
/// temporary file, and a plan or update that reads a file another call is replacing would read a moving target.
/// A writer conflicts with any entry for the same path; a reader conflicts with a writer. Other processes are
/// not covered.
/// </summary>
internal static class ModelSyncInFlight
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (int Readers, bool Writer)> Entries = new(LoadedModelPaths.Comparer);

    /// <summary>Registers <paramref name="reader"/> and <paramref name="writer"/> (full paths, either
    /// <c>null</c>) until the returned lease is disposed.</summary>
    /// <exception cref="InvalidOperationException">A call in flight conflicts.</exception>
    internal static Lease Enter(string? reader, string? writer)
    {
        if (reader is not null && writer is not null && LoadedModelPaths.Comparer.Equals(reader, writer))
            reader = null;
        lock (Gate)
        {
            if (writer is not null && Entries.ContainsKey(writer))
                throw new InvalidOperationException(
                    $"Another model sync call of this process is reading or writing '{writer}'. Wait for it to finish " +
                    "before writing the file.");
            if (reader is not null && Entries.TryGetValue(reader, out var entry) && entry.Writer)
                throw new InvalidOperationException(
                    $"Another model sync call of this process is writing '{reader}'. Wait for it to finish before " +
                    "reading the file.");
            if (writer is not null)
                Entries[writer] = (0, true);
            if (reader is not null)
                Entries[reader] = Entries.TryGetValue(reader, out var r) ? (r.Readers + 1, r.Writer) : (1, false);
        }
        return new Lease(reader, writer);
    }

    /// <summary>Whether a call in flight reads or writes <paramref name="fullPath"/>.</summary>
    internal static bool Contains(string fullPath)
    {
        lock (Gate)
            return Entries.ContainsKey(fullPath);
    }

    internal sealed class Lease(string? reader, string? writer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (Gate)
            {
                if (writer is not null)
                    Entries.Remove(writer);
                if (reader is not null && Entries.TryGetValue(reader, out var entry))
                {
                    if (entry.Readers <= 1 && !entry.Writer)
                        Entries.Remove(reader);
                    else
                        Entries[reader] = (Math.Max(0, entry.Readers - 1), entry.Writer);
                }
            }
        }
    }
}
