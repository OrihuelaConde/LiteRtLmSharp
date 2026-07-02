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

    // The single pending attachment (image OR audio) staged for the next send. The binding accepts
    // several attachments per message; the sample keeps one at a time to keep the UI simple.
    private byte[]? _pendingBytes;
    private LiteRtAttachmentKind _pendingKind;
    private string? _pendingFileName;

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
            HeaderLabel.Text = $"{model.DisplayName} · {_engine.LoadedBackend} · context {EngineService.ContextTokens} tokens"
                + $" · {_engine.SpeculativeLabel} · {_engine.ThinkingLabel} · {_engine.ModalityLabel}";
            InputEntry.IsEnabled = true;
            SendButton.IsEnabled = true;
            // Offer the attach buttons only for modalities the loaded model actually supports.
            AttachImageButton.IsVisible = AttachImageButton.IsEnabled = model.SupportsVision;
            AttachAudioButton.IsVisible = AttachAudioButton.IsEnabled = model.SupportsAudio;
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
        ClearPending();
        GaugeLabel.IsVisible = false;
        HeaderLabel.Text = "Loading…";
        InputEntry.IsEnabled = false;
        SendButton.IsEnabled = false;
        AttachImageButton.IsVisible = AttachAudioButton.IsVisible = false;
    }

    private void OnNewConversation(object? sender, EventArgs e)
    {
        if (!_engine.IsLoaded || _replyCts is not null) return;
        _conversation?.Dispose();
        _conversation = _engine.NewConversation();
        Messages.Clear();
        ClearPending();
        GaugeLabel.IsVisible = false;
    }

    private async void OnAttachImage(object? sender, EventArgs e)
    {
        try
        {
            // PickPhotoAsync is the simplest single-select photo picker; the newer PickPhotosAsync is
            // multi-select, which this one-attachment-at-a-time sample does not need.
#pragma warning disable CS0618 // Type or member is obsolete
            FileResult? file = await MediaPicker.Default.PickPhotoAsync();
#pragma warning restore CS0618
            if (file is not null)
                await SetPendingAsync(file, LiteRtAttachmentKind.Image);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Attach image failed", ex.Message, "OK");
        }
    }

    private async void OnAttachAudio(object? sender, EventArgs e)
    {
        try
        {
            FileResult? file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Pick an audio file" });
            if (file is not null)
                await SetPendingAsync(file, LiteRtAttachmentKind.Audio);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Attach audio failed", ex.Message, "OK");
        }
    }

    private async Task SetPendingAsync(FileResult file, LiteRtAttachmentKind kind)
    {
        await using var stream = await file.OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        _pendingBytes = ms.ToArray();
        _pendingKind = kind;
        _pendingFileName = file.FileName;
        ShowAttachmentPreview();
    }

    private void ShowAttachmentPreview()
    {
        if (_pendingBytes is null) { AttachmentPreview.IsVisible = false; return; }

        if (_pendingKind == LiteRtAttachmentKind.Image)
        {
            byte[] bytes = _pendingBytes; // ImageSource.FromStream needs a fresh stream per request
            AttachThumb.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
            AttachThumb.IsVisible = true;
            AttachLabel.Text = _pendingFileName ?? "image";
        }
        else
        {
            AttachThumb.IsVisible = false;
            AttachThumb.Source = null;
            AttachLabel.Text = $"🎵 {_pendingFileName ?? "audio"}";
        }
        AttachmentPreview.IsVisible = true;
    }

    private void OnClearAttachment(object? sender, EventArgs e) => ClearPending();

    private void ClearPending()
    {
        _pendingBytes = null;
        _pendingFileName = null;
        AttachThumb.Source = null;
        AttachmentPreview.IsVisible = false;
    }

    private void OnSend(object? sender, EventArgs e)
    {
        string prompt = InputEntry.Text?.Trim() ?? "";
        // Allow sending an attachment with no text (a bare "describe this image" turn).
        if ((prompt.Length == 0 && _pendingBytes is null) || _conversation is null || _replyCts is not null)
            return;
        InputEntry.Text = "";

        byte[]? bytes = _pendingBytes;
        LiteRtAttachmentKind kind = _pendingKind;
        string? fileName = _pendingFileName;
        ClearPending();

        _replyTask = StreamReplyAsync(prompt, bytes, kind, fileName);
    }

    private async Task StreamReplyAsync(string prompt, byte[]? attachmentBytes, LiteRtAttachmentKind kind, string? fileName)
    {
        // User bubble: show the attachment thumbnail (image) or a chip (audio) above the text.
        IReadOnlyList<LiteRtAttachment> attachments = [];
        ChatMessage userMsg;
        if (attachmentBytes is { } bytes)
        {
            if (kind == LiteRtAttachmentKind.Image)
            {
                userMsg = ChatMessage.User(prompt, ImageSource.FromStream(() => new MemoryStream(bytes)), null);
                attachments = [LiteRtAttachment.Image(bytes)];
            }
            else
            {
                userMsg = ChatMessage.User(prompt, null, $"🎵 {fileName ?? "audio"}");
                attachments = [LiteRtAttachment.Audio(bytes)];
            }
        }
        else
        {
            userMsg = ChatMessage.User(prompt);
        }
        Messages.Add(userMsg);

        var reply = ChatMessage.Assistant();
        Messages.Add(reply);

        _replyCts = new CancellationTokenSource();
        SetBusy(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Chunks are tagged as reasoning ("thinking", only with EnableThinking on) or answer;
            // the chat conversation has no tools, so no tool-call chunks arrive. The image/audio
            // attachments ride along in the user message and are encoded into vision/audio tokens.
            await foreach (LiteRtStreamChunk chunk in _conversation!.SendStreamingAsync(prompt, attachments, options: null, _replyCts.Token))
            {
                if (chunk.IsThinking)
                    reply.AppendThinking(chunk.Text);
                else
                    reply.Append(chunk.Text);
            }
            if (reply.Text.Length == 0 && reply.ThinkingText.Length == 0)
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
        // Block staging a new attachment mid-reply (the buttons exist only for capable models).
        if (AttachImageButton.IsVisible) AttachImageButton.IsEnabled = !busy;
        if (AttachAudioButton.IsVisible) AttachAudioButton.IsEnabled = !busy;
    }

    private void UpdateGauge(TimeSpan elapsed)
    {
        string bench = BenchSuffix();
        try
        {
            int used = _conversation?.TokenCount ?? 0;
            double frac = (double)used / EngineService.ContextTokens;
            GaugeLabel.Text = $"context {used}/{EngineService.ContextTokens} ({frac:P0}) · {elapsed.TotalSeconds:F1}s{bench}";
            GaugeLabel.TextColor = frac > 0.85 ? Colors.Red : Colors.Gray;
            GaugeLabel.IsVisible = true;
        }
        catch (EntryPointNotFoundException)
        {
            GaugeLabel.Text = $"{elapsed.TotalSeconds:F1}s{bench}";
            GaugeLabel.IsVisible = true;
        }
    }

    // Decode throughput + time-to-first-token from the benchmark API (engine loaded with
    // EnableBenchmark). Empty when unavailable — no decode turn recorded, or an older native
    // binary without the benchmark API.
    private string BenchSuffix()
    {
        try
        {
            if (_conversation?.GetBenchmarkInfo() is { NumDecodeTurns: > 0 } b)
                return $" · {b.LastDecodeTokensPerSecond:F1} tok/s decode · TTFT {b.TimeToFirstTokenSeconds:F2}s";
        }
        catch (EntryPointNotFoundException) { /* native binary predates the benchmark API */ }
        return "";
    }
}

/// <summary>A chat bubble. Assistant text grows as streaming chunks arrive; user bubbles may carry
/// an attached image thumbnail or an audio chip.</summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private ChatMessage(string role, string text, ImageSource? attachmentImage = null, string? attachmentLabel = null)
    {
        Role = role;
        _text = text;
        AttachmentImage = attachmentImage;
        AttachmentLabel = attachmentLabel ?? "";
    }

    public static ChatMessage User(string text) => new("user", text);
    public static ChatMessage User(string text, ImageSource? attachmentImage, string? attachmentLabel)
        => new("user", text, attachmentImage, attachmentLabel);
    public static ChatMessage Assistant() => new("assistant", "");

    public string Role { get; }

    /// <summary>An attached image to render in the bubble, or <c>null</c>.</summary>
    public ImageSource? AttachmentImage { get; }
    public bool HasImage => AttachmentImage is not null;

    /// <summary>A chip for a non-image attachment (e.g. "🎵 clip.wav"), or empty.</summary>
    public string AttachmentLabel { get; }
    public bool HasAttachmentLabel => AttachmentLabel.Length > 0;

    private string _text = "";
    public string Text
    {
        get => _text;
        private set
        {
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            // The text Label hides when empty (image-only user turns); it must reappear as the
            // assistant's streamed answer grows from "".
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasText)));
        }
    }

    public bool HasText => _text.Length > 0;

    // The reasoning ("thinking") trace, shown dimmed above the answer when EnableThinking is on.
    private string _thinkingText = "";
    public string ThinkingText
    {
        get => _thinkingText;
        private set
        {
            _thinkingText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThinking)));
        }
    }

    public bool HasThinking => _thinkingText.Length > 0;

    public void Append(string chunk) =>
        MainThread.BeginInvokeOnMainThread(() => Text += chunk);

    public void AppendThinking(string chunk) =>
        MainThread.BeginInvokeOnMainThread(() => ThinkingText += chunk);

    public Color Background => Role == "user"
        ? (Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#2A4365") : Color.FromArgb("#DBEAFE"))
        : (Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#333333") : Color.FromArgb("#F1F1F1"));

    public LayoutOptions Alignment => Role == "user" ? LayoutOptions.End : LayoutOptions.Start;

    public event PropertyChangedEventHandler? PropertyChanged;
}
