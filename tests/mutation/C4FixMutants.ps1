# The C4 repair-fixes mutants for C4R-M01 (the cadence inside one big folder) and C4R-M02 (TEST-L9: the space check's inputs), dot-sourced by
# Invoke-C4Mutants.ps1 (-Set Fixes), which defines $fixMutants. Same method as C4RepairMutants.ps1: exact text replacements in a scratch
# copy, KILLED only when a test of the mutant's own Filter fails that does not fail in the unmutated run.
#
# Always-false replacements use a runtime expression (never the constant false): the compiler reports unreachable code (CS0162) as a
# warning, the build treats warnings as errors, and a mutant that does not compile proves nothing.

$L = 'src/StorageInventory.Library'
$S = "$L/Import/SnapshotImporter.cs"
$C = "$L/CancellationSupport.cs"
$bigFilter = @('LargeFolderTests', 'SpaceGuardTests')
$inputFilter = @('SpaceInputsTests')

function Replace-One([string] $File, [string] $Find, [string] $Replace) { @{ File = $File; Find = $Find; Replace = $Replace } }

$ADVANCE_INTERN = 'Advance(writer, lease, state, options, lengths, inserted: (int)(names.NewNames - before), worked: 1);'
$PAGECOUNT_READ = 'var pageCount = ReadEngineInput(writer, lease, options, kind, EngineInput.PageCount);'
$PAGESIZE_READ = 'var pageSize = ReadEngineInput(writer, lease, options, kind, EngineInput.PageSize);'
$LENGTHS_READ = 'var (main, journal) = lengths();'
$PENDING = 'return Math.Max(0L, checked(pageCount * pageSize) - mainFileLength);'
$UNREADABLE_RETURN = 'if (guard is null && record is null) return;   // nobody consults the numbers: an unreadable length is not a failure'

$fixMutants = @(
    # ---- C4R-M01: the checks inside one big folder ----
    @{ Id = 'C4F-A01'; Suite = 'Library'; Filter = $bigFilter; What = 'M01 as reviewed: the name interning of a folder counts nothing and looks at nothing (no check inside the folder)'; Edits = @(Replace-One $S $ADVANCE_INTERN '_ = before;') },
    @{ Id = 'C4F-A02'; Suite = 'Library'; Filter = $bigFilter; What = 'name and folder-path rows are not counted toward the 4,096-row interval (the clock still looks, the guard does not)'; Edits = @(Replace-One $S $ADVANCE_INTERN 'Advance(writer, lease, state, options, lengths, inserted: 0, worked: 1);') },
    @{ Id = 'C4F-A03'; Suite = 'Library'; Filter = $bigFilter; What = 'a new folder-path row is not counted (only the folder row)'; Edits = @(Replace-One $S 'Advance(writer, lease, state, options, lengths, inserted: 1 + pathRows);' 'Advance(writer, lease, state, options, lengths, inserted: 1 + pathRows * 0);') },
    @{ Id = 'C4F-A04'; Suite = 'Library'; Filter = $bigFilter; What = 'the time bound never fires (the elapsed time is not looked at)'; Edits = @(Replace-One $S 'if (state.Checker.SinceLast < MaxLookGap) return;' 'if (state.Work >= 0) return;') },
    @{ Id = 'C4F-A05'; Suite = 'Library'; Filter = $bigFilter; What = 'the clock is read every 100,000,000 units of work instead of every 64'; Edits = @(Replace-One $S 'internal const int ClockPoll = 64;' 'internal const int ClockPoll = 100_000_000;') },
    @{ Id = 'C4F-A06'; Suite = 'Library'; Filter = $bigFilter; What = 'the time bound is 10 s instead of 100 ms'; Edits = @(Replace-One $S 'TimeSpan.FromMilliseconds(100);' 'TimeSpan.FromSeconds(10);') },
    @{ Id = 'C4F-A07'; Suite = 'Library'; Filter = $bigFilter; What = 'a folder boundary resets the 4,096-row interval counter (the next check is postponed by a whole interval at every folder)'; Edits = @(Replace-One $S 'var files = rows.FilesOfFolder(folderIndex);' "state.NextGuard = state.Rows + GuardInterval;`n            var files = rows.FilesOfFolder(folderIndex);") },
    @{ Id = 'C4F-A08'; Suite = 'Library'; Filter = $bigFilter; What = 'a folder boundary resets the units-of-work counter (many small folders never reach the clock poll)'; Edits = @(Replace-One $S 'var files = rows.FilesOfFolder(folderIndex);' "state.Work = 0;`n            var files = rows.FilesOfFolder(folderIndex);") },
    @{ Id = 'C4F-A09'; Suite = 'Library'; Filter = $bigFilter; What = 'a name looked up (inserting nothing) is counted as an inserted row'; Edits = @(Replace-One $S 'state.Rows += inserted;' 'state.Rows += inserted + worked;') },
    @{ Id = 'C4F-A10'; Suite = 'Library'; Filter = $bigFilter; What = 'the look forced by time notes the observation but never throws on a cancelled token'; Edits = @(Replace-One $S "        writer.CheckCurrent(lease);`n        state.Checker.Check();`n    }`n`n    /// <summary>Interns one name" "        writer.CheckCurrent(lease);`n        state.Checker.Note();`n    }`n`n    /// <summary>Interns one name") },
    @{ Id = 'C4F-A11'; Suite = 'Library'; Filter = $bigFilter; What = 'the token checker ignores the clock it was given when it reads the time (the supplied clock only sets its starting point)'; Edits = @(Replace-One $C 'private readonly Func<long> _clock = clock ?? Stopwatch.GetTimestamp;' 'private readonly Func<long> _clock = clock is null ? Stopwatch.GetTimestamp : Stopwatch.GetTimestamp;') },
    @{ Id = 'C4F-A12'; Suite = 'Library'; Filter = $bigFilter; What = 'the folder-phase name look-up is not accounted (the folder name is interned without Advance)'; Edits = @(Replace-One $S 'var nameId = Intern(writer, lease, names, folder.Name, state, options, lengths);' 'var nameId = names.Intern(lease, folder.Name);') },

    # ---- C4R-M02: the space check's inputs ----
    @{ Id = 'C4F-B01'; Suite = 'Library'; Filter = $inputFilter; What = 'Λ is not clamped at 0 (reviewer mutant RV-10)'; Edits = @(Replace-One $S $PENDING 'return checked(pageCount * pageSize) - mainFileLength;') },
    @{ Id = 'C4F-B02'; Suite = 'Library'; Filter = $inputFilter; What = 'the engine''s own error is not an unreadable input (reviewer mutant RV-14: IsUnreadable without SqliteException)'; Edits = @(Replace-One $S 'ex is IOException or UnauthorizedAccessException or SqliteException or FormatException' 'ex is IOException or UnauthorizedAccessException or FormatException') },
    @{ Id = 'C4F-B03'; Suite = 'Library'; Filter = $inputFilter; What = 'a page_count that cannot be read reads as 0'; Edits = @(Replace-One $S 'return options?.OnEngineInput is { } seam ? seam(input, kind, value) : value;' "if (options?.OnEngineInput is not { } seam) return value;`n        try { return seam(input, kind, value); }`n        catch (Exception) when (input == EngineInput.PageCount) { return 0; }") },
    @{ Id = 'C4F-B04'; Suite = 'Library'; Filter = $inputFilter; What = 'the page size is read at BEGIN only and assumed 4,096 at every later check (the old cache)'; Edits = @(Replace-One $S $PAGESIZE_READ 'var pageSize = kind == SpaceCheckKind.Begin ? ReadEngineInput(writer, lease, options, kind, EngineInput.PageSize) : 4096L;') },
    @{ Id = 'C4F-B05'; Suite = 'Library'; Filter = $inputFilter; What = 'page_count is read at the first check and frozen at the periodic checks'; Edits = @(
        Replace-One $S 'internal long Work;' "internal long Work;`n        internal long Frozen;"
        Replace-One $S $PAGECOUNT_READ "var pageCount = kind == SpaceCheckKind.Rows && state.Frozen > 0 ? state.Frozen : ReadEngineInput(writer, lease, options, kind, EngineInput.PageCount);`n            if (state.Frozen == 0) state.Frozen = pageCount;") },
    @{ Id = 'C4F-B06'; Suite = 'Library'; Filter = $inputFilter; What = 'the main file''s length is 0 at the periodic checks'; Edits = @(Replace-One $S $LENGTHS_READ "var (main, journal) = lengths();`n            if (kind == SpaceCheckKind.Rows) main = 0;") },
    @{ Id = 'C4F-B07'; Suite = 'Library'; Filter = $inputFilter; What = 'the journal''s length is 0 at the periodic checks'; Edits = @(Replace-One $S $LENGTHS_READ "var (main, journal) = lengths();`n            if (kind == SpaceCheckKind.Rows) journal = 0;") },
    @{ Id = 'C4F-B08'; Suite = 'Library'; Filter = $inputFilter; What = 'Λ is computed in 32 bits'; Edits = @(Replace-One $S $PENDING 'return Math.Max(0L, unchecked((int)(pageCount * pageSize)) - mainFileLength);') },
    @{ Id = 'C4F-B09'; Suite = 'Library'; Filter = $inputFilter; What = 'out-of-range inputs of Λ are accepted (no validation)'; Edits = @(Replace-One $S 'if (pageCount < 0 || pageSize <= 0 || mainFileLength < 0) throw new FormatException("An input of the space check is out of range.");' '') },
    @{ Id = 'C4F-B10'; Suite = 'Library'; Filter = $inputFilter; What = 'Λ is clamped at 1 instead of 0'; Edits = @(Replace-One $S $PENDING 'return Math.Max(1L, checked(pageCount * pageSize) - mainFileLength);') },
    @{ Id = 'C4F-B11'; Suite = 'Library'; Filter = $inputFilter; What = 'an unreadable input at BEGIN is ignored'; Edits = @(Replace-One $S $UNREADABLE_RETURN 'if (kind == SpaceCheckKind.Begin || (guard is null && record is null)) return;') },
    @{ Id = 'C4F-B12'; Suite = 'Library'; Filter = $inputFilter; What = 'an unreadable input at a periodic check is ignored'; Edits = @(Replace-One $S $UNREADABLE_RETURN 'if (kind == SpaceCheckKind.Rows || (guard is null && record is null)) return;') },
    @{ Id = 'C4F-B13'; Suite = 'Library'; Filter = $inputFilter; What = 'an unreadable input at the final check is ignored'; Edits = @(Replace-One $S $UNREADABLE_RETURN 'if (kind == SpaceCheckKind.Final || (guard is null && record is null)) return;') },
    @{ Id = 'C4F-B14'; Suite = 'Library'; Filter = $inputFilter; What = 'a failing free-space query inside the guard is not a stop (the guard''s exception is swallowed and the import continues)'; Edits = @(Replace-One $S 'throw new ImportException(CaptureFailureKind.LibraryFull, $"The space guard failed at {kind}: {ex.Message}", ex);' 'permit = true;') }
)
