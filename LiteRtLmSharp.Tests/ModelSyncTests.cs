using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// <see cref="LiteRtModelSync"/> on generated containers served from memory, without a model: plans, first
/// downloads, differential and in-place updates, a remote that cannot serve ranges, the source failures and their
/// statuses, the contract the bridge enforces on its sources, cancel, progress, the refusals before any native call,
/// and the per-call handles. The request sequences and byte counts were measured on LiteRT-LM v0.20.0, win-x64.
/// </summary>
public sealed class ModelSyncTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public ModelSyncTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Directory.CreateTempSubdirectory("litertlm-sync-").FullName;
    }

    public void Dispose()
    {
        ModelSyncAsserts.DeleteFolder(_dir);
    }

    private string Put(string name, byte[] data)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string PathIn(string name) => Path.Combine(_dir, name);

    private static string Sequence(ModelSyncMemorySource source) =>
        string.Join(" ", source.Requests.Select(r => $"[{r.Offset},+{r.Length}]{(r.Outcome == "served" ? "" : ":" + r.Outcome)}"));

    private static void AssertBytes(byte[] expected, string path) => Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(expected), path);

    // ---------------------------------------------------------------- plans

    [Theory]
    [InlineData("A", "A", true, 0, 80896, 129024)]
    [InlineData("A", "A1", false, 9376, 72704, 129024)]
    [InlineData("A", "B", false, 21664, 72704, 145408)]
    [InlineData("A", "M", false, 129024, 0, 129024)]
    [InlineData("H", "H", false, 129024, 0, 129024)]
    [InlineData("A", "H", false, 129024, 0, 129024)]
    [InlineData("H", "A", false, 129024, 0, 129024)]
    [InlineData("Small", "Small1", false, 2912, 800, 3716)]
    [InlineData(null, "A", false, 129024, 0, 129024)]
    public async Task CreatePlanAsync_ReportsTheRuntimesNumbers(
        string? local, string remote, bool upToDate, long download, long reuse, long total)
    {
        string? path = local is null ? null : Put("local.litertlm", Fixture(local));
        var source = new ModelSyncMemorySource(Fixture(remote));
        LiteRtModelSyncPlan plan = await LiteRtModelSync.CreatePlanAsync(path, source);
        _output.WriteLine(plan.ToString());
        Assert.Equal(upToDate, plan.IsUpToDate);
        Assert.Equal(download, plan.BytesToDownload);
        Assert.Equal(reuse, plan.BytesToReuse);
        Assert.Equal(total, plan.TotalRemoteSize);
        Assert.Equal("[0,+16384]", Sequence(source));
        Assert.False(string.IsNullOrWhiteSpace(plan.ToString()));
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task CreatePlanAsync_LongHeader_FetchesTheRestOfTheHeader()
    {
        string local = Put("l.litertlm", ModelSyncFixtures.LongHeader);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.LongHeader1);
        LiteRtModelSyncPlan plan = await LiteRtModelSync.CreatePlanAsync(local, source);
        Assert.Equal(29344, plan.BytesToDownload);
        Assert.Equal(72704, plan.BytesToReuse);
        Assert.Equal("[0,+16384] [16384,+4768]", Sequence(source));
    }

    [Fact]
    public async Task CreatePlanAsync_EmptyLocalPath_IsNoLocalFile()
    {
        var plan = await LiteRtModelSync.CreatePlanAsync("", new ModelSyncMemorySource(ModelSyncFixtures.A));
        Assert.False(plan.IsUpToDate);
        Assert.Equal(0, plan.BytesToReuse);
    }

    private static byte[] Fixture(string name) => name switch
    {
        "A" => ModelSyncFixtures.A,
        "A1" => ModelSyncFixtures.A1,
        "B" => ModelSyncFixtures.B,
        "M" => ModelSyncFixtures.M,
        "H" => ModelSyncFixtures.H,
        "Small" => ModelSyncFixtures.Small,
        "Small1" => ModelSyncFixtures.Small1,
        "LongHeader" => ModelSyncFixtures.LongHeader,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    // ---------------------------------------------------------------- updates

    [Theory]
    [InlineData("A")]
    [InlineData("Small")]
    [InlineData("LongHeader")]
    public async Task UpdateAsync_FirstDownload_WritesTheRemoteFile(string name)
    {
        byte[] remote = Fixture(name);
        string output = PathIn("first.litertlm");
        var source = new ModelSyncMemorySource(remote);
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(null, output, source);
        _output.WriteLine(Sequence(source));
        AssertBytes(remote, output);
        Assert.Equal(ModelSyncFixtures.FirstDownloadBytes(remote), result.BytesReceived);
        // The header, then the rest of the file after it.
        if (name == "A")
            Assert.Equal("[0,+16384] [1184,+127840]", Sequence(source));
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task UpdateAsync_SamePathTwice_DownloadsWhenTheFileIsMissing()
    {
        string path = PathIn("model.litertlm");
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(path, path, source);
        AssertBytes(ModelSyncFixtures.A, path);
        Assert.Equal(144224, result.BytesReceived);
    }

    [Fact]
    public async Task UpdateAsync_FromAFileSource_WritesTheRemoteFile()
    {
        string remote = Put("remote.bin", ModelSyncFixtures.A);
        string output = PathIn("out.litertlm");
        var source = new ModelSyncFileSource(remote);
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(null, output, source);
        AssertBytes(ModelSyncFixtures.A, output);
        Assert.Equal(144224, result.BytesReceived);
        Assert.Equal(2, source.Requests.Count);
    }

    [Fact]
    public async Task UpdateAsync_Differential_FetchesTheHeaderAndTheChangedSectionOnly()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        string output = PathIn("a1.litertlm");
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1);
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(local, output, source);
        Assert.Equal("[0,+16384] [81920,+8192]", Sequence(source));
        Assert.Equal(16384 + 8192, result.BytesReceived);
        AssertBytes(ModelSyncFixtures.A1, output);
        AssertBytes(ModelSyncFixtures.A, local);

        var inPlace = new ModelSyncMemorySource(ModelSyncFixtures.B);
        await LiteRtModelSync.UpdateAsync(local, local, inPlace);
        Assert.Equal("[0,+16384] [81920,+20480]", Sequence(inPlace));
        AssertBytes(ModelSyncFixtures.B, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task UpdateAsync_InPlaceUpToDate_TouchesNothing()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        DateTime before = File.GetLastWriteTimeUtc(local);
        await Task.Delay(50);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(local, local, source);
        Assert.Equal("[0,+16384]", Sequence(source));
        Assert.Equal(16384, result.BytesReceived);
        Assert.Equal(before, File.GetLastWriteTimeUtc(local));
        AssertBytes(ModelSyncFixtures.A, local);
        Assert.Single(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task UpdateAsync_InPlaceUnderASpellingThatFullPathsMakeEqual_IsInPlace()
    {
        // The runtime compares the path strings; an up-to-date update to another spelling of the local file
        // fails at the copy (measured), so the binding passes the same bytes for both.
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        string other = Path.Combine(_dir, ".", "a.litertlm");
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
        await LiteRtModelSync.UpdateAsync(local, other, source);
        Assert.Equal("[0,+16384]", Sequence(source));
        AssertBytes(ModelSyncFixtures.A, local);
    }

    [Fact]
    public async Task UpdateAsync_UpToDateToAnotherPath_CopiesTheLocalFile()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        string output = PathIn("copy.litertlm");
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
        await LiteRtModelSync.UpdateAsync(local, output, source);
        Assert.Equal("[0,+16384]", Sequence(source));
        AssertBytes(ModelSyncFixtures.A, output);
    }

    // ---------------------------------------------------------------- a remote without range support

    [Fact]
    public async Task UpdateAsync_RemoteWithoutRanges_Differential_FallsBackToTheWholeFileFromByteZero()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1) { ServesRanges = false };
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(local, local, source);
        Assert.Equal("[0,+16384] [81920,+8192]:refused [1184,+127840]:refused [0,+129024]", Sequence(source));
        Assert.Equal(16384 + 129024, result.BytesReceived);
        AssertBytes(ModelSyncFixtures.A1, local);
    }

    [Fact]
    public async Task UpdateAsync_RemoteWithoutRanges_FirstDownload_MakesThreeRequests()
    {
        string output = PathIn("first.litertlm");
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A) { ServesRanges = false };
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(null, output, source);
        Assert.Equal("[0,+16384] [1184,+127840]:refused [0,+129024]", Sequence(source));
        Assert.Equal(145408, result.BytesReceived);
        AssertBytes(ModelSyncFixtures.A, output);
    }

    [Fact]
    public async Task RemoteWithoutRanges_LongHeader_FailsWithUnimplemented()
    {
        string output = PathIn("long.litertlm");
        var source = new ModelSyncMemorySource(ModelSyncFixtures.LongHeader) { ServesRanges = false };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(null, output, source));
        Assert.Equal(LiteRtStatusCode.Unimplemented, e.StatusCode);
        Assert.Equal("[0,+16384] [16384,+4768]:refused", Sequence(source));
        Assert.False(File.Exists(output));

        var plan = new ModelSyncMemorySource(ModelSyncFixtures.LongHeader) { ServesRanges = false };
        var pe = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.CreatePlanAsync(null, plan));
        Assert.Equal(LiteRtStatusCode.Unimplemented, pe.StatusCode);
    }

    // ---------------------------------------------------------------- source failures

    public static TheoryData<string, LiteRtStatusCode> SourceFailures => new()
    {
        { "httpNoStatus", LiteRtStatusCode.Unavailable },
        { "timeout", LiteRtStatusCode.DeadlineExceeded },
        { "otherCancel", LiteRtStatusCode.DeadlineExceeded },
        { "io", LiteRtStatusCode.Unavailable },
        { "changed", LiteRtStatusCode.Aborted },
        { "contentRange", LiteRtStatusCode.DataLoss },
        { "other", LiteRtStatusCode.Unknown },
    };

    private static Exception FailureFor(string name) => name switch
    {
        "httpNoStatus" => new HttpRequestException("name not resolved"),
        "timeout" => new TimeoutException("no byte for 60 seconds"),
        "otherCancel" => new OperationCanceledException("not the caller's token"),
        "io" => new IOException("the body ended early"),
        "changed" => new ModelSyncSourceException(LiteRtStatusCode.Aborted, "The remote file changed during the sync."),
        "contentRange" => new ModelSyncSourceException(LiteRtStatusCode.DataLoss, "The response starts at another offset."),
        _ => new FormatException("unexpected"),
    };

    [Theory]
    [MemberData(nameof(SourceFailures))]
    public async Task SourceFailure_OnASection_BecomesTheStatus_WithTheSourceExceptionInside(string name, LiteRtStatusCode expected)
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        Exception failure = FailureFor(name);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { Throw = failure, WriteBeforeThrow = 1000 } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, source));
        _output.WriteLine(e.Message);
        Assert.Equal(expected, e.StatusCode);
        Assert.Same(failure, e.InnerException);
        Assert.Contains("Failed to fetch section payload: " + failure.Message.TrimEnd('.'), e.Message, StringComparison.Ordinal);
        // No fallback and no retry for a failure: the header and the section.
        Assert.Equal(2, source.Requests.Count);
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task SourceFailure_OnTheHeader_FailsThePlan()
    {
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A) { Script = (_, _, _) => new SourceAction { Throw = new IOException("down") } };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.CreatePlanAsync(null, source));
        Assert.Equal(LiteRtStatusCode.Unavailable, e.StatusCode);
        Assert.Contains("Failed to fetch initial remote header: down", e.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(e.InnerException);
    }

    [Fact]
    public async Task SectionWithTheWrongBytes_FailsTheRuntimesHashCheck()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { OtherData = ModelSyncFixtures.A } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, source));
        Assert.Equal(LiteRtStatusCode.DataLoss, e.StatusCode);
        Assert.Contains("SHA-256", e.Message, StringComparison.Ordinal);
        // The binding's hint, after the runtime's reason.
        Assert.Contains("may have changed during the call", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("may not be a .litertlm file", e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteThatIsNotAContainer_FailsWithInvalidArgument_AndTheHint(bool plan)
    {
        // Measured: "INVALID_ARGUMENT: Remote file does not start with LITERTLM magic bytes".
        var source = new ModelSyncMemorySource(ModelSyncFixtures.Payload(77, 129024));
        string output = PathIn("out.litertlm");
        var e = await Assert.ThrowsAsync<LiteRtException>(() =>
            plan ? LiteRtModelSync.CreatePlanAsync(null, source) : LiteRtModelSync.UpdateAsync(null, output, source));
        _output.WriteLine(e.Message);
        Assert.Equal(LiteRtStatusCode.InvalidArgument, e.StatusCode);
        Assert.Contains("LITERTLM magic", e.Message, StringComparison.Ordinal);
        Assert.Contains("may not be a .litertlm file", e.Message, StringComparison.Ordinal);
        Assert.Equal("[0,+16384]", Sequence(source));
        Assert.False(File.Exists(output));
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    /// <summary>A folder the runtime cannot create the temporary file in: on Windows a deny rule for creating
    /// files, elsewhere a folder without write permission. The hands-on measurement was made on win-x64; CI runs the
    /// test on linux-x64, linux-arm64, and osx-arm64 too.</summary>
    [SkippableFact]
    public async Task OutputFolderWithoutWriteAccess_FailsWithPermissionDenied_AndTheHint()
    {
        string folder = Directory.CreateDirectory(PathIn("nowrite")).FullName;
        using IDisposable restore = ModelSyncFolderAccess.DenyFileCreation(folder);
        bool blocked;
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "check.bin"), [1]);
            blocked = false;
        }
        catch (UnauthorizedAccessException)
        {
            blocked = true;
        }
        Skip.IfNot(blocked, "This user can create files in a folder without write permission (for example root).");

        var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(null, Path.Combine(folder, "o.litertlm"), source));
        _output.WriteLine(e.Message);
        Assert.Equal(LiteRtStatusCode.PermissionDenied, e.StatusCode);
        Assert.Contains("cannot create", e.Message, StringComparison.Ordinal);
        Assert.Contains("writable", e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
    }

    // ---------------------------------------------------------------- the contract the bridge enforces

    [Fact]
    public async Task WholeFileWritePastTheLength_FailsWithDataLoss_AndWritesNoOutput()
    {
        // The runtime itself accepts a whole-file write past the length and leaves a longer, corrupt file (measured).
        string output = PathIn("out.litertlm");
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A)
        {
            Script = (_, offset, _) => offset == ModelSyncFixtures.HeaderEnd(ModelSyncFixtures.A) ? new SourceAction { ExtraBytes = 100 } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(null, output, source));
        Assert.Equal(LiteRtStatusCode.DataLoss, e.StatusCode);
        Assert.Contains("more bytes than the requested range", e.Message, StringComparison.Ordinal);
        Assert.Equal("[0,+16384] [1184,+127840]:InvalidOperationException", Sequence(source));
        Assert.False(File.Exists(output));
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task SectionOrHeaderWritePastTheLength_FailsWithDataLoss()
    {
        // The header half fails only because of the binding's check: the runtime accepts extra header bytes. For a
        // section, the runtime's own size check returns the same status, so that half does not tell the two apart.
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var section = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { ExtraBytes = 100 } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, section));
        Assert.Equal(LiteRtStatusCode.DataLoss, e.StatusCode);
        AssertBytes(ModelSyncFixtures.A, local);

        var header = new ModelSyncMemorySource(ModelSyncFixtures.A) { Script = (index, _, _) => index == 0 ? new SourceAction { ExtraBytes = 10 } : null };
        var he = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.CreatePlanAsync(null, header));
        Assert.Equal(LiteRtStatusCode.DataLoss, he.StatusCode);
    }

    [Fact]
    public async Task FalseAfterWriting_OrAtByteZero_FailsWithUnknown()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var afterWrite = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { WriteBeforeRefusal = 4096 } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, afterWrite));
        Assert.Equal(LiteRtStatusCode.Unknown, e.StatusCode);
        Assert.Equal(2, afterWrite.Requests.Count);

        var atZero = new ModelSyncMemorySource(ModelSyncFixtures.A) { Script = (index, _, _) => index == 0 ? new SourceAction { Refuse = true } : null };
        var ze = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.CreatePlanAsync(null, atZero));
        Assert.Equal(LiteRtStatusCode.Unknown, ze.StatusCode);
        AssertBytes(ModelSyncFixtures.A, local);
    }

    [Fact]
    public async Task ShortWrite_AfterTheFirstFetch_FailsWithDataLoss()
    {
        // Only the first fetch of a call, the header fetch, may end short. The bridge fails a later fetch that
        // writes fewer bytes than asked: a section, the rest of a long header, and the whole file of a remote
        // without section hashes, which the runtime would otherwise keep truncated.
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { MissingBytes = 100 } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, source));
        Assert.Equal(LiteRtStatusCode.DataLoss, e.StatusCode);
        Assert.Contains("fewer bytes than the range the runtime asked for: 8092 of 8192 bytes from byte 81920", e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
        AssertBytes(ModelSyncFixtures.A, local);

        byte[] noHashes = ModelSyncFixtures.H;
        long headerEnd = ModelSyncFixtures.HeaderEnd(noHashes);
        string output = PathIn("h.litertlm");
        var whole = new ModelSyncMemorySource(noHashes)
        {
            Script = (_, offset, _) => offset == headerEnd ? new SourceAction { MissingBytes = 100 } : null,
        };
        var we = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(null, output, whole));
        _output.WriteLine(we.Message);
        Assert.Equal(LiteRtStatusCode.DataLoss, we.StatusCode);
        Assert.Contains("Failed to fetch full model payload", we.Message, StringComparison.Ordinal);
        Assert.Contains("fewer bytes than the range the runtime asked for", we.Message, StringComparison.Ordinal);
        Assert.Equal($"[0,+16384] [{headerEnd},+{noHashes.Length - headerEnd}]", Sequence(whole));
        Assert.False(File.Exists(output));

        var header = new ModelSyncMemorySource(ModelSyncFixtures.LongHeader)
        {
            Script = (index, _, _) => index == 1 ? new SourceAction { MissingBytes = 100 } : null,
        };
        var he = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.CreatePlanAsync(null, header));
        Assert.Equal(LiteRtStatusCode.DataLoss, he.StatusCode);
        Assert.Contains("remaining remote header bytes", he.Message, StringComparison.Ordinal);
        Assert.Equal("[0,+16384] [16384,+4768]", Sequence(header));
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    // ---------------------------------------------------------------- cancel

    [Fact]
    public async Task Cancel_BeforeTheCall_FetchesNothing()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LiteRtModelSync.UpdateAsync(local, local, source, cancellationToken: new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LiteRtModelSync.CreatePlanAsync(local, source, new CancellationToken(true)));
        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task Cancel_WhileTheSourceWaits_EndsTheCall_AndLeavesTheOriginal()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var gate = new TaskCompletionSource();
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { Gate = gate.Task } : null,
        };
        using var cts = new CancellationTokenSource();
        Task<LiteRtModelUpdateResult> update = LiteRtModelSync.UpdateAsync(local, local, source, cancellationToken: cts.Token);
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The runtime creates the temporary file after the header fetch, so the cancel comes mid-download.
        Assert.True(File.Exists(local + ".tmp_updating"));
        cts.Cancel();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(cts.Token, e.CancellationToken);
        var inner = Assert.IsType<LiteRtException>(e.InnerException);
        Assert.Equal(LiteRtStatusCode.Cancelled, inner.StatusCode);
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task Cancel_WithASourceThatIgnoresItsToken_EndsTheCall_AndRefusesTheLateWrite()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var gate = new TaskCompletionSource();
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { Gate = gate.Task, IgnoreToken = true } : null,
        };
        using var cts = new CancellationTokenSource();
        Task<LiteRtModelUpdateResult> update = LiteRtModelSync.UpdateAsync(local, local, source, cancellationToken: cts.Token);
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(30)));

        gate.SetResult();
        await source.IgnoredTokenRequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsType<ObjectDisposedException>(Assert.Single(source.LateWriteFailures));
        AssertBytes(ModelSyncFixtures.A, local);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    [Fact]
    public async Task Cancel_AfterTheLastFetch_ReturnsTheResult()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress(total =>
        {
            if (total == 16384 + 8192)
                cts.Cancel();
        });
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(
            local, local, new ModelSyncMemorySource(ModelSyncFixtures.A1), new LiteRtModelSyncOptions { Progress = progress }, cts.Token);
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(16384 + 8192, result.BytesReceived);
        AssertBytes(ModelSyncFixtures.A1, local);
    }

    // ---------------------------------------------------------------- progress

    /// <summary>A receiver that runs synchronously, unlike <see cref="Progress{T}"/>, which posts.</summary>
    private sealed class RecordingProgress(Action<long>? onReport = null) : IProgress<long>
    {
        public List<(long Total, int Thread, bool Pool)> Reports { get; } = [];

        public void Report(long value)
        {
            Reports.Add((value, Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
            onReport?.Invoke(value);
        }
    }

    [Fact]
    public async Task Progress_IsMonotonic_OnTheCallingThread_AndEndsAtBytesReceived()
    {
        byte[] big = ModelSyncFixtures.Build(
            [ModelSyncFixtures.Payload(51, 1024), ModelSyncFixtures.Payload(52, 3 * 1024 * 1024), ModelSyncFixtures.Payload(53, 4096)],
            dataTypes: [5, 3, 3], modelTypes: [null, "tf_lite_prefill_decode", "tf_lite_embedder"]);
        string output = PathIn("big.litertlm");
        var progress = new RecordingProgress();
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(
            null, output, new ModelSyncMemorySource(big), new LiteRtModelSyncOptions { Progress = progress });
        _output.WriteLine(string.Join(", ", progress.Reports.Select(r => r.Total)));
        Assert.True(progress.Reports.Count >= 4);
        Assert.All(progress.Reports.Zip(progress.Reports.Skip(1)), pair => Assert.True(pair.First.Total < pair.Second.Total));
        Assert.Equal(result.BytesReceived, progress.Reports[^1].Total);
        Assert.Single(progress.Reports.Select(r => r.Thread).Distinct());
        Assert.DoesNotContain(progress.Reports, r => r.Pool);
        AssertBytes(big, output);
    }

    [Fact]
    public async Task Progress_ThatThrows_FailsWithUnknown()
    {
        var failure = new InvalidOperationException("receiver failed");
        var progress = new RecordingProgress(_ => throw failure);
        string output = PathIn("out.litertlm");
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(
            null, output, new ModelSyncMemorySource(ModelSyncFixtures.A), new LiteRtModelSyncOptions { Progress = progress }));
        Assert.Equal(LiteRtStatusCode.Unknown, e.StatusCode);
        Assert.Same(failure, e.InnerException);
        Assert.False(File.Exists(output));
    }

    // ---------------------------------------------------------------- refusals before any native call

    [Fact]
    public async Task Refusals_ComeBeforeAnyFetch()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        string folder = Directory.CreateDirectory(PathIn("folder")).FullName;
        string missing = PathIn("missing.litertlm");
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1);

        await Assert.ThrowsAsync<ArgumentNullException>(() => LiteRtModelSync.UpdateAsync(local, local, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => LiteRtModelSync.CreatePlanAsync(local, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => LiteRtModelSync.UpdateAsync(local, null!, source));
        await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.UpdateAsync(local, "", source));
        var e = await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.UpdateAsync(missing, PathIn("out.litertlm"), source));
        Assert.Equal("localModelPath", e.ParamName);
        e = await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.CreatePlanAsync(missing, source));
        Assert.Equal("localModelPath", e.ParamName);
        e = await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.CreatePlanAsync(folder, source));
        Assert.Equal("localModelPath", e.ParamName);
        e = await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.UpdateAsync(local, folder, source));
        Assert.Equal("outputModelPath", e.ParamName);
        e = await Assert.ThrowsAsync<ArgumentException>(
            () => LiteRtModelSync.UpdateAsync(local, Path.Combine(_dir, "no-such-folder", "out.litertlm"), source));
        Assert.Equal("outputModelPath", e.ParamName);
        Assert.False(Directory.Exists(PathIn("no-such-folder")));

        Assert.Empty(source.Requests);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [SkippableFact]
    public async Task Refusals_ALocalFileThatCannotBeOpened()
    {
        // FileShare.None blocks other opens only on Windows.
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only: an exclusive open blocks readers there.");
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
        using (new FileStream(local, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var e = await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.CreatePlanAsync(local, source));
            Assert.Equal("localModelPath", e.ParamName);
        }
        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task Refusals_AnOutputALiveEngineHolds()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
        string key = Native.LoadedModelPaths.Default.Add(local);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiteRtModelSync.UpdateAsync(null, local, source));
            Assert.Empty(source.Requests);
            // Reading the file is allowed: a plan, or an update to another path.
            Assert.True((await LiteRtModelSync.CreatePlanAsync(local, source)).IsUpToDate);
            await LiteRtModelSync.UpdateAsync(local, PathIn("next.litertlm"), new ModelSyncMemorySource(ModelSyncFixtures.A1));
            AssertBytes(ModelSyncFixtures.A1, PathIn("next.litertlm"));
        }
        finally
        {
            Native.LoadedModelPaths.Default.Release(key);
        }
        await LiteRtModelSync.UpdateAsync(local, local, new ModelSyncMemorySource(ModelSyncFixtures.A1));
        AssertBytes(ModelSyncFixtures.A1, local);
    }

    [Fact]
    public async Task Refusals_FilesAnotherCallIsWriting()
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        var gate = new TaskCompletionSource();
        var slow = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { Gate = gate.Task } : null,
        };
        Task<LiteRtModelUpdateResult> first = LiteRtModelSync.UpdateAsync(local, local, slow);
        try
        {
            await slow.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var source = new ModelSyncMemorySource(ModelSyncFixtures.A1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiteRtModelSync.UpdateAsync(null, local, source));
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiteRtModelSync.CreatePlanAsync(local, source));
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiteRtModelSync.UpdateAsync(local, PathIn("other.litertlm"), source));
            Assert.Empty(source.Requests);
        }
        finally
        {
            gate.TrySetResult();
        }
        await first.WaitAsync(TimeSpan.FromSeconds(30));
        AssertBytes(ModelSyncFixtures.A1, local);
        Assert.True((await LiteRtModelSync.CreatePlanAsync(local, new ModelSyncMemorySource(ModelSyncFixtures.A1))).IsUpToDate);
    }

    // ---------------------------------------------------------------- threads and handles

    [Fact]
    public async Task Callbacks_RunOnTheCallingThread_AndNoneAfterTheReturn()
    {
        var reports = new List<ModelSyncCallReport>();
        // The hooks are process-wide: keep a receiver that is already there, and put both back afterwards.
        Action<ModelSyncCallReport>? previous = ModelSyncCall.Completed;
        TimeSpan previousWindow = ModelSyncCall.LateCallbackWindow;
        ModelSyncCall.Completed = r =>
        {
            lock (reports) reports.Add(r);
            previous?.Invoke(r);
        };
        // Keeps each call's handle valid after the return, so a late fetcher call would be counted.
        ModelSyncCall.LateCallbackWindow = TimeSpan.FromMilliseconds(300);
        try
        {
            string local = Put("a.litertlm", ModelSyncFixtures.A);
            await LiteRtModelSync.CreatePlanAsync(local, new ModelSyncMemorySource(ModelSyncFixtures.A1));
            await LiteRtModelSync.UpdateAsync(local, local, new ModelSyncMemorySource(ModelSyncFixtures.A1));
            await LiteRtModelSync.UpdateAsync(null, PathIn("first.litertlm"), new ModelSyncMemorySource(ModelSyncFixtures.LongHeader));
            await LiteRtModelSync.UpdateAsync(local, local, new ModelSyncMemorySource(ModelSyncFixtures.B) { ServesRanges = false });

            // A cancel during a fetch whose source ignores its token.
            var gate = new TaskCompletionSource();
            var stalled = new ModelSyncMemorySource(ModelSyncFixtures.A1)
            {
                Script = (_, offset, _) => offset == 81920 ? new SourceAction { Gate = gate.Task, IgnoreToken = true } : null,
            };
            using var cts = new CancellationTokenSource();
            Task<LiteRtModelUpdateResult> update = LiteRtModelSync.UpdateAsync(PathIn("first.litertlm"), PathIn("next.litertlm"), stalled, cancellationToken: cts.Token);
            await stalled.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(30)));
            gate.SetResult();
            await stalled.IgnoredTokenRequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            ModelSyncCall.Completed = previous;
            ModelSyncCall.LateCallbackWindow = previousWindow;
        }
        Assert.Equal(5, reports.Count);
        Assert.All(reports, r =>
        {
            Assert.True(r.Fetches > 0);
            Assert.Equal(0, r.OffThreadFetches);
            Assert.Equal(0, r.LateFetches);
        });
        Assert.Equal(0, ModelSyncCall.LiveHandles);
    }

    [Fact]
    public async Task RepeatedPlans_LeaveTheManagedHandleCountAtZero()
    {
        var source = new ModelSyncMemorySource(ModelSyncFixtures.Small);
        for (int i = 0; i < 3; i++)
            await LiteRtModelSync.CreatePlanAsync(null, source);
        Assert.Equal(3, source.Requests.Count);
        Assert.Equal(0, ModelSyncCall.LiveHandles);
    }

    [Fact]
    public async Task ConcurrentCalls_OnDifferentFiles_AndOnOneSource()
    {
        string a = Put("a.litertlm", ModelSyncFixtures.A);
        string b = Put("b.litertlm", ModelSyncFixtures.A);
        await Task.WhenAll(
            LiteRtModelSync.UpdateAsync(a, a, new ModelSyncMemorySource(ModelSyncFixtures.A1)),
            LiteRtModelSync.UpdateAsync(b, b, new ModelSyncMemorySource(ModelSyncFixtures.B)));
        AssertBytes(ModelSyncFixtures.A1, a);
        AssertBytes(ModelSyncFixtures.B, b);

        var shared = new ModelSyncMemorySource(ModelSyncFixtures.A);
        LiteRtModelSyncPlan[] plans = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => LiteRtModelSync.CreatePlanAsync(null, shared)));
        Assert.All(plans, p => Assert.Equal(129024, p.TotalRemoteSize));
        Assert.Equal(8, shared.Requests.Count);
    }

    // ---------------------------------------------------------------- leftovers, fallbacks and free space

    /// <summary>A process that ends during an update leaves its temporary file (measured by ending a child
    /// process on Windows). The next update of that output overwrites it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeftoverTemporary_IsOverwrittenByTheNextUpdate(bool inPlace)
    {
        string output = inPlace ? Put("a.litertlm", ModelSyncFixtures.A) : PathIn("new.litertlm");
        File.WriteAllBytes(output + ".tmp_updating", ModelSyncFixtures.Payload(31, 70_000));
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1);
        await LiteRtModelSync.UpdateAsync(inPlace ? output : null, output, source);
        AssertBytes(ModelSyncFixtures.A1, output);
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    /// <summary>A source that refuses every range after the header, the whole file from byte 0 included, fails
    /// the update without a temporary file and leaves the local file.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteThatRefusesEveryRangeAfterTheHeader_FailsWithUnknown_AndLeavesNoTemporary(bool differential)
    {
        string local = Put("a.litertlm", ModelSyncFixtures.A);
        string output = differential ? local : PathIn("new.litertlm");
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (index, _, _) => index > 0 ? new SourceAction { Refuse = true } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(differential ? local : null, output, source));
        _output.WriteLine($"[{e.StatusCode}] {e.Message}");
        Assert.Equal(LiteRtStatusCode.Unknown, e.StatusCode);
        Assert.Contains("could not serve the start of the file", e.Message, StringComparison.Ordinal);
        Assert.Equal(
            differential
                ? "[0,+16384] [81920,+8192]:refused [1184,+127840]:refused [0,+129024]:refused"
                : "[0,+16384] [1184,+127840]:refused [0,+129024]:refused",
            Sequence(source));
        AssertBytes(ModelSyncFixtures.A, local);
        Assert.Equal(differential, File.Exists(output));
        ModelSyncAsserts.NoTemporaries(_dir);
    }

    /// <summary>A remote whose header declares 40 TiB fails the runtime's free-space check after the header fetch,
    /// before anything is written.</summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteLargerThanTheFreeSpace_FailsWithResourceExhausted_BeforeWriting(bool inPlace)
    {
        byte[] huge = (byte[])ModelSyncFixtures.A.Clone();
        long headerEnd = ModelSyncFixtures.HeaderEnd(huge);
        int[] ends = [.. Enumerable.Range(32, (int)headerEnd - 39)
            .Where(i => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(huge.AsSpan(i, 8)) == (ulong)huge.Length)];
        Assert.Single(ends);
        const long declared = 40L * 1024 * 1024 * 1024 * 1024;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(huge.AsSpan(ends[0], 8), declared);
        Skip.If(new DriveInfo(Path.GetPathRoot(_dir)!).AvailableFreeSpace >= declared, "The volume has 40 TiB free.");

        string output = inPlace ? Put("a.litertlm", ModelSyncFixtures.A) : PathIn("big.litertlm");
        var source = new ModelSyncMemorySource(huge);
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(inPlace ? output : null, output, source));
        _output.WriteLine($"[{e.StatusCode}] {e.Message}");
        Assert.Equal(LiteRtStatusCode.ResourceExhausted, e.StatusCode);
        Assert.Contains("free space at least equal to the remote file's size", e.Message, StringComparison.Ordinal);
        Assert.Equal("[0,+16384]", Sequence(source));
        Assert.Equal(inPlace, File.Exists(output));
        if (inPlace)
            AssertBytes(ModelSyncFixtures.A, output);
        ModelSyncAsserts.NoTemporaries(_dir);
    }
}
