using System.Text;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// Paths through <see cref="LiteRtModelSync"/>: non-ASCII folders, characters outside the Windows ANSI code page,
/// long paths and UNC paths. On Windows the expectations come from the active code page read at run time, never
/// assumed. The Windows behaviour of these calls was measured on LiteRT-LM v0.20.0, win-x64, code page 1252;
/// under code page 65001, only the runtime, called directly, was measured.
/// </summary>
public sealed class ModelSyncPathTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public ModelSyncPathTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Directory.CreateTempSubdirectory("litertlm-paths-").FullName;
    }

    public void Dispose()
    {
        ModelSyncAsserts.DeleteFolder(_dir);
    }

    /// <summary>Whether the runtime can open <paramref name="text"/> in a path: always off Windows and under the
    /// UTF-8 code page, else when the ANSI code page has every character. Decided with .NET's own code page
    /// tables, independently of the binding's encoder.</summary>
    private static bool Representable(string text)
    {
        if (NarrowPath.ActiveCodePage is not { } codePage || codePage == 65001)
            return true;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding ansi = Encoding.GetEncoding((int)codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            ansi.GetBytes(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData("José")]
    [InlineData("日本語")]
    [InlineData("emoji-\U0001F600")]
    public async Task NonAsciiFolder_WorksWhenTheCodePageHasItsCharacters_AndIsRefusedOtherwise(string name)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        string local = Path.Combine(folder, "a.litertlm");
        bool representable = Representable(folder);
        _output.WriteLine($"code page {NarrowPath.ActiveCodePage}, representable {representable}");
        if (!representable)
        {
            var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
            var e = await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.UpdateAsync(null, local, source));
            Assert.Contains($"({NarrowPath.ActiveCodePage})", e.Message, StringComparison.Ordinal);
            Assert.Equal("outputModelPath", e.ParamName);
            File.WriteAllBytes(local, ModelSyncFixtures.A);
            e = await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.CreatePlanAsync(local, source));
            Assert.Equal("localModelPath", e.ParamName);
            Assert.Empty(source.Requests);
            return;
        }

        var first = new ModelSyncMemorySource(ModelSyncFixtures.A);
        await LiteRtModelSync.UpdateAsync(null, local, first);
        Assert.True(File.ReadAllBytes(local).AsSpan().SequenceEqual(ModelSyncFixtures.A));
        // The plan opens the local file: up to date.
        Assert.True((await LiteRtModelSync.CreatePlanAsync(local, new ModelSyncMemorySource(ModelSyncFixtures.A))).IsUpToDate);
        var differential = new ModelSyncMemorySource(ModelSyncFixtures.A1);
        await LiteRtModelSync.UpdateAsync(local, local, differential);
        Assert.Equal(2, differential.Requests.Count);
        Assert.True(File.ReadAllBytes(local).AsSpan().SequenceEqual(ModelSyncFixtures.A1));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp_updating"));
    }

    [SkippableFact]
    public async Task NonAsciiPath_FailureMessage_SaysTheReasonCanShowThePathWrongly()
    {
        Skip.IfNot(NarrowPath.ActiveCodePage is { } cp && cp != 65001 && Representable("José"),
            "Needs Windows with an ANSI code page other than UTF-8 that has é.");
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "José")).FullName;
        string local = Path.Combine(folder, "a.litertlm");
        File.WriteAllBytes(local, ModelSyncFixtures.A);
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { OtherData = ModelSyncFixtures.A } : null,
        };
        var e = await Assert.ThrowsAsync<LiteRtException>(() => LiteRtModelSync.UpdateAsync(local, local, source));
        Assert.Contains("ANSI code page", e.Message, StringComparison.Ordinal);
    }

    /// <summary>A file path of exactly <paramref name="length"/> characters under the test folder.</summary>
    private string PathOfLength(string prefix, int length)
    {
        string baseDir = Path.Combine(_dir, prefix);
        const string file = "m.litertlm";
        int remaining = length - baseDir.Length - 2 - file.Length;
        Assert.True(remaining >= 1, "The temporary folder is too long for this test.");
        var parts = new List<string> { baseDir };
        while (remaining > 0)
        {
            if (remaining <= 100)
            {
                parts.Add(new string('d', remaining));
                break;
            }
            int n = remaining - 100 >= 2 ? 100 : remaining - 2;
            parts.Add(new string('d', n));
            remaining -= n + 1;
        }
        parts.Add(file);
        string path = Path.Combine([.. parts]);
        Assert.Equal(length, path.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>On Windows, plain paths of 259 characters work and of 260 or more fail in the runtime (measured);
    /// the binding passes the extended-length form from 260 on, so every length works.</summary>
    [Theory]
    [InlineData(258)]
    [InlineData(259)]
    [InlineData(260)]
    [InlineData(261)]
    [InlineData(280)]
    public async Task LongLocalPath_PlansAndUpdatesInPlace(int length)
    {
        string path = PathOfLength("L", length);
        File.WriteAllBytes(path, ModelSyncFixtures.A);
        Assert.True((await LiteRtModelSync.CreatePlanAsync(path, new ModelSyncMemorySource(ModelSyncFixtures.A))).IsUpToDate);
        await LiteRtModelSync.UpdateAsync(path, path, new ModelSyncMemorySource(ModelSyncFixtures.A1));
        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(ModelSyncFixtures.A1));
    }

    [Theory]
    [InlineData(259)]
    [InlineData(260)]
    public async Task OutputWhoseTemporaryReachesTheLimit_IsWritten(int temporaryLength)
    {
        string output = PathOfLength("T", temporaryLength - ".tmp_updating".Length);
        await LiteRtModelSync.UpdateAsync(null, output, new ModelSyncMemorySource(ModelSyncFixtures.A));
        Assert.True(File.ReadAllBytes(output).AsSpan().SequenceEqual(ModelSyncFixtures.A));
    }

    [SkippableFact]
    public async Task LongPathWithANonAsciiFolder_Works()
    {
        Skip.IfNot(Representable("José"), "The ANSI code page lacks é.");
        string path = PathOfLength("José", 280);
        File.WriteAllBytes(path, ModelSyncFixtures.A);
        Assert.True((await LiteRtModelSync.CreatePlanAsync(path, new ModelSyncMemorySource(ModelSyncFixtures.A))).IsUpToDate);
    }

    /// <summary>Through the loopback administrative share: a short UNC path works (measured); a call that needs
    /// extended-length paths refuses a UNC path, whose extended form was not measured.</summary>
    [SkippableFact]
    public async Task UncPaths_ShortWorks_LongIsRefused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        string root = Path.GetPathRoot(_dir)!;
        Skip.IfNot(root.Length == 3 && root[1] == ':', "The temporary folder is not on a drive letter.");
        string share = $@"\\localhost\{char.ToLowerInvariant(root[0])}$\";
        Skip.IfNot(Directory.Exists(share), "The administrative share is not reachable.");

        string shortUnc = share + Path.Combine(_dir, "unc.litertlm")[3..];
        await LiteRtModelSync.UpdateAsync(null, shortUnc, new ModelSyncMemorySource(ModelSyncFixtures.A));
        Assert.True(File.ReadAllBytes(Path.Combine(_dir, "unc.litertlm")).AsSpan().SequenceEqual(ModelSyncFixtures.A));

        string longFolder = Path.GetDirectoryName(PathOfLength("U", 265))!;
        string longUnc = share + Path.Combine(longFolder, "o.litertlm")[3..];
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.UpdateAsync(null, longUnc, source));
        Assert.Contains("UNC path", e.Message, StringComparison.Ordinal);
        Assert.Empty(source.Requests);
    }

    /// <summary>The extended-length spelling of a file is the same file: in place, for the live-engine guard and
    /// for the calls in flight. Given one spelling per path, the runtime fails an up-to-date update with INTERNAL
    /// "File exists", and two updates under the two spellings share one temporary file and fail (measured).</summary>
    [SkippableFact]
    public async Task ExtendedLengthSpelling_IsTheSameFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        string plain = Path.Combine(_dir, "a.litertlm");
        File.WriteAllBytes(plain, ModelSyncFixtures.A);
        string prefixed = @"\\?\" + plain;

        var upToDate = new ModelSyncMemorySource(ModelSyncFixtures.A);
        await LiteRtModelSync.UpdateAsync(prefixed, plain, upToDate);
        Assert.Equal(16384, Assert.Single(upToDate.Requests).Length);

        foreach (var (loaded, updated) in new[] { (plain, prefixed), (prefixed, plain) })
        {
            string key = LoadedModelPaths.Default.Add(loaded);
            var source = new ModelSyncMemorySource(ModelSyncFixtures.A1);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => LiteRtModelSync.UpdateAsync(null, updated, source));
            }
            finally
            {
                LoadedModelPaths.Default.Release(key);
            }
            Assert.Empty(source.Requests);
        }

        var gate = new TaskCompletionSource();
        var slow = new ModelSyncMemorySource(ModelSyncFixtures.A1)
        {
            Script = (_, offset, _) => offset == 81920 ? new SourceAction { Gate = gate.Task } : null,
        };
        Task<LiteRtModelUpdateResult> first = LiteRtModelSync.UpdateAsync(plain, plain, slow);
        try
        {
            await slow.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var second = new ModelSyncMemorySource(ModelSyncFixtures.A);
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiteRtModelSync.UpdateAsync(null, prefixed, second));
            Assert.Empty(second.Requests);
        }
        finally
        {
            gate.TrySetResult();
        }
        await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.ReadAllBytes(plain).AsSpan().SequenceEqual(ModelSyncFixtures.A1));
    }

    /// <summary>A path that only the extended form can name (a folder whose name ends in a dot) keeps its prefix
    /// and reads its own file.</summary>
    [SkippableFact]
    public async Task ExtendedLengthPath_ThatNoPlainPathNames_KeepsItsPrefix()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        string folder = @"\\?\" + Path.Combine(_dir, "x.");
        Directory.CreateDirectory(folder);
        string path = folder + @"\a.litertlm";
        File.WriteAllBytes(path, ModelSyncFixtures.A);
        try
        {
            Assert.True((await LiteRtModelSync.CreatePlanAsync(path, new ModelSyncMemorySource(ModelSyncFixtures.A))).IsUpToDate);
            var source = new ModelSyncMemorySource(ModelSyncFixtures.A1);
            await LiteRtModelSync.UpdateAsync(path, path, source);
            Assert.Equal([0L, 81920L], source.Requests.Select(r => r.Offset));
            Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(ModelSyncFixtures.A1));
        }
        finally
        {
            // The plain API cannot delete a folder whose name ends in a dot.
            File.Delete(path);
            Directory.Delete(folder);
        }
    }

    /// <summary>The device spelling (<c>\\.\</c>) of a local path of 270 characters: .NET does not find the
    /// file under that spelling (measured), so the binding passes the plain form, in its extended-length
    /// form.</summary>
    [SkippableFact]
    public async Task DeviceSpelling_OfALongLocalPath_ReadsTheFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        string path = PathOfLength("D", 270);
        File.WriteAllBytes(path, ModelSyncFixtures.A);
        Assert.True((await LiteRtModelSync.CreatePlanAsync(@"\\.\" + path, new ModelSyncMemorySource(ModelSyncFixtures.A))).IsUpToDate);
    }

    /// <summary>Spellings that differ only in case: one file in a folder that ignores case, two files in a
    /// case-sensitive folder. There, given the output's spelling as the local path, the runtime ignores the missing
    /// local file and downloads the whole file instead of reusing the local one (measured).</summary>
    [SkippableFact]
    public async Task CaseDifferingSpellings_AreOneFile_OnlyWhereTheFolderIgnoresCase()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "Paths are compared ordinally here.");
        string upper = Path.Combine(_dir, "Model.litertlm");
        File.WriteAllBytes(upper, ModelSyncFixtures.A);
        string lower = Path.Combine(_dir, "model.litertlm");
        Skip.IfNot(File.Exists(lower), "The temporary folder is case-sensitive.");
        var upToDate = new ModelSyncMemorySource(ModelSyncFixtures.A);
        await LiteRtModelSync.UpdateAsync(upper, lower, upToDate);
        Assert.Equal(16384, Assert.Single(upToDate.Requests).Length);

        Skip.IfNot(OperatingSystem.IsWindows(), "No case-sensitive folder can be made here.");
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "cs")).FullName;
        Skip.IfNot(MakeCaseSensitive(folder), "This machine does not let the user mark a folder case-sensitive.");
        string csUpper = Path.Combine(folder, "Model.litertlm");
        File.WriteAllBytes(csUpper, ModelSyncFixtures.A);
        string csLower = Path.Combine(folder, "model.litertlm");
        Assert.False(File.Exists(csLower));
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A1);
        await LiteRtModelSync.UpdateAsync(csUpper, csLower, source);
        // The local file was used: the header and the changed section only.
        Assert.Equal([0L, 81920L], source.Requests.Select(r => r.Offset));
        Assert.True(File.ReadAllBytes(csLower).AsSpan().SequenceEqual(ModelSyncFixtures.A1));
        Assert.True(File.ReadAllBytes(csUpper).AsSpan().SequenceEqual(ModelSyncFixtures.A));
    }

    private static bool MakeCaseSensitive(string folder)
    {
        var start = new System.Diagnostics.ProcessStartInfo("fsutil.exe", ["file", "setCaseSensitiveInfo", folder, "enable"])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>A UNC path in extended form that needs extended-length paths is refused like a plain UNC
    /// path.</summary>
    [SkippableFact]
    public async Task ExtendedUncPath_ThatNeedsExtendedLength_IsRefused()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only.");
        string root = Path.GetPathRoot(_dir)!;
        Skip.IfNot(root.Length == 3 && root[1] == ':', "The temporary folder is not on a drive letter.");
        string share = $@"\\localhost\{char.ToLowerInvariant(root[0])}$\";
        Skip.IfNot(Directory.Exists(share), "The administrative share is not reachable.");
        string longFolder = Path.GetDirectoryName(PathOfLength("V", 265))!;
        string unc = @"\\?\UNC" + share[1..] + Path.Combine(longFolder, "o.litertlm")[3..];
        var source = new ModelSyncMemorySource(ModelSyncFixtures.A);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => LiteRtModelSync.UpdateAsync(null, unc, source));
        Assert.Contains("UNC path", e.Message, StringComparison.Ordinal);
        Assert.Empty(source.Requests);
    }
}
