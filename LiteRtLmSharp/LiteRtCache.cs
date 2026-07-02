namespace LiteRtLmSharp;

/// <summary>
/// Where the engine keeps its compiled-artifact cache (GPU shaders / converted weights), which speeds
/// up subsequent loads. Choose <see cref="Default"/> (next to the model file), <see cref="Disabled"/>,
/// <see cref="InMemory"/>, or <see cref="Directory"/> for an explicit path. Set it on
/// <see cref="LiteRtEngineOptions.Cache"/>.
/// </summary>
/// <remarks>
/// Each mode is an explicit value, so a cache location can never be confused with a directory path
/// that happens to look like a special token. The default value (<c>default(LiteRtCache)</c>) is
/// <see cref="Default"/>.
/// </remarks>
public readonly struct LiteRtCache : IEquatable<LiteRtCache>
{
    // null => Default (don't set cache_dir; the engine writes next to the model). Otherwise the native
    // cache_dir value: ":nocache", ":memory", or a directory path.
    private readonly string? _nativeValue;

    private LiteRtCache(string? nativeValue) => _nativeValue = nativeValue;

    /// <summary>The engine default: write the cache next to the model file.</summary>
    public static LiteRtCache Default => new((string?)null);

    /// <summary>Disable the on-disk cache entirely.</summary>
    public static LiteRtCache Disabled => new(":nocache");

    /// <summary>Keep the cache in RAM only (CPU backend only; not available on Windows).</summary>
    public static LiteRtCache InMemory => new(":memory");

    /// <summary>Use a specific directory for the cache.</summary>
    /// <exception cref="System.ArgumentException"><paramref name="path"/> is null or empty, or starts
    /// with <c>':'</c> — that prefix is reserved for the engine's special cache tokens (a real directory
    /// named e.g. <c>":memory"</c> would silently become <see cref="InMemory"/>).</exception>
    public static LiteRtCache Directory(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path[0] == ':')
            throw new ArgumentException(
                $"Cache directory '{path}' starts with ':' — that prefix is reserved for the engine's " +
                "special cache tokens (e.g. \":nocache\", \":memory\"). Use Disabled/InMemory for those.",
                nameof(path));
        return new(path);
    }

    /// <summary>The native <c>cache_dir</c> value, or <c>null</c> for <see cref="Default"/>
    /// (in which case <c>set_cache_dir</c> is not called).</summary>
    internal string? NativeValue => _nativeValue;

    /// <inheritdoc/>
    public bool Equals(LiteRtCache other) => string.Equals(_nativeValue, other._nativeValue, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LiteRtCache other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _nativeValue is null ? 0 : StringComparer.Ordinal.GetHashCode(_nativeValue);

    /// <summary>A readable form: <c>Default</c>, <c>Disabled</c>, <c>InMemory</c>, or <c>Directory(path)</c>.</summary>
    public override string ToString() => _nativeValue switch
    {
        null => "Default",
        ":nocache" => "Disabled",
        ":memory" => "InMemory",
        var p => $"Directory({p})",
    };

    /// <summary>Compares two cache settings by their native value.</summary>
    public static bool operator ==(LiteRtCache left, LiteRtCache right) => left.Equals(right);

    /// <summary>Compares two cache settings by their native value.</summary>
    public static bool operator !=(LiteRtCache left, LiteRtCache right) => !left.Equals(right);
}
