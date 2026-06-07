using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteLMSharp.Native;

/// <summary>
/// Resolves the <c>LiteRtLm</c> native library. Default probing finds it when the
/// binaries sit next to the managed assembly (PoC copy-to-output). For the NuGet
/// <c>runtimes/&lt;rid&gt;/native</c> layout we additionally probe that folder.
/// Registered lazily from <see cref="LiteRtEngine"/>'s static constructor.
/// </summary>
internal static class NativeLibraryResolver
{
    private static int _registered;

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            return;

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LiteRtLmNative.Library)
            return nint.Zero;

        // 1. Default resolution (binaries next to the assembly).
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint handle))
            return handle;

        // 2. runtimes/<rid>/native relative to the assembly.
        string rid = RuntimeInformation.RuntimeIdentifier;
        string baseDir = AppContext.BaseDirectory;
        string fileName = GetPlatformFileName(libraryName);
        string candidate = Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
            return handle;

        return nint.Zero;
    }

    private static string GetPlatformFileName(string name)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return name + ".dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "lib" + name + ".dylib";
        return "lib" + name + ".so";
    }
}
