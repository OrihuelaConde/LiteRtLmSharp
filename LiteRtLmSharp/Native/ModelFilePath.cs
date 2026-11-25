namespace LiteRtLmSharp.Native;

/// <summary>
/// One spelling for the path of a model file, so the checks that compare paths (the in-place update, the live-engine
/// guard and the calls in flight) see one file as one string.
/// </summary>
/// <remarks>
/// <see cref="Path.GetFullPath(string)"/> keeps a <c>\\?\</c> or <c>\\.\</c> prefix, so on Windows
/// <c>\\?\C:\m\a.litertlm</c> and <c>C:\m\a.litertlm</c> would count as two files. <see cref="Normalize"/> removes
/// the prefix when the plain path names the same file. The binding adds <c>\\?\</c> again where the native call needs
/// it.
/// </remarks>
internal static class ModelFilePath
{
    private const string Extended = @"\\?\";
    private const string Device = @"\\.\";

    /// <summary>
    /// Returns the full path of <paramref name="path"/> in the binding's spelling. On Windows, <c>\\?\X:\</c> and
    /// <c>\\.\X:\</c> become <c>X:\</c>, and <c>\\?\UNC\</c> and <c>\\.\UNC\</c> become <c>\\</c>, when
    /// <see cref="Path.GetFullPath(string)"/> leaves the plain path as it is. A <c>\\?\X:\</c> path whose plain form
    /// names another file, such as a folder whose name ends in a dot, keeps its prefix.
    /// </summary>
    /// <exception cref="ArgumentException">On Windows, a device path that names no drive letter and no UNC share
    /// (for example a volume GUID path), a <c>\\.\</c> path whose plain form names another file, or a UNC path in
    /// extended form whose plain form names another file.</exception>
    internal static string Normalize(string path, string paramName)
    {
        string full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
            return full;
        bool extended = full.StartsWith(Extended, StringComparison.Ordinal);
        if (!extended && !full.StartsWith(Device, StringComparison.Ordinal))
            return full;

        string rest = full[Extended.Length..];
        if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            string unc = @"\\" + rest[4..];
            return IsPlain(unc)
                ? unc
                : throw new ArgumentException(
                    $"'{path}' is a UNC path in extended form that names a file a plain UNC path cannot. Pass a plain " +
                    "UNC path or a path on a drive letter.", paramName);
        }
        if (rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\')
        {
            if (IsPlain(rest))
                return rest;
            // Only the extended form can name this file, and the native calls accept that form (measured).
            return extended
                ? full
                : throw new ArgumentException(
                    $"'{path}' is a device path that names a file the plain path cannot. Pass the path without the " +
                    $@"'\\.\' prefix.", paramName);
        }
        throw new ArgumentException(
            $"'{path}' is a device path that names no drive letter and no UNC share. Pass a path that starts with a " +
            "drive letter, or a UNC path.", paramName);
    }

    /// <summary>The key of <paramref name="path"/> in the registries of model files: its <see cref="Normalize"/>d
    /// form, or its full path when <see cref="Normalize"/> refuses it. Never throws for a path that names an
    /// existing file, so an engine can register its file after the native engine exists.</summary>
    internal static string Key(string path)
    {
        try
        {
            return Normalize(path, nameof(path));
        }
        catch (ArgumentException)
        {
        }
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Whether two <see cref="Normalize"/>d paths name the same file: when they are equal, or when they differ
    /// only in case and every name that differs resolves to one entry of a folder that ignores case.
    /// </summary>
    /// <remarks>
    /// Folders on Windows and macOS usually ignore case, but a folder can be case-sensitive (on Windows a folder
    /// marked with <c>fsutil file setCaseSensitiveInfo</c>, on macOS a case-sensitive volume). There,
    /// <c>Model.litertlm</c> and <c>model.litertlm</c> are two files. A name counts as the same entry when the folder
    /// holds exactly one entry with that name ignoring case and both spellings exist. Hard links and short 8.3 names
    /// are not detected.
    /// </remarks>
    /// <param name="first">A normalized full path.</param>
    /// <param name="second">A normalized full path.</param>
    /// <param name="entries">Lists the names in a folder; <c>null</c> reads the file system. For tests.</param>
    /// <param name="exists">Whether a path exists; <c>null</c> reads the file system. For tests.</param>
    internal static bool SameFile(
        string first, string second, Func<string, IEnumerable<string>>? entries = null, Func<string, bool>? exists = null)
    {
        if (string.Equals(first, second, StringComparison.Ordinal))
            return true;
        if (first.Length != second.Length || !LoadedModelPaths.Comparer.Equals(first, second))
            return false;
        entries ??= static folder => Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p));
        exists ??= Path.Exists;
        try
        {
            return SameEntryIgnoringCase(first, second, entries, exists);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed: treat the paths as two files, so each keeps its own spelling.
            return false;
        }
    }

    private static bool SameEntryIgnoringCase(
        string first, string second, Func<string, IEnumerable<string>> entries, Func<string, bool> exists)
    {
        string root = Path.GetPathRoot(first) ?? "";
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        string[] a = first[root.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries);
        string[] b = second[root.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (a.Length != b.Length)
            return false;
        // Drive letters and UNC server and share names ignore case.
        string folder = root;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                string name = a[i];
                string[] matches = entries(folder).Where(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1
                    || !exists(Path.Combine(folder, a[i]))
                    || !exists(Path.Combine(folder, b[i])))
                    return false;
                folder = Path.Combine(folder, matches[0]);
            }
            else
            {
                folder = Path.Combine(folder, a[i]);
            }
        }
        return true;
    }

    /// <summary>Whether <see cref="Path.GetFullPath(string)"/> leaves <paramref name="plain"/> as it is, so the
    /// plain path names the same file as its extended form.</summary>
    private static bool IsPlain(string plain)
    {
        try
        {
            return string.Equals(Path.GetFullPath(plain), plain, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
