using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LiteRtLmSharp.Native;

/// <summary>
/// Paths for the native model metadata reader (<c>litert_lm_loaded_file_create</c>), which opens the same
/// path bytes twice: once through LiteRT's file API, which decodes them as UTF-8, and once through the C
/// runtime's narrow <c>std::ifstream</c>. On Linux, macOS and Android both take UTF-8. On Windows the narrow
/// open decodes the bytes in the active ANSI code page, so both opens find the file only when the two
/// decodings agree: for an ASCII path, or for any path when the ANSI code page is UTF-8 (65001). Any other
/// non-ASCII path (for example a user folder such as <c>C:\Users\José</c>) needs an ASCII alias: the 8.3
/// short name when the volume keeps one, else a temporary DOS device name for the file.
/// </summary>
internal static partial class NarrowPath
{
    private const uint DDD_REMOVE_DEFINITION = 0x2, DDD_EXACT_MATCH_ON_REMOVE = 0x4, DDD_NO_BROADCAST_SYSTEM = 0x8;
    private const uint CP_UTF8 = 65001;

    /// <summary>
    /// Encodes <paramref name="path"/> as the null-terminated bytes both opens agree on, without creating an
    /// alias: UTF-8 off Windows, for an ASCII path, or when the ANSI code page is UTF-8; else the 8.3 short
    /// alias when the volume keeps an ASCII one. <paramref name="opens"/> is <c>false</c> when none applies
    /// and the native call will not find the file.
    /// </summary>
    internal static byte[] Encode(string path, out bool opens)
    {
        opens = true;
        if (!OperatingSystem.IsWindows() || IsAscii(path) || GetACP() == CP_UTF8)
            return NullTerminated(Encoding.UTF8.GetBytes(path));
        if (ShortPath(Path.GetFullPath(path)) is { } alias && IsAscii(alias))
            return NullTerminated(Encoding.ASCII.GetBytes(alias));
        opens = false;
        return NullTerminated(Encoding.UTF8.GetBytes(path));
    }

    /// <summary>
    /// Encodes <paramref name="path"/> like <see cref="Encode"/> and, on Windows, when no encoding opens it
    /// and <paramref name="allowDeviceAlias"/> is <c>true</c>, defines a temporary DOS device name for the
    /// file (<c>\\?\LiteRtLmSharp-&lt;guid&gt;</c>, ASCII) and passes that instead. The alias targets the
    /// extended-length form of the full path (see <see cref="ExtendedPath"/>), so it also works for a path
    /// longer than <c>MAX_PATH</c> in a process that is not long-path aware. The alias exists in the user's
    /// logon session until the returned lease is disposed, so dispose it right after the native call that
    /// opens the file.
    /// </summary>
    internal static Lease Acquire(string path, bool allowDeviceAlias = true)
    {
        byte[] bytes = Encode(path, out bool opens);
        if (opens || !allowDeviceAlias || !OperatingSystem.IsWindows())
            return new Lease(bytes, opens, null, null);
        string target = ExtendedPath(Path.GetFullPath(path));
        string device = $"LiteRtLmSharp-{Guid.NewGuid():N}";
        // Without DDD_RAW_TARGET_PATH the target is a Win32 path. In a process that is not long-path aware,
        // DefineDosDeviceW rejects a plain target longer than MAX_PATH (ERROR_INVALID_NAME) and accepts its
        // extended-length form. DDD_NO_BROADCAST_SYSTEM keeps Explorer and other top-level windows from
        // being told about a name that lives for one call.
        if (!DefineDosDeviceW(DDD_NO_BROADCAST_SYSTEM, device, target))
            return new Lease(bytes, false, null, null);
        return new Lease(NullTerminated(Encoding.ASCII.GetBytes(@"\\?\" + device)), true, device, target);
    }

    /// <summary>
    /// Returns the extended-length form of <paramref name="fullPath"/>, a path that
    /// <see cref="Path.GetFullPath(string)"/> has normalized: <c>\\?\UNC\server\share\…</c> for a UNC path,
    /// <c>\\?\</c> followed by the path for a drive path, and a path that already starts with <c>\\?\</c> or
    /// <c>\\.\</c> unchanged. Windows does not normalize an extended-length path, which is why the input must
    /// be a full path.
    /// </summary>
    internal static string ExtendedPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + fullPath[2..];
        return @"\\?\" + fullPath;
    }

    /// <summary>Whether a path reaches native narrow-API code unchanged on every platform.</summary>
    internal static bool IsAscii(string path)
    {
        foreach (char c in path)
        {
            if (c > 127)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The bytes to pass for a path, and the DOS device name that backs them when <see cref="Acquire"/> had
    /// to define one; disposing removes the name.
    /// </summary>
    internal sealed class Lease : IDisposable
    {
        private string? _device;
        private readonly string? _target;

        internal Lease(byte[] bytes, bool opens, string? device, string? target)
        {
            Bytes = bytes;
            Opens = opens;
            _device = device;
            _target = target;
        }

        /// <summary>The null-terminated path bytes for the native call.</summary>
        internal byte[] Bytes { get; }

        /// <summary><c>false</c> when no encoding or alias opens the file, so the native call fails.</summary>
        internal bool Opens { get; }

        /// <summary>Whether <see cref="Bytes"/> name a temporary DOS device alias.</summary>
        internal bool IsDeviceAlias => _device is not null;

        public void Dispose()
        {
            if (_device is { } device && OperatingSystem.IsWindows())
            {
                // Exact match: removes this definition only, even if the same name were defined twice.
                DefineDosDeviceW(DDD_REMOVE_DEFINITION | DDD_EXACT_MATCH_ON_REMOVE | DDD_NO_BROADCAST_SYSTEM, device, _target);
            }
            _device = null;
        }
    }

    private static byte[] NullTerminated(byte[] bytes)
    {
        byte[] result = new byte[bytes.Length + 1];
        bytes.CopyTo(result, 0);
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string? ShortPath(string fullPath)
    {
        int length = GetShortPathNameW(fullPath, null, 0);
        if (length <= 0)
            return null;
        char[] buffer = new char[length];
        fixed (char* p = buffer)
        {
            int written = GetShortPathNameW(fullPath, p, length);
            return written > 0 && written < length ? new string(p, 0, written) : null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetACP")]
    [SupportedOSPlatform("windows")]
    private static partial uint GetACP();

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static unsafe partial int GetShortPathNameW(string longPath, char* shortPath, int bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DefineDosDeviceW(uint flags, string deviceName, string? targetPath);
}
