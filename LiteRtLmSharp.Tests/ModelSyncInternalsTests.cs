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
    public void LiteRtException_InternalConstructor_CarriesTheStatusAndTheCause()
    {
        var cause = new IOException("cause");
        var e = new LiteRtException("message", LiteRtStatusCode.Unavailable, cause);
        Assert.Equal(LiteRtStatusCode.Unavailable, e.StatusCode);
        Assert.Same(cause, e.InnerException);
    }
}
