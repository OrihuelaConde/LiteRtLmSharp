using LiteRtLmSharp;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The hint for a sampler type the runtime does not implement, such as one the model file declares.
/// Model-free; the model-backed sampler tests are in <see cref="EngineImprovementsModelTests"/>.
/// </summary>
public class SamplerDiagnosticsTests
{
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
