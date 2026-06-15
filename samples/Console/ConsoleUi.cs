using LiteRtLmSharp;

namespace LiteRtLmSharp.Sample;

// ─────────────────────────────────────────────────────────────────────────────
// Console plumbing: colors, menus, model/backend pickers, argument parsing.
// NOTHING in this file is required to use LiteRtLmSharp — it only makes the
// sample pleasant to drive. The library calls all live in Program.cs.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Parsed command-line arguments for the scripted (non-interactive) modes.</summary>
record CliArgs(string? ModelPath, string Backend, string? OneShotPrompt, bool ToolsMode, int ContextTokens, bool Speculative, string? CacheDir)
{
    public static CliArgs Parse(string[] args)
    {
        string? model = null, prompt = null, cacheDir = null;
        string backend = "cpu";
        bool tools = false, spec = false;
        int ctx = 4096;
        var rest = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tools": tools = true; break;
                case "--spec": spec = true; break;
                case "--backend" when i + 1 < args.Length: backend = args[++i]; break;
                case "--context" when i + 1 < args.Length && int.TryParse(args[i + 1], out int c): ctx = c; i++; break;
                // --cache <disk|no|memory|PATH>: disk = default (next to model), no/memory map to the
                // engine sentinels, anything else is treated as a cache directory path.
                case "--cache" when i + 1 < args.Length: cacheDir = CacheArg(args[++i]); break;
                default: rest.Add(args[i]); break;
            }
        }
        if (rest.Count > 0) { model = rest[0]; if (rest.Count > 1) prompt = string.Join(' ', rest.Skip(1)); }
        return new CliArgs(model, backend, prompt, tools, ctx, spec, cacheDir);
    }

    private static string? CacheArg(string v) => v.ToLowerInvariant() switch
    {
        "disk" or "" => null,                                   // engine default: next to the model
        "no" or "none" or "off" => LiteRtEngineOptions.CacheDisabled,
        "memory" or "ram" => LiteRtEngineOptions.CacheInMemory,
        _ => v,                                                 // a directory path
    };
}

/// <summary>Colored console output helpers.</summary>
static class Ui
{
    public static void Write(string s, ConsoleColor c) { var p = Console.ForegroundColor; Console.ForegroundColor = c; Console.Write(s); Console.ForegroundColor = p; }
    public static void WriteLine(string s, ConsoleColor c) => Write(s + Environment.NewLine, c);
    public static void Info(string s) => WriteLine(s, ConsoleColor.DarkGray);
    public static void Success(string s) => WriteLine(s, ConsoleColor.Green);
    public static void Error(string s) => WriteLine(s, ConsoleColor.Red);
    public static void Prompt(string s) => Write(s, ConsoleColor.Cyan);
    public static void Rule() => WriteLine(new string('─', 49), ConsoleColor.DarkGray);

    public static void Banner() => WriteLine("""

        ┌───────────────────────────────────────────────┐
        │   LiteRtLmSharp · on-device LLM for .NET      │
        └───────────────────────────────────────────────┘
        """, ConsoleColor.Cyan);

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

    /// <summary>Temporarily routes Ctrl+C to cancel the given source instead of killing the app.</summary>
    public static IDisposable HookCtrlC(CancellationTokenSource cts)
    {
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += handler;
        return new Unhook(() => Console.CancelKeyPress -= handler);
    }

    private sealed class Unhook(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

/// <summary>Interactive pickers for the model file and the backend.</summary>
static class Picker
{
    /// <summary>Lists nearby *.litertlm files and lets the user pick one. Null = quit.</summary>
    public static string? Model()
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

    public static string Backend()
    {
        Ui.WriteLine("\nSelect a backend:", ConsoleColor.White);
        Ui.WriteLine("  1) CPU  (most compatible)", ConsoleColor.Gray);
        Ui.WriteLine("  2) GPU  (WebGPU → D3D12/Vulkan/Metal; sampling falls back to CPU)", ConsoleColor.Gray);
        return Ui.Pick(1, 2) == 2 ? "gpu" : "cpu";
    }

    /// <summary>Whether to enable speculative decoding (the MTP drafter). Default off.</summary>
    public static bool Speculative()
    {
        Ui.WriteLine("\nSpeculative decoding (needs an MTP-drafter model, e.g. Gemma 4 E2B/E4B):", ConsoleColor.White);
        Ui.WriteLine("  1) Off  (default)", ConsoleColor.Gray);
        Ui.WriteLine("  2) On   (faster decode on supported models)", ConsoleColor.Gray);
        return Ui.Pick(1, 2) == 2;
    }

    // Searches the current directory and a few ancestors for *.litertlm and models/*.litertlm.
    private static List<string> FindModels()
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
}
