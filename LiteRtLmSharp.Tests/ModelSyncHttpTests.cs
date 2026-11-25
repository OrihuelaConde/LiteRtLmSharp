using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// <see cref="LiteRtModelSyncSource.FromHttp"/> over an in-memory <see cref="HttpMessageHandler"/>, without
/// sockets and without a model: the <c>Range</c> header, 206 and 200 answers, <c>Content-Range</c> checks, short,
/// truncated and over-long bodies, the status mapping, the entity tag pin, the idle timeout, cancel, and the
/// caller's client. The request sequences the runtime makes were measured on LiteRT-LM v0.20.0, win-x64.
/// </summary>
public sealed class ModelSyncHttpTests : IDisposable
{
    private static readonly Uri Remote = new("https://models.invalid/repo/resolve/0123abcd/model.litertlm");

    private readonly ITestOutputHelper _output;
    private readonly string _dir;
    private readonly List<HttpClient> _clients = [];

    public ModelSyncHttpTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Directory.CreateTempSubdirectory("litertlm-http-").FullName;
    }

    public void Dispose()
    {
        foreach (HttpClient client in _clients)
            client.Dispose();
        ModelSyncAsserts.DeleteFolder(_dir);
    }

    private string Put(string name, byte[] data)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string PathIn(string name) => Path.Combine(_dir, name);

    private static void AssertBytes(byte[] expected, string path) => Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(expected), path);

    private HttpClient ClientFor(FakeModelServer server, TimeSpan? timeout = null)
    {
        var client = new HttpClient(server, disposeHandler: false);
        if (timeout is { } t)
            client.Timeout = t;
        _clients.Add(client);
        return client;
    }

    private LiteRtModelSyncSource Source(FakeModelServer server) => LiteRtModelSyncSource.FromHttp(Remote, ClientFor(server));

    private ModelSyncHttpSource Source(FakeModelServer server, TimeSpan idleTimeout) => new(Remote, ClientFor(server), idleTimeout);

    private static string Range(long offset, long length) => $"bytes={offset}-{offset + length - 1}";

    // ---------------------------------------------------------------- the factory

    [Fact]
    public void FromHttp_RefusesAnAddressThatIsNotAbsoluteHttpOrHttps()
    {
        Assert.Throws<ArgumentNullException>(() => LiteRtModelSyncSource.FromHttp(null!));
        Assert.Throws<ArgumentException>(() => LiteRtModelSyncSource.FromHttp(new Uri("model.litertlm", UriKind.Relative)));
        Assert.Throws<ArgumentException>(() => LiteRtModelSyncSource.FromHttp(new Uri("ftp://models.invalid/model.litertlm")));
        Assert.Throws<ArgumentException>(() => LiteRtModelSyncSource.FromHttp(new Uri("file:///C:/models/model.litertlm")));
        Assert.IsType<ModelSyncHttpSource>(LiteRtModelSyncSource.FromHttp(new Uri("http://models.invalid/model.litertlm")));
    }

    [Fact]
    public void FromHttp_WithoutAClient_SharesOneClientWithNoOverallTimeout()
    {
        var first = Assert.IsType<ModelSyncHttpSource>(LiteRtModelSyncSource.FromHttp(Remote));
        var second = Assert.IsType<ModelSyncHttpSource>(LiteRtModelSyncSource.FromHttp(Remote));
        Assert.True(first.UsesDefaultClient);
        Assert.Same(first.Client, second.Client);
        Assert.Equal(Timeout.InfiniteTimeSpan, first.Client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(60), first.IdleTimeout);

        using var client = new HttpClient();
        var own = Assert.IsType<ModelSyncHttpSource>(LiteRtModelSyncSource.FromHttp(Remote, client));
        Assert.False(own.UsesDefaultClient);
        Assert.Same(client, own.Client);
    }

    // ---------------------------------------------------------------- 206

    [Fact]
    public async Task FirstDownload_SendsClosedRanges_AndWritesTheRemoteFile()
    {
        byte[] remote = ModelSyncFixtures.A;
        var server = new FakeModelServer(remote);
        string output = PathIn("first.litertlm");
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(null, output, Source(server));
        _output.WriteLine(server.Sequence());
        // The exact header text: closed ranges, the last byte included.
        Assert.Equal(["bytes=0-16383", "bytes=1184-129023"], server.Requests.Select(r => r.Range));
        Assert.All(server.Requests, r => Assert.Equal(206, r.Status));
        Assert.All(server.Messages, m => Assert.Equal(HttpMethod.Get, m.Method));
        Assert.Equal(144224, result.BytesReceived);
        AssertBytes(remote, output);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task Differential_InPlace_FetchesTheHeaderAndTheChangedSection()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1);
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(local, local, Source(server));
        Assert.Equal($"{Range(0, 16384)}:206 {Range(81920, 8192)}:206", server.Sequence());
        Assert.Equal(16384 + 8192, result.BytesReceived);
        AssertBytes(ModelSyncFixtures.A1, local);
    }

    [Fact]
    public async Task Plan_ReportsTheRuntimesNumbers()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1);
        LiteRtModelSyncPlan plan = await LiteRtModelSync.CreatePlanAsync(local, Source(server));
        Assert.False(plan.IsUpToDate);
        Assert.Equal(9376, plan.BytesToDownload);
        Assert.Equal(72704, plan.BytesToReuse);
        Assert.Equal(129024, plan.TotalRemoteSize);
        Assert.Equal(Range(0, 16384), Assert.Single(server.Requests).Range);
    }

    [Fact]
    public async Task ShortAnswerToTheHeaderRequest_OfASmallFile_IsServed()
    {
        // A 206 that is shorter than the request but complete per its Content-Range: a file under 16 KiB.
        byte[] remote = ModelSyncFixtures.Small;
        var server = new FakeModelServer(remote);
        string output = PathIn("small.litertlm");
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(null, output, Source(server));
        Assert.Equal(6520, result.BytesReceived);
        Assert.Equal("bytes=0-16383", server.Requests[0].Range);
        AssertBytes(remote, output);
    }

    /// <summary>The ranges a source asks for when every 206 carries at most <paramref name="cap"/> bytes.</summary>
    private static IEnumerable<string> CappedRanges(long offset, long length, long cap)
    {
        for (long next = offset; next < offset + length; next += cap)
            yield return Range(next, offset + length - next);
    }

    [Fact]
    public async Task PartialAnswers_AreCompletedWithRequestsForTheRest()
    {
        // RFC 9110 lets a server send a part of the range. Each answer here carries at most 8,000 bytes.
        byte[] remote = ModelSyncFixtures.A;
        var server = new FakeModelServer(remote) { MaxPartial = 8000 };
        string output = PathIn("first.litertlm");
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(null, output, Source(server));
        _output.WriteLine(server.Sequence());
        Assert.Equal([.. CappedRanges(0, 16384, 8000), .. CappedRanges(1184, 127840, 8000)], server.Requests.Select(r => r.Range));
        Assert.All(server.Requests, r => Assert.Equal(206, r.Status));
        Assert.Equal(144224, result.BytesReceived);
        AssertBytes(remote, output);

        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var diff = new FakeModelServer(ModelSyncFixtures.A1) { MaxPartial = 8000 };
        await LiteRtModelSync.UpdateAsync(local, local, Source(diff));
        Assert.Equal([.. CappedRanges(0, 16384, 8000), .. CappedRanges(81920, 8192, 8000)], diff.Requests.Select(r => r.Range));
        AssertBytes(ModelSyncFixtures.A1, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task PartialAnswer_WithoutAFileSize_IsComplete()
    {
        // "bytes 0-3715/*": the remote does not say where the file ends, so the answer is taken as the whole range.
        byte[] remote = ModelSyncFixtures.Small;
        var server = new FakeModelServer(remote) { UnknownLength = true };
        string output = PathIn("small.litertlm");
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(null, output, Source(server));
        Assert.Equal(["bytes=0-16383", "bytes=912-3715"], server.Requests.Select(r => r.Range));
        Assert.Equal(6520, result.BytesReceived);
        AssertBytes(remote, output);
    }

    [Fact]
    public async Task WholeFileAnswer_AfterAPartialOne_FailsWithUnknown()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1)
        {
            MaxPartial = 8000,
            Script = (index, _) => index == 1 ? new FakeHttpAction { Whole = true } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, Source(server)));
        _output.WriteLine(e.Message);
        // Never a status that makes the runtime download the whole file: the bytes already written are kept.
        Assert.Equal(LiteRtStatusCode.Unknown, e.StatusCode);
        Assert.Equal(HttpStatusCode.OK, Assert.IsType<HttpRequestException>(e.InnerException).StatusCode);
        Assert.Equal($"{Range(0, 16384)}:206 {Range(8000, 8384)}:200", server.Sequence());
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task ContentRange_AtAnotherOffset_OrMissing_FailsWithDataLoss()
    {
        foreach (FakeHttpAction wrong in new[] { new FakeHttpAction { ContentRangeFrom = 81921 }, new FakeHttpAction { NoContentRange = true } })
        {
            string local = Put("a.litertlm", ModelSyncFixtures.A);
            var server = new FakeModelServer(ModelSyncFixtures.A1) { Script = (index, _) => index == 1 ? wrong : null };
            var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, Source(server)));
            _output.WriteLine(e.Message);
            Assert.Equal(LiteRtStatusCode.DataLoss, e.StatusCode);
            Assert.IsType<ModelSyncSourceException>(e.InnerException);
            Assert.Equal(2, server.Requests.Count);
            AssertBytes(ModelSyncFixtures.A, local);
            ModelSyncAsserts.NoTemporaries(_dir);
        }
    }

    [Fact]
    public async Task BodyThatEndsBeforeItsDeclaredLength_FailsWithUnavailable()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1) { Script = (index, _) => index == 1 ? new FakeHttpAction { Truncate = 100 } : null };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, Source(server)));
        _output.WriteLine(e.Message);
        Assert.Equal(LiteRtStatusCode.Unavailable, e.StatusCode);
        Assert.IsType<IOException>(e.InnerException);
        Assert.Contains("ended after 8092 of 8192 bytes", e.Message, StringComparison.Ordinal);
        Assert.Equal(2, server.Requests.Count);
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task OverlongAnswers_AreCappedAtTheRequestedLength()
    {
        // The runtime accepts a whole-file write past the requested length and leaves a longer file (measured),
        // so the source never writes past it, on a 206 or on a 200 at byte 0.
        byte[] remote = ModelSyncFixtures.A;
        long headerEnd = ModelSyncFixtures.HeaderEnd(remote);

        var ranged = new FakeModelServer(remote) { Script = (_, _) => new FakeHttpAction { ExtraBytes = 1000 } };
        string output = PathIn("ranged.litertlm");
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(null, output, Source(ranged));
        Assert.Equal(144224, result.BytesReceived);
        AssertBytes(remote, output);
        Assert.Equal($"bytes={headerEnd}-{remote.Length - 1}", ranged.Requests[1].Range);

        var whole = new FakeModelServer(remote) { ServesRanges = false, Script = (_, _) => new FakeHttpAction { ExtraBytes = 100 } };
        string wholeOutput = PathIn("whole.litertlm");
        result = await LiteRtModelSync.UpdateAsync(null, wholeOutput, Source(whole));
        Assert.Equal(16384 + remote.Length, result.BytesReceived);
        AssertBytes(remote, wholeOutput);
        Assert.Equal($"bytes=0-{remote.Length - 1}", whole.Requests[^1].Range);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task ReadRange_CopiesNoByteBeyondTheRequest()
    {
        var server = new FakeModelServer(ModelSyncFixtures.A) { Script = (_, _) => new FakeHttpAction { ExtraBytes = 5000 } };
        LiteRtModelSyncSource call = Source(server, TimeSpan.FromSeconds(60)).ForCall();
        using var destination = new MemoryStream();
        Assert.True(await call.ReadRangeAsync(100, 1000, destination, CancellationToken.None));
        Assert.Equal(ModelSyncFixtures.A.AsSpan(100, 1000).ToArray(), destination.ToArray());
        Assert.Equal(1000, server.Bodies[0].BytesRead);
    }

    // ---------------------------------------------------------------- 200: a server that ignores Range

    [Fact]
    public async Task ServerWithoutRanges_Differential_FallsBackToTheWholeFile_WithoutReadingTheRefusedBodies()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        byte[] remote = ModelSyncFixtures.A1;
        var server = new FakeModelServer(remote) { ServesRanges = false };
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(local, local, Source(server));
        _output.WriteLine(server.Sequence());
        Assert.Equal(
            $"{Range(0, 16384)}:200 {Range(81920, 8192)}:200 {Range(1184, 127840)}:200 {Range(0, 129024)}:200",
            server.Sequence());
        Assert.Equal(16384 + 129024, result.BytesReceived);
        AssertBytes(remote, local);
        // The two answers after byte 0 report "ranges unsupported" before their bodies are read.
        Assert.Equal(0, server.Bodies[1].BytesRead);
        Assert.Equal(0, server.Bodies[2].BytesRead);
    }

    [Fact]
    public async Task ServerWithoutRanges_FirstDownload_MakesThreeRequests()
    {
        var server = new FakeModelServer(ModelSyncFixtures.A) { ServesRanges = false };
        string output = PathIn("first.litertlm");
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(null, output, Source(server));
        Assert.Equal($"{Range(0, 16384)}:200 {Range(1184, 127840)}:200 {Range(0, 129024)}:200", server.Sequence());
        Assert.Equal(145408, result.BytesReceived);
        AssertBytes(ModelSyncFixtures.A, output);
    }

    [Fact]
    public async Task ServerWithoutRanges_HeaderOver16KiB_FailsThePlanAndTheUpdate()
    {
        var server = new FakeModelServer(ModelSyncFixtures.LongHeader) { ServesRanges = false };
        var pe = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.CreatePlanAsync(null, Source(server)));
        Assert.Equal(LiteRtStatusCode.Unimplemented, pe.StatusCode);
        Assert.Contains("remaining remote header bytes", pe.Message, StringComparison.Ordinal);

        string output = PathIn("long.litertlm");
        var ue = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(null, output, Source(server)));
        Assert.Equal(LiteRtStatusCode.Unimplemented, ue.StatusCode);
        Assert.False(File.Exists(output));
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task ReadRange_200AfterByteZero_ReportsRangesUnsupported_WithoutReadingTheBody()
    {
        var server = new FakeModelServer(ModelSyncFixtures.A) { ServesRanges = false };
        LiteRtModelSyncSource call = Source(server, TimeSpan.FromSeconds(60)).ForCall();
        using var destination = new MemoryStream();
        Assert.False(await call.ReadRangeAsync(100, 1000, destination, CancellationToken.None));
        Assert.Equal(0, destination.Length);
        Assert.Equal(0, server.Bodies[0].BytesRead);
    }

    // ---------------------------------------------------------------- HTTP failures

    public static TheoryData<int, LiteRtStatusCode> Statuses => new()
    {
        { 401, LiteRtStatusCode.Unauthenticated },
        { 403, LiteRtStatusCode.PermissionDenied },
        { 404, LiteRtStatusCode.NotFound },
        { 410, LiteRtStatusCode.NotFound },
        { 408, LiteRtStatusCode.Unavailable },
        { 429, LiteRtStatusCode.Unavailable },
        { 500, LiteRtStatusCode.Unavailable },
        { 502, LiteRtStatusCode.Unavailable },
        { 503, LiteRtStatusCode.Unavailable },
        // Never one of the statuses that make the runtime download the whole file after an error.
        { 400, LiteRtStatusCode.Unknown },
        { 405, LiteRtStatusCode.Unknown },
        { 416, LiteRtStatusCode.Unknown },
        { 302, LiteRtStatusCode.Unknown },
        { 204, LiteRtStatusCode.Unknown },
    };

    [Theory]
    [MemberData(nameof(Statuses))]
    public async Task ErrorStatus_OnASection_BecomesTheCallsStatus_WithoutAFallback(int status, LiteRtStatusCode expected)
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1)
        {
            Script = (index, _) => index == 1 ? new FakeHttpAction { Status = (HttpStatusCode)status } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, Source(server)));
        _output.WriteLine(e.Message);
        Assert.Equal(expected, e.StatusCode);
        var inner = Assert.IsType<HttpRequestException>(e.InnerException);
        Assert.Equal((HttpStatusCode)status, inner.StatusCode);
        Assert.Contains($"answered {status}", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Remote.Host, e.Message, StringComparison.Ordinal);
        // The hint about files the runtime cannot open is for the runtime's own PERMISSION_DENIED, not a 403.
        Assert.DoesNotContain("cannot create", e.Message, StringComparison.Ordinal);
        Assert.Equal(2, server.Requests.Count);
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task ErrorStatus_OnTheHeader_FailsThePlan()
    {
        var server = new FakeModelServer(ModelSyncFixtures.A) { Script = (_, _) => new FakeHttpAction { Status = HttpStatusCode.NotFound } };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.CreatePlanAsync(null, Source(server)));
        Assert.Equal(LiteRtStatusCode.NotFound, e.StatusCode);
        Assert.Contains("Failed to fetch initial remote header", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestWithoutAResponse_FailsWithUnavailable()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var failure = new HttpRequestException("No connection could be made.");
        var server = new FakeModelServer(ModelSyncFixtures.A1) { Script = (index, _) => index == 1 ? new FakeHttpAction { Throw = failure } : null };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, Source(server)));
        Assert.Equal(LiteRtStatusCode.Unavailable, e.StatusCode);
        Assert.Same(failure, e.InnerException);
        AssertBytes(ModelSyncFixtures.A, local);
    }

    // ---------------------------------------------------------------- the entity tag pin

    [Fact]
    public async Task ChangedStrongETag_MidCall_FailsWithAborted_AndLeavesTheFile()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1) { Script = (index, _) => index == 1 ? new FakeHttpAction { ETag = "\"v2\"" } : null };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, Source(server)));
        _output.WriteLine(e.Message);
        Assert.Equal(LiteRtStatusCode.Aborted, e.StatusCode);
        Assert.IsType<ModelSyncSourceException>(e.InnerException);
        Assert.Contains("changed during the sync", e.Message, StringComparison.Ordinal);
        Assert.Equal(2, server.Requests.Count);
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task ChangedStrongETag_OnARefused200_FailsWithAborted_InsteadOfAFullDownload()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1)
        {
            ServesRanges = false,
            Script = (index, _) => index == 1 ? new FakeHttpAction { ETag = "\"v2\"" } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, Source(server)));
        Assert.Equal(LiteRtStatusCode.Aborted, e.StatusCode);
        Assert.Equal(2, server.Requests.Count);
        AssertBytes(ModelSyncFixtures.A, local);
    }

    [Theory]
    [InlineData("W/\"v1\"", "W/\"v2\"")]
    [InlineData(null, null)]
    [InlineData("\"v1\"", "")]
    [InlineData("W/\"v1\"", "\"v2\"")]
    public async Task WeakOrMissingETag_DoesNotPin(string? first, string? second)
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1)
        {
            ETag = first,
            Script = (index, _) => index == 1 ? new FakeHttpAction { ETag = second ?? "" } : null,
        };
        await LiteRtModelSync.UpdateAsync(local, local, Source(server));
        AssertBytes(ModelSyncFixtures.A1, local);
    }

    [Fact]
    public async Task PinIsPerCall_APlanThenARemoteChangeThenAnUpdate_Works()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A);
        LiteRtModelSyncSource source = Source(server);
        Assert.True((await LiteRtModelSync.CreatePlanAsync(local, source)).IsUpToDate);

        server.Data = ModelSyncFixtures.A1;
        server.ETag = "\"v2\"";
        await LiteRtModelSync.UpdateAsync(local, local, source);
        AssertBytes(ModelSyncFixtures.A1, local);
        Assert.Null(Assert.IsType<ModelSyncHttpSource>(source).PinnedETag);
    }

    [Fact]
    public async Task TwoCallsAtOnce_OnOneSource_HaveTheirOwnPins()
    {
        string first = Put("first.litertlm", ModelSyncFixtures.A);
        string second = Put("second.litertlm", ModelSyncFixtures.A);
        var gate = new TaskCompletionSource();
        var server = new FakeModelServer(ModelSyncFixtures.A1);
        // The first call's section request waits; meanwhile the remote changes and a second call runs whole.
        server.Script = (index, _) => index == 1 ? new FakeHttpAction { HeaderGate = gate.Task } : null;
        LiteRtModelSyncSource source = Source(server);

        Task<LiteRtModelUpdateResult> held = LiteRtModelSync.UpdateAsync(first, first, source);
        await server.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        server.Data = ModelSyncFixtures.B;
        server.ETag = "\"v2\"";
        await LiteRtModelSync.UpdateAsync(second, second, source);
        AssertBytes(ModelSyncFixtures.B, second);

        gate.SetResult();
        var e = await Assert.ThrowsAsync<LiteRtException>(() => held.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(LiteRtStatusCode.Aborted, e.StatusCode);
        AssertBytes(ModelSyncFixtures.A, first);
    }

    [Fact]
    public async Task TwoFirstDownloadsAtOnce_OnOneSource()
    {
        var server = new FakeModelServer(ModelSyncFixtures.A);
        LiteRtModelSyncSource source = Source(server);
        string a = PathIn("a.litertlm"), b = PathIn("b.litertlm");
        await Task.WhenAll(LiteRtModelSync.UpdateAsync(null, a, source), LiteRtModelSync.UpdateAsync(null, b, source));
        AssertBytes(ModelSyncFixtures.A, a);
        AssertBytes(ModelSyncFixtures.A, b);
        Assert.Equal(4, server.Requests.Count);
    }

    // ---------------------------------------------------------------- timeouts and cancel

    [Fact]
    public async Task StalledBody_HitsTheIdleTimeout_AndFailsWithDeadlineExceeded()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1) { Script = (index, _) => index == 1 ? new FakeHttpAction { StallAfter = 1000 } : null };
        var e = await Assert.ThrowsAsync<LiteRtException>(
            () => LiteRtModelSync.UpdateAsync(local, local, Source(server, TimeSpan.FromSeconds(2))).WaitAsync(TimeSpan.FromSeconds(30)));
        _output.WriteLine(e.Message);
        Assert.Equal(LiteRtStatusCode.DeadlineExceeded, e.StatusCode);
        Assert.IsType<TimeoutException>(e.InnerException);
        Assert.Contains("sent no data for 2 seconds", e.Message, StringComparison.Ordinal);
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task StalledHeaders_HitTheIdleTimeout_AndFailWithDeadlineExceeded()
    {
        var gate = new TaskCompletionSource();
        var server = new FakeModelServer(ModelSyncFixtures.A) { Script = (_, _) => new FakeHttpAction { HeaderGate = gate.Task } };
        var e = await Assert.ThrowsAsync<LiteRtException>(
            () => LiteRtModelSync.CreatePlanAsync(null, Source(server, TimeSpan.FromSeconds(2))).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(LiteRtStatusCode.DeadlineExceeded, e.StatusCode);
        Assert.IsType<TimeoutException>(e.InnerException);
        Assert.Contains("sent no response", e.Message, StringComparison.Ordinal);
        gate.SetResult();
    }

    [Fact]
    public async Task ClientTimeout_OnTheHeaders_FailsWithDeadlineExceeded()
    {
        var gate = new TaskCompletionSource();
        var server = new FakeModelServer(ModelSyncFixtures.A) { Script = (_, _) => new FakeHttpAction { HeaderGate = gate.Task } };
        LiteRtModelSyncSource source = LiteRtModelSyncSource.FromHttp(Remote, ClientFor(server, TimeSpan.FromMilliseconds(300)));
        var e = await Assert.ThrowsAsync<LiteRtException>(
            () => LiteRtModelSync.CreatePlanAsync(null, source).WaitAsync(TimeSpan.FromSeconds(30)));
        _output.WriteLine(e.Message);
        Assert.Equal(LiteRtStatusCode.DeadlineExceeded, e.StatusCode);
        Assert.IsAssignableFrom<OperationCanceledException>(e.InnerException);
        gate.SetResult();
    }

    [Fact]
    public async Task Cancel_MidBody_EndsTheCall_AndLeavesTheFile()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1) { Script = (index, _) => index == 1 ? new FakeHttpAction { StallAfter = 1000 } : null };
        using var cts = new CancellationTokenSource();
        Task<LiteRtModelUpdateResult> update = LiteRtModelSync.UpdateAsync(local, local, Source(server), cancellationToken: cts.Token);
        await server.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(cts.Token, e.CancellationToken);
        Assert.Equal(LiteRtStatusCode.Cancelled, Assert.IsType<LiteRtException>(e.InnerException).StatusCode);
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    // ---------------------------------------------------------------- the caller's client

    [Fact]
    public async Task CallersClient_IsNeverDisposed()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var server = new FakeModelServer(ModelSyncFixtures.A1);
        HttpClient client = ClientFor(server);
        LiteRtModelSyncSource source = LiteRtModelSyncSource.FromHttp(Remote, client);
        await LiteRtModelSync.UpdateAsync(local, local, source);
        server.Script = (_, _) => new FakeHttpAction { Status = HttpStatusCode.NotFound };
        await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.CreatePlanAsync(local, source));

        Assert.False(server.Disposed);
        server.Script = null;
        using HttpResponseMessage response = await client.GetAsync(Remote);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
