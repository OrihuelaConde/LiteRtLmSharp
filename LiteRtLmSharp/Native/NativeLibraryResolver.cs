using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

/// <summary>
/// Resolves the <c>LiteRtLm</c> native library. Probes the assembly directory and the NuGet
/// <c>runtimes/&lt;rid&gt;/native</c> layout. The official LiteRT-LM prebuilt is one monolithic
/// library per platform, so the only companions that exist today are the DirectX Shader Compiler
/// pair on win-x64 (<c>dxcompiler.dll</c>, <c>dxil.dll</c>). Dawn loads <c>dxcompiler.dll</c> at
/// runtime by base name, through a <c>LoadLibrary</c> search that never covers the NuGet
/// <c>runtimes/</c> subdirectory, and the GPU engine fails to create without it (LiteRT-LM v0.20.0,
/// WebGPU on Direct3D 12); <c>dxil.dll</c> ships beside it. The resolver pre-loads every other
/// library in the folder so those by-name loads hit the already-loaded module. Linux/macOS get the
/// same treatment with <c>RTLD_GLOBAL</c>
/// (a no-op with the current single-file layout). Registered lazily from
/// <see cref="LiteRtEngine"/>'s static constructor.
/// When the library cannot be resolved, throws a <see cref="DllNotFoundException"/> whose message
/// names the fix: the missing <c>LiteRtLmSharp.runtime.&lt;rid&gt;</c> package for this process's
/// RID, or — when the binary exists but fails to load — the usual system prerequisites. When the
/// library loads but implements another C API version, throws a <see cref="LiteRtException"/> (see
/// <see cref="NativeApiVersion"/>) and frees the library.
/// </summary>
/// <remarks>
/// <para>The runtime calls the resolver the first time each P/Invoke method binds, from any thread. The
/// first load runs the version check under a lock. A library that passes is cached, so every later call
/// returns it without probing again. A library that fails is freed and its refusal is cached, so every later
/// call throws a <see cref="LiteRtException"/> with the same message without loading anything again (the
/// companion libraries pre-loaded next to it stay loaded). A <see cref="DllNotFoundException"/> is not
/// cached: the next call probes again.</para>
/// <para>CoreCLR propagates an exception thrown by the resolver to the P/Invoke call site. Mono, which runs
/// the Android RIDs, logs it, discards it and falls back to its own probing, which would bind the refused
/// library. The refusal therefore does not rely on the resolver: every public entry point that can be the
/// first native use calls <see cref="EnsureCompatible"/> before its first P/Invoke. On Mono the resolver
/// check alone does not stop a P/Invoke that binds without passing an entry point.</para>
/// </remarks>
internal static partial class NativeLibraryResolver
{
    private static int _registered;
    private static readonly Lock Gate = new();
    private static nint _verified;
    private static string? _refusal;

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            return;

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    /// <summary>
    /// Loads the native library and runs the C API version check from managed code, so a mismatched library
    /// is refused before the first P/Invoke on every runtime. Returns immediately when a library has already
    /// passed; throws a <see cref="LiteRtException"/> with the cached message after a refusal, and a
    /// <see cref="DllNotFoundException"/> when nothing loads. On iOS, when the framework does not load, it
    /// returns and leaves the report to the runtime's own probing at the first P/Invoke.
    /// </summary>
    internal static void EnsureCompatible()
    {
        if (Volatile.Read(ref _verified) != nint.Zero)
            return;
        _ = Resolve(LiteRtLmNative.Library, typeof(NativeLibraryResolver).Assembly, searchPath: null);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LiteRtLmNative.Library)
            return nint.Zero;

        nint verified = Volatile.Read(ref _verified);
        if (verified != nint.Zero)
            return verified;

        lock (Gate)
        {
            if (_verified != nint.Zero)
                return _verified;
            if (_refusal is not null)
                throw new LiteRtException(_refusal);
            (nint handle, string location) = Load(libraryName, assembly, searchPath);
            if (handle == nint.Zero)
                return nint.Zero;
            if (NativeApiVersion.Check(handle, location) is { } problem)
            {
                _refusal = problem;
                NativeLibrary.Free(handle);
                throw new LiteRtException(problem);
            }
            Volatile.Write(ref _verified, handle);
            return handle;
        }
    }

    /// <summary>Loads the library and says where it came from; zero only on iOS when nothing loaded (the
    /// runtime's own probing then reports the failure).</summary>
    private static (nint Handle, string Location) Load(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // iOS: the native library is Google's official CLiteRTLM.xcframework, embedded by the build
        // as <app>/Frameworks/CLiteRTLM.framework/CLiteRTLM (NativeReference Kind=Framework from the
        // runtime package's buildTransitive .targets) — not the desktop runtimes/<rid>/native layout.
        // The framework keeps its upstream name; only the P/Invoke name is ours. (Desktop macOS /
        // osx-arm64 is NOT this case — it falls through to the directory probing below. MacCatalyst
        // is out of scope.) The runtime path is hardware-untested — CI validates build/link and the
        // exported-symbol surface, not execution.
        if (OperatingSystem.IsIOS())
        {
            string fw = Path.Combine(AppContext.BaseDirectory, "Frameworks", IosFrameworkName + ".framework", IosFrameworkName);
            if (NativeLibrary.TryLoad(fw, out nint fwHandle))
                return (fwHandle, fw);
            return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint def0)
                ? (def0, $"{libraryName} (default search paths)")
                : (nint.Zero, libraryName);
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
                return (handle, main);
            // The file is there but would not load — remember it so the failure message can point at
            // system prerequisites instead of (wrongly) telling the user to install the runtime package.
            foundButFailed = main;
        }

        // Fallback to the default OS resolution (e.g. the library is on PATH/LD_LIBRARY_PATH).
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint def))
            return (def, $"{fileName} (default search paths)");

        // Nothing resolved. Throw with an actionable message instead of returning zero, which would
        // surface as a bare "Unable to load DLL" with no hint of the fix.
        throw new DllNotFoundException(foundButFailed is not null
            ? BuildFoundButFailedMessage(foundButFailed)
            : BuildNotFoundMessage(fileName, CandidateDirectories()));
    }

    /// <summary>The RIDs this project publishes a <c>LiteRtLmSharp.runtime.&lt;rid&gt;</c> package for.</summary>
    private static readonly string[] OfficialRids =
        ["win-x64", "linux-x64", "linux-arm64", "osx-arm64", "android-arm64", "android-x64"];

    /// <summary>Name of the official iOS framework (Google's <c>CLiteRTLM.xcframework</c>), loaded as
    /// <c>Frameworks/CLiteRTLM.framework/CLiteRTLM</c> inside the app bundle.</summary>
    internal const string IosFrameworkName = "CLiteRTLM";

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
        => $"The LiteRT-LM native library was found at '{path}' but failed to load. Common causes: a " +
           "process/binary architecture mismatch (for example the linux-x64 runtime package on an arm64 " +
           "machine), a system library the binary links that is missing (Linux: check with 'ldd'; macOS: " +
           "'otool -L'), or a missing <uses-native-library> manifest entry for vendor GPU libraries " +
           "(Android 12+).";

    private static IEnumerable<string> CandidateDirectories()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
    }

    /// <summary>
    /// Pre-loads the other shared libraries in <paramref name="dir"/> so the engine's own dynamic
    /// loads resolve to already-loaded modules. On Linux/macOS: dlopen with RTLD_GLOBAL, multiple
    /// passes for inter-dependencies (currently nothing to load: the official library is a single
    /// file). On Windows: Dawn's shader compiler loads <c>dxcompiler.dll</c> / <c>dxil.dll</c> at
    /// runtime by base name, and that native LoadLibrary searches the exe directory + PATH — never
    /// the NuGet <c>runtimes/&lt;rid&gt;/native</c> folder. Pre-loading each companion by absolute
    /// path makes every later by-name load hit the loader's already-loaded-module (base name)
    /// short-circuit, which applies regardless of any LOAD_LIBRARY_SEARCH_* flags the native code
    /// passes. A single pass suffices on Windows: an absolute-path load resolves that companion's
    /// own same-directory static imports by itself (altered search path). Failures are ignored on
    /// all platforms (the companions are optional).
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
            .Where(f => IsPreloadCandidate(Path.GetFileName(f), mainFileName))
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
    /// library and the retired self-built companions. With the official monolithic build the only
    /// companions are the DXC pair (<c>dxcompiler.dll</c>, <c>dxil.dll</c>), which Dawn loads dynamically
    /// at the first shader compile; the main library is excluded because it is loaded right after, by
    /// absolute path, as the P/Invoke target.
    /// </summary>
    internal static IEnumerable<string> SelectWindowsCompanions(IEnumerable<string> dllPaths, string mainFileName)
    {
        foreach (string path in dllPaths)
        {
            if (IsPreloadCandidate(Path.GetFileName(path), mainFileName))
                yield return path;
        }
    }

    /// <summary>
    /// File-name prefixes of the companion libraries the pre-1.2.0 self-built packages shipped next to the
    /// engine (the LiteRT runtime, the GPU accelerators and samplers, Dawn, the constraint provider). A stale
    /// copy left in an output folder (a <c>dotnet publish -o</c> over a 1.1.x publish) must never be pre-loaded:
    /// it would bind an older runtime to the current engine. The official monolithic library embeds all of
    /// them. It still carries the file names of its accelerator and sampler plugins (on win-x64,
    /// <c>libLiteRtGpuAccelerator.dll</c>, <c>libLiteRtWebGpuAccelerator.dll</c>,
    /// <c>libLiteRtVulkanAccelerator.dll</c> and <c>libLiteRtTopKWebGpuSampler.dll</c>), but none for Dawn or
    /// the constraint provider. With files under those names beside it, a GPU engine had none of them loaded,
    /// checked after its load and after a send (LiteRT-LM v0.20.0, win-x64).
    /// </summary>
    private static readonly string[] RetiredCompanionPrefixes =
        ["libLiteRt", "LiteRt", "libwebgpu_dawn", "webgpu_dawn", "libGemmaModelConstraintProvider", "GemmaModelConstraintProvider"];

    /// <summary>Whether a library in the natives folder should be pre-loaded: not the main library, and not
    /// one of the retired self-built companions (see <see cref="RetiredCompanionPrefixes"/>).</summary>
    internal static bool IsPreloadCandidate(string fileName, string mainFileName)
    {
        if (fileName.Equals(mainFileName, StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (string prefix in RetiredCompanionPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
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
