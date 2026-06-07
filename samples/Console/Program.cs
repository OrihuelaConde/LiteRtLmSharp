using LiteLMSharp;

// Usage: LiteLMSharp.Sample <path-to-model.litertlm> [prompt]
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: LiteLMSharp.Sample <model.litertlm> [prompt]");
    return 1;
}

string modelPath = args[0];
string? oneShotPrompt = args.Length > 1 ? string.Join(' ', args[1..]) : null;

LiteRtEngine.SetMinLogLevel(3); // WARNING and above

Console.WriteLine($"Loading model: {modelPath}");
var sw = System.Diagnostics.Stopwatch.StartNew();

// MaxNumTokens is the TOTAL context window (KV cache = prompt + response, accumulated across
// turns). Too small and a long answer fills it, so later turns overflow and degrade into gibberish.
const int contextTokens = 4096;
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = modelPath,
    Backend = "cpu",
    MaxNumTokens = contextTokens,
});

using var conversation = engine.CreateConversation();

Console.WriteLine($"Engine ready in {sw.Elapsed.TotalSeconds:F1}s.\n");

if (oneShotPrompt is not null)
{
    Console.WriteLine("== streaming SendMessageStreamingAsync ==");
    Console.Write("Model: ");
    await foreach (string chunk in conversation.SendMessageStreamingAsync(oneShotPrompt))
        Console.Write(chunk);
    Console.WriteLine();
    return 0;
}

Console.WriteLine("Type a message (empty line to quit).");
while (true)
{
    Console.Write("\nYou: ");
    string? line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line))
        break;
    await StreamReplyAsync(conversation, line);

    // Context usage readout. Available only on binaries that export
    // litert_lm_conversation_get_token_count (added upstream after native-v0.12.0-a).
    try
    {
        int used = conversation.TokenCount;
        Console.ForegroundColor = used > contextTokens * 0.85 ? ConsoleColor.Red : ConsoleColor.DarkGray;
        Console.WriteLine($"[context: {used}/{contextTokens} tokens]");
        Console.ResetColor();
        if (used > contextTokens * 0.85)
            Console.WriteLine("[warning: context almost full — further answers may degrade]");
    }
    catch (EntryPointNotFoundException)
    {
        // Older prebuilt binary without the token-count export; skip the readout.
    }
}

return 0;

static async Task StreamReplyAsync(LiteRtConversation conversation, string prompt)
{
    Console.Write("Model: ");
    await foreach (string chunk in conversation.SendMessageStreamingAsync(prompt))
        Console.Write(chunk);
    Console.WriteLine();
}
