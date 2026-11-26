namespace LiteRtLmSharp.SampleMaui.Services;

/// <summary>A known LiteRT-LM model that the app can download and run.</summary>
/// <remarks>
/// <paramref name="GpuOnly"/>: the Models page offers only the GPU backend for the model (this catalog
/// sets it on the 12B entry). A .litertlm file that lists only GPU in
/// <see cref="LiteRtLmSharp.LiteRtModelInfo.SupportedBackends"/> fails to load on CPU: with LiteRT-LM
/// v0.20.0, gemma-4-E2B-it-gpu failed with NOT_FOUND ("tf_lite_prefill_decode not found in the model").
/// <paramref name="SupportsSpeculativeDecoding"/>: the model ships an MTP drafter (all the Gemma 4
/// builds do), so speculative decoding can be turned on. Turning it on does not guarantee a speedup: with
/// gemma-4-E2B it lowered decode throughput on every desktop CPU and GPU backend measured, and it was not
/// measured on a device with LiteRT-LM v0.20.0. For these models, the Models page asks on each load whether
/// to enable speculative decoding, with no default; for the rest it doesn't ask.
/// <paramref name="SupportsVision"/> / <paramref name="SupportsAudio"/>: the model can take image /
/// audio attachments (the Gemma 4 E-series are full text+vision+audio). When set, the engine is loaded
/// with the matching vision/audio backend and the Chat tab shows the attach buttons.
/// </remarks>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    string FileName,
    string DownloadUrl,
    string ApproxSize,
    string RamHint,
    bool MobileFriendly,
    bool GpuOnly = false,
    bool SupportsSpeculativeDecoding = true,
    bool SupportsVision = false,
    bool SupportsAudio = false);

/// <summary>
/// Gemma models published as ready-to-run <c>.litertlm</c> files by the
/// <c>litert-community</c> org on Hugging Face (open license, not gated).
/// </summary>
public static class ModelCatalog
{
    private const string Hf = "https://huggingface.co";

    public static IReadOnlyList<ModelInfo> Models { get; } =
    [
        new(
            Id: "gemma-4-E2B-it",
            DisplayName: "Gemma 4 E2B (instruct)",
            FileName: "gemma-4-E2B-it.litertlm",
            DownloadUrl: $"{Hf}/litert-community/gemma-4-E2B-it-litert-lm/resolve/main/gemma-4-E2B-it.litertlm",
            ApproxSize: "~2.5 GB",
            RamHint: "Needs ~3 GB free RAM — high-end phones",
            MobileFriendly: true,
            SupportsVision: true,
            SupportsAudio: true),
        new(
            Id: "gemma-4-E4B-it",
            DisplayName: "Gemma 4 E4B (instruct)",
            FileName: "gemma-4-E4B-it.litertlm",
            DownloadUrl: $"{Hf}/litert-community/gemma-4-E4B-it-litert-lm/resolve/main/gemma-4-E4B-it.litertlm",
            ApproxSize: "~4 GB",
            RamHint: "Needs ~5 GB free RAM — flagship phones / desktop",
            MobileFriendly: true,
            SupportsVision: true,
            SupportsAudio: true),
        new(
            Id: "gemma-4-12B-it",
            DisplayName: "Gemma 4 12B (instruct)",
            FileName: "gemma-4-12B-it.litertlm",
            DownloadUrl: $"{Hf}/litert-community/gemma-4-12B-it-litert-lm/resolve/main/gemma-4-12B-it.litertlm",
            ApproxSize: "~6.5 GB",
            RamHint: "Needs ~8 GB free RAM — GPU only, desktop-class",
            MobileFriendly: false,
            GpuOnly: true,
            SupportsVision: true,
            SupportsAudio: true),
    ];
}
