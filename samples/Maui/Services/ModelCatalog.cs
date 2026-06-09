namespace LiteLMSharp.SampleMaui.Services;

/// <summary>A known LiteRT-LM model that the app can download and run.</summary>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    string FileName,
    string DownloadUrl,
    string ApproxSize,
    string RamHint,
    bool MobileFriendly);

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
            MobileFriendly: true),
        new(
            Id: "gemma-4-E4B-it",
            DisplayName: "Gemma 4 E4B (instruct)",
            FileName: "gemma-4-E4B-it.litertlm",
            DownloadUrl: $"{Hf}/litert-community/gemma-4-E4B-it-litert-lm/resolve/main/gemma-4-E4B-it.litertlm",
            ApproxSize: "~4 GB",
            RamHint: "Needs ~5 GB free RAM — flagship phones / desktop",
            MobileFriendly: true),
        new(
            Id: "gemma-4-12B-it",
            DisplayName: "Gemma 4 12B (instruct)",
            FileName: "gemma-4-12B-it.litertlm",
            DownloadUrl: $"{Hf}/litert-community/gemma-4-12B-it-litert-lm/resolve/main/gemma-4-12B-it.litertlm",
            ApproxSize: "~12 GB",
            RamHint: "Desktop-class only",
            MobileFriendly: false),
    ];
}
