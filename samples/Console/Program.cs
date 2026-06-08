using System.Diagnostics;
using System.Text;
using LiteLMSharp;

// LiteLMSharp interactive console sample.
//
// Interactive (no args):   LiteLMSharp.Sample
// Scripted (for testing):  LiteLMSharp.Sample <model.litertlm> [prompt]
//                          LiteLMSharp.Sample <model.litertlm> --tools
//                          LiteLMSharp.Sample <model.litertlm> --backend gpu --context 8192

Console.OutputEncoding = Encoding.UTF8;
LiteRtEngine.SetMinLogLevel(3); // WARNING+ (early native logs still print to stderr)

CliArgs cli = ParseArgs(args);

string? modelPath = cli.ModelPath;
string backend = cli.Backend;

if (modelPath is null)
{
    PrintBanner();
    modelPath = SelectModel();
    if (modelPath is null)
        return 0; // user quit
    backend = SelectBackend();
}

int contextTokens = cli.ContextTokens;

LiteRtEngine engine;
try
{
    Ui.Info($"\nLoading model ({Path.GetFileName(modelPath)}, {backend}, ctx {contextTokens}) …");
    var sw = Stopwatch.StartNew();
    engine = LiteRtEngine.Load(new LiteRtEngineOptions
    {
        ModelPath = modelPath,
        Backend = backend,
        MaxNumTokens = contextTokens,
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

    await MainMenuAsync(engine, modelPath, backend, contextTokens);
}
return 0;

// ───────────────────────────── Local functions ─────────────────────────────

static async Task MainMenuAsync(LiteRtEngine engine, string modelPath, string backend, int contextTokens)
{
    LiteRtConversation chat = NewChat(engine);
    try
    {
        while (true)
        {
            Ui.Rule();
            Ui.Menu("Main menu", "Chat (streaming)", "Function-calling demo", "New conversation", "Info", "Quit");
            switch (Ui.Pick(1, 5))
            {
                case 1: await ChatLoopAsync(chat, contextTokens); break;
                case 2: RunToolsDemo(engine); break;
                case 3: chat.Dispose(); chat = NewChat(engine); Ui.Success("Started a fresh conversation."); break;
                case 4: PrintInfo(modelPath, backend, contextTokens, chat); break;
                case 5: return;
            }
        }
    }
    finally
    {
        chat.Dispose();
    }
}

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
    using var hookup = HookCtrlC(cts);

    Ui.Write("Model: ", ConsoleColor.Green);
    var sw = Stopwatch.StartNew();
    try
    {
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
        int used = chat.TokenCount;
        double frac = contextTokens > 0 ? (double)used / contextTokens : 0;
        var color = frac > 0.85 ? ConsoleColor.Red : ConsoleColor.DarkGray;
        Ui.WriteLine($"[context {used}/{contextTokens} ({frac:P0}) · {elapsed.TotalSeconds:F1}s]", color);
        if (frac > 0.85)
            Ui.WriteLine("[context almost full — start a new conversation to avoid degraded replies]", ConsoleColor.Red);
    }
    catch (EntryPointNotFoundException)
    {
        Ui.WriteLine($"[{elapsed.TotalSeconds:F1}s]", ConsoleColor.DarkGray);
    }
}

static void RunToolsDemo(LiteRtEngine engine)
{
    Ui.Info("\nFunction-calling demo — asks the model for the weather, runs a mock tool, feeds it back.\n");

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
    Ui.Write("You: ", ConsoleColor.Cyan); Console.WriteLine(prompt);

    LiteRtResponse response = conv.Send(prompt);
    if (response.IsToolCall)
    {
        var results = new List<LiteRtToolResult>();
        foreach (LiteRtToolCall call in response.ToolCalls)
        {
            Ui.WriteLine($"  → tool call: {call.Name}({call.ArgumentsJson})", ConsoleColor.Magenta);
            string result = ExecuteTool(call);
            Ui.WriteLine($"  ← tool result: {result}", ConsoleColor.DarkGray);
            results.Add(new LiteRtToolResult(call.Name, result));
        }
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

static void PrintBanner() => Ui.WriteLine("""

    ┌───────────────────────────────────────────────┐
    │   LiteLMSharp · on-device LLM for .NET          │
    └───────────────────────────────────────────────┘
    """, ConsoleColor.Cyan);

static string? SelectModel()
{
    var models = FindModels();
    Ui.WriteLine("Select a model:", ConsoleColor.White);
    for (int i = 0; i < models.Count; i++)
    {
        var fi = new FileInfo(models[i]);
        Ui.WriteLine($"  {i + 1}) {fi.Name}  ({fi.Length / (1024 * 1024)} MB)", ConsoleColor.Gray);
    }
    Ui.WriteLine("  C) Enter a custom path", ConsoleColor.Gray);
    Ui.WriteLine("  Q) Quit", ConsoleColor.Gray);

    while (true)
    {
        Ui.Prompt("> ");
        string? choice = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(choice)) continue;
        if (choice.Equals("Q", StringComparison.OrdinalIgnoreCase)) return null;
        if (choice.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            Ui.Prompt("Path to .litertlm: ");
            string? path = Console.ReadLine()?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            Ui.Error("File not found.");
            continue;
        }
        if (int.TryParse(choice, out int n) && n >= 1 && n <= models.Count)
            return models[n - 1];
        Ui.Error("Invalid choice.");
    }
}

static string SelectBackend()
{
    Ui.WriteLine("\nSelect a backend:", ConsoleColor.White);
    Ui.WriteLine("  1) CPU  (most compatible)", ConsoleColor.Gray);
    Ui.WriteLine("  2) GPU  (WebGPU → D3D12/Vulkan/Metal; sampling falls back to CPU)", ConsoleColor.Gray);
    return Ui.Pick(1, 2) == 2 ? "gpu" : "cpu";
}

// Searches the current directory and a few ancestors for *.litertlm and models/*.litertlm.
static List<string> FindModels()
{
    var results = new List<string>();
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    for (int depth = 0; dir is not null && depth < 6; depth++, dir = dir.Parent)
    {
        foreach (var sub in new[] { dir.FullName, Path.Combine(dir.FullName, "models") })
        {
            if (Directory.Exists(sub))
                results.AddRange(Directory.GetFiles(sub, "*.litertlm"));
        }
        if (results.Count > 0) break; // closest match wins
    }
    return results.Distinct().ToList();
}

static void PrintInfo(string modelPath, string backend, int contextTokens, LiteRtConversation chat)
{
    Ui.WriteLine("\nSession info", ConsoleColor.White);
    Ui.WriteLine($"  Model:   {modelPath}", ConsoleColor.Gray);
    Ui.WriteLine($"  Backend: {backend}", ConsoleColor.Gray);
    Ui.WriteLine($"  Context: {contextTokens} tokens", ConsoleColor.Gray);
    try { Ui.WriteLine($"  In use:  {chat.TokenCount} tokens", ConsoleColor.Gray); }
    catch (EntryPointNotFoundException) { /* token count not available on this binary */ }
}

static CliArgs ParseArgs(string[] args)
{
    string? model = null, prompt = null, backend = "cpu";
    bool tools = false;
    int ctx = 4096;
    var rest = new List<string>();

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--tools": tools = true; break;
            case "--backend" when i + 1 < args.Length: backend = args[++i]; break;
            case "--context" when i + 1 < args.Length && int.TryParse(args[i + 1], out int c): ctx = c; i++; break;
            default: rest.Add(args[i]); break;
        }
    }
    if (rest.Count > 0) { model = rest[0]; if (rest.Count > 1) prompt = string.Join(' ', rest.Skip(1)); }
    return new CliArgs(model, backend, prompt, tools, ctx);
}

// Temporarily routes Ctrl+C to cancel the given source instead of killing the app.
static IDisposable HookCtrlC(CancellationTokenSource cts)
{
    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.CancelKeyPress += handler;
    return new Disposable(() => Console.CancelKeyPress -= handler);
}

// ───────────────────────────── Types ─────────────────────────────

record CliArgs(string? ModelPath, string Backend, string? OneShotPrompt, bool ToolsMode, int ContextTokens);

sealed class Disposable(Action onDispose) : IDisposable
{
    public void Dispose() => onDispose();
}

static class Ui
{
    public static void Write(string s, ConsoleColor c) { var p = Console.ForegroundColor; Console.ForegroundColor = c; Console.Write(s); Console.ForegroundColor = p; }
    public static void WriteLine(string s, ConsoleColor c) { Write(s + Environment.NewLine, c); }
    public static void Info(string s) => WriteLine(s, ConsoleColor.DarkGray);
    public static void Success(string s) => WriteLine(s, ConsoleColor.Green);
    public static void Error(string s) => WriteLine(s, ConsoleColor.Red);
    public static void Prompt(string s) => Write(s, ConsoleColor.Cyan);
    public static void Rule() => WriteLine(new string('─', 49), ConsoleColor.DarkGray);

    public static void Menu(string title, params string[] items)
    {
        WriteLine(title, ConsoleColor.White);
        for (int i = 0; i < items.Length; i++)
            WriteLine($"  {i + 1}) {items[i]}", ConsoleColor.Gray);
    }

    public static int Pick(int min, int max)
    {
        while (true)
        {
            Prompt("> ");
            if (int.TryParse(Console.ReadLine(), out int n) && n >= min && n <= max)
                return n;
            Error($"Enter a number {min}-{max}.");
        }
    }
}
