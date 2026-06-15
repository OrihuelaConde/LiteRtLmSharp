using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LiteRtLmSharp.SampleMaui.Services;

namespace LiteRtLmSharp.SampleMaui.Pages;

public partial class ModelsPage : ContentPage
{
    private readonly EngineService _engine;

    public ObservableCollection<ModelRow> Rows { get; } = [];

    public ModelsPage(ModelStore store, EngineService engine)
    {
        InitializeComponent();
        _engine = engine;

        foreach (var model in ModelCatalog.Models)
            Rows.Add(new ModelRow(model, store, engine, this));

        BindingContext = this;
        _engine.Loaded += () => MainThread.BeginInvokeOnMainThread(RefreshBanner);
        RefreshBanner();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        foreach (var row in Rows)
            row.RefreshState();
        RefreshBanner();
    }

    private void RefreshBanner()
    {
        if (_engine.LoadedModel is { } m)
        {
            EngineBanner.Text = $"✓ {m.DisplayName} ({_engine.LoadedBackend} · {_engine.SpeculativeLabel}) is loaded — chat is ready. Load another model or backend to switch.";
            EngineBanner.IsVisible = true;
        }
    }

    internal Task ShowError(string title, string message) => DisplayAlertAsync(title, message, "OK");

    internal Task<bool> Confirm(string title, string message) => DisplayAlertAsync(title, message, "Switch", "Cancel");

    /// <summary>Asks whether to enable speculative decoding, with a one-line explanation. Returns
    /// true to enable. Shown right after the backend pick so the choice is tied to the engine load.</summary>
    internal Task<bool> AskSpeculativeDecoding() => DisplayAlertAsync(
        "Speculative decoding",
        "The model drafts several tokens ahead with a small MTP drafter and verifies them in one " +
        "step — this can speed up decoding on models that ship a drafter (this one does). The gain " +
        "is largest on accelerator GPUs; on desktop CPU it may be slower. Enable it?",
        "Enable", "Off");

    internal Task GoToChat() => Shell.Current.GoToAsync("//ChatPage");

    internal void RefreshAllRows()
    {
        foreach (var row in Rows)
            row.RefreshState();
        RefreshBanner();
    }
}

/// <summary>Per-model row state for the Models list.</summary>
public sealed class ModelRow : INotifyPropertyChanged
{
    private readonly ModelStore _store;
    private readonly EngineService _engine;
    private readonly ModelsPage _page;
    private CancellationTokenSource? _downloadCts;

    public ModelInfo Model { get; }

    public ModelRow(ModelInfo model, ModelStore store, EngineService engine, ModelsPage page)
    {
        Model = model;
        _store = store;
        _engine = engine;
        _page = page;

        DownloadCommand = new Command(async () => await DownloadAsync());
        CancelCommand = new Command(() => _downloadCts?.Cancel());
        DeleteCommand = new Command(async () => await DeleteAsync());
        LoadCommand = new Command(async () => await LoadAsync());

        RefreshState();
    }

    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand LoadCommand { get; }

    private string _status = "";
    public string Status { get => _status; private set { _status = value; OnChanged(); } }

    private double _progress;
    public double Progress { get => _progress; private set { _progress = value; OnChanged(); } }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set { _isDownloading = value; OnChanged(); OnChanged(nameof(CanDownload)); OnChanged(nameof(CanLoad)); OnChanged(nameof(CanDelete)); }
    }

    public bool CanDownload => !IsDownloading && !_store.IsDownloaded(Model);
    public bool CanDelete => !IsDownloading && (_store.IsDownloaded(Model) || _store.HasPartialDownload(Model));
    // Load stays available while another model is loaded: the engine can be swapped in-place
    // (also lets you reload the SAME model on the other backend).
    public bool CanLoad => !IsDownloading && _store.IsDownloaded(Model);

    public void RefreshState()
    {
        if (IsDownloading) return;

        if (_store.IsDownloaded(Model))
        {
            long mb = (_store.GetDownloadedBytes(Model) ?? 0) / (1024 * 1024);
            Status = _engine.LoadedModel?.Id == Model.Id
                ? $"Loaded on {_engine.LoadedBackend} ({mb} MB on disk)"
                : $"Downloaded ({mb} MB)";
        }
        else if (_store.HasPartialDownload(Model))
        {
            Status = "Partial download — Download resumes it";
        }
        else
        {
            Status = Model.MobileFriendly ? "Not downloaded" : "Not downloaded (not recommended on phones)";
        }
        OnChanged(nameof(CanDownload)); OnChanged(nameof(CanLoad)); OnChanged(nameof(CanDelete));
    }

    private async Task DownloadAsync()
    {
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                if (p.Total > 0)
                {
                    Progress = (double)p.Done / p.Total;
                    Status = $"Downloading… {p.Done / (1024 * 1024)} / {p.Total / (1024 * 1024)} MB";
                }
                else
                {
                    Status = $"Downloading… {p.Done / (1024 * 1024)} MB";
                }
            });
            await _store.DownloadAsync(Model, progress, _downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Keep the .partial file: next Download resumes from it.
        }
        catch (Exception ex)
        {
            await _page.ShowError("Download failed", ex.Message);
        }
        finally
        {
            _downloadCts = null;
            IsDownloading = false;
            RefreshState();
        }
    }

    private async Task DeleteAsync()
    {
        if (_engine.LoadedModel?.Id == Model.Id)
        {
            await _page.ShowError("Model in use", "This model is loaded in the engine. Load another model first.");
            return;
        }
        _store.Delete(Model);
        RefreshState();
    }

    private async Task LoadAsync()
    {
        // Switching models/backends is fine, but make it deliberate: the old engine is
        // disposed and every conversation (Chat and Tools tabs) is reset.
        if (_engine.IsLoaded &&
            !await _page.Confirm("Switch model?",
                $"This unloads {_engine.LoadedModel!.DisplayName} ({_engine.LoadedBackend}) and clears all conversations."))
            return;

        // GPU-only models reject CPU at engine creation, so don't offer it.
        string[] backends = Model.GpuOnly ? ["GPU"] : ["CPU", "GPU"];
        string backend = await _page.DisplayActionSheetAsync("Backend", "Cancel", null, backends) switch
        {
            "GPU" => "gpu",
            "CPU" => "cpu",
            _ => "",
        };
        if (backend.Length == 0) return;

        // Speculative decoding is fixed at engine creation, so ask right before loading — only for
        // models that ship an MTP drafter (others would just no-op). Tying it to the load keeps the
        // choice coupled to the engine restart instead of a switch sitting on the row.
        bool speculative = Model.SupportsSpeculativeDecoding && await _page.AskSpeculativeDecoding();

        Status = "Loading model… (this can take a while)";
        try
        {
            await _engine.LoadAsync(Model, _store.GetLocalPath(Model), backend, speculative);
            _page.RefreshAllRows(); // the previously loaded model's row needs its status back
            await _page.GoToChat();
        }
        catch (Exception ex)
        {
            Status = "Load failed";
            await _page.ShowError("Load failed", ex.Message);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => MainThread.BeginInvokeOnMainThread(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
}
