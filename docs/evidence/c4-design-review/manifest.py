"""C4 design repair (erratum E4): the inventory of this evidence directory, with sizes and SHA-256, and a check that every file
the README names exists and is not ignored by git.

Usage:  python manifest.py            (run from this directory; writes MANIFEST.md and prints any problem)
"""
import hashlib
import os
import re
import subprocess
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

HERE = os.path.dirname(os.path.abspath(__file__))


def tracked_or_trackable(rel):
    r = subprocess.run(['git', 'check-ignore', '-q', rel], cwd=HERE)
    return r.returncode != 0   # 0 means ignored


def main():
    entries = []
    for root, dirs, files in os.walk(HERE):
        dirs[:] = sorted(d for d in dirs if d != '__pycache__')
        for f in sorted(files):
            rel = os.path.relpath(os.path.join(root, f), HERE).replace('\\', '/')
            if rel == 'MANIFEST.md':
                continue
            data = open(os.path.join(root, f), 'rb').read()
            entries.append((rel, len(data), hashlib.sha256(data).hexdigest(), tracked_or_trackable(rel)))
    problems = [f'ignored by git: {rel}' for rel, _, _, ok in entries if not ok]
    readme = open(os.path.join(HERE, 'README.md'), encoding='utf-8').read()
    names = set(e[0] for e in entries) | {'MANIFEST.md'}   # this script writes it
    for ref in sorted(set(re.findall(r'`((?:results|repair-results|census)/[^`*<>]+|[A-Za-z_]+\.(?:py|patch|md))`', readme))):
        if ref not in names and not any(n.startswith(ref.rstrip('/') + '/') for n in names):
            problems.append(f'named in README but missing: {ref}')
    lines = ['# Evidence inventory', '', 'Every file of `docs/evidence/c4-design-review/`, written by `manifest.py` (sizes in bytes; SHA-256 of the',
             'committed bytes, LF line endings). Regenerate after any change; `git diff` on this file shows what changed.', '',
             '| File | Bytes | SHA-256 |', '|---|---:|---|']
    lines += [f'| `{rel}` | {size:,} | `{digest}` |' for rel, size, digest, _ in entries]
    lines += ['', f'{len(entries)} files.']
    with open(os.path.join(HERE, 'MANIFEST.md'), 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    print(f'{len(entries)} files inventoried; problems: {len(problems)}')
    for p in problems:
        print('  ' + p)
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
