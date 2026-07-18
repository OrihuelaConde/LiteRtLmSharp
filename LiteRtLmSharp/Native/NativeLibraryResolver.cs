using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

/// <summary>
/// Resolves the <c>LiteRtLm</c> native library. Probes the assembly directory and the NuGet
/// <c>runtimes/&lt;rid&gt;/native</c> layout. On Linux/macOS it pre-loads the companion shared
/// libraries (e.g. libLiteRt) from the same folder with <c>RTLD_GLOBAL</c> so the main library's
/// dependencies resolve without an rpath/LD_LIBRARY_PATH. On Windows it pre-loads them too: the
/// main library's STATIC imports resolve from its own directory (altered search path), but the
/// engine also <c>LoadLibrary</c>s accelerator libraries at runtime by base name, and that search
/// never covers the NuGet <c>runtimes/</c> subdirectory. Registered lazily from
/// <see cref="LiteRtEngine"/>'s static constructor.
/// When the library cannot be resolved, throws a <see cref="DllNotFoundException"/> whose message
/// names the fix: the missing <c>LiteRtLmSharp.runtime.&lt;rid&gt;</c> package for this process's
/// RID, or — when the binary exists but fails to load — the usual system prerequisites.
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

        // iOS: the natives ship as embedded dynamic .frameworks (NativeReference Kind=Framework),
        // placed by the build at <app>/Frameworks/<Name>.framework/<Name> — not the desktop
        // runtimes/<rid>/native layout. Load the main framework binary directly; the companions are
        // dlopen'd by the native engine through the OS loader. (Desktop macOS / osx-arm64 is NOT
        // this case — it falls through to the directory probing below. MacCatalyst is out of scope.)
        // The runtime path is hardware-untested at the package's first release — CI validates
        // build/link + the exported-symbol surface, not execution.
        if (OperatingSystem.IsIOS())
        {
            string fw = Path.Combine(AppContext.BaseDirectory, "Frameworks", libraryName + ".framework", libraryName);
            if (NativeLibrary.TryLoad(fw, out nint fwHandle))
                return fwHandle;
            return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint def0) ? def0 : nint.Zero;
        }

        string fileName = GetPlatformFileName(libraryName);
        string? foundButFailed = null;

        foreach (string dir in CandidateDirectories())
        {
            string main = Path.Combine(dir, fileName);
            if (!File.Exists(main))
                continue;

            PreloadCompanions(dir, fileName);
            if (NativeLibrary.TryLoad(main, out nint handle))
                return handle;
            // The file is there but would not load — remember it so the failure message can point at
            // system prerequisites instead of (wrongly) telling the user to install the runtime package.
            foundButFailed = main;
        }

        // Fallback to the default OS resolution (e.g. the library is on PATH/LD_LIBRARY_PATH).
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint def))
            return def;

        // Nothing resolved. Throw with an actionable message instead of returning zero, which would
        // surface as a bare "Unable to load DLL" with no hint of the fix.
        throw new DllNotFoundException(foundButFailed is not null
            ? BuildFoundButFailedMessage(foundButFailed)
            : BuildNotFoundMessage(fileName, CandidateDirectories()));
    }

    /// <summary>The RIDs this project publishes a <c>LiteRtLmSharp.runtime.&lt;rid&gt;</c> package for.</summary>
    private static readonly string[] OfficialRids = ["win-x64", "linux-x64", "osx-arm64", "android-arm64"];

    /// <summary>Failure message when no native binary was found anywhere: names the runtime package
    /// for this process's RID (or says none exists for it) and lists the searched locations.</summary>
    internal static string BuildNotFoundMessage(string fileName, IEnumerable<string> searchedDirs)
    {
        string rid = RuntimeInformation.RuntimeIdentifier;
        string advice = OfficialRids.Contains(rid)
            ? $"Install the 'LiteRtLmSharp.runtime.{rid}' NuGet package alongside 'LiteRtLmSharp'."
            : $"No official native package exists for this platform (published RIDs: {string.Join(", ", OfficialRids)}).";
        return $"The LiteRT-LM native library '{fileName}' was not found. Searched: " +
               $"{string.Join("; ", searchedDirs)}; then the default OS search paths. This process runs " +
               $"as RID '{rid}'. {advice} If you build your own native binaries, place them next to the " +
               "application executable.";
    }

    /// <summary>Failure message when a native binary exists but failed to load: points at system
    /// prerequisites rather than the (already installed) runtime package.</summary>
    internal static string BuildFoundButFailedMessage(string path)
        => $"The LiteRT-LM native library was found at '{path}' but failed to load. Common causes: the " +
           "Microsoft Visual C++ Redistributable is not installed (Windows), missing system libraries " +
           "(Linux/macOS), a missing <uses-native-library> manifest entry for vendor GPU libraries " +
           "(Android 12+), or a process/binary architecture mismatch.";

    private static IEnumerable<string> CandidateDirectories()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
    }

    /// <summary>
    /// Pre-loads the other shared libraries in <paramref name="dir"/> so the engine's own dynamic
    /// loads resolve to already-loaded modules. On Linux/macOS: dlopen with RTLD_GLOBAL, multiple
    /// passes for inter-dependencies. On Windows: the engine loads its accelerators at runtime by
    /// base name (libLiteRt loads libLiteRtWebGpuAccelerator.dll; LiteRtLm loads
    /// libLiteRtTopKWebGpuSampler.dll; Dawn's shader compiler loads dxcompiler.dll/dxil.dll) and
    /// that native LoadLibrary searches the exe directory + PATH — never the NuGet
    /// <c>runtimes/&lt;rid&gt;/native</c> folder. Pre-loading each companion by absolute path makes
    /// every later by-name load hit the loader's already-loaded-module (base name) short-circuit,
    /// which applies regardless of any LOAD_LIBRARY_SEARCH_* flags the native code passes.
    /// A single pass suffices on Windows: an absolute-path load resolves that companion's own
    /// same-directory static imports by itself (altered search path). Failures are ignored on all
    /// platforms (accelerators are optional / may have optional deps).
    /// </summary>
    private static void PreloadCompanions(string dir, string mainFileName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Only for the runtimes/<rid>/native layout. When the natives sit flat next to the exe,
            // the native LoadLibrary search already covers them (exe directory comes first) — and
            // that folder is the whole app output, where "*.dll" would sweep managed assemblies and
            // unrelated third-party native DLLs into the process.
            if (PathsEqual(dir, AppContext.BaseDirectory))
                return;
            foreach (string lib in SelectWindowsCompanions(Directory.EnumerateFiles(dir, "*.dll"), mainFileName))
                NativeLibrary.TryLoad(lib, out _);
            return;
        }

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

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The companion DLLs to pre-load on Windows: every <c>.dll</c> in the folder except the main
    /// library and the unreferenced non-prefixed twins — the win-x64 natives ship byte-identical
    /// <c>X.dll</c>/<c>libX.dll</c> pairs but the native code references only the <c>lib</c>-prefixed
    /// names, and loading both would map a second copy of each module (notably Dawn, the largest,
    /// with its own process-global GPU state). <c>dxcompiler.dll</c>/<c>dxil.dll</c> have no twin
    /// and must survive the filter — Dawn loads them dynamically at first shader compile.
    /// </summary>
    internal static IEnumerable<string> SelectWindowsCompanions(IEnumerable<string> dllPaths, string mainFileName)
    {
        var paths = dllPaths.ToList();
        var names = new HashSet<string>(paths.Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            if (name.Equals(mainFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!name.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && names.Contains("lib" + name))
                continue;
            yield return path;
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
