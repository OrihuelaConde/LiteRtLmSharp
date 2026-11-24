using LiteRtLmSharp;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// How sampler strategies reach the runtime (<see cref="LiteRtConversation.NativeSampler"/>) and the hint for
/// the one sampler failure left, a model-declared sampler type the backend does not build. Model-free; the
/// model-backed half is in <see cref="EngineImprovementsModelTests"/>.
/// </summary>
public class SamplerDiagnosticsTests
{
    [Fact]
    public void NativeSampler_ShouldSendGreedyAsTopPWithTopKOne_KeepingTheRest()
    {
        var greedy = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.Greedy, TopK = 40, TopP = 0.9f, Temperature = 0.7f, Seed = 5 };

        var (type, topK, topP) = LiteRtConversation.NativeSampler(greedy);

        Assert.Equal(LiteRtSamplerType.TopP, type);
        Assert.Equal(1, topK);
        Assert.Equal(0.9f, topP);
    }

    [Fact]
    public void NativeSampler_ShouldSendTopKAsTopPWithTopPOne_KeepingTheRest()
    {
        var topKOnly = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.TopK, TopK = 8, TopP = 0.5f };

        var (type, topK, topP) = LiteRtConversation.NativeSampler(topKOnly);

        Assert.Equal(LiteRtSamplerType.TopP, type);
        Assert.Equal(8, topK);
        Assert.Equal(1.0f, topP);
    }

    [Theory]
    [InlineData(LiteRtSamplerType.TopP)]
    [InlineData(LiteRtSamplerType.Unspecified)]
    public void NativeSampler_ShouldLeaveOtherStrategiesUnchanged(LiteRtSamplerType strategy)
    {
        var sampler = new LiteRtSamplerParams { Strategy = strategy, TopK = 12, TopP = 0.8f };

        var (type, topK, topP) = LiteRtConversation.NativeSampler(sampler);

        Assert.Equal(strategy, type);
        Assert.Equal(12, topK);
        Assert.Equal(0.8f, topP);
    }

    [Theory]
    // The messages upstream emits (runtime/components/sampler_factory.cc, v0.14.0-v0.18.0, CPU backend).
    [InlineData("UNIMPLEMENTED: Sampler type: 3 not implemented yet.")]
    [InlineData("UNIMPLEMENTED: Sampler type: 1 not implemented yet.")]
    public void UnsupportedSamplerError_ShouldBeRecognized(string nativeReason)
        => Assert.True(LiteRtConversation.IsUnsupportedSamplerError(nativeReason));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UNIMPLEMENTED: EnableYnnpack is only supported on linux-arm64")]
    [InlineData("INVALID_ARGUMENT: Cache directory does not exist or is not writable: C:\\nope")]
    [InlineData("CANCELLED: Task cancelled")]
    [InlineData("INTERNAL: Task failed with state: 7.")]
    public void UnsupportedSamplerError_ShouldNotMatchOtherFailures(string? nativeReason)
        => Assert.False(LiteRtConversation.IsUnsupportedSamplerError(nativeReason));

    [Fact]
    public void SendFailureHint_ShouldExplainAnUnsupportedModelSampler()
    {
        string? hint = LiteRtConversation.SendFailureHint(
            "UNIMPLEMENTED: Sampler type: 1 not implemented yet.", mediaHintApplies: false, smallContext: false);

        Assert.Equal(LiteRtConversation.SamplerNotSupportedHint, hint);
        Assert.Contains("LiteRtConversationOptions.Sampler", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void SendFailureHint_ShouldNotMentionTheSamplerForOtherFailures()
    {
        string? hint = LiteRtConversation.SendFailureHint(
            "INTERNAL: Task failed with state: 7.", mediaHintApplies: false, smallContext: false);

        Assert.Null(hint);
    }
}
