using StorageInventory.App.Mvvm;
using StorageInventory.Core;

namespace StorageInventory.App.ViewModels;

/// <summary>Live scan counters. While the total amount of work is unknown the progress bar is indeterminate:
/// no percentage is ever invented.</summary>
public sealed class ScanProgressViewModel : ObservableObject
{
    private StorageScanProgress _last;
    private TimeSpan _elapsed;
    private bool _stopping;
    private string _phaseOverride = "";

    public string Files => Format.Count(_last.FilesDiscovered);
    public string Folders => Format.Count(_last.FoldersDiscovered);
    public string Data => Format.Bytes(_last.BytesDiscovered);
    public string Errors => Format.Count(_last.ScanErrors);
    public string LinksSkipped => Format.Count(_last.ReparsePointsSkipped);
    public string Elapsed => Format.Elapsed(_elapsed);
    public string CurrentFolder => string.IsNullOrEmpty(_last.CurrentRelativePath) ? "—" : _last.CurrentRelativePath;

    public string Phase => _stopping ? "Stopping safely: closing the report files…"
        : _phaseOverride.Length > 0 ? _phaseOverride
        : _last.Phase switch
        {
            ScanPhase.Validating => "Checking the folders…",
            ScanPhase.Enumerating => "Reading folder contents (names, sizes and dates only)…",
            ScanPhase.Aggregating => "Adding up folder totals…",
            ScanPhase.WritingFoldersReport => "Writing the Folders report…",
            ScanPhase.SortingFiles => "Sorting files largest-first…",
            ScanPhase.WritingFilesReport => "Writing the Files report…",
            _ => "Finishing…",
        };

    /// <summary>True unless the current phase has a genuine known total.</summary>
    public bool IsIndeterminate => !_last.HasKnownTotal;

    public double PercentDone => _last.HasKnownTotal ? 100.0 * _last.PhaseItemsDone / _last.PhaseItemsTotal : 0;

    public bool IsStopping => _stopping;

    public void Reset()
    {
        _last = default;
        _elapsed = TimeSpan.Zero;
        _stopping = false;
        _phaseOverride = "";
        RaiseAll();
    }

    public void Update(StorageScanProgress progress)
    {
        _last = progress;
        if (progress.Elapsed > _elapsed) _elapsed = progress.Elapsed;
        RaiseAll();
    }

    public void Tick(TimeSpan elapsed)
    {
        _elapsed = elapsed;
        OnPropertyChanged(nameof(Elapsed));
    }

    public void MarkStopping()
    {
        _stopping = true;
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(IsStopping));
    }

    public void ShowPostProcessing(string text)
    {
        _phaseOverride = text;
        _last = _last with { PhaseItemsDone = 0, PhaseItemsTotal = 0 };
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[] { nameof(Files), nameof(Folders), nameof(Data), nameof(Errors), nameof(LinksSkipped), nameof(Elapsed),
                                     nameof(CurrentFolder), nameof(Phase), nameof(IsIndeterminate), nameof(PercentDone), nameof(IsStopping) })
        {
            OnPropertyChanged(name);
        }
    }
}
