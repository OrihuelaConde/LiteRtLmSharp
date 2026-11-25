using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using LiteRtLmSharp.Native;

namespace LiteRtLmSharp;

/// <summary>Downloads and updates <c>.litertlm</c> model files from a remote copy, reusing the sections that did
/// not change. Needs no engine.</summary>
/// <remarks>
/// <para>The runtime compares the section hashes that the local and the remote headers record. It copies the
/// sections whose hashes match from the local file and downloads the others; when more than 80% of the section
/// bytes changed, or when either file has no section hashes, it downloads the whole file. It verifies the SHA-256
/// of each downloaded section that the remote header hashes, so the bytes of a remote without section hashes are
/// not verified. See <see cref="LiteRtModelSyncPlan"/> for what that means for the files published today.</para>
/// <para>Each call runs the native work on a thread of its own and completes only when the native call has
/// returned. The methods are safe to call from several threads; calls that read or write the same file at the
/// same time are refused (see the exceptions). Other processes are not checked.</para>
/// <para>Paths: the runtime opens them through the C runtime's narrow file functions. On Windows those decode the
/// path in the active ANSI code page, so a path with characters outside that code page is refused; under the UTF-8
/// code page (65001) any path works. A path of 260 characters or more is passed in its extended-length form
/// (<c>\\?\</c>), except a UNC path, which is refused. Measured on LiteRT-LM v0.20.0, win-x64.</para>
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
    /// the local header. When the headers match, it also checks the local sections' bytes against their hashes,
    /// which reads the local file.</para>
    /// <para>A remote that cannot serve byte ranges fails the plan when its header is longer than 16 KiB.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="remote"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="localModelPath"/> does not exist, is a folder or cannot
    /// be opened for reading, or a path cannot be passed to the runtime (see the remarks of
    /// <see cref="LiteRtModelSync"/>). The runtime would otherwise ignore the local file and plan a full
    /// download.</exception>
    /// <exception cref="InvalidOperationException">An update of this process is writing
    /// <paramref name="localModelPath"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the
    /// call, or before a fetch that then failed.</exception>
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
    /// <para>The runtime writes the new file to <c>OUTPUT.tmp_updating</c> next to the output and renames it over
    /// the output at the end, so the output is never partly written: on a failure or a cancel the old output is
    /// still there and the temporary file is removed. The output's volume needs free space at least equal to the
    /// remote file's size. When the local file is already up to date and is the output, nothing is written; when
    /// the output is another file, the runtime copies the local file to it.</para>
    /// <para>Pass the same string for both paths to update a file in place: the runtime compares the path
    /// strings, and paths that <see cref="Path.GetFullPath(string)"/> makes equal count as the same. Another
    /// spelling of the file, such as a hard link or a short 8.3 name, does not.</para>
    /// <para>Cancel: the token is checked before the call and at each fetch, and it interrupts a fetch that waits
    /// for the remote. The runtime's work between fetches (reading the local file, copying reused sections,
    /// renaming the temporary file) is not interrupted, so a cancel then takes effect at the next fetch, or never
    /// when none follows. When the runtime completes the update before it sees the cancel, the method returns the
    /// result instead of throwing, because the output was replaced.</para>
    /// <para>A failed fetch becomes the call's status: <see cref="LiteRtStatusCode.Unavailable"/> for a network
    /// failure or a server error, <see cref="LiteRtStatusCode.NotFound"/>,
    /// <see cref="LiteRtStatusCode.Unauthenticated"/> or <see cref="LiteRtStatusCode.PermissionDenied"/> for the
    /// matching HTTP status, <see cref="LiteRtStatusCode.DeadlineExceeded"/> for a timeout,
    /// <see cref="LiteRtStatusCode.Aborted"/> when the remote file changed during the call, and
    /// <see cref="LiteRtStatusCode.DataLoss"/> when the remote returned other bytes than the ones asked for. A
    /// remote that cannot serve byte ranges makes the runtime download the whole file from byte 0, and fails the
    /// update when its header is longer than 16 KiB.</para>
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
    /// <paramref name="outputModelPath"/>. Write the update to another path, dispose the engine, then replace the
    /// file.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the
    /// call, or before a fetch that then failed.</exception>
    /// <exception cref="LiteRtException">The runtime could not complete the update; <see cref="LiteRtException.StatusCode"/>
    /// says why, and <see cref="Exception.InnerException"/> holds the source's exception when a fetch failed with
    /// one. Also thrown when the native library's C API version does not match this binding.</exception>
    public static async Task<LiteRtModelUpdateResult> UpdateAsync(
        string? localModelPath, string outputModelPath, LiteRtModelSyncSource remote,
        LiteRtModelSyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputModelPath);
        ArgumentNullException.ThrowIfNull(remote);
        string output = Path.GetFullPath(outputModelPath);
        string? local = NormalizeLocal(localModelPath);
        bool inPlace = local is not null && LoadedModelPaths.Comparer.Equals(local, output);
        if (inPlace && !File.Exists(local) && !Directory.Exists(local))
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
                "process, and replacing it could fail after the whole download. Write the update to another path, " +
                "dispose the engine, then replace the file.");
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
            status, failure, sourceException, report => Hint(report?.Code ?? status, report?.Message, call.LastFetchFailed, paths));
        // As the conversation sends do: a failure while the caller's token is cancelled is the cancel.
        return cancellationToken.IsCancellationRequested
            ? new OperationCanceledException("The model sync was cancelled.", exception, cancellationToken)
            : exception;
    }

    private static string? Hint(LiteRtStatusCode code, string? reason, bool fetchFailed, CallPaths paths)
    {
        string? hint = code switch
        {
            LiteRtStatusCode.ResourceExhausted =>
                "The output's volume needs free space at least equal to the remote file's size, because the update " +
                "writes a full temporary copy next to the output. The runtime's reason can mention a --clean flag; " +
                "the binding has no equivalent.",
            LiteRtStatusCode.PermissionDenied =>
                "The runtime reports a file it cannot create or open, the temporary OUTPUT.tmp_updating included, as " +
                "PERMISSION_DENIED, whatever the cause. Check that the output's folder is writable.",
            LiteRtStatusCode.Internal when paths.Output is not null =>
                "The runtime could not move the result onto the output path, for example because a folder or " +
                "another spelling of the local file is there.",
            LiteRtStatusCode.DataLoss when !fetchFailed && reason?.Contains("SHA-256", StringComparison.Ordinal) == true =>
                "A downloaded section does not match the hash in the remote header: the remote file may have changed " +
                "during the call, or it is corrupt.",
            LiteRtStatusCode.InvalidArgument or LiteRtStatusCode.DataLoss when !fetchFailed =>
                "The remote may not be a .litertlm file, or it returned a different number of bytes than its header " +
                "declares.",
            _ => null,
        };
        if (!paths.AnsiNonAscii)
            return hint;
        const string ansi = "The runtime's reason can show non-ASCII characters of a path wrongly: the binding passes " +
                            "paths in the ANSI code page, and the reason is read as UTF-8. The path at the start of this " +
                            "message is the one the call used.";
        return hint is null ? ansi : hint + " " + ansi;
    }

    /// <summary><c>null</c> for a missing local file, else its full path.</summary>
    private static string? NormalizeLocal(string? localModelPath) =>
        string.IsNullOrEmpty(localModelPath) ? null : Path.GetFullPath(localModelPath);

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

        private static string Native(string fullPath, bool extended, string paramName)
        {
            if (!extended || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
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
