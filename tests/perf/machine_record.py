"""The machine record of a TEST-P1 session (§15.4 "Machine record", once per session): CPU model and logical CPUs, RAM, storage model and bus,
file system and cluster size, OS build, power plan and power source, Microsoft Defender's real-time state (and whether the benchmark folder has an
exclusion), whether the diagnostic per-process counters see its engine (filled in by the sampler), the engine version, page size and cache size, and
the commit and build-output hash of the binary measured.

Read without elevation from CIM and the Defender cmdlets; whatever a query cannot answer is recorded as "unavailable: <reason>" and never guessed.
No host name, user name, serial number or path is recorded.
"""
import ctypes
import json
import os
import subprocess
import sys

import loadsource

SCRIPT = r'''
$ErrorActionPreference = 'Stop'
function Probe($b) { try { & $b } catch { "unavailable: " + $_.Exception.Message.Split("`n")[0] } }
$drive = $env:GATE_DRIVE
$out = [ordered]@{}
$out.cpu = Probe { $p = @(Get-CimInstance Win32_Processor); [ordered]@{ model = ($p[0].Name -replace '\s+', ' ').Trim(); packages = $p.Count; cores = ($p | Measure-Object NumberOfCores -Sum).Sum; logicalProcessorsWmi = ($p | Measure-Object NumberOfLogicalProcessors -Sum).Sum } }
$out.ramBytes = Probe { (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory }
$out.os = Probe { $o = Get-CimInstance Win32_OperatingSystem; [ordered]@{ caption = $o.Caption; version = $o.Version; build = $o.BuildNumber } }
$out.volume = Probe { $v = Get-CimInstance Win32_Volume | Where-Object { $_.DriveLetter -eq $drive } | Select-Object -First 1; if ($v) { [ordered]@{ fileSystem = $v.FileSystem; clusterBytes = $v.BlockSize; capacityBytes = $v.Capacity; freeBytes = $v.FreeSpace } } else { "unavailable: no volume for $drive" } }
$out.storage = Probe { $letter = $drive.TrimEnd(':'); $d = Get-Partition -DriveLetter $letter | Get-Disk; $pd = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq $d.Number } | Select-Object -First 1; [ordered]@{ model = ($pd.FriendlyName -replace '\s+', ' ').Trim(); busType = [string]$pd.BusType; mediaType = [string]$pd.MediaType; partitionStyle = [string]$d.PartitionStyle } }
$out.powerPlan = Probe { $line = (powercfg /getactivescheme) -join ' '; if ($line -match '\((.+)\)') { $Matches[1] } else { $line } }
$out.defender = Probe { $s = Get-MpComputerStatus; [ordered]@{ realTimeProtectionEnabled = $s.RealTimeProtectionEnabled; antivirusEnabled = $s.AntivirusEnabled; engineVersion = $s.AMEngineVersion; serviceVersion = $s.AMServiceVersion; antivirusSignatureVersion = $s.AntivirusSignatureVersion } }
$out.defenderExclusions = Probe { $x = Get-MpPreference; $paths = @($x.ExclusionPath); if ($paths.Count -eq 1 -and $paths[0] -like 'N/A*') { "unavailable: exclusions are not readable without elevation" } else { [ordered]@{ pathExclusions = $paths.Count; benchmarkFolderExcluded = [bool]($paths | Where-Object { $env:GATE_FOLDER -and $env:GATE_FOLDER.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) } } }
$out | ConvertTo-Json -Depth 5 -Compress
'''


class SystemPowerStatus(ctypes.Structure):
    _fields_ = [('ACLineStatus', ctypes.c_ubyte), ('BatteryFlag', ctypes.c_ubyte), ('BatteryLifePercent', ctypes.c_ubyte),
                ('SystemStatusFlag', ctypes.c_ubyte), ('BatteryLifeTime', ctypes.c_ulong), ('BatteryFullLifeTime', ctypes.c_ulong)]


def power_source():
    s = SystemPowerStatus()
    if not ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(s)):
        return 'unavailable: GetSystemPowerStatus failed'
    ac = {0: 'battery', 1: 'AC power', 255: 'unknown'}.get(s.ACLineStatus, 'unknown')
    return {'source': ac, 'batteryPercent': None if s.BatteryLifePercent == 255 else s.BatteryLifePercent, 'noBattery': bool(s.BatteryFlag & 128)}


def collect(bench_root, machine_json=None):
    """The record. `bench_root` is the folder the benchmark's Libraries live in (its drive is the one described)."""
    drive = os.path.splitdrive(os.path.abspath(bench_root))[0].upper() or 'C:'
    env = dict(os.environ, GATE_DRIVE=drive, GATE_FOLDER=os.path.abspath(bench_root))
    p = subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-Command', SCRIPT], capture_output=True, text=True, encoding='utf-8', env=env, timeout=180)
    try:
        rec = json.loads(p.stdout.strip().splitlines()[-1])
    except (ValueError, IndexError):
        rec = {'error': 'unavailable: the machine query produced no JSON: ' + (p.stderr.strip().splitlines() or ['no output'])[-1]}
    rec['logicalProcessors'] = loadsource.logical_processors()
    rec['processorGroups'] = ctypes.windll.kernel32.GetActiveProcessorGroupCount()
    rec['powerSource'] = power_source()
    rec['benchmarkDrive'] = drive
    rec['python'] = sys.version.split()[0]
    if machine_json:
        rec['engine'] = machine_json
    return rec


if __name__ == '__main__':
    print(json.dumps(collect(sys.argv[1] if len(sys.argv) > 1 else os.environ.get('TEMP', 'C:\\')), indent=1))
