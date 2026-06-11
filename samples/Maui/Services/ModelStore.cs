namespace LiteRtLmSharp.SampleMaui.Services;

/// <summary>
/// Manages model files on local storage: download (with progress + resume), delete, lookup.
/// Models live under <c>{AppDataDirectory}/models/</c>; in-flight downloads use a
/// <c>.partial</c> suffix and are renamed only when complete, so an interrupted download
/// never looks like a usable model and can be resumed with an HTTP Range request.
/// </summary>
public sealed class ModelStore
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string ModelsDirectory { get; } = Path.Combine(FileSystem.AppDataDirectory, "models");

    public string GetLocalPath(ModelInfo model) => Path.Combine(ModelsDirectory, model.FileName);

    private string GetPartialPath(ModelInfo model) => GetLocalPath(model) + ".partial";

    public bool IsDownloaded(ModelInfo model) => File.Exists(GetLocalPath(model));

    public long? GetDownloadedBytes(ModelInfo model)
        => IsDownloaded(model) ? new FileInfo(GetLocalPath(model)).Length : null;

    public bool HasPartialDownload(ModelInfo model) => File.Exists(GetPartialPath(model));

    public void Delete(ModelInfo model)
    {
        if (!Directory.Exists(ModelsDirectory))
            return;
        // The runtime drops cache files next to the model (XNNPack flags, GPU mldrift weights —
        // the latter can be as large as the model itself). They are all named
        // "<model file name><suffix>", as is our ".partial", so one sweep removes everything.
        foreach (string file in Directory.GetFiles(ModelsDirectory, model.FileName + "*"))
            File.Delete(file);
    }

    /// <summary>
    /// Downloads (or resumes) the model. Progress reports (downloadedBytes, totalBytes);
    /// totalBytes is -1 when the server doesn't say.
    /// </summary>
    public async Task DownloadAsync(ModelInfo model, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelsDirectory);
        string partial = GetPartialPath(model);
        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
        if (existing > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // Server ignored the Range request → start over.
        if (existing > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            existing = 0;
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength is { } len ? existing + len : -1;

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(partial, existing > 0 ? FileMode.Append : FileMode.Create,
                                              FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buffer = new byte[1 << 16];
            long done = existing;
            long lastReport = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (done - lastReport > 4 << 20) // report every 4 MB
                {
                    progress.Report((done, total));
                    lastReport = done;
                }
            }
            progress.Report((done, total));
        }

        File.Move(partial, GetLocalPath(model), overwrite: true);
    }
}
