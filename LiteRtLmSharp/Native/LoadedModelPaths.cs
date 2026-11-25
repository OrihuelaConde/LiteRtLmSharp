namespace LiteRtLmSharp.Native;

/// <summary>
/// Counts, per full path, the live <see cref="LiteRtEngine"/> and <see cref="LiteRtEmbeddingEngine"/> instances of
/// this process that loaded a model file, so a model sync can refuse to replace a file an engine holds.
/// </summary>
/// <remarks>
/// A count is taken after a successful native engine create and given back in the handle's
/// <c>ReleaseHandle</c>, which runs on Dispose and on finalization, like the slot of <see cref="EngineLiveness"/>.
/// The handle carries the key this class returned, because a finalizer must not depend on other managed objects.
/// Paths are compared like <see cref="Comparer"/>. <see cref="Default"/> is the registry the engines use; tests
/// create their own instances.
/// </remarks>
internal sealed class LoadedModelPaths
{
    /// <summary>The process-wide registry.</summary>
    public static LoadedModelPaths Default { get; } = new();

    /// <summary>Ordinal and case-insensitive on Windows and macOS, whose default file systems ignore case, and
    /// ordinal elsewhere.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _counts = new(Comparer);

    /// <summary>Counts one more engine for <paramref name="modelPath"/> and returns the key to pass to
    /// <see cref="Release"/>: the full path.</summary>
    public string Add(string modelPath)
    {
        string key = Path.GetFullPath(modelPath);
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

    /// <summary>How many live engines loaded the file at <paramref name="fullPath"/>, a path that
    /// <see cref="Path.GetFullPath(string)"/> has normalized.</summary>
    public int Count(string fullPath)
    {
        lock (_lock)
        {
            return _counts.TryGetValue(fullPath, out int count) ? count : 0;
        }
    }
}
