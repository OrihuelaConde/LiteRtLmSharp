namespace LiteRtLmSharp;

/// <summary>
/// A compute backend for the engine or one of its encoders. Use a well-known value
/// (<see cref="Cpu"/>, <see cref="Gpu"/>, <see cref="Npu"/>) or <see cref="Custom"/> for a backend
/// exposed by your own native build. The underlying native identifier is <see cref="Value"/>; backends
/// compare by it.
/// </summary>
/// <remarks>
/// This is a closed-but-extensible "smart enum": the named values cover the backends this project
/// builds and validates, while <see cref="Custom"/> lets a caller who ships their own LiteRT-LM
/// natives target a backend those binaries add, without waiting for a new enum member. The default
/// value (<c>default(LiteRtBackend)</c>) is <see cref="Cpu"/>.
/// </remarks>
public readonly struct LiteRtBackend : IEquatable<LiteRtBackend>
{
    // null is treated as "cpu" so default(LiteRtBackend) == Cpu (a sensible, safe default).
    private readonly string? _value;

    private LiteRtBackend(string value) => _value = value;

    /// <summary>The native backend identifier passed to LiteRT-LM (e.g. <c>"cpu"</c>, <c>"gpu"</c>,
    /// <c>"npu"</c>).</summary>
    public string Value => _value ?? "cpu";

    /// <summary>Run on the CPU. The default backend.</summary>
    public static LiteRtBackend Cpu => new("cpu");

    /// <summary>Run on the GPU (WebGPU / Metal / OpenCL, depending on platform).</summary>
    public static LiteRtBackend Gpu => new("gpu");

    /// <summary>
    /// Run on a neural processing unit (NPU). Requires vendor NPU libraries the native engine can load
    /// (Qualcomm / MediaTek on Android, Intel OpenVINO on Windows/Linux), which this project does not
    /// ship or validate. On a build without them, <see cref="LiteRtEngine.Load"/> fails with a
    /// <see cref="LiteRtException"/>.
    /// </summary>
    public static LiteRtBackend Npu => new("npu");

    /// <summary>
    /// A backend identified by an arbitrary native string, for a custom LiteRT-LM build that supports
    /// additional backends. The string is passed through to the native layer verbatim.
    /// </summary>
    /// <exception cref="System.ArgumentException"><paramref name="value"/> is null or empty.</exception>
    public static LiteRtBackend Custom(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    /// <summary>
    /// Parses a backend identifier (case-insensitive): <c>"cpu"</c>, <c>"gpu"</c> and <c>"npu"</c> map to
    /// the named values; anything else becomes a <see cref="Custom"/> backend carrying the given string.
    /// Handy for config/CLI-driven hosts.
    /// </summary>
    /// <exception cref="System.ArgumentException"><paramref name="value"/> is null or empty.</exception>
    public static LiteRtBackend Parse(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "cpu" => Cpu,
            "gpu" => Gpu,
            "npu" => Npu,
            // Whitespace-normalize custom values too (case is preserved): Parse(" xpu ") and
            // Parse("xpu") must yield the same Value/equality, or config-driven hosts get
            // silently distinct backends.
            _ => new(value.Trim()),
        };
    }

    /// <inheritdoc/>
    public bool Equals(LiteRtBackend other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LiteRtBackend other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>The native backend identifier (same as <see cref="Value"/>).</summary>
    public override string ToString() => Value;

    /// <summary>Compares two backends by their native value.</summary>
    public static bool operator ==(LiteRtBackend left, LiteRtBackend right) => left.Equals(right);

    /// <summary>Compares two backends by their native value.</summary>
    public static bool operator !=(LiteRtBackend left, LiteRtBackend right) => !left.Equals(right);
}
