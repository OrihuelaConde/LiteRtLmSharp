using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>Downloads and updates <c>.litertlm</c> model files from a remote copy, reusing the sections that did
/// not change. Needs no engine.</summary>
/// <remarks>
/// <para>This API is experimental. Using it reports the diagnostic <c>LITERTLM003</c>, and its types and members
/// can change or be removed in a minor release.</para>
/// <para>The calls load no model and create no engine. The runtime compares the section hashes that the local and
/// the remote headers record. It copies the sections whose hashes match from the local file and downloads the
/// others; when more than 80% of the section bytes changed, or when either file has no section hashes, it
/// downloads the whole file. It verifies the SHA-256 of each downloaded section that the remote header hashes, so
/// the bytes of a remote without section hashes are not verified. Statements marked as measured were measured
/// on LiteRT-LM v0.20.0, win-x64.</para>
/// <para>Each call runs the native work on a thread of its own and completes only when the native call has
/// returned. The methods are safe to call from several threads. A call that writes a file that another call of
/// this process reads or writes is refused, and so is a call that reads a file another call writes (see the
/// exceptions); calls that only read one file run together. Other processes are not checked: two processes that
/// update the same output at the same time write the same temporary file, and one of them fails at the end with
/// <see cref="LiteRtStatusCode.Internal"/> (measured).</para>
/// <para>Paths on Windows: unless the process runs under the UTF-8 code page (65001), the runtime decodes paths in
/// the active ANSI code page (measured), so the binding refuses a path with characters outside that code page
/// before any native call. Under the UTF-8 code page, non-ASCII folder names work (measured with Latin, Japanese
/// and emoji names). When a
/// path of the call, or the temporary file next to the output, reaches 260 characters, the binding passes every
/// path of the call in its extended-length form (<c>\\?\</c>), because the runtime fails on plain paths of that
/// length (measured); a UNC path in such a call is refused. A path that starts with <c>\\?\</c> or <c>\\.\</c>
/// is compared and passed in its plain form when that form names the same file, so both spellings count as one
/// file; a device path that names no drive letter and no UNC share, such as a volume GUID path, is
/// refused.</para>
/// </remarks>
[Experimental(ExperimentalApis.ModelSync, UrlFormat = ExperimentalApis.ModelSyncUrl)]
public static class LiteRtModelSync
{
    private const double FullDownloadThreshold = 0.80;
    private const string TemporarySuffix = ".tmp_updating";
    private const int MaxPath = 260;

    static LiteRtModelSync() => NativeLibraryResolver.Initialize();

    /// <summary>Compares a local model file with the remote one and reports what an update would download and
    /// reuse, without writing anything.</summary>
    /// <param name="localModelPath">The local <c>.litertlm</c> file, or <c>null</c> (or empty) when there is
    /// none.</param>
    /// <param name="remote">The remote file.</param>
    /// <param name="cancellationToken">Stops the plan at its next fetch from <paramref name="remote"/>.</param>
    /// <returns>The plan.</returns>
    /// <remarks>
    /// <para>The runtime fetches the remote header (the first 16 KiB, then the rest of a longer header) and reads
    /// the local header. When the headers match, the result also depends on the local sections' bytes: a local
    /// section whose bytes do not match the hash its header records makes the file not up to date.</para>
    /// <para>Cost of files without section hashes: when the remote file or the local file has no section hashes,
    /// the plan reports the whole file as <see cref="LiteRtModelSyncPlan.BytesToDownload"/> and the local file is
    /// never up to date, even when it is identical to the remote file. An update then downloads the whole file
    /// every time. None of the <c>.litertlm</c> files checked on Hugging Face in November 2026 (Gemma 4 E2B and
    /// E4B, EmbeddingGemma) had section hashes. Check the plan before you update: with an
    /// existing local file, <see cref="LiteRtModelSyncPlan.BytesToReuse"/> equal to 0 means the update downloads
    /// everything.</para>
    /// <para>A remote that cannot serve byte ranges fails the plan with <see cref="LiteRtStatusCode.Unimplemented"/>
    /// when its header is longer than 16 KiB.</para>
    /// <para>Cancel: the token is checked before the call and at each fetch, and it interrupts a fetch that waits
    /// for the remote. The runtime calls no fetcher while it works on the local file, so a cancel during that
    /// work takes effect at the next fetch, or not at all when none follows. When both files have section hashes
    /// and their headers match, that work checks the local sections after the only fetch: about 3.3 seconds for
    /// a 1 GiB file that the operating system had cached, on a desktop computer (measured). A plan of a 1 GiB
    /// file without section hashes ended about 1 millisecond after that fetch (measured).</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="remote"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="localModelPath"/> does not exist, is a folder or cannot
    /// be opened for reading, or a path cannot be passed to the runtime (see the remarks of
    /// <see cref="LiteRtModelSync"/>). The runtime would otherwise ignore the local file and plan a full
    /// download.</exception>
    /// <exception cref="InvalidOperationException">An update of this process is writing
    /// <paramref name="localModelPath"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the
    /// call, or the call failed while the token was cancelled, whatever the cause of the failure. The
    /// <see cref="LiteRtException"/> of the failure is the <see cref="Exception.InnerException"/>.</exception>
    /// <exception cref="LiteRtException">The runtime could not make the plan, for example because a fetch failed
    /// or the remote is not a <c>.litertlm</c> file; <see cref="LiteRtException.StatusCode"/> says why. Also
    /// thrown when the native library's C API version does not match this binding.</exception>
    public static async Task<LiteRtModelSyncPlan> CreatePlanAsync(
        string? localModelPath, LiteRtModelSyncSource remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        string? local = NormalizeLocal(localModelPath);
        if (local is not null)
            CheckReadableLocal(local, nameof(localModelPath));
        CallPaths paths = CallPaths.Encode(local, output: null, inPlace: false);
        using ModelSyncInFlight.Lease lease = ModelSyncInFlight.Enter(reader: local, writer: null);
        cancellationToken.ThrowIfCancellationRequested();
        NativeLibraryResolver.EnsureCompatible();

        var call = new ModelSyncCall(remote.ForCall(), progress: null, cancellationToken);
        string failure = local is null
            ? "litert_lm_model_sync_plan_create_from_file failed (no local file)."
            : $"litert_lm_model_sync_plan_create_from_file failed for '{local}'.";
        return await RunDedicated(() => Plan(call, paths, failure, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Writes the remote model to <paramref name="outputModelPath"/>, reusing the unchanged sections of
    /// <paramref name="localModelPath"/>, and replaces the file atomically when both paths name the same
    /// file.</summary>
    /// <param name="localModelPath">The local <c>.litertlm</c> file to reuse sections from, or <c>null</c> (or
    /// empty) for a first download. It can be the same path as <paramref name="outputModelPath"/>, which then
    /// does not have to exist yet.</param>
    /// <param name="outputModelPath">The file to write. Its folder must exist.</param>
    /// <param name="remote">The remote file.</param>
    /// <param name="options">The progress receiver, or <c>null</c>.</param>
    /// <param name="cancellationToken">Stops the update at its next fetch from <paramref name="remote"/>.</param>
    /// <returns>What the update received.</returns>
    /// <remarks>
    /// <para>Replacing the file: the runtime writes the new file to <c>OUTPUT.tmp_updating</c> next to the output
    /// and renames it over the output at the end; the C API describes an update of one path as atomic. On a failed
    /// fetch, on bytes the binding refuses, and on a cancel during a fetch, the runtime deletes the temporary file
    /// and leaves the old output (measured). When the process ends during an update, the temporary file stays
    /// next to the output and the old output is intact; the next update of that output overwrites the temporary
    /// file (measured). When the local file is already up to date and is the output, nothing is written; when the
    /// output is another file, the runtime copies the local file to it.</para>
    /// <para>Disk space: the temporary file is a full copy of the remote file, and it sits next to the old output
    /// until the rename. The binding turns on the runtime's check of free space before the download: when the
    /// output's volume has less free space than the remote file's size, the update fails with
    /// <see cref="LiteRtStatusCode.ResourceExhausted"/> after it reads the remote header and before it writes
    /// anything (measured). A volume that fills up during the download is not measured.</para>
    /// <para>Cost of files without section hashes: when the remote file or the local file has no section hashes,
    /// nothing is reused and the update downloads the whole file, even when the local file is identical to the
    /// remote file. Calling this method at every start then downloads the whole model every time. None of the
    /// <c>.litertlm</c> files checked on Hugging Face in November 2026 (Gemma 4 E2B and E4B, EmbeddingGemma) had
    /// section hashes. Call <see cref="CreatePlanAsync"/> first, and update when the plan shows sections to reuse
    /// or when the user accepts <see cref="LiteRtModelSyncPlan.BytesToDownload"/>.</para>
    /// <para>In place: pass the same string for both paths to update a file in place. The runtime compares the path
    /// strings, and paths that <see cref="Path.GetFullPath(string)"/> makes equal count as the same, with or without
    /// a <c>\\?\</c> prefix. On Windows and macOS, paths that differ only in case count as the same when the folder
    /// ignores case; in a case-sensitive folder they name two files. Another spelling of the file, such as a hard
    /// link or a short 8.3 name, does not count as the same.</para>
    /// <para>Engines: the call refuses an output that a live engine of this process loaded, and checks that only
    /// when it starts. Without that check, on Windows the runtime downloads what the update needs, writes the
    /// temporary file, and then fails to replace the output with <see cref="LiteRtStatusCode.Internal"/>, and the
    /// engine keeps working (measured with a <see cref="LiteRtEngine"/> on CPU and a
    /// <see cref="LiteRtEmbeddingEngine"/> on CPU and GPU, on files without section hashes, so the whole file was
    /// downloaded; Linux and macOS are not measured). An engine that loads the output while the update runs is
    /// not detected, so do not load the output until the update completes. A <see cref="LiteRtModelInfo.Read"/>
    /// of the output that runs when the runtime replaces it makes the update fail the same way, and the read can
    /// fail with <see cref="LiteRtStatusCode.Internal"/> too (measured), so do not read the output during an
    /// update. An update that writes another path can read a local file that a live engine holds (measured with
    /// a <see cref="LiteRtEmbeddingEngine"/> on CPU and GPU).</para>
    /// <para>Compiled cache: when a process loaded the file and the file is then replaced, by an in-place update or
    /// by moving the update's output over it, an engine that the same process loads from that path afterwards with
    /// <see cref="LiteRtCache.Default"/> can use the compiled cache of the old file and compute with the old model.
    /// A <see cref="LiteRtEmbeddingEngine"/> on CPU and on GPU returned the old model's embeddings after both kinds
    /// of replacement (measured on Windows). Before such a process loads the file again, delete the cache files
    /// next to it, whose names start with the model file's name and end with <c>xnnpack_cache</c> or
    /// <c>_cache.bin</c>, or load it with <see cref="LiteRtCache.Disabled"/>, or with
    /// <see cref="LiteRtCache.Directory(string)"/> and a folder that holds no cache of the file (measured after a
    /// move). A process that had not loaded the file before an in-place update, whether it ran the update or not,
    /// built a new cache and computed with the new model; on GPU it left the old cache files next to the
    /// model.</para>
    /// <para>Cancel: the token is checked before the call and at each fetch, and it interrupts a fetch that waits
    /// for the remote or copies its bytes. The runtime's own work between fetches, such as reading the local file,
    /// copying reused sections or renaming the temporary file, has no callback, so a cancel during that work takes
    /// effect at the next fetch, or not at all when none follows. An update of a local file that is already up to
    /// date reads the whole file after the only fetch: about 3.3 seconds for a 1 GiB file that the operating
    /// system had cached, on a desktop computer (measured). When the runtime completes the update before it
    /// sees the cancel, the method returns the result instead of throwing, because the output was
    /// replaced.</para>
    /// <para>Failures: a failed fetch becomes the call's status: <see cref="LiteRtStatusCode.Unavailable"/> for a network
    /// failure or a server error, <see cref="LiteRtStatusCode.NotFound"/>,
    /// <see cref="LiteRtStatusCode.Unauthenticated"/> or <see cref="LiteRtStatusCode.PermissionDenied"/> for the
    /// matching HTTP status, <see cref="LiteRtStatusCode.DeadlineExceeded"/> for a timeout,
    /// <see cref="LiteRtStatusCode.Aborted"/> when the remote file changed during the call, and
    /// <see cref="LiteRtStatusCode.DataLoss"/> when the remote returned other bytes than the ones asked for. The
    /// runtime does not retry a failed fetch. A remote that cannot serve byte ranges makes the runtime download the
    /// whole file from byte 0: before that, an update that reuses sections makes two requests the remote cannot
    /// serve (the changed section, then the file after its header), and a first download makes one. Such a remote
    /// fails the update with <see cref="LiteRtStatusCode.Unimplemented"/> when its header is longer than
    /// 16 KiB.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="outputModelPath"/> or <paramref name="remote"/> is
    /// <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="outputModelPath"/> is empty, is a folder, or its folder
    /// does not exist; <paramref name="localModelPath"/> is another file that does not exist, is a folder, or
    /// cannot be opened for reading; or a path cannot be passed to the runtime (see the remarks of
    /// <see cref="LiteRtModelSync"/>). The runtime would otherwise ignore a missing local file and download
    /// everything, or fail after the download.</exception>
    /// <exception cref="InvalidOperationException">Another model sync call of this process reads or writes
    /// <paramref name="outputModelPath"/> or writes <paramref name="localModelPath"/>, or a live
    /// <see cref="LiteRtEngine"/> or <see cref="LiteRtEmbeddingEngine"/> of this process loaded
    /// <paramref name="outputModelPath"/>. An engine stays alive until it and every conversation and session
    /// created from it are disposed. Write the update to another path, dispose the engine with its conversations
    /// and sessions, then replace the file; before this process loads the file again, delete its compiled cache
    /// files (see the remarks). Engines that loaded the file under another spelling, such as a hard link or a
    /// short 8.3 name, are not detected.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the
    /// call, or the call failed while the token was cancelled, whatever the cause of the failure. The
    /// <see cref="LiteRtException"/> of the failure is the <see cref="Exception.InnerException"/>.</exception>
    /// <exception cref="LiteRtException">The runtime could not complete the update; <see cref="LiteRtException.StatusCode"/>
    /// says why, and <see cref="Exception.InnerException"/> holds the source's exception when a fetch failed with
    /// one. Also thrown when the native library's C API version does not match this binding.</exception>
    public static async Task<LiteRtModelUpdateResult> UpdateAsync(
        string? localModelPath, string outputModelPath, LiteRtModelSyncSource remote,
        LiteRtModelSyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputModelPath);
        ArgumentNullException.ThrowIfNull(remote);
        string output = ModelFilePath.Normalize(outputModelPath, nameof(outputModelPath));
        string? local = NormalizeLocal(localModelPath);
        bool inPlace = local is not null && ModelFilePath.SameFile(local, output);
        if (local is not null && string.Equals(local, output, StringComparison.Ordinal)
            && !File.Exists(local) && !Directory.Exists(local))
        {
            // A first download into the path that later updates will use in place. The runtime gets no local
            // file, the case that was measured.
            local = null;
            inPlace = false;
        }
        if (local is not null)
            CheckReadableLocal(local, nameof(localModelPath));
        CheckOutput(output);
        CallPaths paths = CallPaths.Encode(local, output, inPlace);
        if (LoadedModelPaths.Default.Count(output) > 0)
            throw new InvalidOperationException(
                $"'{output}' is the model file of a LiteRtEngine or LiteRtEmbeddingEngine that is still alive in this " +
                "process, and replacing it could fail after the download. An engine stays alive until it and " +
                "every conversation and session created from it are disposed. Write the update to another path, " +
                "dispose the engine with its conversations and sessions, then replace the file. Before this process " +
                "loads the file again, delete its compiled cache files, or the engine can compute with the old model.");
        using ModelSyncInFlight.Lease lease = ModelSyncInFlight.Enter(reader: local, writer: output);
        cancellationToken.ThrowIfCancellationRequested();
        NativeLibraryResolver.EnsureCompatible();

        var call = new ModelSyncCall(remote.ForCall(), options?.Progress, cancellationToken);
        string failure = $"litert_lm_model_sync_update failed for '{output}'.";
        return await RunDedicated(() => Update(call, paths, failure, cancellationToken)).ConfigureAwait(false);
    }

    private static Task<T> RunDedicated<T>(Func<T> body) =>
        Task.Factory.StartNew(
            body, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private static unsafe LiteRtModelSyncPlan Plan(
        ModelSyncCall call, CallPaths paths, string failure, CancellationToken cancellationToken)
    {
        using ModelSyncOptionsHandle options = CreateOptions();
        nint planPtr = nint.Zero;
        LiteRtStatusCode status = LiteRtStatusCode.Unknown;
        nint user = call.Begin();
        try
        {
            fixed (byte* local = paths.Local)
                status = LiteRtLmNative.litert_lm_model_sync_plan_create_from_file(
                    local, ModelSyncCall.Fetcher, user, options.Ptr, &planPtr);
        }
        finally
        {
            call.End(status);
        }
        using var plan = new ModelSyncPlanHandle(planPtr);
        // The thread's message belongs to this call: read it here, on the thread that made the call.
        if (status != LiteRtStatusCode.Ok)
            throw Failure(call, status, failure, paths, cancellationToken);
        NativeError.Clear();

        byte upToDate = 0;
        ulong download = 0, reuse = 0, total = 0;
        nint summary = nint.Zero;
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_model_sync_plan_is_up_to_date(plan.Ptr, &upToDate),
            nameof(LiteRtLmNative.litert_lm_model_sync_plan_is_up_to_date));
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_download(plan.Ptr, &download),
            nameof(LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_download));
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_reuse(plan.Ptr, &reuse),
            nameof(LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_reuse));
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_model_sync_plan_get_total_remote_size(plan.Ptr, &total),
            nameof(LiteRtLmNative.litert_lm_model_sync_plan_get_total_remote_size));
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_model_sync_plan_format(plan.Ptr, &summary),
            nameof(LiteRtLmNative.litert_lm_model_sync_plan_format));
        // The summary belongs to the plan: copy it before the plan is deleted.
        string text = Marshal.PtrToStringUTF8(summary) ?? "";
        return new LiteRtModelSyncPlan(upToDate != 0, Checked(download), Checked(reuse), Checked(total), text);
    }

    private static unsafe LiteRtModelUpdateResult Update(
        ModelSyncCall call, CallPaths paths, string failure, CancellationToken cancellationToken)
    {
        using ModelSyncOptionsHandle options = CreateOptions();
        LiteRtStatusCode status = LiteRtStatusCode.Unknown;
        nint user = call.Begin();
        try
        {
            fixed (byte* local = paths.Local)
            fixed (byte* output = paths.Output)
                status = LiteRtLmNative.litert_lm_model_sync_update(local, output, ModelSyncCall.Fetcher, user, options.Ptr);
        }
        finally
        {
            call.End(status);
        }
        if (status != LiteRtStatusCode.Ok)
            throw Failure(call, status, failure, paths, cancellationToken);
        // A failed writer call inside a fetch the runtime recovered from can leave a message on this thread.
        NativeError.Clear();
        return new LiteRtModelUpdateResult(call.BytesReceived);
    }

    /// <summary>Creates the options every call sends: the full-download threshold at 0.80, no deletion of the local
    /// file before the download, and the disk space check on. They are the native defaults, sent explicitly so a
    /// change of those defaults changes nothing here.</summary>
    private static unsafe ModelSyncOptionsHandle CreateOptions()
    {
        nint ptr;
        NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_model_sync_options_create(&ptr),
            nameof(LiteRtLmNative.litert_lm_model_sync_options_create));
        var options = new ModelSyncOptionsHandle(ptr);
        try
        {
            NativeError.ThrowIfFailed(
                LiteRtLmNative.litert_lm_model_sync_options_set_full_download_threshold(options.Ptr, FullDownloadThreshold),
                nameof(LiteRtLmNative.litert_lm_model_sync_options_set_full_download_threshold));
            NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_model_sync_options_set_clean(options.Ptr, false),
                nameof(LiteRtLmNative.litert_lm_model_sync_options_set_clean));
            NativeError.ThrowIfFailed(LiteRtLmNative.litert_lm_model_sync_options_set_check_disk_space(options.Ptr, true),
                nameof(LiteRtLmNative.litert_lm_model_sync_options_set_check_disk_space));
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    private static long Checked(ulong value) => value <= long.MaxValue
        ? (long)value
        : throw new LiteRtException(
            $"The runtime reported a size of {value} bytes, past the largest file size the binding supports.",
            LiteRtStatusCode.DataLoss);

    private static Exception Failure(
        ModelSyncCall call, LiteRtStatusCode status, string failure, CallPaths paths, CancellationToken cancellationToken)
    {
        Exception? sourceException = call.LastFetchException;
        LiteRtException exception = NativeError.Exception(
            status, failure, sourceException,
            report => Hint(report?.Code ?? status, report?.Message, call.LastFetchFailed, call.LastWriterFailed,
                update: paths.Output is not null, paths.AnsiNonAscii));
        // As the conversation sends do: a failure while the caller's token is cancelled is the cancel.
        return cancellationToken.IsCancellationRequested
            ? new OperationCanceledException("The model sync was cancelled.", exception, cancellationToken)
            : exception;
    }

    /// <summary>The hint added to a failed call's message, or <c>null</c>.</summary>
    /// <param name="code">The status of the runtime's message, or the call's status.</param>
    /// <param name="reason">The runtime's message.</param>
    /// <param name="fetchFailed">Whether the last fetch failed for a reason the source or the bridge reported: the
    /// runtime then reports that status, and the hints about the runtime's own checks do not apply.</param>
    /// <param name="writerFailed">Whether the runtime's writer refused bytes of the last fetch: the failure is in
    /// the runtime's storage of the bytes, not in the remote.</param>
    /// <param name="update">Whether the call is an update, which writes files.</param>
    /// <param name="ansiNonAscii">Whether a non-ASCII path went to the runtime in an ANSI code page.</param>
    internal static string? Hint(
        LiteRtStatusCode code, string? reason, bool fetchFailed, bool writerFailed, bool update, bool ansiNonAscii)
    {
        bool runtimeCheck = !fetchFailed && !writerFailed;
        string? hint = code switch
        {
            LiteRtStatusCode.ResourceExhausted =>
                "The output's volume needs free space at least equal to the remote file's size, because the update " +
                "writes a full temporary copy next to the output. The runtime's reason can mention a --clean flag; " +
                "the binding has no equivalent.",
            LiteRtStatusCode.PermissionDenied when runtimeCheck && update =>
                "The runtime reports a file it cannot create, such as the temporary OUTPUT.tmp_updating next to the " +
                "output, as PERMISSION_DENIED, whatever the cause. Check that the output's folder is writable.",
            LiteRtStatusCode.Internal when update && reason?.Contains("Failed to rename temporary model", StringComparison.Ordinal) == true =>
                "The runtime wrote the temporary file but could not replace the output with it, which happens on " +
                "Windows while the output or its temporary file OUTPUT.tmp_updating is open: for example by a " +
                "LiteRtModelInfo.Read of the output, or by a second update of the same output in another process. An " +
                "engine that loaded the output under another spelling, or after the update started, can cause it " +
                "too. Close the file and update again, or write the update to another path.",
            LiteRtStatusCode.Internal when update =>
                "The runtime could not write the output path. If the output is another spelling of the local file, " +
                "such as a hard link or a short 8.3 name, pass the same path for both.",
            LiteRtStatusCode.DataLoss when runtimeCheck && reason?.Contains("SHA-256", StringComparison.Ordinal) == true =>
                "A downloaded section does not match the hash in the remote header: the remote file may have changed " +
                "during the call, or it is corrupt.",
            LiteRtStatusCode.InvalidArgument or LiteRtStatusCode.DataLoss when runtimeCheck =>
                "The remote may not be a .litertlm file, or it returned a different number of bytes than its header " +
                "declares.",
            _ => null,
        };
        if (!ansiNonAscii)
            return hint;
        const string ansi = "The runtime's reason can show non-ASCII characters of a path wrongly: the binding passes " +
                            "paths in the ANSI code page, and the reason is read as UTF-8. The path at the start of this " +
                            "message is the one the call used.";
        return hint is null ? ansi : hint + " " + ansi;
    }

    /// <summary><c>null</c> for a missing local file, else its full path in the binding's spelling.</summary>
    private static string? NormalizeLocal(string? localModelPath) =>
        string.IsNullOrEmpty(localModelPath) ? null : ModelFilePath.Normalize(localModelPath, nameof(localModelPath));

    private static void CheckReadableLocal(string local, string paramName)
    {
        if (Directory.Exists(local))
            throw new ArgumentException(
                $"'{local}' is a folder, not a model file. Pass null for a first download.", paramName);
        if (!File.Exists(local))
            throw new ArgumentException(
                $"Local model file not found: {local}. Pass null for a first download; the runtime would otherwise " +
                "ignore the path and download the whole file.", paramName);
        try
        {
            using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException(
                $"The local model file '{local}' cannot be opened for reading ({e.Message}); the runtime would ignore " +
                "it and download the whole file.", paramName, e);
        }
    }

    private static void CheckOutput(string output)
    {
        if (Directory.Exists(output))
            throw new ArgumentException(
                $"'{output}' is a folder. Pass the path of the model file to write.", "outputModelPath");
        string? folder = Path.GetDirectoryName(output);
        if (folder is not null && !Directory.Exists(folder))
            throw new ArgumentException(
                $"The folder of '{output}' does not exist. Create it first: the runtime does not create it, and would " +
                "fail after fetching the remote header.", "outputModelPath");
    }

    /// <summary>The bytes of a call's paths, encoded once and kept alive for the native call.</summary>
    private sealed class CallPaths
    {
        private CallPaths(byte[]? local, byte[]? output, bool ansiNonAscii)
        {
            Local = local;
            Output = output;
            AnsiNonAscii = ansiNonAscii;
        }

        public byte[]? Local { get; }
        public byte[]? Output { get; }

        /// <summary>Whether a non-ASCII path went to the runtime in an ANSI code page other than UTF-8.</summary>
        public bool AnsiNonAscii { get; }

        /// <summary>Encodes the full paths of a call. On Windows, when the local path, the output or the temporary
        /// file next to the output reaches 260 characters, every path of the call gets the extended-length prefix:
        /// plain paths of 260 characters or more fail in a process that is not long-path aware, and 259 work
        /// (measured). In place, the runtime compares the two strings, so it gets the same bytes twice.</summary>
        public static CallPaths Encode(string? local, string? output, bool inPlace)
        {
            bool extended = OperatingSystem.IsWindows()
                            && ((local?.Length ?? 0) >= MaxPath || (output is not null && output.Length + TemporarySuffix.Length >= MaxPath));
            byte[]? outputBytes = output is null
                ? null
                : NarrowPath.EncodeForModelSync(Native(output, extended, "outputModelPath"), "outputModelPath");
            byte[]? localBytes = local is null
                ? null
                : inPlace ? outputBytes : NarrowPath.EncodeForModelSync(Native(local, extended, "localModelPath"), "localModelPath");
            bool ansiNonAscii = NarrowPath.ActiveCodePage is { } codePage && codePage != 65001
                                &&((local is not null && !NarrowPath.IsAscii(local)) || (output is not null && !NarrowPath.IsAscii(output)));
            return new CallPaths(localBytes, outputBytes, ansiNonAscii);
        }

        /// <summary>The spelling the runtime gets for a path that <see cref="ModelFilePath.Normalize"/> returned:
        /// only a path that no plain path can name still starts with <c>\\?\</c>, and none with <c>\\.\</c> or
        /// <c>\\?\UNC\</c>.</summary>
        private static string Native(string fullPath, bool extended, string paramName)
        {
            if (!extended || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
                return fullPath;
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException(
                    $"The call needs extended-length paths, because a path or the temporary file next to the output " +
                    $"reaches {MaxPath} characters, and '{fullPath}' is a UNC path, whose extended form is not supported. " +
                    "Use a shorter path or a drive letter.", paramName);
            return @"\\?\" + fullPath;
        }
    }
}
