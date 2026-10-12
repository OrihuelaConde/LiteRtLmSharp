using LiteRtLmSharp;
using LiteRtLmSharp.Native;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The opt-in multi-engine gate (<see cref="LiteRtEngine.AllowMultipleEngines"/>), tested against
/// <see cref="EngineLiveness"/> directly: it is pure bookkeeping, so the rule can be pinned without a model
/// (and without a second 2.4 GB engine). The gate is process-global static state, so every test restores
/// both the flag and the counter — the test assembly runs sequentially
/// (<c>AssemblyInfo.cs</c>), and a leaked slot would fail unrelated engine tests.
/// </summary>
public class EngineLivenessTests : IDisposable
{
    public EngineLivenessTests() => Reset();

    public void Dispose() => Reset();

    /// <summary>Back to the shipped defaults: rule in force, no engine alive.</summary>
    private static void Reset()
    {
        while (EngineLiveness.LiveCount > 0)
            EngineLiveness.Release();
        EngineLiveness.AllowMultiple = false;
    }

    [Fact]
    public void EngineLiveness_ShouldAllowOneEngineByDefault()
    {
        Assert.False(LiteRtEngine.AllowMultipleEngines);
        Assert.Equal(0, LiteRtEngine.LiveEngineCount);

        Assert.True(EngineLiveness.TryAcquire());
        Assert.Equal(1, LiteRtEngine.LiveEngineCount);
        // The rule itself: the second acquisition is refused while the first engine is alive.
        Assert.False(EngineLiveness.TryAcquire());
        Assert.Equal(1, LiteRtEngine.LiveEngineCount);

        EngineLiveness.Release();
        Assert.Equal(0, LiteRtEngine.LiveEngineCount);
        // ...and only then does the next engine fit.
        Assert.True(EngineLiveness.TryAcquire());
    }

    [Fact]
    public void EngineLiveness_ShouldCountIndependentEnginesWhenOptedIn()
    {
        LiteRtEngine.AllowMultipleEngines = true;
        Assert.True(LiteRtEngine.AllowMultipleEngines);

        for (int i = 1; i <= 3; i++)
        {
            Assert.True(EngineLiveness.TryAcquire());
            Assert.Equal(i, LiteRtEngine.LiveEngineCount);
        }

        // Each disposal frees exactly its own slot — the count never collapses to "no engine" early,
        // which is what keeps the remaining engines' protection meaningful.
        EngineLiveness.Release();
        EngineLiveness.Release();
        Assert.Equal(1, LiteRtEngine.LiveEngineCount);
        EngineLiveness.Release();
        Assert.Equal(0, LiteRtEngine.LiveEngineCount);
    }

    [Fact]
    public void EngineLiveness_ShouldClampAtZeroAndRestoreTheRule()
    {
        // A double dispose (or a finalizer racing Dispose) must not go negative: a negative count would
        // silently let the next engine past the gate.
        EngineLiveness.Release();
        Assert.Equal(0, LiteRtEngine.LiveEngineCount);

        LiteRtEngine.AllowMultipleEngines = true;
        Assert.True(EngineLiveness.TryAcquire());
        // Turning the flag off does not disturb engines that are already alive...
        LiteRtEngine.AllowMultipleEngines = false;
        Assert.Equal(1, LiteRtEngine.LiveEngineCount);
        // ...but it re-arms the rule for the next acquisition.
        Assert.False(EngineLiveness.TryAcquire());
        EngineLiveness.Release();
        Assert.True(EngineLiveness.TryAcquire());
    }
}
