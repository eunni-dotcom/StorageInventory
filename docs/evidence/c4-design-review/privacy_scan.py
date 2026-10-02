"""C4 design repair: privacy scan of the evidence before it is committed.

Usage:  python privacy_scan.py <dir or file> ... [--names <harvested names file>] [--term <text> ...] [--processes]

Looks, in every text file given (recursively), for:
  identity    the user-profile path, the user name and the computer name of this machine (from the environment) and every
              --term given (for example an e-mail address), case-insensitively
  paths       absolute Windows paths (X:\\...) other than the generic ones the harness names on purpose (the three system trees
              it harvested from, the synthetic root) and the placeholders <user>, <scratch>, <worktree>
  names       every file name of the harvested names file of the C4 design review (--names; the file stays in the scratch
              directory and is never committed), as a whole token, if it is at least 8 code units long and contains a dot
  processes   (--processes) the image names of the processes running now, as whole tokens, case-sensitively, except the two
              the specification names on purpose (System, MsMpEng) and the generic words in BENIGN; and, in data files, a load
              line that kept other processes' names
Prints counts per category and per file; the matching texts are printed only to the console, never written to a file.
Exit code 1 if anything is found.
"""
import os
import re
import subprocess
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

TEXT = {'.md', '.py', '.json', '.jsonl', '.log', '.patch', '.txt', '.csv', ''}
ALLOWED_PATHS = [r'C:\Program', r'C:\Windows', r'D:\Media', 'D:\\', 'X:\\']   # the harness's harvest roots and synthetic root
# Tokens that match a running process's image name or a harvested name but are generic words of the scripts themselves: the
# interpreter and shell named in usage lines, the tool this scan calls, the system DLL the harness imports, a word in comments
BENIGN = {'python', 'powershell', 'svchost', 'tasklist', 'Code', 'kernel32.dll',
          # public file names of the product's own dependencies and of the specification's text, and English words that
          # happen to be process names here (the specification's branch names start with "claude/")
          'README.md', 'e_sqlite3.dll', 'esent.dll', 'winsqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll',
          'Everything', 'claude', 'services', 'tail', 'head'}
NAMED_PROCESSES = {'System', 'MsMpEng', 'Idle', '_Total', 'Registry'}


def read_names(path):
    """The design review's harvest file: BinaryWriter(Encoding.Unicode): int32 list count; per list int32 n, n strings, each a
    7-bit encoded byte length and UTF-16LE bytes."""
    data = open(path, 'rb').read()
    pos = 0

    def i32():
        nonlocal pos
        v = int.from_bytes(data[pos:pos + 4], 'little')
        pos += 4
        return v

    def s7():
        nonlocal pos
        n = shift = 0
        while True:
            b = data[pos]
            pos += 1
            n |= (b & 0x7F) << shift
            shift += 7
            if not b & 0x80:
                break
        t = data[pos:pos + n].decode('utf-16-le', 'replace')
        pos += n
        return t

    names = set()
    for _ in range(i32()):
        for _ in range(i32()):
            names.add(s7())
    return names


def files(args):
    for a in args:
        if os.path.isdir(a):
            for root, _, fs in os.walk(a):
                for f in fs:
                    if os.path.splitext(f)[1].lower() in TEXT:
                        yield os.path.join(root, f)
        elif os.path.isfile(a):
            yield a


def main(argv):
    targets = [a for i, a in enumerate(argv) if not a.startswith('--') and (i == 0 or argv[i - 1] not in ('--names', '--term'))]
    terms = [argv[i + 1] for i, a in enumerate(argv) if a == '--term']
    identity = [t for t in [os.environ.get('USERPROFILE'), os.environ.get('USERNAME'), os.environ.get('COMPUTERNAME')] + terms if t and len(t) >= 3]
    names = read_names(argv[argv.index('--names') + 1]) if '--names' in argv else set()
    names = {n for n in names if len(n) >= 8 and '.' in n}
    procs = set()
    if '--processes' in argv:
        out = subprocess.run(['tasklist', '/fo', 'csv', '/nh'], capture_output=True, text=True).stdout
        for line in out.splitlines():
            image = line.split('","')[0].strip('"')
            base = image[:-4] if image.lower().endswith('.exe') else image
            if len(base) >= 4 and base not in NAMED_PROCESSES:
                procs.add(base)
    totals = {'identity': 0, 'paths': 0, 'names': 0, 'processes': 0}
    scanned = 0
    for path in files(targets):
        scanned += 1
        text = open(path, encoding='utf-8', errors='replace').read()
        found = {'identity': [], 'paths': [], 'names': [], 'processes': []}
        low = text.lower()
        for t in identity:
            # a path or address anywhere; a bare name only as a whole word (so a longer word that contains it is not a match)
            if ('\\' in t or '@' in t or '.' in t) and t.lower() in low or re.search(r'\b' + re.escape(t) + r'\b', text, re.I):
                found['identity'].append(t)
        for m in re.finditer(r'\b[A-Z]:\\[^\s"|`)<>]*', text):
            p = m.group(0)
            if len(p) > 3 and not any(p.startswith(a) for a in ALLOWED_PATHS):   # a bare drive root names nothing private
                found['paths'].append(p)
        if names or procs:
            tokens = set(re.findall(r'[\w.\-+()\[\]~]+', text))
            found['names'] = sorted((tokens & names) - BENIGN)
            found['processes'] = sorted(t for t in tokens if t in procs and t not in BENIGN)
        # a load line that kept other processes' names ("busy elsewhere (cores): <name> 0.98, ...")
        if os.path.splitext(path)[1].lower() in ('.log', '.jsonl', '.json', '.md', '.txt'):   # data, not the code that formats it
            found['processes'] += re.findall(r'busy elsewhere \(cores\): (?!total)[^;\n]*', text)
        if any(found.values()):
            print(f'{path}: ' + ', '.join(f'{k} {len(v)}' for k, v in found.items() if v))
            for k, v in found.items():
                for x in v[:5]:
                    print(f'    {k}: {x}')
        for k in totals:
            totals[k] += len(found[k])
    print(f'scanned {scanned} files; harvested names checked {len(names)}; process names checked {len(procs)}; findings: {totals}')
    return 1 if any(totals.values()) else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
