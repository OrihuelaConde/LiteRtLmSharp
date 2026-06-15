using System.Collections.ObjectModel;
using LiteRtLmSharp;
using LiteRtLmSharp.SampleMaui.Services;

namespace LiteRtLmSharp.SampleMaui.Pages;

/// <summary>
/// Function-calling demo, kept separate from the chat (like the console sample's tools mode).
/// Every question runs in a FRESH conversation that is disposed afterwards, so the Tools and
/// Chat tabs never share or pollute each other's context.
/// </summary>
public partial class ToolsPage : ContentPage
{
    private readonly EngineService _engine;
    private Task? _runTask;

    public ObservableCollection<ToolLogEntry> Log { get; } = [];

    // The demo tools: two answered by REAL device APIs (MAUI Battery / DeviceInfo), one mock.
    // Tool parameters are described with JSON Schema, OpenAI/Gemini style.
    private static readonly LiteRtTool[] DemoTools =
    [
        new(
            Name: "get_battery_status",
            Description: "Get the device's current battery charge level and charging state.",
            ParametersJson: """{"type":"object","properties":{}}"""),
        new(
            Name: "get_device_info",
            Description: "Get the manufacturer, model and operating system of this device.",
            ParametersJson: """{"type":"object","properties":{}}"""),
        new(
            Name: "get_current_weather",
            Description: "Get the current weather for a city.",
            ParametersJson: """
            {"type":"object","properties":{"location":{"type":"string","description":"City, e.g. Tokyo"},"unit":{"type":"string","enum":["celsius","fahrenheit"]}},"required":["location"]}
            """),
    ];

    public ToolsPage(EngineService engine)
    {
        InitializeComponent();
        _engine = engine;
        BindingContext = this;
        _engine.Loaded += () => MainThread.BeginInvokeOnMainThread(RefreshEngineState);
        _engine.Unloading += ReleaseAsync;
        // Keep the newest log entry in view (see ChatPage for the pattern).
        LogStack.SizeChanged += (_, _) => _ = LogScroll.ScrollToAsync(0, LogStack.Height, animated: false);
        RefreshEngineState();
    }

    private void RefreshEngineState()
    {
        if (_engine.LoadedModel is { } model)
        {
            HeaderLabel.Text = $"{model.DisplayName} · {_engine.LoadedBackend} · {_engine.SpeculativeLabel}";
            InputEntry.IsEnabled = true;
            SendButton.IsEnabled = true;
        }
    }

    /// <summary>The engine is about to be disposed: wait for the in-flight run (it owns a conversation).</summary>
    private async Task ReleaseAsync()
    {
        if (_runTask is not null)
            await _runTask; // never faults: RunToolPromptAsync handles its own errors
        Log.Clear();
        HeaderLabel.Text = "Loading…";
        InputEntry.IsEnabled = false;
        SendButton.IsEnabled = false;
    }

    private void OnSuggestion(object? sender, EventArgs e)
    {
        InputEntry.Text = ((Button)sender!).Text switch
        {
            "Battery?" => "What's my battery level?",
            "My device?" => "What device am I running on?",
            _ => "What's the weather in Tokyo in celsius?",
        };
        OnSend(sender, e);
    }

    private void OnSend(object? sender, EventArgs e)
    {
        string prompt = InputEntry.Text?.Trim() ?? "";
        if (prompt.Length == 0 || !_engine.IsLoaded || _runTask is not null)
            return;
        InputEntry.Text = "";
        _runTask = RunToolPromptAsync(prompt);
    }

    private async Task RunToolPromptAsync(string prompt)
    {
        SetBusy(true);
        Log.Add(ToolLogEntry.User(prompt));
        try
        {
            // Fresh conversation per question: tools are fixed at creation time, and disposing
            // it afterwards keeps every run (and the Chat tab) on a clean context.
            using var conversation = _engine.NewToolConversation(DemoTools);

            // Generation is blocking native work — keep it off the UI thread.
            LiteRtResponse response = await Task.Run(() => conversation.Send(prompt));

            if (response.IsToolCall)
            {
                var results = new List<LiteRtToolResult>();
                foreach (LiteRtToolCall call in response.ToolCalls)
                {
                    Log.Add(ToolLogEntry.Call($"→ {call.Name}({call.ArgumentsJson})"));
                    string result = ExecuteTool(call); // ← app code answers the tool call
                    Log.Add(ToolLogEntry.Result($"← {result}"));
                    results.Add(new LiteRtToolResult(call.Name, result));
                }

                // Feed the results back; the model writes the final answer.
                response = await Task.Run(() => conversation.SendToolResults(results));
                Log.Add(ToolLogEntry.Model($"Model: {response.Text?.Trim()}"));
            }
            else
            {
                Log.Add(ToolLogEntry.Model($"Model (no tool call): {response.Text?.Trim()}"));
            }
        }
        catch (Exception ex)
        {
            Log.Add(ToolLogEntry.Result($"[error: {ex.Message}]"));
        }
        finally
        {
            _runTask = null;
            SetBusy(false);
        }
    }

    /// <summary>Runs a tool call. Battery and device info come from the real MAUI device APIs.</summary>
    private static string ExecuteTool(LiteRtToolCall call) => call.Name switch
    {
        "get_battery_status" =>
            $$"""{"level_percent":{{(int)Math.Round(Battery.Default.ChargeLevel * 100)}},"state":"{{Battery.Default.State}}","power_source":"{{Battery.Default.PowerSource}}"}""",
        "get_device_info" =>
            $$"""{"manufacturer":"{{Json(DeviceInfo.Current.Manufacturer)}}","model":"{{Json(DeviceInfo.Current.Model)}}","platform":"{{DeviceInfo.Current.Platform}}","os_version":"{{Json(DeviceInfo.Current.VersionString)}}"}""",
        "get_current_weather" =>
            """{"temperature":15,"unit":"celsius","conditions":"sunny"}""",
        _ => """{"error":"unknown tool"}""",
    };

    private static string Json(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // Entry stays enabled while busy — see ChatPage.SetBusy for why (Windows focus stealing).
    private void SetBusy(bool busy) => SendButton.IsEnabled = !busy;
}

/// <summary>One line in the tools log, colored by kind.</summary>
public sealed class ToolLogEntry
{
    private ToolLogEntry(string text, Color color)
    {
        Text = text;
        Color = color;
    }

    public string Text { get; }
    public Color Color { get; }

    private static bool Dark => Application.Current?.RequestedTheme == AppTheme.Dark;

    public static ToolLogEntry User(string text) => new($"You: {text}", Dark ? Colors.White : Colors.Black);
    public static ToolLogEntry Call(string text) => new(text, Dark ? Colors.Plum : Colors.Purple);
    public static ToolLogEntry Result(string text) => new(text, Colors.Gray);
    public static ToolLogEntry Model(string text) => new(text, Dark ? Colors.LightGreen : Colors.DarkGreen);
}
