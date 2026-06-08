using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteLMSharp.Native;

/// <summary>
/// Resolves the <c>LiteRtLm</c> native library. Probes the assembly directory and the NuGet
/// <c>runtimes/&lt;rid&gt;/native</c> layout. On Linux/macOS it pre-loads the companion shared
/// libraries (e.g. libLiteRt) from the same folder with <c>RTLD_GLOBAL</c> so the main library's
/// dependencies resolve without an rpath/LD_LIBRARY_PATH. On Windows same-directory resolution
/// already works. Registered lazily from <see cref="LiteRtEngine"/>'s static constructor.
/// </summary>
internal static partial class NativeLibraryResolver
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

        string fileName = GetPlatformFileName(libraryName);

        foreach (string dir in CandidateDirectories())
        {
            string main = Path.Combine(dir, fileName);
            if (!File.Exists(main))
                continue;

            PreloadCompanions(dir, fileName);
            if (NativeLibrary.TryLoad(main, out nint handle))
                return handle;
        }

        // Fallback to the default OS resolution (e.g. the library is on PATH/LD_LIBRARY_PATH).
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint def) ? def : nint.Zero;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
    }

    /// <summary>
    /// Pre-loads the other shared libraries in <paramref name="dir"/> with RTLD_GLOBAL so the main
    /// library's dependencies resolve to already-loaded modules. No-op on Windows (same-dir search
    /// works). Multiple passes handle inter-dependencies; failures are ignored (accelerators load
    /// lazily / may have optional deps).
    /// </summary>
    private static void PreloadCompanions(string dir, string mainFileName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        string pattern = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "*.dylib" : "*.so";
        var pending = Directory.EnumerateFiles(dir, pattern)
            .Where(f => !Path.GetFileName(f).Equals(mainFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        for (int pass = 0; pass < 3 && pending.Count > 0; pass++)
        {
            var stillPending = pending.Where(lib => DlopenGlobal(lib) == nint.Zero).ToList();
            if (stillPending.Count == pending.Count)
                break; // no progress this pass
            pending = stillPending;
        }
    }

    // RTLD_LAZY (defer symbol resolution) | RTLD_GLOBAL (export symbols to later-loaded libs).
    // RTLD_GLOBAL differs by OS: 0x8 on macOS, 0x100 on Linux. RTLD_LAZY is 0x1 on both.
    private const int RtldLazy = 0x1;
    private static int RtldGlobal => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0x8 : 0x100;

    private static nint DlopenGlobal(string path)
    {
        int flags = RtldLazy | RtldGlobal;
        try
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? dlopen_macos(path, flags)
                : dlopen_linux(path, flags);
        }
        catch (DllNotFoundException)
        {
            return nint.Zero;
        }
    }

    [LibraryImport("libdl.so.2", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlopen_linux(string path, int flags);

    [LibraryImport("libdl.dylib", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlopen_macos(string path, int flags);

    private static string GetPlatformFileName(string name)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return name + ".dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "lib" + name + ".dylib";
        return "lib" + name + ".so";
    }
}
