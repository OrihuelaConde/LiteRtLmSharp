using System.Reflection;
using System.Runtime.InteropServices;

namespace LiteRtLmSharp.Native;

/// <summary>
/// Checks that the loaded native library implements the LiteRT-LM C API version this binding is built for.
/// </summary>
/// <remarks>
/// LiteRT-LM releases before and after C API 1.0.0 export the same function names with different
/// signatures, so a managed binding paired with a native library of another version would not fail with
/// <see cref="EntryPointNotFoundException"/>: it would corrupt memory. <see cref="NativeLibraryResolver"/>
/// therefore calls <c>litert_lm_get_api_version</c> once, right after it loads the library, and refuses the
/// library unless the check passes. Every public entry point that can be the first native use runs that
/// load and check from managed code (<see cref="NativeLibraryResolver.EnsureCompatible"/>) before its first
/// P/Invoke, so the refusal does not depend on the runtime propagating an exception out of the
/// <c>DllImportResolver</c> callback, which Mono does not do. The rule is the header's own: the same major
/// version, and at least the minor version the binding was built against (an older minor version may lack
/// functions the binding imports).
/// </remarks>
internal static class NativeApiVersion
{
    /// <summary>The C API major version the binding is built for (<c>LITERT_LM_C_API_VERSION_MAJOR</c>).</summary>
    internal const int RequiredMajor = 1;

    /// <summary>The lowest C API minor version the binding accepts (<c>LITERT_LM_C_API_VERSION_MINOR</c> of
    /// the headers it is built against).</summary>
    internal const int RequiredMinor = 1;

    internal const string ExportName = "litert_lm_get_api_version";

    /// <summary>Whether a library that reports <paramref name="major"/>.<paramref name="minor"/> can serve
    /// this binding.</summary>
    internal static bool IsCompatible(int major, int minor) => major == RequiredMajor && minor >= RequiredMinor;

    /// <summary>
    /// Calls <c>litert_lm_get_api_version</c> through <paramref name="library"/>'s export table (a P/Invoke
    /// from inside the resolver would re-enter it) and returns the reason to refuse the library when it does
    /// not export the function, fails to report, or reports an incompatible version; <c>null</c> when it can
    /// serve this binding.
    /// </summary>
    /// <param name="library">The handle the resolver loaded.</param>
    /// <param name="location">Where the library came from, for the message (a path or a search
    /// description).</param>
    internal static unsafe string? Check(nint library, string location)
    {
        string? problem;
        if (!NativeLibrary.TryGetExport(library, ExportName, out nint export))
        {
            problem = Problem(exported: false, LiteRtStatusCode.Ok, 0, 0, 0, location);
        }
        else
        {
            int major = 0, minor = 0, patch = 0;
            var getVersion = (delegate* unmanaged[Cdecl]<int*, int*, int*, int>)export;
            // The call fails only on a null out pointer, which this never passes; the versions are read only
            // when it returns OK.
            var status = (LiteRtStatusCode)getVersion(&major, &minor, &patch);
            problem = Problem(exported: true, status, major, minor, patch, location);
        }
        return problem;
    }

    /// <summary>
    /// The reason to refuse a library, or <c>null</c> when it can serve this binding. Pure, so the rule and
    /// the message are tested without a native library.
    /// </summary>
    /// <param name="exported">Whether the library exports <c>litert_lm_get_api_version</c>.</param>
    /// <param name="status">What the call returned (ignored when not exported).</param>
    /// <param name="major">The reported major version (read only on success).</param>
    /// <param name="minor">The reported minor version (read only on success).</param>
    /// <param name="patch">The reported patch version (read only on success).</param>
    /// <param name="location">Where the library came from.</param>
    internal static string? Problem(bool exported, LiteRtStatusCode status, int major, int minor, int patch, string location)
    {
        string found;
        if (!exported)
            found = $"does not export {ExportName}, so it implements a C API older than 1.0.0 (a LiteRT-LM release " +
                    "before v0.19.0)";
        else if (status != LiteRtStatusCode.Ok)
            found = $"failed to report its C API version ({ExportName} returned {NativeError.CanonicalName(status)})";
        else if (IsCompatible(major, minor))
            return null;
        else
            found = $"implements C API {major}.{minor}.{patch}";

        string binding = BindingVersion();
        string rid = RuntimeInformation.RuntimeIdentifier;
        string? native = typeof(NativeApiVersion).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LiteRtLmVersion")?.Value;
        return $"LiteRtLmSharp {binding} needs a native LiteRT-LM library that implements C API " +
               $"{RequiredMajor}.{RequiredMinor} or a later {RequiredMajor}.x version, but the library loaded from " +
               $"'{location}' {found}. The LiteRtLmSharp package and the LiteRtLmSharp.runtime.<rid> package must " +
               $"have the same version: install LiteRtLmSharp.runtime.{rid} {binding}" +
               (native is null ? "" : $", or, with a native library you build yourself, build it from LiteRT-LM {native}") +
               ", and remove any other copy of the native library from the application folder.";
    }

    private static string BindingVersion()
    {
        string? informational = typeof(NativeApiVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
            return typeof(NativeApiVersion).Assembly.GetName().Version?.ToString(3) ?? "(unknown version)";
        int plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
