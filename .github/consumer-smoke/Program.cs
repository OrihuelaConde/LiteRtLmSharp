// External consumer smoke (see the csproj header for how this is wired into pack-nuget.yml).
// Usage: ConsumerSmoke <model-path> [cpu|gpu] [raw|meai|sk|embed]   (embed takes an embedding model)
// Native logging is left at its default so the GPU load-path signal is visible on stderr: a
// healthy GPU engine prints the WebGPU/Dawn init lines; a package whose accelerator DLLs cannot
// be found prints none and dies in engine_create. Generation (not just engine creation) is the
// assertion: dxcompiler/dxil load lazily at the first shader compile, so "engine created" alone
// can be a false green.
using LiteRtLmSharp;
using LiteRtLmSharp.Extensions.AI;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

string backend = args.Length > 1 ? args[1] : "cpu";
string mode = args.Length > 2 ? args[2] : "raw";

if (mode == "embed")
{
    // The embedding engine and the IEmbeddingGenerator from the packed feed: the embedding entry points
    // resolve in the packaged native library, and a query lands close to the document it asks about.
    using var embedder = LiteRtEmbeddingEngine.Load(new LiteRtEmbeddingEngineOptions
    {
        ModelPath = args[0],
        Backend = LiteRtBackend.Parse(backend),
    });
    using IEmbeddingGenerator<string, Embedding<float>> generator = new LiteRtEmbeddingGenerator(embedder);
    var vectors = await generator.GenerateAsync(
    [
        "title: none | text: The bakery opens at 7 a.m. on weekdays.",
        "title: none | text: Renew the car insurance before March.",
        "task: search result | query: When does the bakery open?",
    ]);
    ReadOnlySpan<float> bakery = vectors[0].Vector.Span, insurance = vectors[1].Vector.Span, query = vectors[2].Vector.Span;
    static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }
    bool finite = true;
    foreach (var vector in vectors)
        foreach (float x in vector.Vector.Span)
            finite &= float.IsFinite(x);
    float related = Dot(query, bakery), unrelated = Dot(query, insurance), norm = MathF.Sqrt(Dot(query, query));
    Console.WriteLine($"ConsumerSmoke[{backend}/embed] {vectors.Count} vectors of {query.Length}, " +
                      $"related {related:F3}, unrelated {unrelated:F3}, norm {norm:F4}");
    // Written as the passing condition: a NaN fails every comparison, so it cannot slip through.
    bool ok = finite && query.Length > 0 && bakery.Length == query.Length && insurance.Length == query.Length
              && MathF.Abs(norm - 1) < 0.01f && related > unrelated + 0.05f;
    if (!ok)
        throw new InvalidOperationException(
            "The embeddings are empty, not finite, not normalized, or do not rank the related text first.");
    return;
}

using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = args[0],
    Backend = LiteRtBackend.Parse(backend),
    MaxNumTokens = 1024,
});

switch (mode)
{
    case "raw":
    {
        using var conv = engine.CreateConversation();
        Console.WriteLine($"ConsumerSmoke[{backend}/raw] says: " + conv.Send("Say hello in 4 words.").Text);
        break;
    }
    case "meai":
    {
        // IChatClient over the same engine — proves the Extensions.AI package's dependency chain
        // restores and runs against the base package from the same feed.
        using var client = new LiteRtChatClient(engine);
        var reply = await client.GetResponseAsync("Say hello in 4 words.");
        Console.WriteLine($"ConsumerSmoke[{backend}/meai] says: " + reply.Text);
        break;
    }
    case "sk":
    {
        // Semantic Kernel connector — same idea one layer up (core SK package referenced like a
        // real SK consumer would; the connector itself only depends on Abstractions).
        var builder = Kernel.CreateBuilder();
        builder.AddLiteRtChatCompletion(engine);
        var kernel = builder.Build();
        var result = await kernel.InvokePromptAsync("Say hello in 4 words.");
        Console.WriteLine($"ConsumerSmoke[{backend}/sk] says: " + result);
        break;
    }
    default:
        throw new ArgumentException($"unknown mode '{mode}'");
}
