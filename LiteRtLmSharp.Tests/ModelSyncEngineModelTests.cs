using System.Runtime.CompilerServices;
using LiteRtLmSharp.Native;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The live-engine guard of <see cref="LiteRtModelSync.UpdateAsync"/> with real engines: a loaded model file counts
/// as held until the native engine is deleted, on Dispose and on finalization, and an update that would replace it
/// is refused before any fetch. Needs <c>LITERTLM_TEST_MODEL</c> (a chat model) and, for the embedding cases,
/// <c>LITERTLM_TEST_EMBEDDING_MODEL</c>.
/// </summary>
public sealed class ModelSyncEngineModelTests
{
    private static string? ChatModel => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
    private static string? EmbeddingModel => Environment.GetEnvironmentVariable("LITERTLM_TEST_EMBEDDING_MODEL");

    private static void SkipWithout(string? model, string variable) =>
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), $"Set {variable} to a .litertlm model to run.");

    /// <summary>A source that fails its first request, so an update the guard fails to refuse ends at the header
    /// fetch and never replaces the model file the other tests load.</summary>
    private static ModelSyncMemorySource FailingSource() => new(ModelSyncFixtures.A)
    {
        Script = (index, _, _) => index == 0 ? new SourceAction { Throw = new IOException("This source never serves.") } : null,
    };

    [SkippableFact]
    public async Task ChatEngine_HoldsItsModelFile_UntilDisposed()
    {
        SkipWithout(ChatModel, "LITERTLM_TEST_MODEL");
        string full = Path.GetFullPath(ChatModel!);
        int before = LoadedModelPaths.Default.Count(full);
        var source = FailingSource();
        using (LiteRtEngine engine = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = ChatModel!, Backend = LiteRtBackend.Cpu }))
        {
            Assert.Equal(before + 1, LoadedModelPaths.Default.Count(full));
            DateTime written = File.GetLastWriteTimeUtc(full);
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiteRtModelSync.UpdateAsync(null, ChatModel!, source));
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiteRtModelSync.UpdateAsync(full, full, source));
            Assert.Empty(source.Requests);
            Assert.Equal(written, File.GetLastWriteTimeUtc(full));
        }
        Assert.Equal(before, LoadedModelPaths.Default.Count(full));
    }

    [SkippableFact]
    public void ChatEngine_ReleasesItsModelFile_OnFinalization()
    {
        SkipWithout(ChatModel, "LITERTLM_TEST_MODEL");
        string full = Path.GetFullPath(ChatModel!);
        int before = LoadedModelPaths.Default.Count(full);
        LoadAndDrop(() => LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = ChatModel!, Backend = LiteRtBackend.Cpu }), full, before);
        WaitForCount(full, before);
    }

    [SkippableFact]
    public async Task EmbeddingEngine_HoldsItsModelFile_UntilDisposed()
    {
        SkipWithout(EmbeddingModel, "LITERTLM_TEST_EMBEDDING_MODEL");
        string full = Path.GetFullPath(EmbeddingModel!);
        int before = LoadedModelPaths.Default.Count(full);
        var source = FailingSource();
        using (LiteRtEmbeddingEngine engine = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions { ModelPath = EmbeddingModel! }))
        {
            Assert.Equal(before + 1, LoadedModelPaths.Default.Count(full));
            await Assert.ThrowsAsync<InvalidOperationException>(() => LiteRtModelSync.UpdateAsync(null, EmbeddingModel!, source));
            Assert.Empty(source.Requests);
        }
        Assert.Equal(before, LoadedModelPaths.Default.Count(full));
    }

    [SkippableFact]
    public void EmbeddingEngine_ReleasesItsModelFile_OnFinalization()
    {
        SkipWithout(EmbeddingModel, "LITERTLM_TEST_EMBEDDING_MODEL");
        string full = Path.GetFullPath(EmbeddingModel!);
        int before = LoadedModelPaths.Default.Count(full);
        LoadAndDrop(() => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions { ModelPath = EmbeddingModel! }), full, before);
        WaitForCount(full, before);
    }

    // Not inlined, so no reference to the engine outlives the call.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadAndDrop(Func<object> load, string full, int before)
    {
        object engine = load();
        Assert.Equal(before + 1, LoadedModelPaths.Default.Count(full));
        GC.KeepAlive(engine);
    }

    private static void WaitForCount(string full, int expected)
    {
        for (int i = 0; i < 20 && LoadedModelPaths.Default.Count(full) != expected; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.Equal(expected, LoadedModelPaths.Default.Count(full));
    }
}
