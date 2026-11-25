namespace LiteRtLmSharp;

/// <summary>The diagnostic ids and guide links of the members marked with
/// <see cref="System.Diagnostics.CodeAnalysis.ExperimentalAttribute"/>. An id is part of the contract: a consumer
/// suppresses it by name, so it never changes.</summary>
internal static class ExperimentalApis
{
    /// <summary>Session checkpoints, rewind and reset: <see cref="LiteRtSession.SaveCheckpoint"/>,
    /// <see cref="LiteRtSession.RewindTo"/>, <see cref="LiteRtSession.Reset"/> and
    /// <see cref="LiteRtSessionCheckpoint"/>.</summary>
    public const string SessionCheckpoints = "LITERTLM001";

    /// <summary>The guide section on <see cref="SessionCheckpoints"/>.</summary>
    public const string SessionCheckpointsUrl =
        "https://orihuelaconde.github.io/LiteRtLmSharp/sessions.html#experimental-checkpoints-rewind-and-reset";
}
