"""C4 design repair: copies a run's raw outputs into the evidence directory without private details.

Usage:  python sanitize.py <raw results dir> <evidence results dir> [--worktree <path>] [--scratch <path>]

Rewrites the user-profile path as <user>, the scratch worktree as <worktree>, the scratch directory as <scratch>, and removes
the names of other processes from the load lines ("busy elsewhere (cores): ...; total X of N" keeps only the total). Copies
repair-*.jsonl, repair-*.log and ranges/*.json. Nothing else of the raw directory (Library copies, journals) is copied.
"""
import os
import re
import shutil
import sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')


def clean(text, replacements):
    for raw, token in replacements:
        if raw:
            for form in {raw, raw.replace('\\', '\\\\'), raw.replace('\\', '/')}:
                text = text.replace(form, token)
    return re.sub(r'busy elsewhere \(cores\):[^;\n]*; total', 'busy elsewhere (cores): total', text)


def main(src, dst, worktree, scratch):
    home = os.path.expanduser('~')
    replacements = sorted([(worktree, '<worktree>'), (scratch, '<scratch>'), (home, '<user>')], key=lambda p: -len(p[0] or ''))
    os.makedirs(os.path.join(dst, 'ranges'), exist_ok=True)
    copied = []
    for f in sorted(os.listdir(src)):
        if f.startswith('repair-') and (f.endswith('.jsonl') or f.endswith('.log')):
            copied.append((os.path.join(src, f), os.path.join(dst, f)))
    rg = os.path.join(src, 'ranges')
    if os.path.isdir(rg):
        for f in sorted(os.listdir(rg)):
            if f.endswith('.json'):
                copied.append((os.path.join(rg, f), os.path.join(dst, 'ranges', f)))
    for a, b in copied:
        text = open(a, encoding='utf-8-sig', errors='strict').read()
        open(b, 'w', encoding='utf-8', newline='\n').write(clean(text, replacements))
        print('copied', os.path.relpath(b, dst))


if __name__ == '__main__':
    args = sys.argv[1:]
    wt = args[args.index('--worktree') + 1] if '--worktree' in args else None
    sc = args[args.index('--scratch') + 1] if '--scratch' in args else None
    main(args[0], args[1], wt, sc)
