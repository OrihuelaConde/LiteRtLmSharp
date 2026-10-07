using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LiteRtLmSharp.Native;

/// <summary>
/// Paths for native code that opens files through the C runtime's narrow API, such as the v0.18.0 model
/// metadata reader (<c>std::ifstream</c> on a <c>std::string</c> path). On Linux, macOS and Android the
/// narrow API takes UTF-8. On Windows it reads the bytes in the active ANSI code page, so the UTF-8 the
/// binding usually passes names a different file as soon as the path has non-ASCII characters (for example
/// a user folder such as <c>C:\Users\José</c>). The engines open files through LiteRT's own UTF-8 aware API
/// and need none of this.
/// </summary>
internal static partial class NarrowPath
{
    /// <summary>
    /// Encodes <paramref name="path"/> as the null-terminated bytes the narrow API expects: UTF-8 off
    /// Windows; on Windows the ANSI code page when every character converts without loss, else the 8.3 short
    /// alias when the volume keeps one. <paramref name="opens"/> is <c>false</c> when neither works and the
    /// native call will not find the file.
    /// </summary>
    internal static byte[] Encode(string path, out bool opens)
    {
        opens = true;
        if (!OperatingSystem.IsWindows() || IsAscii(path))
            return NullTerminated(Encoding.UTF8.GetBytes(path));
        if (AnsiBytes(path) is { } ansi)
            return NullTerminated(ansi);
        if (ShortPath(Path.GetFullPath(path)) is { } alias && IsAscii(alias))
            return NullTerminated(Encoding.ASCII.GetBytes(alias));
        opens = false;
        return NullTerminated(Encoding.UTF8.GetBytes(path));
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

    private static byte[] NullTerminated(byte[] bytes)
    {
        byte[] result = new byte[bytes.Length + 1];
        bytes.CopyTo(result, 0);
        return result;
    }

    // CP_ACP with WC_NO_BEST_FIT_CHARS: a character without an exact ANSI equivalent sets usedDefault
    // instead of silently turning into a look-alike that names another file.
    [SupportedOSPlatform("windows")]
    private static unsafe byte[]? AnsiBytes(string path)
    {
        const uint CP_ACP = 0, WC_NO_BEST_FIT_CHARS = 0x400;
        fixed (char* wide = path)
        {
            int usedDefault = 0;
            int length = WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, wide, path.Length, null, 0, null, &usedDefault);
            if (length <= 0 || usedDefault != 0)
                return null;
            byte[] bytes = new byte[length];
            fixed (byte* multi = bytes)
            {
                if (WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, wide, path.Length, multi, length, null, &usedDefault) != length
                    || usedDefault != 0)
                    return null;
            }
            return bytes;
        }
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

    [LibraryImport("kernel32.dll", EntryPoint = "WideCharToMultiByte")]
    [SupportedOSPlatform("windows")]
    private static unsafe partial int WideCharToMultiByte(
        uint codePage, uint flags, char* wide, int wideLength, byte* multi, int multiLength, byte* defaultChar, int* usedDefault);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static unsafe partial int GetShortPathNameW(string longPath, char* shortPath, int bufferLength);
}
