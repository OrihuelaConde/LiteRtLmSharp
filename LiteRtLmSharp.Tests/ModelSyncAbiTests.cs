using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LiteRtLmSharp.Native;
using Xunit;
using Xunit.Abstractions;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The C API 1.1 binary interface of the model sync functions (<c>c/model_sync.h</c>), called directly and without
/// a model: every import is exported, NULL arguments are rejected with INVALID_ARGUMENT and a message naming the
/// argument, pointer outputs are set to NULL, scalar outputs are left unwritten, and the fetcher callback's status
/// and message reach the call. The plans use the generated containers of <see cref="ModelSyncFixtures"/>. The
/// expected messages were measured on LiteRT-LM v0.20.0, win-x64.
/// </summary>
public sealed unsafe class ModelSyncAbiTests(ITestOutputHelper output)
{
    static ModelSyncAbiTests() => LiteRtEngine.SetMinLogLevel(3); // registers the resolver

    /// <summary>All 19 functions of <c>c/model_sync.h</c>. The packaging checks
    /// <c>.github/scripts/inspect-pe.py</c>, <c>.github/scripts/assert-elf-exports.sh</c> and the Apple steps of
    /// <c>native-release.yml</c> require the two entry points, <c>litert_lm_model_sync_update</c> and
    /// <c>litert_lm_model_sync_plan_create_from_file</c>.</summary>
    internal static readonly string[] ModelSyncImports =
    [
        "litert_lm_model_sync_range_writer_write",
        "litert_lm_model_sync_range_writer_set_error",
        "litert_lm_model_sync_options_create",
        "litert_lm_model_sync_options_delete",
        "litert_lm_model_sync_options_set_full_download_threshold",
        "litert_lm_model_sync_options_set_clean",
        "litert_lm_model_sync_options_set_check_disk_space",
        "litert_lm_model_sync_options_set_allow_offline_fallback",
        "litert_lm_model_sync_plan_create_from_file",
        "litert_lm_model_sync_plan_delete",
        "litert_lm_model_sync_plan_is_up_to_date",
        "litert_lm_model_sync_plan_get_bytes_to_download",
        "litert_lm_model_sync_plan_get_bytes_to_reuse",
        "litert_lm_model_sync_plan_get_total_remote_size",
        "litert_lm_model_sync_plan_format",
        "litert_lm_model_sync_update",
        "litert_lm_model_sync_cache_sync",
        "litert_lm_model_sync_result_delete",
        "litert_lm_model_sync_result_get_cached_path",
    ];

    private static string? TakeMessage()
    {
        string? message = Marshal.PtrToStringUTF8(LiteRtLmNative.litert_lm_get_last_error_message());
        NativeError.Clear();
        return message;
    }

    /// <summary>The library the binding's resolver loaded exports every model sync function.</summary>
    [Fact]
    public void ModelSyncImports_AreExported()
    {
        NativeLibraryResolver.EnsureCompatible();
        FieldInfo? verified = typeof(NativeLibraryResolver).GetField("_verified", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(verified);
        nint library = (nint)verified.GetValue(null)!;
        Assert.NotEqual(nint.Zero, library);
        Assert.Equal(19, ModelSyncImports.Distinct().Count());
        string[] missing = ModelSyncImports.Where(name => !NativeLibrary.TryGetExport(library, name, out _)).ToArray();
        Assert.Empty(missing);
    }

    /// <summary>The options: a NULL out pointer and a NULL handle are rejected, the threshold accepts
    /// <c>[0, 1]</c> and rejects NaN, infinity and values outside it, and the delete is NULL-safe.</summary>
    [Fact]
    public void Options_RejectNullArguments_AndValidateTheThreshold()
    {
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_options_create(null));
        Assert.Equal("out_options must not be NULL.", TakeMessage());

        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_options_set_full_download_threshold(0, 0.5));
        Assert.Equal("options must not be NULL.", TakeMessage());
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_options_set_clean(0, false));
        Assert.Equal("options must not be NULL.", TakeMessage());
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_options_set_check_disk_space(0, true));
        Assert.Equal("options must not be NULL.", TakeMessage());
        Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_options_set_allow_offline_fallback(0, true));
        Assert.Equal("options must not be NULL.", TakeMessage());

        nint options;
        Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_options_create(&options));
        Assert.NotEqual(nint.Zero, options);
        try
        {
            foreach (double bad in new[] { double.NaN, -0.1, 1.1, double.PositiveInfinity })
            {
                Assert.Equal(LiteRtStatusCode.InvalidArgument,
                    LiteRtLmNative.litert_lm_model_sync_options_set_full_download_threshold(options, bad));
                Assert.Equal("full_download_threshold must be within [0.0, 1.0].", TakeMessage());
            }
            foreach (double good in new[] { 0.0, -0.0, 0.8, 1.0 })
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_options_set_full_download_threshold(options, good));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_options_set_clean(options, false));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_options_set_check_disk_space(options, true));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_options_set_allow_offline_fallback(options, true));
        }
        finally
        {
            LiteRtLmNative.litert_lm_model_sync_options_delete(options);
        }

        LiteRtLmNative.litert_lm_model_sync_options_delete(0);
        LiteRtLmNative.litert_lm_model_sync_plan_delete(0);
        LiteRtLmNative.litert_lm_model_sync_result_delete(0);
    }

    /// <summary>Plan, update and cache sync reject NULL and empty arguments before any fetch, set their out
    /// pointers to NULL, and the plan getters leave their outputs unwritten for a NULL plan.</summary>
    [Fact]
    public void Calls_RejectNullArguments_WithoutFetching()
    {
        var remote = new RawRemote(ModelSyncFixtures.A);
        GCHandle gc = GCHandle.Alloc(remote);
        try
        {
            nint user = GCHandle.ToIntPtr(gc);
            delegate* unmanaged[Cdecl]<ulong, ulong, nint, nint, LiteRtStatusCode> fetch = &RawRemote.Fetch;

            nint plan = 0x5A5A;
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_create_from_file(null, null, user, 0, &plan));
            Assert.Equal(nint.Zero, plan);
            Assert.Equal("remote_fetcher must not be NULL.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_create_from_file(null, fetch, user, 0, null));
            Assert.Equal("out_plan must not be NULL.", TakeMessage());

            byte[] empty = [0];
            byte[] name = "abi-output.litertlm\0"u8.ToArray();
            fixed (byte* e = empty)
            fixed (byte* n = name)
            {
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_update(null, null, fetch, user, 0));
                Assert.Equal("output_model_path must not be NULL.", TakeMessage());
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_update(null, e, fetch, user, 0));
                Assert.Equal("output_model_path must not be empty.", TakeMessage());
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_update(null, n, null, user, 0));
                Assert.Equal("remote_fetcher must not be NULL.", TakeMessage());

                nint result = 0x5A5A;
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_cache_sync(null, n, fetch, user, 0, &result));
                Assert.Equal(nint.Zero, result);
                Assert.Equal("cache_dir must not be NULL.", TakeMessage());
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_cache_sync(e, n, fetch, user, 0, &result));
                Assert.Equal("cache_dir must not be empty.", TakeMessage());
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_cache_sync(n, null, fetch, user, 0, &result));
                Assert.Equal("model_id must not be NULL.", TakeMessage());
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_cache_sync(n, e, fetch, user, 0, &result));
                Assert.Equal("model_id must not be empty.", TakeMessage());
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_cache_sync(n, n, null, user, 0, &result));
                Assert.Equal("remote_fetcher must not be NULL.", TakeMessage());
                Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_cache_sync(n, n, fetch, user, 0, null));
                Assert.Equal("out_result must not be NULL.", TakeMessage());
            }
            Assert.False(File.Exists("abi-output.litertlm"));
            Assert.Empty(remote.Requests);

            byte flag = 0xEE;
            ulong bytes = 0xDEAD;
            nint text = 0x5A5A;
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_is_up_to_date(0, &flag));
            Assert.Equal("plan must not be NULL.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_download(0, &bytes));
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_reuse(0, &bytes));
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_get_total_remote_size(0, &bytes));
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_format(0, &text));
            Assert.Equal("plan must not be NULL.", TakeMessage());
            Assert.Equal((byte)0xEE, flag);
            Assert.Equal(0xDEADUL, bytes);
            Assert.Equal(nint.Zero, text);

            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_result_get_cached_path(0, &text));
            Assert.Equal("result must not be NULL.", TakeMessage());
            Assert.Equal(nint.Zero, text);

            byte one = 1;
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_range_writer_write(0, &one, 1));
            Assert.Equal("writer must not be NULL.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_range_writer_set_error(0, null));
            Assert.Equal("writer must not be NULL.", TakeMessage());
        }
        finally
        {
            gc.Free();
        }
    }

    /// <summary>A plan of a container against no local file: one header fetch on the calling thread, a full
    /// download planned, a non-empty summary, and INVALID_ARGUMENT for each getter given a NULL out pointer, after
    /// which the plan stays usable. <c>writer_write(writer, NULL, 0)</c> inside the callback is OK.</summary>
    [Fact]
    public void Plan_WithoutLocalFile_PlansAFullDownload()
    {
        byte[] a = ModelSyncFixtures.A;
        var remote = new RawRemote(a) { WriteNullZeroFirst = true };
        nint plan = Plan(remote, local: null, out LiteRtStatusCode status);
        Assert.Equal(LiteRtStatusCode.Ok, status);
        try
        {
            Assert.Equal([(0UL, (ulong)ModelSyncFixtures.InitialHeaderFetch)], remote.Requests);
            Assert.All(remote.Statuses, s => Assert.Equal(LiteRtStatusCode.Ok, s));
            Assert.True(remote.AllOnCallingThread);

            byte upToDate = 0xEE;
            ulong download = 0, reuse = 0xDEAD, total = 0;
            nint text;
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_plan_is_up_to_date(plan, &upToDate));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_download(plan, &download));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_reuse(plan, &reuse));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_plan_get_total_remote_size(plan, &total));
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_plan_format(plan, &text));
            Assert.Equal((byte)0, upToDate);
            Assert.Equal((ulong)a.Length, download);
            Assert.Equal(0UL, reuse);
            Assert.Equal((ulong)a.Length, total);
            string? summary = Marshal.PtrToStringUTF8(text);
            output.WriteLine(summary);
            Assert.False(string.IsNullOrWhiteSpace(summary));

            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_is_up_to_date(plan, null));
            Assert.Equal("out_is_up_to_date must not be NULL.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_download(plan, null));
            Assert.Equal("out_bytes must not be NULL.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_reuse(plan, null));
            Assert.Equal("out_bytes must not be NULL.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_get_total_remote_size(plan, null));
            Assert.Equal("out_bytes must not be NULL.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.InvalidArgument, LiteRtLmNative.litert_lm_model_sync_plan_format(plan, null));
            Assert.Equal("out_formatted_string must not be NULL.", TakeMessage());
            Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_plan_get_total_remote_size(plan, &total));
        }
        finally
        {
            LiteRtLmNative.litert_lm_model_sync_plan_delete(plan);
        }
    }

    /// <summary>A plan of a container against a local copy of itself is up to date and reuses every section.</summary>
    [Fact]
    public void Plan_AgainstItself_IsUpToDate()
    {
        byte[] a = ModelSyncFixtures.A;
        string dir = Directory.CreateTempSubdirectory("litertlm-abi-").FullName;
        try
        {
            string local = Path.Combine(dir, "a.litertlm");
            File.WriteAllBytes(local, a);
            var remote = new RawRemote(a);
            nint plan = Plan(remote, local, out LiteRtStatusCode status);
            Assert.Equal(LiteRtStatusCode.Ok, status);
            try
            {
                byte upToDate = 0;
                ulong download = 1, reuse = 0;
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_plan_is_up_to_date(plan, &upToDate));
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_download(plan, &download));
                Assert.Equal(LiteRtStatusCode.Ok, LiteRtLmNative.litert_lm_model_sync_plan_get_bytes_to_reuse(plan, &reuse));
                Assert.Equal((byte)1, upToDate);
                Assert.Equal(0UL, download);
                Assert.Equal(1024UL + 40960 + 8192 + 30720, reuse);
            }
            finally
            {
                LiteRtLmNative.litert_lm_model_sync_plan_delete(plan);
            }
        }
        finally
        {
            // A cleanup failure must not hide the test's own exception.
            ModelSyncAsserts.DeleteFolder(dir);
        }
    }

    /// <summary>The status the callback returns is the call's status, a value outside 1 to 16 reads as UNKNOWN,
    /// and the <c>set_error</c> text follows the runtime's status and fetch phase in the last error.</summary>
    [Theory]
    [InlineData(14, "source text", "UNAVAILABLE: Failed to fetch initial remote header: source text")]
    [InlineData(1, null, "CANCELLED: Failed to fetch initial remote header: Range fetch failed")]
    [InlineData(99, null, "UNKNOWN: Failed to fetch initial remote header: Range fetch failed")]
    [InlineData(-1, null, "UNKNOWN: Failed to fetch initial remote header: Range fetch failed")]
    public void HeaderFetchFailure_PassesTheStatusAndMessageThrough(int returned, string? message, string expected)
    {
        var remote = new RawRemote(ModelSyncFixtures.A) { FailWith = (LiteRtStatusCode)returned, ErrorText = message };
        nint plan = Plan(remote, local: null, out LiteRtStatusCode status);
        Assert.Equal(nint.Zero, plan);
        Assert.Equal(returned is >= 1 and <= 16 ? (LiteRtStatusCode)returned : LiteRtStatusCode.Unknown, status);
        Assert.Equal(expected, TakeMessage());
        Assert.Single(remote.Requests);
    }

    private static nint Plan(RawRemote remote, string? local, out LiteRtStatusCode status)
    {
        GCHandle gc = GCHandle.Alloc(remote);
        try
        {
            remote.CallingThread = Environment.CurrentManagedThreadId;
            // Encoded like the binding, so a non-ASCII temporary folder still reaches the runtime.
            byte[]? path = local is null ? null : NarrowPath.EncodeForModelSync(Path.GetFullPath(local), nameof(local));
            nint plan;
            fixed (byte* p = path)
                status = LiteRtLmNative.litert_lm_model_sync_plan_create_from_file(p, &RawRemote.Fetch, GCHandle.ToIntPtr(gc), 0, &plan);
            return plan;
        }
        finally
        {
            gc.Free();
        }
    }

    /// <summary>A remote served straight from the C callback, without the binding's bridge.</summary>
    private sealed class RawRemote(byte[] data)
    {
        public List<(ulong Offset, ulong Length)> Requests { get; } = [];
        public List<LiteRtStatusCode> Statuses { get; } = [];
        public int CallingThread { get; set; }
        public bool AllOnCallingThread { get; private set; } = true;
        public bool WriteNullZeroFirst { get; init; }
        public LiteRtStatusCode FailWith { get; init; }
        public string? ErrorText { get; init; }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static LiteRtStatusCode Fetch(ulong offset, ulong length, nint writer, nint user)
        {
            try
            {
                return ((RawRemote)GCHandle.FromIntPtr(user).Target!).Serve(offset, length, writer);
            }
            catch
            {
                return LiteRtStatusCode.Unknown;
            }
        }

        private LiteRtStatusCode Serve(ulong offset, ulong length, nint writer)
        {
            Requests.Add((offset, length));
            if (Environment.CurrentManagedThreadId != CallingThread)
                AllOnCallingThread = false;
            LiteRtStatusCode status = LiteRtStatusCode.Ok;
            if (FailWith != LiteRtStatusCode.Ok)
            {
                if (ErrorText is not null)
                {
                    byte[] text = Encoding.UTF8.GetBytes(ErrorText + "\0");
                    fixed (byte* t = text)
                        LiteRtLmNative.litert_lm_model_sync_range_writer_set_error(writer, t);
                }
                status = FailWith;
            }
            else
            {
                if (WriteNullZeroFirst)
                    status = LiteRtLmNative.litert_lm_model_sync_range_writer_write(writer, null, 0);
                long available = (long)Math.Min(length, (ulong)Math.Max(0, data.Length - (long)offset));
                if (status == LiteRtStatusCode.Ok && available > 0)
                {
                    fixed (byte* p = &data[(int)offset])
                        status = LiteRtLmNative.litert_lm_model_sync_range_writer_write(writer, p, (nuint)available);
                }
            }
            Statuses.Add(status);
            return status;
        }
    }
}
