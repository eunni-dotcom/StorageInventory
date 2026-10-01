using System.ComponentModel;
using System.Windows.Input;
using StorageInventory.App.Mvvm;
using StorageInventory.Core;

namespace StorageInventory.App.ViewModels;

/// <summary>The scan's commands. The scan, its cancellation, progress and outcome live in the app-lifetime
/// <see cref="Services.ScanSession"/>; this page only asks it to start, stop or put a result away.</summary>
public sealed partial class MainViewModel
{
    public ScanProgressViewModel Progress => Session.Progress;

    public ResultsViewModel? Results => Session.Results;

    public ICommand CancelScanCommand { get; private set; } = null!;
    public ICommand NewScanCommand { get; private set; } = null!;

    /// <summary>True while a scan (or its optional workbook step) is running.</summary>
    public bool IsBusy => Session.IsBusy;

    /// <summary>Completes when the running scan has fully finished and every report file is closed.</summary>
    public Task WhenIdle => Session.WhenIdle;

    private void InitialiseScanning()
    {
        CancelScanCommand = new RelayCommand(CancelScan, () => Stage == AppStage.Scanning && !Progress.IsStopping);
        NewScanCommand = new RelayCommand(NewScan, () => Stage == AppStage.Results);
    }

    private void StartScan()
    {
        if (!Preflight.CanScan || IsBusy) return;
        Session.Start(new StorageScanOptions { RootPath = SourcePath, OutputPath = OutputPath, SortFiles = SortFiles }, CreateWorkbook);
    }

    /// <summary>Asks Core to stop at its next safe point (through the session). Core closes every report file and
    /// returns a Cancelled result listing the incomplete files; nothing is terminated abruptly and nothing is deleted.</summary>
    public void CancelScan() => Session.Cancel();

    private void NewScan()
    {
        Session.Clear();
        ScheduleValidation();
    }

    /// <summary>Re-raises the session's changes under v1's property names, so v1's bindings are unchanged.</summary>
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Services.ScanSession.Stage):
                OnPropertyChanged(nameof(Stage));
                OnPropertyChanged(nameof(IsSetup));
                OnPropertyChanged(nameof(IsScanning));
                OnPropertyChanged(nameof(IsResults));
                break;
            case nameof(Services.ScanSession.Results):
                OnPropertyChanged(nameof(Results));
                break;
        }
    }
}
