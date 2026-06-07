using LiteLMSharp;

// Usage:
//   LiteLMSharp.Sample <model.litertlm> [prompt]   interactive / one-shot chat (streaming)
//   LiteLMSharp.Sample <model.litertlm> --tools    function-calling demo (blocking)
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: LiteLMSharp.Sample <model.litertlm> [prompt | --tools]");
    return 1;
}

string modelPath = args[0];
bool toolsDemo = args.Contains("--tools");
string[] rest = args[1..].Where(a => a != "--tools").ToArray();
string? oneShotPrompt = rest.Length > 0 ? string.Join(' ', rest) : null;

LiteRtEngine.SetMinLogLevel(3); // WARNING and above

Console.WriteLine($"Loading model: {modelPath}");
var sw = System.Diagnostics.Stopwatch.StartNew();

// MaxNumTokens is the TOTAL context window (KV cache = prompt + response, accumulated across turns).
const int contextTokens = 4096;
using var engine = LiteRtEngine.Load(new LiteRtEngineOptions
{
    ModelPath = modelPath,
    Backend = "cpu",
    MaxNumTokens = contextTokens,
});
Console.WriteLine($"Engine ready in {sw.Elapsed.TotalSeconds:F1}s.\n");

if (toolsDemo)
{
    RunToolsDemo(engine);
    return 0;
}

using var conversation = engine.CreateConversation();

if (oneShotPrompt is not null)
{
    await StreamReplyAsync(conversation, oneShotPrompt);
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

    try
    {
        int used = conversation.TokenCount;
        Console.ForegroundColor = used > contextTokens * 0.85 ? ConsoleColor.Red : ConsoleColor.DarkGray;
        Console.WriteLine($"[context: {used}/{contextTokens} tokens]");
        Console.ResetColor();
    }
    catch (EntryPointNotFoundException)
    {
        // Binary without the token-count export (e.g. 032334d8); skip the readout.
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

// Demonstrates the function-calling loop: define a tool, let the model request it,
// execute it locally, feed the result back, and print the final answer. Uses the
// blocking path. Requires a version-matched native binary (config path crashes on 0.12.0-a).
static void RunToolsDemo(LiteRtEngine engine)
{
    var weatherTool = new LiteRtTool(
        Name: "get_current_weather",
        Description: "Get the current weather for a city.",
        ParametersJson: """
        {"type":"object","properties":{"location":{"type":"string","description":"City, e.g. Tokyo"},"unit":{"type":"string","enum":["celsius","fahrenheit"]}},"required":["location"]}
        """);

    using var conv = engine.CreateConversation(new LiteRtConversationOptions
    {
        SystemMessage = "You are a helpful assistant. Use the available tools when needed.",
        Tools = [weatherTool],
        EnableConstrainedDecoding = true,
        MaxOutputTokens = 256,
    });

    const string prompt = "What's the weather in Tokyo in celsius?";
    Console.WriteLine($"You: {prompt}");

    LiteRtResponse response = conv.Send(prompt);

    if (response.IsToolCall)
    {
        var results = new List<LiteRtToolResult>();
        foreach (LiteRtToolCall call in response.ToolCalls)
        {
            Console.WriteLine($"[tool call] {call.Name}({call.ArgumentsJson})");
            string result = ExecuteTool(call);
            Console.WriteLine($"[tool result] {result}");
            results.Add(new LiteRtToolResult(call.Name, result));
        }

        LiteRtResponse final = conv.SendToolResults(results);
        Console.WriteLine($"Model: {final.Text}");
    }
    else
    {
        Console.WriteLine($"Model (no tool call): {response.Text}");
        Console.WriteLine($"[raw] {response.RawJson}");
    }
}

// Mock tool implementation.
static string ExecuteTool(LiteRtToolCall call) => call.Name switch
{
    "get_current_weather" => """{"temperature":15,"unit":"celsius","conditions":"sunny"}""",
    _ => """{"error":"unknown tool"}""",
};
