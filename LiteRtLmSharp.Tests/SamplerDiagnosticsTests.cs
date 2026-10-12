using LiteRtLmSharp;
using Xunit;

namespace LiteRtLmSharp.Tests;

/// <summary>
/// The sampler diagnostics behind <see cref="LiteRtSamplerNotSupportedException"/> — the CPU backend of
/// LiteRT-LM v0.14.0–v0.18.0 implements only TopP, and its rejection (<c>UNIMPLEMENTED: Sampler type: 3</c>)
/// names a number instead of the problem. These are model-free tests of the message reader and the
/// recommended profile; the model-backed behaviour (a rejected conversation, the probe) is in
/// <see cref="EngineImprovementsModelTests"/>.
/// </summary>
public class SamplerDiagnosticsTests
{
    [Theory]
    // The two shapes the runtime has been observed to produce (v0.14.0–v0.18.0, CPU).
    [InlineData("UNIMPLEMENTED: Sampler type: 3")]
    [InlineData("Sampler type: 1 not implemented on this backend")]
    [InlineData("INVALID_ARGUMENT: sampler type is not supported by the CPU sampler factory")]
    // Case-insensitive, because the binding reads the runtime's text, not a contract.
    [InlineData("unimplemented: sampler TYPE: 3")]
    public void UnsupportedSamplerError_ShouldBeRecognized(string nativeReason)
        => Assert.True(LiteRtConversation.IsUnsupportedSamplerError(nativeReason));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UNIMPLEMENTED: EnableYnnpack is only supported on linux-arm64")]
    [InlineData("INVALID_ARGUMENT: Cache directory does not exist or is not writable: C:\\nope")]
    [InlineData("CANCELLED: Cancelled by the user")]
    [InlineData("Failed to invoke the compiled model Failed to allocate tensors")]
    public void UnsupportedSamplerError_ShouldNotMatchOtherFailures(string? nativeReason)
        => Assert.False(LiteRtConversation.IsUnsupportedSamplerError(nativeReason));

    [Fact]
    public void TranslateSamplerFailure_ShouldExplainAndNameTheStrategy()
    {
        var native = new LiteRtException(
            "litert_lm_conversation_create returned null: UNIMPLEMENTED: Sampler type: 3",
            LiteRtStatusCode.Unimplemented);

        var translated = Assert.IsType<LiteRtSamplerNotSupportedException>(
            LiteRtConversation.TranslateSamplerFailure(native, LiteRtSamplerType.Greedy));

        Assert.Equal(LiteRtSamplerType.Greedy, translated.SamplerType);
        Assert.Equal(LiteRtStatusCode.Unimplemented, translated.StatusCode);
        // The runtime's own reason survives (diagnosis), next to the explanation and the way out.
        Assert.Contains("Sampler type: 3", translated.Message, StringComparison.Ordinal);
        Assert.Contains("Greedy", translated.Message, StringComparison.Ordinal);
        Assert.Contains("NearGreedy", translated.Message, StringComparison.Ordinal);
        // Callers that catch the base type must keep working.
        Assert.IsAssignableFrom<LiteRtException>(translated);
    }

    [Fact]
    public void TranslateSamplerFailure_ShouldPassThroughUnrelatedFailures()
    {
        var native = new LiteRtException("litert_lm_conversation_create returned null: NOT_FOUND: no such model");
        Assert.Same(native, LiteRtConversation.TranslateSamplerFailure(native, LiteRtSamplerType.TopP));
    }

    [Fact]
    public void TranslateSamplerFailure_ShouldStillExplainWithoutAKnownStrategy()
    {
        // A stream error chunk can carry the failure without the binding knowing which sampler it was
        // (the model file's own sampler, say), so the message must stand without a strategy name.
        var native = new LiteRtException("litert_lm_conversation_send_message returned null: UNIMPLEMENTED: Sampler type: 3");

        var translated = Assert.IsType<LiteRtSamplerNotSupportedException>(
            LiteRtConversation.TranslateSamplerFailure(native, strategy: null));

        Assert.Null(translated.SamplerType);
        Assert.Contains("does not implement that sampler", translated.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void NearGreedy_ShouldBeTheSupportedAlmostArgmaxProfile()
    {
        LiteRtSamplerParams profile = LiteRtSamplerParams.NearGreedy;

        // TopP is the only strategy the CPU backend implements, and a low temperature over a narrow
        // nucleus is what makes it behave like the greedy decoding that backend cannot do.
        Assert.Equal(LiteRtSamplerType.TopP, profile.Strategy);
        Assert.Equal(0.5f, profile.TopP);
        Assert.Equal(0.1f, profile.Temperature);
        Assert.Equal(40, profile.TopK);
        // Deterministic: the same prompt and state reproduce the same reply.
        Assert.Equal(0, profile.Seed);
    }

    [Fact]
    public void SamplerNotSupportedException_ShouldDefaultToUnimplemented()
    {
        var ex = new LiteRtSamplerNotSupportedException("no sampler", LiteRtSamplerType.TopK);
        Assert.Equal(LiteRtStatusCode.Unimplemented, ex.StatusCode);
        Assert.Equal(LiteRtSamplerType.TopK, ex.SamplerType);
    }
}
