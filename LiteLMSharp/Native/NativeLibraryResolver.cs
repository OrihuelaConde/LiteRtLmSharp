using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteLMSharp.Native;

/// <summary>
/// Resolves the <c>LiteRtLm</c> native library. Probes the assembly directory and the NuGet
/// <c>runtimes/&lt;rid&gt;/native</c> layout. On Linux/macOS it pre-loads the companion shared
/// libraries (e.g. libLiteRt) from the same folder first, so the loader resolves them without an
/// rpath/LD_LIBRARY_PATH (on Windows same-directory resolution already works).
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
    /// Pre-loads the other shared libraries in <paramref name="dir"/> so the main library's
    /// dependencies resolve to already-loaded modules. No-op on Windows (same-dir search works).
    /// Multiple passes handle inter-dependencies; failures are ignored (accelerators load lazily).
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
            var stillPending = pending.Where(lib => !NativeLibrary.TryLoad(lib, out _)).ToList();
            if (stillPending.Count == pending.Count)
                break; // no progress this pass
            pending = stillPending;
        }
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
