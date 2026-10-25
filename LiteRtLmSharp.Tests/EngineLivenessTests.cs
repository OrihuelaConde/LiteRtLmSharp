using LiteRtLmSharp.Native;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The live-engine gate behind <see cref="LiteRtEngineOptions.AllowMultipleEngines"/>, tested on fresh
/// <see cref="EngineLiveness"/> instances: it is pure bookkeeping, so the rule can be pinned without a model
/// (and without a second 2.4 GB engine), and without touching <see cref="EngineLiveness.Default"/>, which a
/// live engine of another test may hold.
/// </summary>
public class EngineLivenessTests
{
    [Fact]
    public void EngineLiveness_ShouldAllowOneEngineByDefault()
    {
        var gate = new EngineLiveness();
        Assert.Equal(0, gate.LiveCount);

        Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: false));
        Assert.Equal(1, gate.LiveCount);
        // The rule itself: a second engine is refused while the first one is alive.
        Assert.Equal(EngineAdmission.NewEngineNotOptedIn, gate.TryAcquire(allowMultiple: false));
        Assert.Equal(1, gate.LiveCount);

        gate.Release(allowMultiple: false);
        Assert.Equal(0, gate.LiveCount);
        // ...and only then does the next engine fit.
        Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: false));
    }

    [Fact]
    public void EngineLiveness_ShouldCountEnginesThatAllOptedIn()
    {
        var gate = new EngineLiveness();
        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: true));
            Assert.Equal(i, gate.LiveCount);
        }

        // Each disposal frees exactly its own slot; the count never collapses to "no engine" early.
        gate.Release(allowMultiple: true);
        gate.Release(allowMultiple: true);
        Assert.Equal(1, gate.LiveCount);
        gate.Release(allowMultiple: true);
        Assert.Equal(0, gate.LiveCount);
    }

    [Fact]
    public void EngineLiveness_ShouldNotLetAnOptedInEngineJoinOneThatDidNotOptIn()
    {
        var gate = new EngineLiveness();
        Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: false));

        // The live engine was loaded by code that relies on being alone: nothing may load next to it.
        Assert.Equal(EngineAdmission.LiveEngineNotOptedIn, gate.TryAcquire(allowMultiple: true));
        Assert.Equal(1, gate.LiveCount);

        gate.Release(allowMultiple: false);
        Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: true));
        Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: true));
    }

    [Fact]
    public void EngineLiveness_ShouldNotLetAnEngineThatDidNotOptInJoinOptedInEngines()
    {
        var gate = new EngineLiveness();
        Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: true));
        Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: true));

        Assert.Equal(EngineAdmission.NewEngineNotOptedIn, gate.TryAcquire(allowMultiple: false));
        Assert.Equal(2, gate.LiveCount);

        // Once the opted-in engines are gone, the default rule applies again.
        gate.Release(allowMultiple: true);
        gate.Release(allowMultiple: true);
        Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: false));
        Assert.Equal(EngineAdmission.LiveEngineNotOptedIn, gate.TryAcquire(allowMultiple: true));
    }

    [Fact]
    public void EngineLiveness_ShouldClampAtZero()
    {
        // A release with nothing alive (a defensive double release) must not go negative: a negative count
        // would let the next engines past the gate.
        var gate = new EngineLiveness();
        gate.Release(allowMultiple: false);
        gate.Release(allowMultiple: true);
        Assert.Equal(0, gate.LiveCount);

        Assert.Equal(EngineAdmission.Admitted, gate.TryAcquire(allowMultiple: false));
        Assert.Equal(EngineAdmission.NewEngineNotOptedIn, gate.TryAcquire(allowMultiple: false));
    }

    [Fact]
    public void EngineLiveness_ShouldAdmitExactlyOneOfManyConcurrentEnginesThatDidNotOptIn()
    {
        // Loads race from several threads: with the default rule exactly one may win, every time.
        for (int round = 0; round < 50; round++)
        {
            var gate = new EngineLiveness();
            using var start = new Barrier(8);
            var results = new EngineAdmission[8];
            var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                results[i] = gate.TryAcquire(allowMultiple: i % 2 == 0);
            })).ToArray();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            int admitted = results.Count(r => r == EngineAdmission.Admitted);
            int optedInAdmitted = Enumerable.Range(0, 8).Count(i => i % 2 == 0 && results[i] == EngineAdmission.Admitted);
            int notOptedInAdmitted = admitted - optedInAdmitted;
            // Either one engine that did not opt in is alone, or only opted-in engines got in.
            Assert.True(notOptedInAdmitted == 0 || admitted == 1, $"round {round}: {string.Join(",", results)}");
            Assert.Equal(admitted, gate.LiveCount);
        }
    }
}
