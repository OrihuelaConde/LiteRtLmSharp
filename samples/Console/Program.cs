using System.Diagnostics;
using System.Text;
using LiteRtLmSharp;
using LiteRtLmSharp.Sample;

// LiteRtLmSharp console sample.
//
// THIS file is the one worth reading: every LiteRtLmSharp API call in the sample lives here.
// ConsoleUi.cs is console plumbing (colors, menus, pickers, argument parsing) and teaches
// nothing about the library.
//
// The library in one paragraph: LiteRtEngine.Load() loads the model (heavy — the weights);
// engine.CreateConversation() starts a cheap, stateful chat session; conversation.Send() /
// SendMessageStreamingAsync() generate replies. Dispose conversations, then the engine.
// Only one engine may be alive at a time — dispose it first to switch model or backend.
//
// Interactive (no args):   LiteRtLmSharp.Sample
// Scripted (for testing):  LiteRtLmSharp.Sample <model.litertlm> [prompt]
//                          LiteRtLmSharp.Sample <model.litertlm> --tools
//                          LiteRtLmSharp.Sample <model.litertlm> --spec     (speculative decoding)
//                          LiteRtLmSharp.Sample <model.litertlm> --backend gpu --context 8192

Console.OutputEncoding = Encoding.UTF8;
LiteRtEngine.SetMinLogLevel(3); // WARNING+ (early native logs still print to stderr)

CliArgs cli = CliArgs.Parse(args);

string? modelPath = cli.ModelPath;
string backend = cli.Backend;
int contextTokens = cli.ContextTokens;
bool speculative = cli.Speculative;

if (modelPath is null)
{
    Ui.Banner();
    modelPath = Picker.Model();
    if (modelPath is null)
        return 0; // user quit
    backend = Picker.Backend();
    speculative = Picker.Speculative();
}

while (true)
{
    LiteRtEngine engine;
    try
    {
        // Speculative decoding on the WebGPU GPU backend needs the disk cache OFF (the MTP drafter's
        // shared weight-cache file fails to open on Windows otherwise — upstream issue). Default to
        // disabling it for that combo unless the user picked a cache mode explicitly via --cache.
        string? cacheDir = cli.CacheDir
            ?? (speculative && backend == "gpu" ? LiteRtEngineOptions.CacheDisabled : null);

        string cacheNote = cacheDir switch
        {
            LiteRtEngineOptions.CacheDisabled => ", cache off",
            LiteRtEngineOptions.CacheInMemory => ", cache in-memory",
            null or "" => "",
            _ => $", cache {cacheDir}",
        };
        Ui.Info($"\nLoading model ({Path.GetFileName(modelPath)}, {backend}, ctx {contextTokens}"
            + $"{(speculative ? ", speculative" : "")}{cacheNote}) …");
        var sw = Stopwatch.StartNew();

        engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = modelPath,
            Backend = backend,          // "cpu" or "gpu"
            MaxNumTokens = contextTokens, // total context window (prompt + replies, all turns)
            EnableSpeculativeDecoding = speculative, // MTP drafter → faster decode (supported models)
            EnableBenchmark = true,     // so the gauge can show decode tok/s and time-to-first-token
            CacheDir = cacheDir,        // null = next to model; ":nocache" disables it (GPU+spec)
        });

        Ui.Success($"Engine ready in {sw.Elapsed.TotalSeconds:F1}s.\n");
    }
    catch (Exception ex)
    {
        Ui.Error($"Failed to load the model: {ex.Message}");
        return 1;
    }

    using (engine)
    {
        // Scripted modes (kept for testing/automation).
        if (cli.ToolsMode)
        {
            RunToolsDemo(engine);
            return 0;
        }
        if (cli.OneShotPrompt is not null)
        {
            using var c = engine.CreateConversation();
            await StreamReplyAsync(c, cli.OneShotPrompt, contextTokens);
            return 0;
        }

        bool switchModel = await MainMenuAsync(engine, modelPath, backend, contextTokens, speculative);
        if (!switchModel)
            return 0;
    } // ← the engine is disposed here; only then may a new one be loaded.

    modelPath = Picker.Model();
    if (modelPath is null)
        return 0;
    backend = Picker.Backend();
    speculative = Picker.Speculative();
}

// ─────────────────────────── Menu loop ───────────────────────────

// Returns true when the user wants to switch model/backend (caller disposes this engine
// and loads a new one), false to quit.
static async Task<bool> MainMenuAsync(
    LiteRtEngine engine, string modelPath, string backend, int contextTokens, bool speculative)
{
    LiteRtConversation chat = NewChat(engine);
    try
    {
        while (true)
        {
            Ui.Rule();
            Ui.Menu("Main menu",
                "Chat (streaming)",
                "Function-calling demo",
                "New conversation",
                "Switch model / backend / speculative",
                "Info",
                "Quit");
            switch (Ui.Pick(1, 6))
            {
                case 1: await ChatLoopAsync(chat, contextTokens); break;
                case 2: RunToolsDemo(engine); break;
                case 3: chat.Dispose(); chat = NewChat(engine); Ui.Success("Started a fresh conversation."); break;
                case 4: return true;
                case 5: PrintInfo(modelPath, backend, contextTokens, speculative, chat); break;
                case 6: return false;
            }
        }
    }
    finally
    {
        chat.Dispose(); // conversations must be disposed before the engine
    }
}

// ─────────────────── Chat: streaming generation ───────────────────

static LiteRtConversation NewChat(LiteRtEngine engine) => engine.CreateConversation(new LiteRtConversationOptions
{
    SystemMessage = "You are a concise, helpful assistant.",
    Sampler = new SamplerParams { Type = SamplerType.TopP, TopK = 40, TopP = 0.95f, Temperature = 0.8f },
});

static async Task ChatLoopAsync(LiteRtConversation chat, int contextTokens)
{
    Ui.Info("\nChat — type a message. Empty line returns to the menu. Ctrl+C cancels a reply.\n");
    while (true)
    {
        Ui.Prompt("You: ");
        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
            break;
        await StreamReplyAsync(chat, line, contextTokens);
    }
}

static async Task StreamReplyAsync(LiteRtConversation chat, string prompt, int contextTokens)
{
    using var cts = new CancellationTokenSource();
    using var hookup = Ui.HookCtrlC(cts);

    Ui.Write("Model: ", ConsoleColor.Green);
    var sw = Stopwatch.StartNew();
    try
    {
        // Tokens arrive as chunks; cancellation stops generation mid-reply.
        await foreach (string chunk in chat.SendMessageStreamingAsync(prompt, cts.Token))
            Console.Write(chunk);
    }
    catch (OperationCanceledException)
    {
        Ui.Write("  [cancelled]", ConsoleColor.DarkYellow);
    }
    catch (LiteRtException ex)
    {
        Ui.Write($"  [error: {ex.Message}]", ConsoleColor.Red);
    }
    Console.WriteLine();
    PrintContextGauge(chat, contextTokens, sw.Elapsed);
}

static void PrintContextGauge(LiteRtConversation chat, int contextTokens, TimeSpan elapsed)
{
    try
    {
        // chat.TokenCount = context used so far (prompt + replies, all turns).
        int used = chat.TokenCount;
        double frac = contextTokens > 0 ? (double)used / contextTokens : 0;
        var color = frac > 0.85 ? ConsoleColor.Red : ConsoleColor.DarkGray;
        Ui.WriteLine($"[context {used}/{contextTokens} ({frac:P0}) · {elapsed.TotalSeconds:F1}s{BenchSuffix(chat)}]", color);
        if (frac > 0.85)
            Ui.WriteLine("[context almost full — start a new conversation to avoid degraded replies]", ConsoleColor.Red);
    }
    catch (EntryPointNotFoundException)
    {
        Ui.WriteLine($"[{elapsed.TotalSeconds:F1}s]", ConsoleColor.DarkGray); // older native binary without TokenCount
    }
}

// Decode throughput + time-to-first-token from the benchmark API. Empty when benchmark info is
// unavailable (benchmark not enabled, no decode turn yet, or an older native binary).
static string BenchSuffix(LiteRtConversation chat)
{
    try
    {
        if (chat.GetBenchmarkInfo() is { NumDecodeTurns: > 0 } b)
            return $" · {b.LastDecodeTokensPerSecond:F1} tok/s decode · TTFT {b.TimeToFirstTokenSeconds:F2}s";
    }
    catch (EntryPointNotFoundException) { /* native binary predates the benchmark API */ }
    return "";
}

// ──────────────────── Function calling (tools) ────────────────────

static void RunToolsDemo(LiteRtEngine engine)
{
    Ui.Info("\nFunction-calling demo — asks the model for the weather, runs a mock tool, feeds it back.\n");

    // 1. Describe the tool (JSON Schema parameters, OpenAI/Gemini style).
    var weatherTool = new LiteRtTool(
        Name: "get_current_weather",
        Description: "Get the current weather for a city.",
        ParametersJson: """
        {"type":"object","properties":{"location":{"type":"string","description":"City, e.g. Tokyo"},"unit":{"type":"string","enum":["celsius","fahrenheit"]}},"required":["location"]}
        """);

    // 2. Tools are fixed per conversation; constrained decoding forces well-formed calls.
    using var conv = engine.CreateConversation(new LiteRtConversationOptions
    {
        SystemMessage = "You are a helpful assistant. Use the available tools when needed.",
        Tools = [weatherTool],
        EnableConstrainedDecoding = true,
        MaxOutputTokens = 256,
    });

    const string prompt = "What's the weather in Tokyo in celsius?";
    Ui.Write("You: ", ConsoleColor.Cyan); Console.WriteLine(prompt);

    // 3. The model either answers directly or asks us to run tools.
    LiteRtResponse response = conv.Send(prompt);
    if (response.IsToolCall)
    {
        var results = new List<LiteRtToolResult>();
        foreach (LiteRtToolCall call in response.ToolCalls)
        {
            Ui.WriteLine($"  → tool call: {call.Name}({call.ArgumentsJson})", ConsoleColor.Magenta);
            string result = ExecuteTool(call); // ← your code runs the tool
            Ui.WriteLine($"  ← tool result: {result}", ConsoleColor.DarkGray);
            results.Add(new LiteRtToolResult(call.Name, result));
        }

        // 4. Feed the results back; the model writes the final answer.
        Ui.Write("Model: ", ConsoleColor.Green);
        Console.WriteLine(conv.SendToolResults(results).Text);
    }
    else
    {
        Ui.Write("Model (no tool call): ", ConsoleColor.Green);
        Console.WriteLine(response.Text);
    }
}

static string ExecuteTool(LiteRtToolCall call) => call.Name switch
{
    "get_current_weather" => """{"temperature":15,"unit":"celsius","conditions":"sunny"}""",
    _ => """{"error":"unknown tool"}""",
};

// ───────────────────────────── Info ─────────────────────────────

static void PrintInfo(string modelPath, string backend, int contextTokens, bool speculative, LiteRtConversation chat)
{
    Ui.WriteLine("\nSession info", ConsoleColor.White);
    Ui.WriteLine($"  Model:       {modelPath}", ConsoleColor.Gray);
    Ui.WriteLine($"  Backend:     {backend}", ConsoleColor.Gray);
    Ui.WriteLine($"  Context:     {contextTokens} tokens", ConsoleColor.Gray);
    Ui.WriteLine($"  Speculative: {(speculative ? "on (MTP drafter)" : "off")}", ConsoleColor.Gray);
    try { Ui.WriteLine($"  In use:      {chat.TokenCount} tokens", ConsoleColor.Gray); }
    catch (EntryPointNotFoundException) { /* token count not available on this binary */ }
    try
    {
        if (chat.GetBenchmarkInfo() is { NumDecodeTurns: > 0 } b)
            Ui.WriteLine($"  Last decode: {b.LastDecodeTokensPerSecond:F1} tok/s · TTFT {b.TimeToFirstTokenSeconds:F2}s", ConsoleColor.Gray);
    }
    catch (EntryPointNotFoundException) { /* benchmark API not available on this binary */ }
}
