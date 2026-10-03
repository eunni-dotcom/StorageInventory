"""Starts the gate harness (StorageInventory.Library.Tests.exe --benchmark gate ...) as child processes and reads their records.

Shared by perf_session.py, fixtures/make_fixtures.py and verify_generator.py. The harness is the C# executable of tests/StorageInventory.Library.Tests
(`--benchmark gate`, PerfGate/GateBenchmark.cs); this module only builds its command lines and its environment.

A child's environment is built EXPLICITLY (like the reference harness's ChildEnvironment): the parent's environment without the variables that
tune the .NET runtime (so a setting in the shell that started the session can never change a measured process unannounced), and with the
names of what was removed returned to the caller for the machine record.
"""
import json
import os
import subprocess
import sys

TUNING_PREFIXES = ('DOTNET_GC', 'DOTNET_gc', 'COMPlus_', 'DOTNET_Tiered', 'DOTNET_TC_', 'DOTNET_ReadyToRun', 'DOTNET_TieredPGO', 'DOTNET_PROCESSOR_COUNT',
                   'DOTNET_Thread', 'DOTNET_SYSTEM_GC', 'DOTNET_JitStress', 'DOTNET_JIT', 'DOTNET_OSR', 'SI_')
KEEP = ('DOTNET_ROOT', 'DOTNET_ROOT(x86)', 'DOTNET_MULTILEVEL_LOOKUP')


class Harness:
    """The command line of the harness: either an apphost executable, or a dotnet host and the assembly."""

    def __init__(self, exe, dll=None, bench_root=None):
        self.prefix = [exe] + ([dll] if dll else [])
        self.exe = exe
        self.dll = dll
        self.bench_root = bench_root

    def environment(self):
        env = dict(os.environ)
        removed = sorted(k for k in env if k.startswith(TUNING_PREFIXES) and k not in KEEP)
        for k in removed:
            del env[k]
        if self.bench_root:
            env['SI_BENCH_ROOT'] = self.bench_root
        return env, removed

    def command(self, *args):
        return self.prefix + ['--benchmark', 'gate'] + [str(a) for a in args]

    def start(self, *args, **popen):
        env, _ = self.environment()
        return subprocess.Popen(self.command(*args), stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, text=True, encoding='utf-8', **popen)

    def run(self, *args, timeout=None):
        """Runs one child to completion: (exit code, stdout lines, last JSON object or None, stderr text)."""
        p = self.start(*args)
        try:
            out, err = p.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            p.kill()
            out, err = p.communicate()
            return -9, out.splitlines(), None, err + '\n(timed out)'
        return p.returncode, out.splitlines(), last_json(out.splitlines()), err


def last_json(lines):
    for line in reversed(lines):
        line = line.strip()
        if line.startswith('{'):
            try:
                return json.loads(line)
            except ValueError:
                return None
    return None


def default_exe(repo_root):
    """The Release apphost built from this worktree."""
    return os.path.join(repo_root, 'tests', 'StorageInventory.Library.Tests', 'bin', 'Release', 'net10.0-windows', 'StorageInventory.Library.Tests.exe')


def repo_root():
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.abspath(os.path.join(here, '..', '..'))


def git_identity(root):
    """(commit, dirty) of the worktree the binary was built from."""
    def git(*a):
        return subprocess.run(['git', '-C', root] + list(a), capture_output=True, text=True, encoding='utf-8')
    head = git('rev-parse', 'HEAD')
    commit = head.stdout.strip() if head.returncode == 0 else 'unknown'
    status = git('status', '--porcelain', '--untracked-files=no')
    dirty = status.returncode == 0 and bool(status.stdout.strip())
    return commit, dirty


if __name__ == '__main__':
    print('gate_exe is a library; see perf_session.py', file=sys.stderr)
    sys.exit(2)
