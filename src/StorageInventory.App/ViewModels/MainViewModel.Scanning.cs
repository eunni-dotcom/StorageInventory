using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using StorageInventory.App.Mvvm;
using StorageInventory.Core;
using StorageInventory.Core.Reports;

namespace StorageInventory.App.ViewModels;

/// <summary>The scan and its outcome. The UI never touches the scanned tree: everything goes through Core.</summary>
public sealed partial class MainViewModel
{
    private readonly IStorageInventoryScanner _scanner = new InventoryScanner();
    private readonly Stopwatch _scanClock = new();
    private DispatcherTimer? _elapsedTimer;
    private CancellationTokenSource? _scanCancellation;
    private Task? _scanTask;
    private ResultsViewModel? _results;

    public ScanProgressViewModel Progress { get; } = new();

    public ResultsViewModel? Results { get => _results; private set => Set(ref _results, value); }

    public ICommand CancelScanCommand { get; private set; } = null!;
    public ICommand NewScanCommand { get; private set; } = null!;

    /// <summary>True while a scan (or its optional workbook step) is running.</summary>
    public bool IsBusy => _scanTask is { IsCompleted: false };

    /// <summary>Completes when the running scan has fully finished and every report file is closed.</summary>
    public Task WhenIdle => _scanTask ?? Task.CompletedTask;

    private void InitialiseScanning()
    {
        CancelScanCommand = new RelayCommand(CancelScan, () => Stage == AppStage.Scanning && !Progress.IsStopping);
        NewScanCommand = new RelayCommand(NewScan, () => Stage == AppStage.Results);
    }

    private void StartScan()
    {
        if (!Preflight.CanScan || IsBusy) return;
        var options = new StorageScanOptions { RootPath = SourcePath, OutputPath = OutputPath, SortFiles = SortFiles };
        var createWorkbook = CreateWorkbook;
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
    public void CancelScan()
    {
        if (_scanCancellation is null || Progress.IsStopping) return;
        Progress.MarkStopping();
        _scanCancellation.Cancel();
        CommandManager.InvalidateRequerySuggested();
    }

    private void NewScan()
    {
        Results = null;
        Stage = AppStage.Setup;
        ScheduleValidation();
    }
}
