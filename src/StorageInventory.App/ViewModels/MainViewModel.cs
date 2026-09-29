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
/// The window's state machine: Setup (paths, options, pre-flight) → Scanning → Results. All path safety comes from
/// Core's <see cref="PathPolicy"/>; the scanner validates again before it starts, whatever the UI showed.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan ValidationDelay = TimeSpan.FromMilliseconds(300);

    private readonly IFolderPicker _folderPicker;
    private readonly SynchronizationContext _ui;
    private AppStage _stage = AppStage.Setup;
    private string _sourcePath = "";
    private string _outputPath;
    private bool _sortFiles = true;
    private bool _createWorkbook;
    private int _validationVersion;
    private CancellationTokenSource? _pendingValidation;

    public MainViewModel(IFolderPicker folderPicker)
    {
        _folderPicker = folderPicker;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StorageInventory");
        BrowseSourceCommand = new RelayCommand(BrowseSource, () => Stage == AppStage.Setup);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => Stage == AppStage.Setup);
        StartScanCommand = new RelayCommand(StartScan, () => Stage == AppStage.Setup && Preflight.CanScan);
        InitialiseScanning();
        ScheduleValidation();
    }

    public PreflightViewModel Preflight { get; } = new();

    public AppStage Stage
    {
        get => _stage;
        private set
        {
            if (Set(ref _stage, value))
            {
                OnPropertyChanged(nameof(IsSetup));
                OnPropertyChanged(nameof(IsScanning));
                OnPropertyChanged(nameof(IsResults));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

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
