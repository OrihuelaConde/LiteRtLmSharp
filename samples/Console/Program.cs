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
// SendStreamingAsync() generate replies. Dispose conversations, then the engine.
// Only one engine may be alive at a time — dispose it first to switch model or backend.
//
// Interactive (no args):   LiteRtLmSharp.Sample
// Scripted (for testing):  LiteRtLmSharp.Sample <model.litertlm> [prompt]
//                          LiteRtLmSharp.Sample <model.litertlm> --tools
//                          LiteRtLmSharp.Sample <model.litertlm> --spec     (speculative decoding)
//                          LiteRtLmSharp.Sample <model.litertlm> --thinking (reasoning mode)
//                          LiteRtLmSharp.Sample <model.litertlm> --backend gpu --context 8192
//                          LiteRtLmSharp.Sample <model.litertlm> --threads 4 (CPU executor thread count)

Console.OutputEncoding = Encoding.UTF8;
LiteRtEngine.SetMinLogLevel(3); // WARNING+ (early native logs still print to stderr)

CliArgs cli = CliArgs.Parse(args);

string? modelPath = cli.ModelPath;
string backend = cli.Backend;
int contextTokens = cli.ContextTokens;
bool speculative = cli.Speculative;
bool thinking = cli.Thinking;

if (modelPath is null)
{
    Ui.Banner();
    modelPath = Picker.Model();
    if (modelPath is null)
        return 0; // user quit
    backend = Picker.Backend();
    speculative = Picker.Speculative();
    thinking = Picker.Thinking();
}

while (true)
{
    LiteRtEngine engine;
    try
    {
        // Historical note: on LiteRT-LM v0.13.1 speculative decoding on the WebGPU backend needed the
        // disk cache OFF (the MTP drafter's shared weight cache failed to mmap on Windows — upstream
        // #2572). Fixed in v0.14.0 and re-verified end to end, so the default cache is safe again.
        LiteRtCache cache = cli.Cache ?? LiteRtCache.Default;

        string cacheNote =
            cache == LiteRtCache.Disabled ? ", cache off"
            : cache == LiteRtCache.InMemory ? ", cache in-memory"
            : cache == LiteRtCache.Default ? ""
            : $", cache {cache}";
        Ui.Info($"\nLoading model ({Path.GetFileName(modelPath)}, {backend}, ctx {contextTokens}"
            + $"{(speculative ? ", speculative" : "")}{(thinking ? ", thinking" : "")}{cacheNote}"
            + $"{(cli.Threads is { } threads ? $", {threads} threads" : "")}) …");
        var sw = Stopwatch.StartNew();

        engine = LiteRtEngine.Load(new LiteRtEngineOptions
        {
            ModelPath = modelPath,
            Backend = LiteRtBackend.Parse(backend),  // "cpu" or "gpu"
            MaxNumTokens = contextTokens, // total context window (prompt + replies, all turns)
            EnableSpeculativeDecoding = speculative, // MTP drafter → faster decode (supported models)
            EnableBenchmark = true,     // so the gauge can show decode tok/s and time-to-first-token
            Cache = cache,              // Default = disk cache next to the model
            NumThreads = cli.Threads,   // CPU text-executor thread count; null = engine default (CPU-only, no-op on GPU)
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
            await RunToolsDemo(engine);
            return 0;
        }
        if (cli.OneShotPrompt is not null)
        {
            using var c = engine.CreateConversation(new LiteRtConversationOptions { EnableThinking = thinking });
            await StreamReplyAsync(c, cli.OneShotPrompt, contextTokens);
            return 0;
        }

        bool switchModel = await MainMenuAsync(engine, modelPath, backend, contextTokens, speculative, thinking);
        if (!switchModel)
            return 0;
    } // ← the engine is disposed here; only then may a new one be loaded.

    modelPath = Picker.Model();
    if (modelPath is null)
        return 0;
    backend = Picker.Backend();
    speculative = Picker.Speculative();
    thinking = Picker.Thinking();
}

// ─────────────────────────── Menu loop ───────────────────────────

// Returns true when the user wants to switch model/backend (caller disposes this engine
// and loads a new one), false to quit.
static async Task<bool> MainMenuAsync(
    LiteRtEngine engine, string modelPath, string backend, int contextTokens, bool speculative, bool thinking)
{
    LiteRtConversation chat = NewChat(engine, thinking);
    try
    {
        while (true)
        {
            Ui.Rule();
            Ui.Menu("Main menu",
                "Chat (streaming)",
                "Function-calling demo",
                "Tokenizer (count tokens)",
                "New conversation",
                "Switch model / backend / speculative / thinking",
                "Info",
                "Quit");
            switch (Ui.Pick(1, 7))
            {
                case 1: await ChatLoopAsync(chat, contextTokens); break;
                case 2: await RunToolsDemo(engine); break;
                case 3: RunTokenizerDemo(engine); break;
                case 4: chat.Dispose(); chat = NewChat(engine, thinking); Ui.Success("Started a fresh conversation."); break;
                case 5: return true;
                case 6: PrintInfo(modelPath, backend, contextTokens, speculative, thinking, chat); break;
                case 7: return false;
            }
        }
    }
    finally
    {
        chat.Dispose(); // conversations must be disposed before the engine
    }
}

// ─────────────────── Chat: streaming generation ───────────────────

static LiteRtConversation NewChat(LiteRtEngine engine, bool thinking) => engine.CreateConversation(new LiteRtConversationOptions
{
    SystemMessage = "You are a concise, helpful assistant.",
    Sampler = new LiteRtSamplerParams { Strategy = LiteRtSamplerType.TopP, TopK = 40, TopP = 0.95f, Temperature = 0.8f },
    EnableThinking = thinking,
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

    var sw = Stopwatch.StartNew();
    bool thinkingShown = false, answerStarted = false;

    // Print the "Model: " prefix lazily so the (dimmed) thinking trace can come first.
    void StartAnswer()
    {
        if (answerStarted) return;
        if (thinkingShown) Console.WriteLine();   // close the thinking line
        Ui.Write("Model: ", ConsoleColor.Green);
        answerStarted = true;
    }

    try
    {
        // Chunks arrive tagged as reasoning ("thinking", only with EnableThinking on) or answer
        // (this chat has no tools, so no tool-call chunks). Show the thinking trace dimmed above the
        // answer. Cancellation stops generation mid-reply.
        await foreach (LiteRtStreamChunk chunk in chat.SendStreamingAsync(prompt, cts.Token))
        {
            if (chunk.IsThinking)
            {
                if (!thinkingShown) { Ui.Write("Thinking: ", ConsoleColor.DarkGray); thinkingShown = true; }
                Ui.Write(chunk.Text, ConsoleColor.DarkGray);
            }
            else
            {
                StartAnswer();
                Console.Write(chunk.Text);
            }
        }
        StartAnswer(); // ensure the "Model: " prefix even for an empty answer
    }
    catch (OperationCanceledException)
    {
        StartAnswer();
        Ui.Write("  [cancelled]", ConsoleColor.DarkYellow);
    }
    catch (LiteRtException ex)
    {
        StartAnswer();
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

static async Task RunToolsDemo(LiteRtEngine engine)
{
    Ui.Info("\nFunction-calling demo — asks the model for the weather, runs a mock tool, feeds it back.\n"
        + "Streams the tool call as the model builds it (StreamToolCalls), then runs the parsed call.\n");

    // 1. Describe the tool (JSON Schema parameters, OpenAI/Gemini style).
    var weatherTool = new LiteRtTool(
        Name: "get_current_weather",
        Description: "Get the current weather for a city.",
        ParametersJson: """
        {"type":"object","properties":{"location":{"type":"string","description":"City, e.g. Tokyo"},"unit":{"type":"string","enum":["celsius","fahrenheit"]}},"required":["location"]}
        """);

    // 2. Tools are fixed per conversation; constrained decoding forces well-formed calls. StreamToolCalls
    //    makes SendStreamingAsync surface the raw tool-call text as it is generated (ToolCallDelta chunks),
    //    ahead of the complete, parsed ToolCall chunk — a live progress view of the call forming.
    using var conv = engine.CreateConversation(new LiteRtConversationOptions
    {
        SystemMessage = "You are a helpful assistant. Use the available tools when needed.",
        Tools = [weatherTool],
        EnableConstrainedDecoding = true,
        StreamToolCalls = true,
        MaxOutputTokens = 256,
    });

    const string prompt = "What's the weather in Tokyo in celsius?";
    Ui.Write("You: ", ConsoleColor.Cyan); Console.WriteLine(prompt);

    // 3. Stream the reply. ToolCallDelta chunks are raw, unparsed progress fragments — print them as a live
    //    "building the call" line, but NEVER act on them; act on the complete ToolCall chunk that follows.
    //    Answer chunks appear instead when the model just answers without calling a tool.
    var toolCalls = new List<LiteRtToolCall>();
    bool building = false, answering = false;
    await foreach (LiteRtStreamChunk chunk in conv.SendStreamingAsync(prompt))
    {
        switch (chunk.Kind)
        {
            case LiteRtStreamChunkKind.ToolCallDelta:
                if (!building) { Ui.Write("  building call: ", ConsoleColor.DarkMagenta); building = true; }
                Ui.Write(chunk.Text, ConsoleColor.DarkMagenta); // live fragments as the model writes the call
                break;
            case LiteRtStreamChunkKind.ToolCall:
                if (building) Console.WriteLine();
                toolCalls.AddRange(chunk.ToolCalls); // the parsed call — this is what we execute
                break;
            case LiteRtStreamChunkKind.Answer:
                if (!answering) { if (building) Console.WriteLine(); Ui.Write("Model (no tool call): ", ConsoleColor.Green); answering = true; }
                Console.Write(chunk.Text);
                break;
        }
    }
    if (answering) Console.WriteLine();

    // 4. Run each parsed call and feed the results back; the model writes the final answer.
    if (toolCalls.Count > 0)
    {
        var results = new List<LiteRtToolResult>();
        foreach (LiteRtToolCall call in toolCalls)
        {
            Ui.WriteLine($"  → tool call: {call.Name}({call.ArgumentsJson})", ConsoleColor.Magenta);
            string result = ExecuteTool(call); // ← your code runs the tool
            Ui.WriteLine($"  ← tool result: {result}", ConsoleColor.DarkGray);
            results.Add(new LiteRtToolResult(call.Name, result));
        }

        Ui.Write("Model: ", ConsoleColor.Green);
        // Per-send cap: the tool call needed room (conversation cap 256), but the final summary can be
        // short — LiteRtSendOptions.MaxOutputTokens overrides the conversation cap for this one send.
        Console.WriteLine(conv.SendToolResults(results, new LiteRtSendOptions { MaxOutputTokens = 128 }).Text);
    }
}

static string ExecuteTool(LiteRtToolCall call) => call.Name switch
{
    "get_current_weather" => """{"temperature":15,"unit":"celsius","conditions":"sunny"}""",
    _ => """{"error":"unknown tool"}""",
};

// ─────────────────── Tokenizer (count tokens) ───────────────────

// The engine exposes the model's own tokenizer, so you can measure a prompt's exact token cost
// (and what its stop tokens are) without running inference.
static void RunTokenizerDemo(LiteRtEngine engine)
{
    Ui.Info("\nTokenizer demo — turns text into token ids and back with no inference; handy to measure a\n"
        + "prompt's exact token cost before sending.\n");

    Ui.Prompt("Text to tokenize (Enter for a sample): ");
    string text = Console.ReadLine() is { Length: > 0 } typed
        ? typed
        : "The quick brown fox jumps over the lazy dog.";

    // 1. Text → token ids (exact count, no generation).
    int[] ids = engine.Tokenize(text);
    Ui.WriteLine($"  {ids.Length} tokens", ConsoleColor.White);
    Ui.WriteLine($"  ids: {PreviewIds(ids)}", ConsoleColor.DarkGray);

    // 2. Token ids → text (round-trips back through the tokenizer). Detokenize returns the tokenizer's
    //    surface form: Gemma's SentencePiece marks word boundaries with ▁ (the chat path renders spaces).
    string detok = engine.Detokenize(ids);
    Ui.WriteLine($"  detokenized: {detok}", ConsoleColor.DarkGray);
    if (detok.Contains('▁'))
        Ui.WriteLine("  (▁ is this model's SentencePiece space marker)", ConsoleColor.DarkGray);

    // 3. The model's configured start (BOS) / stop (EOS) tokens — generation halts on a stop token.
    if (engine.GetStartToken() is { } start)
        Ui.WriteLine($"  start token: {start}", ConsoleColor.Gray);
    string stops = string.Join(", ", engine.GetStopTokens());
    Ui.WriteLine($"  stop tokens: {(stops.Length > 0 ? stops : "(none)")}", ConsoleColor.Gray);

    // 4. The model never sees the raw text — it sees it wrapped in the chat template. Render the same
    //    text (without sending) and tokenize THAT for the real per-turn cost, template included. Give the
    //    conversation a system message so the preface in step 5 has something to render.
    using var conv = engine.CreateConversation(new LiteRtConversationOptions
    {
        SystemMessage = "You are a concise, helpful assistant.",
    });
    string templated = conv.RenderMessage(text);
    Ui.WriteLine($"  templated prompt — {engine.Tokenize(templated).Length} tokens (what the model actually sees):", ConsoleColor.White);
    Ui.WriteLine("  " + templated.TrimEnd().Replace("\n", "\n  "), ConsoleColor.DarkGray);

    // 5. The preface is the templated preamble before the first user turn (system message + any tools +
    //    restored history). RenderPreface shows it without sending; tokenize it for the up-front cost the
    //    system prompt adds once per conversation. Complements RenderMessage; needs the v0.14.0+ runtime.
    try
    {
        string preface = conv.RenderPreface();
        Ui.WriteLine($"  preface: {engine.Tokenize(preface).Length} tokens (system prompt, rendered once up front):", ConsoleColor.White);
        Ui.WriteLine("  " + preface.TrimEnd().Replace("\n", "\n  "), ConsoleColor.DarkGray);
    }
    catch (EntryPointNotFoundException)
    {
        Ui.WriteLine("  (RenderPreface needs the v0.14.0+ native runtime)", ConsoleColor.DarkGray);
    }
}

// First few token ids, abbreviated so a long input stays readable.
static string PreviewIds(int[] ids)
{
    const int max = 16;
    return ids.Length <= max
        ? string.Join(", ", ids)
        : string.Join(", ", ids[..max]) + $", … (+{ids.Length - max} more)";
}

// ───────────────────────────── Info ─────────────────────────────

static void PrintInfo(string modelPath, string backend, int contextTokens, bool speculative, bool thinking, LiteRtConversation chat)
{
    Ui.WriteLine("\nSession info", ConsoleColor.White);
    Ui.WriteLine($"  Model:       {modelPath}", ConsoleColor.Gray);
    Ui.WriteLine($"  Backend:     {backend}", ConsoleColor.Gray);
    Ui.WriteLine($"  Context:     {contextTokens} tokens", ConsoleColor.Gray);
    Ui.WriteLine($"  Speculative: {(speculative ? "on (MTP drafter)" : "off")}", ConsoleColor.Gray);
    Ui.WriteLine($"  Thinking:    {(thinking ? "on" : "off")}", ConsoleColor.Gray);
    try { Ui.WriteLine($"  In use:      {chat.TokenCount} tokens", ConsoleColor.Gray); }
    catch (EntryPointNotFoundException) { /* token count not available on this binary */ }
    try
    {
        if (chat.GetBenchmarkInfo() is { NumDecodeTurns: > 0 } b)
            Ui.WriteLine($"  Last decode: {b.LastDecodeTokensPerSecond:F1} tok/s · TTFT {b.TimeToFirstTokenSeconds:F2}s", ConsoleColor.Gray);
    }
    catch (EntryPointNotFoundException) { /* benchmark API not available on this binary */ }
}
