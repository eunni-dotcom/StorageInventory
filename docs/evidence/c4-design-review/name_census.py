"""C4 design repair: aggregate file-name statistics of one directory tree (used to calibrate the §15.4 name families).

Names are held in memory only. Nothing but counts, ratios and length statistics is printed: never a name, a path, a folder
name or an extension list. Reparse points are never followed (as v1's scanner, NeverFollow).

Usage:  python name_census.py <root> [--label <text>] [--json <out.json>]

Output (stdout, UTF-8): one JSON object with
  files, folders, unreadable              counts
  distinct_file_names, distinct_ratio     distinct file names / files
  distinct_folder_names, distinct_file_and_folder_names
  prefix_curve                            distinct file names after the first n files of a deterministic walk (sorted entries)
  subtrees                                for every folder at depth 1 to 3 holding >= 25,000 files in its subtree: its file count
                                          and distinct file names (anonymous: no path, sorted by size)
  length                                  file-name length in UTF-16 code units: mean, median, p5, p25, p75, p95, p99, max, and a
                                          histogram (1..255) as counts
  frequency                               how often distinct names repeat: names seen once, 2, 3-9, 10-99, >= 100 times, and the
                                          share of files carried by the most frequent 0.1%, 1% and 10% of distinct names
  leading                                 share of names whose first code unit is an ASCII upper-case letter, lower-case letter,
                                          digit, or anything else
  added                                   files whose creation time lies within 7, 30 and 90 days of the census, and the distinct
                                          names first seen (earliest creation) within those windows: a proxy for re-scan churn
"""
import json
import os
import stat as stat_mod
import sys
import time

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

REPARSE = 0x400
MARKS = [100_000, 250_000, 500_000, 1_000_000, 1_500_000, 2_000_000, 2_500_000, 3_000_000, 4_000_000, 6_000_000, 8_000_000]
SUBTREE_MIN = 25_000
DAY = 86_400.0


def utf16_len(s):
    # code units: characters beyond the BMP take two
    return len(s) + sum(1 for ch in s if ord(ch) > 0xFFFF)


def census(root):
    now = time.time()
    t0 = time.monotonic()
    files = folders = unreadable = 0
    names = {}               # name -> occurrences
    first_birth = {}         # name -> earliest creation time
    folder_names = set()
    hist = [0] * 256         # code-unit length, 255 = 255 or more
    lead = {'upper': 0, 'lower': 0, 'digit': 0, 'other': 0}
    added = {7: 0, 30: 0, 90: 0}
    curve = {}
    sub_sets = {}            # subtree id -> set of names (depth 1..3)
    sub_files = {}
    # depth-first walk over sorted entries, so the prefix curve is deterministic
    stack = [(root, ())]
    next_id = 0
    visited = 0
    while stack:
        path, chain = stack.pop()
        try:
            with os.scandir(path) as it:
                entries = sorted(it, key=lambda e: e.name)
        except OSError:
            unreadable += 1
            continue
        subdirs = []
        for e in entries:
            try:
                st = e.stat(follow_symlinks=False)
            except OSError:
                unreadable += 1
                continue
            if getattr(st, 'st_file_attributes', 0) & REPARSE or stat_mod.S_ISLNK(st.st_mode):
                continue
            if stat_mod.S_ISDIR(st.st_mode):
                folders += 1
                folder_names.add(e.name)
                subdirs.append(e)
                continue
            n = e.name
            files += 1
            names[n] = names.get(n, 0) + 1
            birth = getattr(st, 'st_birthtime', None) or st.st_ctime
            prev = first_birth.get(n)
            if prev is None or birth < prev:
                first_birth[n] = birth
            age = now - birth
            for w in added:
                if age <= w * DAY:
                    added[w] += 1
            ln = utf16_len(n)
            hist[min(ln, 255)] += 1
            c = n[0]
            lead['upper' if 'A' <= c <= 'Z' else 'lower' if 'a' <= c <= 'z' else 'digit' if '0' <= c <= '9' else 'other'] += 1
            for sid in chain:
                sub_sets[sid].add(n)
                sub_files[sid] += 1
            if files in MARKS:
                curve[files] = len(names)
        for e in reversed(subdirs):
            child_chain = chain
            if len(chain) < 3:
                sid = next_id
                next_id += 1
                sub_sets[sid] = set()
                sub_files[sid] = 0
                child_chain = chain + (sid,)
            stack.append((e.path, child_chain))
        # drop finished small subtrees early to bound memory: a subtree is finished when no stack entry carries it
        visited += 1
        if visited % 2_000 == 0:
            live = set()
            for _, ch in stack:
                live.update(ch)
            for sid in [s for s in sub_sets if s not in live and sub_files[s] < SUBTREE_MIN]:
                del sub_sets[sid]
                del sub_files[sid]

    distinct = len(names)
    counts = sorted(names.values(), reverse=True)
    def top_share(fraction):
        k = max(1, int(distinct * fraction))
        return round(sum(counts[:k]) / files, 4) if files else 0.0
    def pct(p):
        target = p * files
        acc = 0
        for ln, c in enumerate(hist):
            acc += c
            if acc >= target:
                return ln
        return 255
    mean_len = sum(ln * c for ln, c in enumerate(hist)) / files if files else 0.0
    freq = {'once': 0, 'twice': 0, '3-9': 0, '10-99': 0, '100+': 0}
    for c in counts:
        freq['once' if c == 1 else 'twice' if c == 2 else '3-9' if c < 10 else '10-99' if c < 100 else '100+'] += 1
    new_names = {w: sum(1 for b in first_birth.values() if now - b <= w * DAY) for w in added}
    subtrees = sorted(({'files': sub_files[s], 'distinct': len(sub_sets[s])} for s in sub_sets if sub_files[s] >= SUBTREE_MIN),
                      key=lambda x: x['files'])
    for s in subtrees:
        s['ratio'] = round(s['distinct'] / s['files'], 4)
    return {
        'files': files, 'folders': folders, 'unreadable': unreadable,
        'distinct_file_names': distinct, 'distinct_ratio': round(distinct / files, 4) if files else 0.0,
        'distinct_folder_names': len(folder_names),
        'distinct_file_and_folder_names': len(folder_names | names.keys()),
        'prefix_curve': {str(k): {'distinct': v, 'ratio': round(v / k, 4)} for k, v in sorted(curve.items())},
        'subtrees': subtrees,
        'length': {'mean': round(mean_len, 2), 'p5': pct(0.05), 'p25': pct(0.25), 'median': pct(0.50), 'p75': pct(0.75),
                   'p95': pct(0.95), 'p99': pct(0.99), 'max_bucket': max((i for i, c in enumerate(hist) if c), default=0),
                   'histogram': hist},
        'frequency': {'distinct_names_by_occurrences': freq, 'files_in_top_0.1pct_names': top_share(0.001),
                      'files_in_top_1pct_names': top_share(0.01), 'files_in_top_10pct_names': top_share(0.10)},
        'leading': {k: round(v / files, 4) if files else 0.0 for k, v in lead.items()},
        'added': {f'{w}d': {'files': added[w], 'files_ratio': round(added[w] / files, 5) if files else 0.0,
                            'new_distinct_names': new_names[w], 'new_names_ratio': round(new_names[w] / files, 5) if files else 0.0}
                  for w in added},
        'seconds': round(time.monotonic() - t0, 1),
    }


if __name__ == '__main__':
    args = sys.argv[1:]
    root = args[0]
    label = args[args.index('--label') + 1] if '--label' in args else 'tree'
    result = {'label': label}
    result.update(census(root))
    text = json.dumps(result, indent=1)
    if '--json' in args:
        with open(args[args.index('--json') + 1], 'w', encoding='utf-8') as f:
            f.write(text + '\n')
    print(text)
