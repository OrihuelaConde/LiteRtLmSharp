using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// <see cref="LiteRtModelSyncSource.FromHttp"/> against a real server, opt-in only: set
/// <c>LITERTLM_TEST_MODELSYNC_URL</c> to the address of a <c>.litertlm</c> file, preferably one pinned to a revision
/// (for example a Hugging Face <c>resolve/COMMIT/FILE</c> address). The test downloads the whole file into a
/// temporary folder, so it needs free space equal to twice the file's size. No CI job sets the variable.
/// </summary>
public sealed class ModelSyncNetworkTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("litertlm-net-").FullName;

    public void Dispose() => ModelSyncAsserts.DeleteFolder(_dir);

    [SkippableFact]
    public async Task Plan_Sync_ThenPlanAgain()
    {
        string? address = Environment.GetEnvironmentVariable("LITERTLM_TEST_MODELSYNC_URL");
        Skip.If(string.IsNullOrEmpty(address), "Set LITERTLM_TEST_MODELSYNC_URL to the address of a .litertlm file to run.");
        LiteRtModelSyncSource source = LiteRtModelSyncSource.FromHttp(new Uri(address!));

        LiteRtModelSyncPlan first = await LiteRtModelSync.CreatePlanAsync(null, source);
        output.WriteLine($"no local file: {first}");
        Assert.False(first.IsUpToDate);
        Assert.Equal(first.TotalRemoteSize, first.BytesToDownload);

        string path = Path.Combine(_dir, "model.litertlm");
        LiteRtModelUpdateResult result = await LiteRtModelSync.UpdateAsync(path, path, source);
        Assert.Equal(first.TotalRemoteSize, new FileInfo(path).Length);
        Assert.True(result.BytesReceived >= first.TotalRemoteSize);
        ModelSyncAsserts.NoTemporaries(_dir);

        LiteRtModelSyncPlan second = await LiteRtModelSync.CreatePlanAsync(path, source);
        output.WriteLine($"after the sync: {second}");
        Assert.Equal(first.TotalRemoteSize, second.TotalRemoteSize);
        // Only a file published with section hashes can be up to date: without them, nothing is reused.
        if (second.BytesToReuse > 0)
            Assert.True(second.IsUpToDate);
        else
            Assert.False(second.IsUpToDate);
    }
}
