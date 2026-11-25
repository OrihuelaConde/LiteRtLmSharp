namespace LiteRtLmSharp.Native;

/// <summary>
/// Counts, per full path, the live <see cref="LiteRtEngine"/> and <see cref="LiteRtEmbeddingEngine"/> instances of
/// this process that loaded a model file, so a model sync can refuse to replace a file an engine holds.
/// </summary>
/// <remarks>
/// A count is taken after a successful native engine create and given back in the handle's
/// <c>ReleaseHandle</c>, which runs on Dispose and on finalization, like the slot of <see cref="EngineLiveness"/>.
/// The handle carries the key this class returned, because a finalizer must not depend on other managed objects.
/// Keys are <see cref="ModelFilePath.Key"/> spellings, compared like <see cref="Comparer"/>. <see cref="Default"/>
/// is the registry the engines use; tests create their own instances.
/// </remarks>
internal sealed class LoadedModelPaths
{
    /// <summary>The process-wide registry.</summary>
    public static LoadedModelPaths Default { get; } = new();

    /// <summary>Ordinal and case-insensitive on Windows and macOS, whose default file systems ignore case, and
    /// ordinal elsewhere. In a case-sensitive folder on those systems it matches two files whose names differ only
    /// in case, so the registries refuse more, never less; the in-place check uses
    /// <see cref="ModelFilePath.SameFile"/> instead.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _counts = new(Comparer);

    /// <summary>Counts one more engine for <paramref name="modelPath"/> and returns the key to pass to
    /// <see cref="Release"/>. Never throws for the path of an existing file.</summary>
    public string Add(string modelPath)
    {
        string key = ModelFilePath.Key(modelPath);
        lock (_lock)
        {
            _counts[key] = _counts.TryGetValue(key, out int count) ? count + 1 : 1;
        }
        return key;
    }

    /// <summary>Counts one engine less for <paramref name="key"/>, never below zero. Safe to call from a
    /// finalizer.</summary>
    public void Release(string key)
    {
        lock (_lock)
        {
            if (!_counts.TryGetValue(key, out int count))
                return;
            if (count <= 1)
                _counts.Remove(key);
            else
                _counts[key] = count - 1;
        }
    }

    /// <summary>How many live engines loaded the file at <paramref name="modelPath"/>, under any spelling that
    /// <see cref="ModelFilePath.Key"/> makes equal.</summary>
    public int Count(string modelPath)
    {
        string key = ModelFilePath.Key(modelPath);
        lock (_lock)
        {
            return _counts.TryGetValue(key, out int count) ? count : 0;
        }
    }
}
