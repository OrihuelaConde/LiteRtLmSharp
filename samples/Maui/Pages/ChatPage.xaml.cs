using System.Collections.ObjectModel;
using System.ComponentModel;
using LiteLMSharp;
using LiteLMSharp.SampleMaui.Services;

namespace LiteLMSharp.SampleMaui.Pages;

public partial class ChatPage : ContentPage
{
    private readonly EngineService _engine;
    private LiteRtConversation? _conversation;
    private CancellationTokenSource? _replyCts;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public ChatPage(EngineService engine)
    {
        InitializeComponent();
        _engine = engine;
        BindingContext = this;
        _engine.Loaded += () => MainThread.BeginInvokeOnMainThread(RefreshEngineState);
        RefreshEngineState();
    }

    private void RefreshEngineState()
    {
        if (_engine.LoadedModel is { } model)
        {
            HeaderLabel.Text = $"{model.DisplayName} · context {EngineService.ContextTokens} tokens";
            InputEntry.IsEnabled = true;
            SendButton.IsEnabled = true;
            _conversation ??= _engine.NewConversation();
        }
    }

    private void OnNewConversation(object? sender, EventArgs e)
    {
        if (!_engine.IsLoaded) return;
        _replyCts?.Cancel();
        _conversation?.Dispose();
        _conversation = _engine.NewConversation();
        Messages.Clear();
        GaugeLabel.IsVisible = false;
    }

    private async void OnSend(object? sender, EventArgs e)
    {
        string prompt = InputEntry.Text?.Trim() ?? "";
        if (prompt.Length == 0 || _conversation is null || _replyCts is not null)
            return;

        InputEntry.Text = "";
        Messages.Add(ChatMessage.User(prompt));
        var reply = ChatMessage.Assistant();
        Messages.Add(reply);

        _replyCts = new CancellationTokenSource();
        SetBusy(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await foreach (string chunk in _conversation.SendMessageStreamingAsync(prompt, _replyCts.Token))
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

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        InputEntry.IsEnabled = !busy;
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
