using System.Text;
using LiteRtLmSharp;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

// LiteRtLmSharp × Microsoft Semantic Kernel — console sample.
//
// Shows the LiteRtLmSharp.SemanticKernel connector plugging an on-device LiteRT-LM model into Semantic
// Kernel as a standard IChatCompletionService. Everything here is plain Semantic Kernel — the only
// LiteRtLmSharp-specific line is the `AddLiteRtChatCompletion(options)` call on the kernel builder: the
// container loads the on-device engine from the options and owns its lifetime.
//
// The connector is STATELESS: each Semantic Kernel call rebuilds a fresh conversation from the supplied
// ChatHistory (prior turns replayed through prefill, the final user turn sent). See docs/semantic-kernel.md.
//
//   Usage:  LiteRtLmSharp.Sample.SemanticKernel [path-to-model.litertlm] [--backend cpu|gpu] [--interactive]
//   With no model path it looks for a single *.litertlm under ./models or the repo's models/ folder.

Console.OutputEncoding = Encoding.UTF8;
LiteRtEngine.SetMinLogLevel(5); // WARNING+ — keep native chatter out of the demo output

string? modelPath = null;
string backend = "cpu";
bool interactive = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--backend" when i + 1 < args.Length: backend = args[++i]; break;
        case "--interactive" or "-i": interactive = true; break;
        default: modelPath ??= args[i]; break;
    }
}

modelPath ??= FindModel();
if (modelPath is null || !File.Exists(modelPath))
{
    Console.WriteLine("Model file not found. Pass the path to a .litertlm file as the first argument, e.g.:");
    Console.WriteLine("  LiteRtLmSharp.Sample.SemanticKernel C:\\models\\gemma-4-E2B-it.litertlm");
    return 1;
}

Banner();
Console.WriteLine($"Loading model ({Path.GetFileName(modelPath)}, {backend}) — this loads the weights, please wait …\n");

// ── Build a Semantic Kernel whose chat-completion service is the on-device model ──────────────────────
//    AddLiteRtChatCompletion(options) is the whole integration: the connector loads the engine from the
//    options, the container owns its lifetime (no LiteRtEngine to load or dispose by hand), and the model is
//    exposed as an IChatCompletionService (and, underneath, as a Microsoft.Extensions.AI IChatClient — so the
//    same model is usable from Microsoft Agent Framework / MEAI too). `eager: true` loads the weights now, so
//    a bad model path or backend surfaces here rather than on the first call.
IKernelBuilder builder = Kernel.CreateBuilder();
builder.AddLiteRtChatCompletion(
    new LiteRtEngineOptions
    {
        ModelPath = modelPath,
        Backend = backend,
        MaxNumTokens = 4096, // total context window (prompt + replies, accumulated across turns)
    },
    modelId: Path.GetFileNameWithoutExtension(modelPath),
    eager: true);
Kernel kernel = builder.Build();

Console.WriteLine("Engine loaded and Semantic Kernel built.\n");

// ── Demo A: a Semantic Kernel prompt function (templated prompt + execution settings) ─────────────────
await DemoPromptFunctionAsync(kernel);

// ── Demo B: a streaming prompt via the kernel ─────────────────────────────────────────────────────────
await DemoStreamingPromptAsync(kernel);

// ── Demo C: direct multi-turn chat through IChatCompletionService, streaming ──────────────────────────
await DemoMultiTurnChatAsync(kernel);

if (interactive)
    await InteractiveChatAsync(kernel);

// The engine is owned by the kernel's service container; disposing the provider tears it down.
(kernel.Services as IDisposable)?.Dispose();

Console.WriteLine("\nDone.");
return 0;

// ──────────────────────────────────────────────────────────────────────────────────────────────────────

static async Task DemoPromptFunctionAsync(Kernel kernel)
{
    Rule("Demo A — Semantic Kernel prompt function");
    Console.WriteLine("kernel.InvokePromptAsync with a templated prompt and per-call execution settings.\n");

    // A LiteRtPromptExecutionSettings flows our sampler/output knobs through SK. A plain
    // PromptExecutionSettings with ExtensionData ({"temperature":…}) would work too (e.g. from a YAML prompt).
    var settings = new LiteRtLmSharp.SemanticKernel.LiteRtPromptExecutionSettings
    {
        Temperature = 0.7f,
        MaxTokens = 200        
    };

    KernelArguments arguments = new(settings) { ["topic"] = "on-device AI" };
    const string promptTemplate = "Write a single upbeat sentence about {{$topic}}.";

    FunctionResult result = await kernel.InvokePromptAsync(promptTemplate, arguments);
    Console.WriteLine($"Prompt : {promptTemplate.Replace("{{$topic}}", "on-device AI")}");
    Console.WriteLine($"Answer : {result}\n");
}

static async Task DemoStreamingPromptAsync(Kernel kernel)
{
    Rule("Demo B — streaming prompt");
    Console.WriteLine("kernel.InvokePromptStreamingAsync — tokens arrive as they are generated.\n");

    Console.Write("Answer : ");
    await foreach (StreamingKernelContent piece in kernel.InvokePromptStreamingAsync(
        "List three benefits of running language models on-device. Be brief."))
    {
        Console.Write(piece);
    }
    Console.WriteLine("\n");
}

static async Task DemoMultiTurnChatAsync(Kernel kernel)
{
    Rule("Demo C — multi-turn chat (IChatCompletionService, streaming)");
    Console.WriteLine("A ChatHistory with a system prompt; two user turns; replies streamed.\n");

    IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();
    var settings = new LiteRtLmSharp.SemanticKernel.LiteRtPromptExecutionSettings { Temperature = 0.8f, MaxTokens = 256 };

    ChatHistory history = new("You are a concise, friendly assistant. Keep answers to one or two sentences.");

    foreach (string userTurn in new[] { "Hi! What are you good at?", "Give me one concrete example." })
    {
        history.AddUserMessage(userTurn);
        Console.WriteLine($"You    : {userTurn}");
        Console.Write("Model  : ");

        var reply = new StringBuilder();
        await foreach (StreamingChatMessageContent chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel))
        {
            Console.Write(chunk.Content);
            reply.Append(chunk.Content);
        }
        Console.WriteLine("\n");

        // Record the assistant turn so the next call sees the full conversation (SK is stateless).
        history.AddAssistantMessage(reply.ToString());
    }
}

static async Task InteractiveChatAsync(Kernel kernel)
{
    Rule("Interactive chat — empty line to quit");
    IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();
    var settings = new LiteRtLmSharp.SemanticKernel.LiteRtPromptExecutionSettings { Temperature = 0.8f, MaxTokens = 512 };
    ChatHistory history = new("You are a concise, helpful assistant.");

    while (true)
    {
        Console.Write("\nYou    : ");
        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
            break;

        history.AddUserMessage(line);
        Console.Write("Model  : ");
        var reply = new StringBuilder();
        await foreach (StreamingChatMessageContent chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel))
        {
            Console.Write(chunk.Content);
            reply.Append(chunk.Content);
        }
        Console.WriteLine();
        history.AddAssistantMessage(reply.ToString());
    }
}

// ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

static string? FindModel()
{
    foreach (string dir in new[] { "models", Path.Combine("..", "..", "..", "..", "..", "models") })
    {
        if (!Directory.Exists(dir)) continue;
        string? first = Directory.EnumerateFiles(dir, "*.litertlm").FirstOrDefault();
        if (first is not null) return first;
    }
    return null;
}

static void Banner()
{
    Console.WriteLine("┌──────────────────────────────────────────────────────────────┐");
    Console.WriteLine("│  LiteRtLmSharp × Microsoft Semantic Kernel — console sample  │");
    Console.WriteLine("└──────────────────────────────────────────────────────────────┘\n");
}

static void Rule(string title)
{
    Console.WriteLine(new string('─', 64));
    Console.WriteLine(title);
    Console.WriteLine(new string('─', 64));
}
