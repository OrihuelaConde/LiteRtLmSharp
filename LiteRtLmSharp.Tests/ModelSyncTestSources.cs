namespace LiteRtLmSharp.Tests;

/// <summary>One request a test source received.</summary>
/// <param name="Index">The request's position in the call, from 0.</param>
/// <param name="Offset">The first byte asked for.</param>
/// <param name="Length">The number of bytes asked for.</param>
/// <param name="Written">The bytes the source wrote before it returned or threw.</param>
/// <param name="Outcome"><c>served</c>, <c>refused</c> or the name of the exception the source let out.</param>
internal sealed record SourceRequest(int Index, long Offset, long Length, long Written, string Outcome)
{
    public override string ToString() => $"[{Offset},+{Length}] {Outcome} wrote={Written}";
}

/// <summary>What a test source does for one request, instead of serving it.</summary>
internal sealed record SourceAction
{
    /// <summary>Return <c>false</c> without writing.</summary>
    public bool Refuse { get; init; }

    /// <summary>Return <c>false</c> after writing <see cref="WriteBeforeRefusal"/> bytes.</summary>
    public int WriteBeforeRefusal { get; init; }

    /// <summary>Throw this exception, after writing <see cref="WriteBeforeThrow"/> bytes.</summary>
    public Exception? Throw { get; init; }

    /// <summary>The bytes written before <see cref="Throw"/>.</summary>
    public int WriteBeforeThrow { get; init; }

    /// <summary>Bytes written past the end of the range, after it.</summary>
    public int ExtraBytes { get; init; }

    /// <summary>Bytes of the range left out at its end.</summary>
    public int MissingBytes { get; init; }

    /// <summary>A task the source waits for before it writes anything.</summary>
    public Task? Gate { get; init; }

    /// <summary>Whether the wait on <see cref="Gate"/> ignores the token.</summary>
    public bool IgnoreToken { get; init; }

    /// <summary>The bytes to serve instead of the source's data, for the same range.</summary>
    public byte[]? OtherData { get; init; }
}

/// <summary>
/// A remote file held in memory. It serves byte ranges in 64 KiB writes and records every request; a script can
/// change what it does per request. With <see cref="ServesRanges"/> set to <c>false</c> it acts like an HTTP
/// server that ignores <c>Range</c>, as the HTTP source maps one: the first bytes for a request at byte 0, and
/// "ranges unsupported" for any other.
/// </summary>
internal sealed class ModelSyncMemorySource(byte[] data) : LiteRtModelSyncSource
{
    private readonly Lock _lock = new();
    private readonly List<SourceRequest> _requests = [];
    private readonly List<Exception> _lateWriteFailures = [];
    private int _next;

    public byte[] Data { get; } = data;

    /// <summary>Whether the source serves ranges after byte 0.</summary>
    public bool ServesRanges { get; init; } = true;

    /// <summary>The size of each write.</summary>
    public int WriteSize { get; init; } = 64 * 1024;

    /// <summary>Picks the action for a request from its index, offset and length; <c>null</c> serves it.</summary>
    public Func<int, long, long, SourceAction?>? Script { get; set; }

    /// <summary>Runs before each request, on the source's thread.</summary>
    public Action<int, long, long>? OnRequest { get; set; }

    /// <summary>The requests, in order.</summary>
    public IReadOnlyList<SourceRequest> Requests
    {
        get { lock (_lock) return [.. _requests]; }
    }

    /// <summary>The exceptions that writes after the fetch had ended threw inside the source.</summary>
    public IReadOnlyList<Exception> LateWriteFailures
    {
        get { lock (_lock) return [.. _lateWriteFailures]; }
    }

    /// <summary>Completes when a request that waits on a gate starts waiting.</summary>
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a request whose wait ignores the token has ended.</summary>
    public TaskCompletionSource IgnoredTokenRequestEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal override async ValueTask<bool> ReadRangeAsync(
        long offset, long length, Stream destination, CancellationToken cancellationToken)
    {
        int index = Interlocked.Increment(ref _next) - 1;
        OnRequest?.Invoke(index, offset, length);
        SourceAction? action = Script?.Invoke(index, offset, length);
        long written = 0;
        try
        {
            if (action?.Gate is { } gate)
            {
                Waiting.TrySetResult();
                if (action.IgnoreToken)
                {
                    await gate.ConfigureAwait(false);
                    try
                    {
                        // The fetch has ended by now: this write must be refused.
                        await destination.WriteAsync(Data.AsMemory(0, 1), CancellationToken.None).ConfigureAwait(false);
                        written++;
                    }
                    catch (Exception e)
                    {
                        lock (_lock)
                            _lateWriteFailures.Add(e);
                        throw;
                    }
                    finally
                    {
                        IgnoredTokenRequestEnded.TrySetResult();
                    }
                }
                else
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (!ServesRanges && offset > 0)
            {
                Record(index, offset, length, 0, "refused");
                return false;
            }
            if (action is { Refuse: true })
            {
                Record(index, offset, length, 0, "refused");
                return false;
            }

            byte[] source = action?.OtherData ?? Data;
            long available = Math.Max(0, Math.Min(length, source.Length - offset));
            if (action is { WriteBeforeRefusal: > 0 } refusal)
            {
                written += await WriteAsync(destination, source, offset, Math.Min(available, refusal.WriteBeforeRefusal), cancellationToken);
                Record(index, offset, length, written, "refused");
                return false;
            }
            if (action?.Throw is { } exception)
            {
                written += await WriteAsync(destination, source, offset, Math.Min(available, action.WriteBeforeThrow), cancellationToken);
                throw exception;
            }

            long count = Math.Max(0, available - (action?.MissingBytes ?? 0));
            written += await WriteAsync(destination, source, offset, count, cancellationToken);
            if (action is { ExtraBytes: > 0 } extra)
                written += await WriteAsync(destination, new byte[extra.ExtraBytes], 0, extra.ExtraBytes, cancellationToken);
            Record(index, offset, length, written, "served");
            return true;
        }
        catch (Exception e)
        {
            Record(index, offset, length, written, e.GetType().Name);
            throw;
        }
    }

    private async Task<long> WriteAsync(Stream destination, byte[] source, long offset, long count, CancellationToken cancellationToken)
    {
        long done = 0;
        while (done < count)
        {
            int n = (int)Math.Min(WriteSize, count - done);
            await destination.WriteAsync(source.AsMemory((int)(offset + done), n), cancellationToken).ConfigureAwait(false);
            done += n;
        }
        return done;
    }

    private void Record(int index, long offset, long length, long written, string outcome)
    {
        lock (_lock)
            _requests.Add(new SourceRequest(index, offset, length, written, outcome));
    }
}

/// <summary>A remote file read from disk, served in 1 MiB writes, with every request recorded.</summary>
internal sealed class ModelSyncFileSource(string path) : LiteRtModelSyncSource
{
    private readonly Lock _lock = new();
    private readonly List<SourceRequest> _requests = [];

    public IReadOnlyList<SourceRequest> Requests
    {
        get { lock (_lock) return [.. _requests]; }
    }

    internal override async ValueTask<bool> ReadRangeAsync(
        long offset, long length, Stream destination, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);
        long available = Math.Max(0, Math.Min(length, file.Length - offset));
        file.Position = offset;
        byte[] buffer = new byte[1024 * 1024];
        long done = 0;
        while (done < available)
        {
            int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, available - done)), cancellationToken);
            if (read == 0)
                throw new IOException("The file ended before the requested range.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            done += read;
        }
        lock (_lock)
            _requests.Add(new SourceRequest(_requests.Count, offset, length, done, "served"));
        return true;
    }
}

/// <summary>Checks and cleanup that the model sync test classes share.</summary>
internal static class ModelSyncAsserts
{
    /// <summary>Asserts that no <c>*.tmp_updating</c> file is left under <paramref name="folder"/>. A file that is
    /// still listed right after the call gets up to 2 seconds to go away, so a deletion that the operating system
    /// completes late does not fail the test. A file that stays fails the test with its size and whether it
    /// opens.</summary>
    public static void NoTemporaries(string folder)
    {
        string[] found = Find(folder);
        if (found.Length == 0)
            return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (found.Length > 0 && clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Sleep(20);
            found = Find(folder);
        }
        if (found.Length == 0)
            return;
        Xunit.Assert.Fail("Temporary file left after the call: " + string.Join("; ", found.Select(Describe)));
    }

    /// <summary>Deletes a test folder, ignoring a file that another process still holds.</summary>
    public static void DeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string[] Find(string folder) => Directory.GetFiles(folder, "*.tmp_updating", SearchOption.AllDirectories);

    private static string Describe(string path)
    {
        string size;
        try { size = new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) { size = e.GetType().Name; }
        string open;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            open = "opens";
        }
        catch (Exception e)
        {
            open = $"does not open ({e.GetType().Name}, 0x{e.HResult:X8})";
        }
        return $"{path}: {size} bytes, {open}";
    }
}

/// <summary>Takes away the right to create files in a folder for the current user, and gives it back on
/// Dispose.</summary>
internal static class ModelSyncFolderAccess
{
    public static IDisposable DenyFileCreation(string folder)
    {
        return OperatingSystem.IsWindows() ? WindowsDeny(folder) : UnixDeny(folder);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static IDisposable UnixDeny(string folder)
    {
        UnixFileMode mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() => File.SetUnixFileMode(folder, mode));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IDisposable WindowsDeny(string folder)
    {
        var info = new DirectoryInfo(folder);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            user, System.Security.AccessControl.FileSystemRights.CreateFiles,
            System.Security.AccessControl.InheritanceFlags.None, System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Deny);
        System.Security.AccessControl.DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        return new Restore(() =>
        {
            System.Security.AccessControl.DirectorySecurity current = info.GetAccessControl();
            current.RemoveAccessRule(deny);
            info.SetAccessControl(current);
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}
