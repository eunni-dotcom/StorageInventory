using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using StorageInventory.App.Mvvm;
using StorageInventory.App.ViewModels;
using StorageInventory.Core;
using StorageInventory.Core.Reports;

namespace StorageInventory.App.Services;

/// <summary>
/// The running scan and its outcome, owned for the whole app session (UI-12). <see cref="App"/> creates exactly one,
/// before the shell, and hands it to the Scan page; no view and no page view model creates or owns a scan. Views come
/// and go (stage changes, re-hosting of the Scan page); the scan, its cancellation, its progress and its result stay
/// here, so replacing a view neither stops nor restarts it.
/// The UI never touches the scanned tree: everything goes through Core.
/// </summary>
public sealed class ScanSession : ObservableObject
{
    private readonly IStorageInventoryScanner _scanner;
    private readonly Stopwatch _scanClock = new();
    private DispatcherTimer? _elapsedTimer;
    private CancellationTokenSource? _scanCancellation;
    private Task? _scanTask;
    private AppStage _stage = AppStage.Setup;
    private ResultsViewModel? _results;

    public ScanSession() : this(new InventoryScanner())
    {
    }

    /// <summary>The scanner is fixed for the session: one scanner, and at most one scan at a time.</summary>
    public ScanSession(IStorageInventoryScanner scanner)
    {
        _scanner = scanner;
    }

    /// <summary>Setup (no scan and no result shown) → Scanning → Results.</summary>
    public AppStage Stage
    {
        get => _stage;
        private set
        {
            if (Set(ref _stage, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public ScanProgressViewModel Progress { get; } = new();

    /// <summary>The outcome of the last scan, built only from Core's <see cref="StorageScanResult"/>.</summary>
    public ResultsViewModel? Results { get => _results; private set => Set(ref _results, value); }

    /// <summary>True while a scan (or its optional workbook step) is running.</summary>
    public bool IsBusy => _scanTask is { IsCompleted: false };

    /// <summary>Completes when the running scan has fully finished and every report file is closed.</summary>
    public Task WhenIdle => _scanTask ?? Task.CompletedTask;

    /// <summary>Starts a scan, unless one is already running (then nothing happens and false is returned). Call it
    /// on the UI thread: progress and the elapsed-time clock are delivered there.</summary>
    public bool Start(StorageScanOptions options, bool createWorkbook)
    {
        if (IsBusy) return false;
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        Progress.Reset();
        Results = null;
        Stage = AppStage.Scanning;

        _scanClock.Restart();
        _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Progress.Tick(_scanClock.Elapsed), Dispatcher.CurrentDispatcher);
        _elapsedTimer.Start();

        // Progress<T> delivers reports on this (UI) thread; the scan itself runs on a background thread in Core.
        var progress = new Progress<StorageScanProgress>(Progress.Update);
        _scanTask = RunScanAsync(options, createWorkbook, progress, token);
        return true;
    }

    private async Task RunScanAsync(StorageScanOptions options, bool createWorkbook, IProgress<StorageScanProgress> progress, CancellationToken token)
    {
        StorageScanResult result;
        WorkbookExportResult? workbook = null;
        try
        {
            result = await _scanner.ScanAsync(options, progress, token);
            if (createWorkbook && result.Finished && !token.IsCancellationRequested)
            {
                Progress.ShowPostProcessing("Creating the optional Excel workbook from the finished reports…");
                workbook = await Task.Run(() => WorkbookExporter.Export(result, token), CancellationToken.None);
            }
        }
        finally
        {
            _elapsedTimer?.Stop();
            _scanClock.Stop();
        }

        Results = new ResultsViewModel(result, workbook, _scanClock.Elapsed);
        Stage = AppStage.Results;
        if (result.Finished) _ = Results.Exploration.LoadAsync(result, CancellationToken.None);
        _scanCancellation?.Dispose();
        _scanCancellation = null;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Asks Core to stop at its next safe point. Core closes every report file and returns a Cancelled
    /// result listing the incomplete files; nothing is terminated abruptly and nothing is deleted.</summary>
    public void Cancel()
    {
        if (_scanCancellation is null || Progress.IsStopping) return;
        Progress.MarkStopping();
        _scanCancellation.Cancel();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Puts the last result away and returns to Setup (New scan). A running scan is never put away.</summary>
    public void Clear()
    {
        if (IsBusy) return;
        Results = null;
        Stage = AppStage.Setup;
    }
}
