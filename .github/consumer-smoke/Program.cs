// External consumer smoke (see the csproj header for how this is wired into pack-nuget.yml).
// Usage: ConsumerSmoke <model-path> [cpu|gpu] [raw|meai|sk]
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
