using System.Collections.ObjectModel;
using System.ComponentModel;
using LiteRtLmSharp;
using LiteRtLmSharp.SampleMaui.Services;

namespace LiteRtLmSharp.SampleMaui.Pages;

public partial class ChatPage : ContentPage
{
    private readonly EngineService _engine;
    private LiteRtConversation? _conversation;
    private CancellationTokenSource? _replyCts;
    private Task? _replyTask;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public ChatPage(EngineService engine)
    {
        InitializeComponent();
        _engine = engine;
        BindingContext = this;
        _engine.Loaded += () => MainThread.BeginInvokeOnMainThread(RefreshEngineState);
        _engine.Unloading += ReleaseConversationAsync;
        // Keep the latest message in view: the stack grows on every add AND on every streamed
        // chunk, and SizeChanged covers both. ScrollToAsync clamps the overshoot.
        MessagesStack.SizeChanged += (_, _) => _ = MessagesScroll.ScrollToAsync(0, MessagesStack.Height, animated: false);
        RefreshEngineState();
    }

    private void RefreshEngineState()
    {
        if (_engine.LoadedModel is { } model)
        {
            HeaderLabel.Text = $"{model.DisplayName} · {_engine.LoadedBackend} · context {EngineService.ContextTokens} tokens";
            InputEntry.IsEnabled = true;
            SendButton.IsEnabled = true;
            _conversation ??= _engine.NewConversation();
        }
    }

    /// <summary>
    /// The engine is about to be disposed (model/backend switch). Stop any in-flight reply and
    /// dispose our conversation — conversations must not outlive their engine.
    /// </summary>
    private async Task ReleaseConversationAsync()
    {
        _replyCts?.Cancel();
        if (_replyTask is not null)
            await _replyTask; // never faults: StreamReplyAsync handles its own errors
        _conversation?.Dispose();
        _conversation = null;

        Messages.Clear();
        GaugeLabel.IsVisible = false;
        HeaderLabel.Text = "Loading…";
        InputEntry.IsEnabled = false;
        SendButton.IsEnabled = false;
    }

    private void OnNewConversation(object? sender, EventArgs e)
    {
        if (!_engine.IsLoaded || _replyCts is not null) return;
        _conversation?.Dispose();
        _conversation = _engine.NewConversation();
        Messages.Clear();
        GaugeLabel.IsVisible = false;
    }

    private void OnSend(object? sender, EventArgs e)
    {
        string prompt = InputEntry.Text?.Trim() ?? "";
        if (prompt.Length == 0 || _conversation is null || _replyCts is not null)
            return;
        InputEntry.Text = "";
        _replyTask = StreamReplyAsync(prompt);
    }

    private async Task StreamReplyAsync(string prompt)
    {
        Messages.Add(ChatMessage.User(prompt));
        var reply = ChatMessage.Assistant();
        Messages.Add(reply);

        _replyCts = new CancellationTokenSource();
        SetBusy(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await foreach (string chunk in _conversation!.SendMessageStreamingAsync(prompt, _replyCts.Token))
                reply.Append(chunk);
            if (reply.Text.Length == 0)
                reply.Append("(empty response)");
        }
        catch (OperationCanceledException)
        {
            reply.Append("  [stopped]");
        }
        catch (Exception ex)
        {
            reply.Append($"  [error: {ex.Message}]");
        }
        finally
        {
            _replyCts = null;
            SetBusy(false);
            UpdateGauge(sw.Elapsed);
        }
    }

    private void OnStop(object? sender, EventArgs e) => _replyCts?.Cancel();

    // The Entry stays enabled while a reply streams: disabling the focused control on Windows
    // throws focus to the toolbar and makes the input area flicker on every message.
    // OnSend already ignores input while a reply is in flight.
    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        StopButton.IsVisible = busy;
    }

    private void UpdateGauge(TimeSpan elapsed)
    {
        try
        {
            int used = _conversation?.TokenCount ?? 0;
            double frac = (double)used / EngineService.ContextTokens;
            GaugeLabel.Text = $"context {used}/{EngineService.ContextTokens} ({frac:P0}) · {elapsed.TotalSeconds:F1}s";
            GaugeLabel.TextColor = frac > 0.85 ? Colors.Red : Colors.Gray;
            GaugeLabel.IsVisible = true;
        }
        catch (EntryPointNotFoundException)
        {
            GaugeLabel.Text = $"{elapsed.TotalSeconds:F1}s";
            GaugeLabel.IsVisible = true;
        }
    }
}

/// <summary>A chat bubble. Assistant text grows as streaming chunks arrive.</summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private ChatMessage(string role, string text)
    {
        Role = role;
        Text = text;
    }

    public static ChatMessage User(string text) => new("user", text);
    public static ChatMessage Assistant() => new("assistant", "");

    public string Role { get; }

    private string _text = "";
    public string Text
    {
        get => _text;
        private set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
    }

    public void Append(string chunk) =>
        MainThread.BeginInvokeOnMainThread(() => Text += chunk);

    public Color Background => Role == "user"
        ? (Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#2A4365") : Color.FromArgb("#DBEAFE"))
        : (Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#333333") : Color.FromArgb("#F1F1F1"));

    public LayoutOptions Alignment => Role == "user" ? LayoutOptions.End : LayoutOptions.Start;

    public event PropertyChangedEventHandler? PropertyChanged;
}
