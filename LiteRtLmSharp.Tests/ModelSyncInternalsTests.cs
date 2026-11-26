using System.Text;
using LiteRtLmSharp.Native;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The model sync internals without a native call: the path encoder, the registry of model files that live
/// engines hold, the registry of files that calls in flight read and write, the exception a source failure maps
/// to, and the write-only destination a source writes into.
/// </summary>
public sealed class ModelSyncInternalsTests
{
    [Fact]
    public void Encoder_AsciiPath_IsUtf8WithATerminator()
    {
        byte[] bytes = NarrowPath.EncodeForModelSync("C:/models/a.litertlm", "p");
        Assert.Equal("C:/models/a.litertlm\0", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Encoder_NonAsciiPath_FollowsTheCodePage()
    {
        const string path = "C:/Jos\u00e9/a.litertlm";
        if (NarrowPath.ActiveCodePage is not { } codePage || codePage == 65001)
        {
            Assert.Equal(Encoding.UTF8.GetBytes(path + "\0"), NarrowPath.EncodeForModelSync(path, "p"));
            return;
        }
        // The expectation comes from .NET's own tables for the active code page (1252 gives E9 for é).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding ansi = Encoding.GetEncoding((int)codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        byte[] expected;
        try
        {
            expected = ansi.GetBytes(path + "\0");
        }
        catch (EncoderFallbackException)
        {
            Assert.Throws<ArgumentException>(() => NarrowPath.EncodeForModelSync(path, "p"));
            return;
        }
        Assert.Equal(expected, NarrowPath.EncodeForModelSync(path, "p"));
    }

    [SkippableFact]
    public void Encoder_CharacterOutsideTheCodePage_IsRefused_NamingTheCodePage()
    {
        Skip.IfNot(NarrowPath.ActiveCodePage is { } cp && cp != 65001, "Needs Windows with an ANSI code page other than UTF-8.");
        // No ANSI code page has an emoji, and none may map it to a similar character.
        var e = Assert.Throws<ArgumentException>(() => NarrowPath.EncodeForModelSync("C:/emoji-\U0001F600/a.litertlm", "outputModelPath"));
        Assert.Equal("outputModelPath", e.ParamName);
        Assert.Contains($"({NarrowPath.ActiveCodePage})", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedModelPaths_CountsPerFullPath_AndNeverGoesBelowZero()
    {
        var registry = new LoadedModelPaths();
        string path = Path.Combine(Path.GetTempPath(), "registry-test.litertlm");
        string key = registry.Add(path);
        Assert.Equal(Path.GetFullPath(path), key);
        string second = registry.Add(Path.Combine(Path.GetTempPath(), ".", "registry-test.litertlm"));
        Assert.Equal(key, second);
        Assert.Equal(2, registry.Count(key));
        registry.Release(key);
        Assert.Equal(1, registry.Count(key));
        registry.Release(key);
        registry.Release(key);
        Assert.Equal(0, registry.Count(key));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            registry.Add(path);
            Assert.Equal(1, registry.Count(key.ToUpperInvariant()));
        }
    }

    [Fact]
    public void InFlight_AWriterExcludesEveryone_AndReadersShare()
    {
        string a = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"inflight-{Guid.NewGuid():N}.litertlm"));
        string b = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"inflight-{Guid.NewGuid():N}.litertlm"));
        using (ModelSyncInFlight.Enter(reader: a, writer: null))
        using (ModelSyncInFlight.Enter(reader: a, writer: b))
        {
            Assert.Throws<InvalidOperationException>(() => ModelSyncInFlight.Enter(reader: null, writer: a));
            Assert.Throws<InvalidOperationException>(() => ModelSyncInFlight.Enter(reader: b, writer: null));
            Assert.Throws<InvalidOperationException>(() => ModelSyncInFlight.Enter(reader: null, writer: b));
        }
        Assert.False(ModelSyncInFlight.Contains(a));
        Assert.False(ModelSyncInFlight.Contains(b));
        // In place: one writer entry, released once.
        ModelSyncInFlight.Lease lease = ModelSyncInFlight.Enter(reader: a, writer: a);
        Assert.True(ModelSyncInFlight.Contains(a));
        lease.Dispose();
        lease.Dispose();
        Assert.False(ModelSyncInFlight.Contains(a));
    }

    [Fact]
    public void SourceException_AllowsOnlyAbortedAndDataLoss()
    {
        Assert.Equal(LiteRtStatusCode.Aborted, new ModelSyncSourceException(LiteRtStatusCode.Aborted, "changed").Status);
        Assert.Equal(LiteRtStatusCode.DataLoss, new ModelSyncSourceException(LiteRtStatusCode.DataLoss, "offset").Status);
        // The statuses that make the runtime fall back to a full download never come from an exception.
        foreach (LiteRtStatusCode status in new[]
                 { LiteRtStatusCode.Unimplemented, LiteRtStatusCode.FailedPrecondition, LiteRtStatusCode.OutOfRange, LiteRtStatusCode.Ok })
            Assert.Throws<ArgumentOutOfRangeException>(() => new ModelSyncSourceException(status, "x"));
    }

    [Fact]
    public void StatusFor_NeverReturnsAFallbackStatus()
    {
        var call = new ModelSyncCall(new ModelSyncMemorySource([]), null, CancellationToken.None);
        Exception[] failures =
        [
            new HttpRequestException("x", null, System.Net.HttpStatusCode.RequestedRangeNotSatisfiable),
            new HttpRequestException("x", null, System.Net.HttpStatusCode.PreconditionFailed),
            new HttpRequestException("x", null, System.Net.HttpStatusCode.NotImplemented),
            new NotImplementedException(),
            new NotSupportedException(),
            new ArgumentOutOfRangeException(),
            new InvalidOperationException(),
        ];
        foreach (Exception failure in failures)
            Assert.DoesNotContain(call.StatusFor(failure),
                new[] { LiteRtStatusCode.Unimplemented, LiteRtStatusCode.FailedPrecondition, LiteRtStatusCode.OutOfRange, LiteRtStatusCode.Ok });
        Assert.Equal(LiteRtStatusCode.Unavailable, call.StatusFor(new HttpRequestException("x", null, System.Net.HttpStatusCode.NotImplemented)));
    }

    [Fact]
    public void StatusFor_TheCallersCancel_IsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var call = new ModelSyncCall(new ModelSyncMemorySource([]), null, cts.Token);
        Assert.Equal(LiteRtStatusCode.DeadlineExceeded, call.StatusFor(new OperationCanceledException()));
        cts.Cancel();
        Assert.Equal(LiteRtStatusCode.Cancelled, call.StatusFor(new OperationCanceledException(cts.Token)));
    }

    [Fact]
    public async Task Destination_RefusesWritesPastTheLength_AndAfterShutdown()
    {
        var destination = new ModelSyncDestination(10, CancellationToken.None);
        await destination.WriteAsync(new byte[6]);
        Assert.Throws<InvalidOperationException>(() => destination.Write(new byte[5]));
        Assert.True(destination.Overflowed);
        Assert.Equal(6, destination.Written);
        Assert.True(destination.Reader.TryRead(out var chunk));
        Assert.Equal(6, chunk.Count);

        var other = new ModelSyncDestination(10, CancellationToken.None);
        other.Shutdown();
        Assert.Throws<ObjectDisposedException>(() => other.Write(new byte[1]));
        Assert.False(other.CanWrite);
        Assert.False(other.CanRead);
        Assert.False(other.CanSeek);
    }

    [Fact]
    public async Task Destination_OverlappingWrites_CannotTogetherPassTheLength()
    {
        // Room for five chunks; four fill the channel, then two writes of one chunk each start before either is
        // queued. Only one of them fits.
        int chunk = ModelSyncDestination.ChunkSize;
        var destination = new ModelSyncDestination(5L * chunk, CancellationToken.None);
        for (int i = 0; i < ModelSyncDestination.Capacity; i++)
            await destination.WriteAsync(new byte[chunk]);
        Task first = destination.WriteAsync(new byte[chunk]).AsTask();
        Task second = destination.WriteAsync(new byte[chunk]).AsTask();
        long drained = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!(first.IsCompleted && second.IsCompleted) && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            while (destination.Reader.TryRead(out ModelSyncDestination.Chunk c))
            {
                drained += c.Count;
                System.Buffers.ArrayPool<byte>.Shared.Return(c.Buffer);
            }
            await Task.Delay(10);
        }
        destination.CompleteWriting();
        while (destination.Reader.TryRead(out ModelSyncDestination.Chunk c))
        {
            drained += c.Count;
            System.Buffers.ArrayPool<byte>.Shared.Return(c.Buffer);
        }
        Assert.True(destination.Overflowed);
        Assert.Equal(5L * chunk, drained);
        Assert.Equal(1, new[] { first, second }.Count(t => t.IsFaulted && t.Exception!.InnerException is InvalidOperationException));
    }

    [Fact]
    public void Encoder_UnpairedSurrogate_IsRefusedOnEveryPlatform()
    {
        const string path = "C:/d/\uD800.litertlm";
        // The ANSI branch on Windows and the UTF-8 branch elsewhere: the replacing encoder would write U+FFFD.
        Assert.Throws<ArgumentException>(() => NarrowPath.EncodeForModelSync(path, "p"));
        var e = Assert.Throws<ArgumentException>(() => NarrowPath.StrictUtf8(path, "p"));
        Assert.Equal("p", e.ParamName);
        Assert.Equal(Encoding.UTF8.GetBytes("C:/d/a\0"), NarrowPath.StrictUtf8("C:/d/a", "p"));
    }

    [Fact]
    public void ModelFilePath_PlainPaths_AreTheirFullPath_AndKeyNeverThrows()
    {
        string plain = Path.Combine(Path.GetTempPath(), "spelling.litertlm");
        Assert.Equal(Path.GetFullPath(plain), ModelFilePath.Normalize(plain, "p"));
        Assert.Equal(Path.GetFullPath(plain), ModelFilePath.Key(Path.Combine(Path.GetTempPath(), ".", "spelling.litertlm")));
        if (!OperatingSystem.IsWindows())
            return;
        string full = Path.GetFullPath(plain);
        Assert.Equal(full, ModelFilePath.Normalize(@"\\?\" + full, "p"));
        Assert.Equal(full, ModelFilePath.Normalize(@"\\.\" + full, "p"));
        Assert.Equal(@"\\server\share\m.litertlm", ModelFilePath.Normalize(@"\\?\UNC\server\share\m.litertlm", "p"));
        Assert.Equal(@"\\server\share\m.litertlm", ModelFilePath.Normalize(@"\\.\UNC\server\share\m.litertlm", "p"));
        // A folder whose name ends in a dot exists only in the extended form: the prefix stays.
        Assert.Equal(@"\\?\C:\m.\a.litertlm", ModelFilePath.Normalize(@"\\?\C:\m.\a.litertlm", "p"));
        Assert.Throws<ArgumentException>(() => ModelFilePath.Normalize(@"\\?\UNC\server\share\m.\a.litertlm", "p"));
        Assert.Throws<ArgumentException>(() => ModelFilePath.Normalize(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\a.litertlm", "p"));
        Assert.Throws<ArgumentException>(() => ModelFilePath.Normalize(@"\\.\pipe\a", "p"));
        // The registry key of a path that Normalize refuses is its full path.
        Assert.Equal(Path.GetFullPath(@"\\.\pipe\a"), ModelFilePath.Key(@"\\.\pipe\a"));
    }

    [Fact]
    public void ModelFilePath_SameFile_InACaseSensitiveFolder_IsTwoFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "case-folder");
        string upper = Path.Combine(folder, "Model.litertlm");
        string lower = Path.Combine(folder, "model.litertlm");
        Assert.True(ModelFilePath.SameFile(upper, upper));
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
        {
            // Ordinal comparison: two names are two files.
            Assert.False(ModelFilePath.SameFile(upper, lower));
            return;
        }
        string[] one = ["Model.litertlm"];
        string[] two = ["Model.litertlm", "model.litertlm"];
        // A folder that ignores case: one entry, both spellings exist.
        Assert.True(ModelFilePath.SameFile(upper, lower, _ => one, _ => true));
        // A case-sensitive folder with both files.
        Assert.False(ModelFilePath.SameFile(upper, lower, _ => two, _ => true));
        // A case-sensitive folder with only one of them.
        Assert.False(ModelFilePath.SameFile(upper, lower, _ => one, p => p == upper));
        // A folder that cannot be listed.
        Assert.False(ModelFilePath.SameFile(upper, lower, _ => throw new UnauthorizedAccessException(), _ => true));
        // Names that differ in more than case are never the same file.
        Assert.False(ModelFilePath.SameFile(upper, Path.Combine(folder, "Other.litertlm"), _ => one, _ => true));
    }

    public static TheoryData<LiteRtStatusCode, string?, bool, bool, bool, string?> Hints => new()
    {
        // Status, reason, the last fetch failed, the writer failed, update, expected text (null: no hint).
        { LiteRtStatusCode.ResourceExhausted, null, false, false, true, "free space" },
        { LiteRtStatusCode.PermissionDenied, "Cannot open output file", false, false, true, "cannot create" },
        { LiteRtStatusCode.PermissionDenied, "HTTP 403", true, false, true, null },
        { LiteRtStatusCode.PermissionDenied, "x", false, false, false, null },
        { LiteRtStatusCode.Internal, "File exists", false, false, true, "pass the same path for both" },
        { LiteRtStatusCode.Internal, "Failed to rename temporary model to destination: Access is denied.", false, false, true, "while the output or its temporary file" },
        { LiteRtStatusCode.Internal, "x", false, false, false, null },
        { LiteRtStatusCode.DataLoss, "Section SHA-256 verification failed", false, false, true, "may have changed during the call" },
        { LiteRtStatusCode.DataLoss, "Section payload truncated", false, false, true, "may not be a .litertlm file" },
        { LiteRtStatusCode.InvalidArgument, "magic bytes", false, false, false, "may not be a .litertlm file" },
        { LiteRtStatusCode.DataLoss, "Content-Range", true, false, true, null },
        // A failure of the runtime's own writer is not the remote's: it gets its own hint.
        { LiteRtStatusCode.DataLoss, "write failed", false, true, true, "could not store the bytes it received" },
        { LiteRtStatusCode.InvalidArgument, "x", false, true, false, null },
        { LiteRtStatusCode.Unavailable, "x", true, false, true, null },
    };

    [Theory]
    [MemberData(nameof(Hints))]
    public void Hint_AppliesOnlyToTheFailuresItExplains(
        LiteRtStatusCode code, string? reason, bool fetchFailed, bool writerFailed, bool update, string? expected)
    {
        string? hint = LiteRtModelSync.Hint(code, reason, fetchFailed, writerFailed, update, ansiNonAscii: false);
        if (expected is null)
            Assert.Null(hint);
        else
            Assert.Contains(expected, hint, StringComparison.Ordinal);
        Assert.Contains("ANSI code page", LiteRtModelSync.Hint(code, reason, fetchFailed, writerFailed, update, ansiNonAscii: true), StringComparison.Ordinal);
    }

    [Fact]
    public void LiteRtException_InternalConstructor_CarriesTheStatusAndTheCause()
    {
        var cause = new IOException("cause");
        var e = new LiteRtException("message", LiteRtStatusCode.Unavailable, cause);
        Assert.Equal(LiteRtStatusCode.Unavailable, e.StatusCode);
        Assert.Same(cause, e.InnerException);
    }
}
