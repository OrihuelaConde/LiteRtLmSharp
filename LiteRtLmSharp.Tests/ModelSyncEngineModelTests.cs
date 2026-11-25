using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The live-engine guard of <see cref="LiteRtModelSync.UpdateAsync"/> with real engines: a loaded model file counts
/// as held until the native engine is deleted, on Dispose and on finalization, and an update that would replace it
/// is refused before any fetch; what the runtime does when the guard is bypassed; and the compiled cache after an
/// in-place update. Needs <c>LITERTLM_TEST_MODEL</c> (a chat model) and, for the embedding cases,
/// <c>LITERTLM_TEST_EMBEDDING_MODEL</c>; <c>LITERTLM_TEST_BACKEND</c> picks CPU or GPU. Every update writes a copy
/// under a temporary folder, or uses a source that fails before the runtime writes anything. The runtime outcomes
/// were measured on LiteRT-LM v0.20.0, win-x64.
/// </summary>
public sealed class ModelSyncEngineModelTests(ITestOutputHelper output)
{
    private static string? ChatModel => Environment.GetEnvironmentVariable("LITERTLM_TEST_MODEL");
    private static string? EmbeddingModel => Environment.GetEnvironmentVariable("LITERTLM_TEST_EMBEDDING_MODEL");

    private static LiteRtBackend Backend =>
        string.Equals(Environment.GetEnvironmentVariable("LITERTLM_TEST_BACKEND"), "gpu", StringComparison.OrdinalIgnoreCase)
            ? LiteRtBackend.Gpu
            : LiteRtBackend.Cpu;

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
        using (LiteRtEngine engine = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = ChatModel!, Backend = Backend }))
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
        LoadAndDrop(() => LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = ChatModel!, Backend = Backend }), full, before);
        WaitForCount(full, before);
    }

    [SkippableFact]
    public async Task EmbeddingEngine_HoldsItsModelFile_UntilDisposed()
    {
        SkipWithout(EmbeddingModel, "LITERTLM_TEST_EMBEDDING_MODEL");
        string full = Path.GetFullPath(EmbeddingModel!);
        int before = LoadedModelPaths.Default.Count(full);
        var source = FailingSource();
        using (LiteRtEmbeddingEngine engine = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions { ModelPath = EmbeddingModel!, Backend = Backend }))
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
        LoadAndDrop(() => LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions { ModelPath = EmbeddingModel!, Backend = Backend }), full, before);
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

    /// <summary>With the guard bypassed by a direct native call, an in-place update of a file that a live engine
    /// holds downloads the whole file and then fails to replace it with <c>INTERNAL</c> on Windows; the old file,
    /// the engine and the folder are left as they were. Uses the small LoRA test bundle next to
    /// <c>LITERTLM_TEST_MODEL</c>, on CPU, because it does not load on GPU.</summary>
    [SkippableFact]
    public void HeldModelFile_WithoutTheGuard_FailsAtTheRename_OnWindows()
    {
        SkipWithout(ChatModel, "LITERTLM_TEST_MODEL");
        Skip.IfNot(OperatingSystem.IsWindows(), "Measured on Windows only; the guard stays on every platform.");
        string toy = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ChatModel!))!, "test_lm_lora.litertlm");
        Skip.If(!File.Exists(toy), "Place test_lm_lora.litertlm next to LITERTLM_TEST_MODEL to run.");
        string dir = Directory.CreateTempSubdirectory("litertlm-held-").FullName;
        try
        {
            byte[] original = File.ReadAllBytes(toy);
            byte[] next = (byte[])original.Clone();
            next[8000] ^= 0x5A; // Padding between the header and the first section: the file still loads.
            string held = Path.Combine(dir, "held.litertlm");
            File.WriteAllBytes(held, original);
            string remote = Path.Combine(dir, "next.litertlm");
            File.WriteAllBytes(remote, next);
            using (LiteRtEngine engine = LiteRtEngine.Load(new LiteRtEngineOptions { ModelPath = held, Backend = LiteRtBackend.Cpu }))
            {
                var source = new ModelSyncFileSource(remote);
                (LiteRtStatusCode status, string? message) = UpdateWithoutTheGuard(held, source);
                output.WriteLine($"[{status}] {message}");
                Assert.Equal(LiteRtStatusCode.Internal, status);
                Assert.Contains("Failed to rename temporary model to destination", message, StringComparison.Ordinal);
                Assert.Equal(2, source.Requests.Count); // The header, then the whole file after it.
                Assert.True(File.ReadAllBytes(held).AsSpan().SequenceEqual(original));
                ModelSyncAsserts.NoTemporaries(dir);
                using LiteRtConversation conversation = engine.CreateConversation(new LiteRtConversationOptions { MaxOutputTokens = 8 });
                Assert.False(string.IsNullOrEmpty(conversation.Send("Say hello.").Text));
            }
            LiteRtModelSync.UpdateAsync(held, held, new ModelSyncFileSource(remote)).GetAwaiter().GetResult();
            Assert.True(File.ReadAllBytes(held).AsSpan().SequenceEqual(next));
        }
        finally
        {
            ModelSyncAsserts.DeleteFolder(dir);
        }
    }

    /// <summary>When a process loaded the file before an in-place update, an embedding engine that it loads from the
    /// path afterwards with the default cache computes with the old model's compiled cache (measured on Windows
    /// only, so asserted there only); with the cache off, or after the cache files next to the model are deleted,
    /// it computes with the new model. If the Windows assertion fails, the runtime no longer reuses the old cache,
    /// and the remarks of <see cref="LiteRtModelSync.UpdateAsync"/> need the new result.</summary>
    [SkippableFact]
    public async Task EmbeddingEngine_AfterAnInPlaceUpdate_UsesTheOldCompiledCache_UntilItsFilesAreDeleted()
    {
        SkipWithout(EmbeddingModel, "LITERTLM_TEST_EMBEDDING_MODEL");
        string dir = Directory.CreateTempSubdirectory("litertlm-cache-").FullName;
        try
        {
            string model = Path.Combine(dir, "e.litertlm");
            File.Copy(EmbeddingModel!, model);
            File.SetLastWriteTimeUtc(model, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            string remote = Path.Combine(dir, "next.litertlm");
            byte[] next = File.ReadAllBytes(EmbeddingModel!);
            // 64 bytes inside the weights of EmbeddingGemma 2 (270M): the file still loads and embeds differently.
            for (int k = 0; k < 64; k++)
                next[130_000_000 + k] ^= 0x40;
            File.WriteAllBytes(remote, next);
            next = null!;

            float[] Embed(string path, LiteRtCache cache)
            {
                using var engine = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions { ModelPath = path, Backend = Backend, Cache = cache });
                return engine.Embed("The quick brown fox jumps over the lazy dog.");
            }

            float[] before = Embed(model, LiteRtCache.Default);
            float[] expected = Embed(remote, LiteRtCache.Disabled);
            Assert.False(before.AsSpan().SequenceEqual(expected), "The changed weights must change the embedding.");

            await LiteRtModelSync.UpdateAsync(model, model, new ModelSyncFileSource(remote));

            float[] stale = Embed(model, LiteRtCache.Default);
            output.WriteLine($"default cache after the update equals the old model: {stale.AsSpan().SequenceEqual(before)}");
            if (OperatingSystem.IsWindows())
                Assert.True(stale.AsSpan().SequenceEqual(before),
                    "The runtime no longer reuses the old compiled cache in the same process.");
            Assert.True(Embed(model, LiteRtCache.Disabled).AsSpan().SequenceEqual(expected));
            string[] cacheFiles = [.. Directory.GetFiles(dir, "e.litertlm?*").Where(f => Path.GetFileName(f) != "e.litertlm")];
            output.WriteLine("cache files: " + string.Join(", ", cacheFiles.Select(Path.GetFileName)));
            Assert.NotEmpty(cacheFiles);
            foreach (string file in cacheFiles)
                File.Delete(file);
            Assert.True(Embed(model, LiteRtCache.Default).AsSpan().SequenceEqual(expected));
        }
        finally
        {
            ModelSyncAsserts.DeleteFolder(dir);
        }
    }

    /// <summary>The native update without the managed guard, on a thread of its own, as the binding calls it.</summary>
    private static (LiteRtStatusCode Status, string? Message) UpdateWithoutTheGuard(string path, LiteRtModelSyncSource source) =>
        Task.Factory.StartNew(() => UpdateWithoutTheGuardCore(path, source), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();

    private static unsafe (LiteRtStatusCode Status, string? Message) UpdateWithoutTheGuardCore(string path, LiteRtModelSyncSource source)
    {
        var call = new ModelSyncCall(source.ForCall(), progress: null, CancellationToken.None);
        byte[] bytes = NarrowPath.EncodeForModelSync(path, nameof(path));
        LiteRtStatusCode status = LiteRtStatusCode.Unknown;
        nint user = call.Begin();
        try
        {
            fixed (byte* p = bytes)
                status = LiteRtLmNative.litert_lm_model_sync_update(p, p, ModelSyncCall.Fetcher, user, 0);
        }
        finally
        {
            call.End(status);
        }
        string? message = Marshal.PtrToStringUTF8(LiteRtLmNative.litert_lm_get_last_error_message());
        NativeError.Clear();
        return (status, message);
    }
}
