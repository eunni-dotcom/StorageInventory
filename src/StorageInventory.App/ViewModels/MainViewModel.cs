using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using StorageInventory.App.Mvvm;
using StorageInventory.App.Services;
using StorageInventory.Core.Paths;

namespace StorageInventory.App.ViewModels;

public enum AppStage
{
    Setup,
    Scanning,
    Results,
}

/// <summary>
/// The Scan page's view model: Setup (paths, options, pre-flight) → Scanning → Results. The scan itself, its stage,
/// progress and result belong to the app-lifetime <see cref="ScanSession"/> (UI-12); this view model presents them
/// under v1's names. All path safety comes from Core's <see cref="PathPolicy"/>; the scanner validates again before it
/// starts, whatever the UI showed.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan ValidationDelay = TimeSpan.FromMilliseconds(300);

    private readonly IFolderPicker _folderPicker;
    private readonly SynchronizationContext _ui;
    private string _sourcePath = "";
    private string _outputPath;
    private bool _sortFiles = true;
    private bool _createWorkbook;
    private int _validationVersion;
    private CancellationTokenSource? _pendingValidation;

    public MainViewModel(ScanSession session, IFolderPicker folderPicker)
    {
        Session = session;
        _folderPicker = folderPicker;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        // Weak: the session outlives page view models, and must never keep one alive.
        PropertyChangedEventManager.AddHandler(session, OnSessionChanged, string.Empty);
        _outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StorageInventory");
        BrowseSourceCommand = new RelayCommand(BrowseSource, () => Stage == AppStage.Setup);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => Stage == AppStage.Setup);
        StartScanCommand = new RelayCommand(StartScan, () => Stage == AppStage.Setup && Preflight.CanScan);
        InitialiseScanning();
        ScheduleValidation();
    }

    /// <summary>The app session's scan, shared by every view of the Scan page.</summary>
    public ScanSession Session { get; }

    public PreflightViewModel Preflight { get; } = new();

    public AppStage Stage => Session.Stage;

    public bool IsSetup => Stage == AppStage.Setup;
    public bool IsScanning => Stage == AppStage.Scanning;
    public bool IsResults => Stage == AppStage.Results;

    public string SourcePath { get => _sourcePath; set { if (Set(ref _sourcePath, value)) ScheduleValidation(); } }
    public string OutputPath { get => _outputPath; set { if (Set(ref _outputPath, value)) ScheduleValidation(); } }
    public bool SortFiles { get => _sortFiles; set => Set(ref _sortFiles, value); }
    public bool CreateWorkbook { get => _createWorkbook; set => Set(ref _createWorkbook, value); }

    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand StartScanCommand { get; }

    private void BrowseSource()
    {
        var chosen = _folderPicker.PickFolder("Choose a folder or drive to scan", SourcePath);
        if (chosen is not null) SourcePath = chosen;
    }

    private void BrowseOutput()
    {
        var chosen = _folderPicker.PickFolder("Choose where to save the reports", Directory.Exists(OutputPath) ? OutputPath : null);
        if (chosen is not null) OutputPath = chosen;
    }

    /// <summary>Re-validates shortly after the user stops typing. Validation reads metadata (and may touch a network
    /// path), so it runs off the UI thread; stale results are discarded.</summary>
    private void ScheduleValidation()
    {
        _pendingValidation?.Cancel();
        var cts = _pendingValidation = new CancellationTokenSource();
        var version = ++_validationVersion;
        var source = SourcePath;
        var output = OutputPath;
        Preflight.ShowChecking();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ValidationDelay, cts.Token).ConfigureAwait(false);
                var result = PathPolicy.Validate(source, output);
                _ui.Post(_ =>
                {
                    if (version != _validationVersion) return;
                    Preflight.Show(result, string.IsNullOrWhiteSpace(source));
                    CommandManager.InvalidateRequerySuggested();
                }, null);
            }
            catch (OperationCanceledException)
            {
                // A newer validation superseded this one.
            }
        });
    }
}
