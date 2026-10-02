"""C4 design final repair (C4DRR-M02): the negative self-tests of journal_ranges.py. Every case runs the checker as a separate
process and asserts its outcome and exit code (PASS 0, FAIL 1, INVALID 2); an INVALID case also asserts the reason it gives.

Usage:  python journal_ranges_selftest.py <analysis dir> <repair-smoke.jsonl> [--json <out.json>]

Inputs: the three analysed runs of the `repair:smoke` plan (harness.patch and probes.patch on 035dc90, SI_DR_KEEP_ANALYSIS=1):
  F  per-source first save of a new source (journal and the copy taken after T0)        expected PASS
  R  per-source 1% re-scan of an existing source                                         expected PASS
  G  the same re-scan cell on the Library-wide name index (the negative control)         expected FAIL
and the pre-COMMIT journal lengths their records hold. Every other case is one of them altered in a temporary directory. Nothing
of the inputs is written to the output: only case names, outcomes, exit codes and reason texts (page numbers and sizes).
"""
import hashlib
import json
import os
import re
import shutil
import sqlite3
import struct
import subprocess
import sys
import tempfile

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

HERE = os.path.dirname(os.path.abspath(__file__))
CHECKER = os.path.join(HERE, 'journal_ranges.py')
EXIT = {'PASS': 0, 'FAIL': 1, 'INVALID': 2}
RUNS = {'F': 'smoke persource obj first 100k/300k d25', 'R': 'smoke persource objrescan 100k/300k n1',
        'G': 'smoke global hash existing 100k/300k'}


def slug(label):
    return re.sub(r'[^A-Za-z0-9]+', '-', label).strip('-')


def recorded_lengths(jsonl):
    out = {}
    for line in open(jsonl, encoding='utf-8-sig'):
        r = json.loads(line)
        m = re.search(r'before COMMIT: page_count \d+, page_size \d+, main \d+ B, journal (\d+) B', r.get('Guard') or '')
        if m:
            out[r['Label']] = int(m.group(1))
    return out


def run(args):
    p = subprocess.run([sys.executable, CHECKER] + args + ['--json'], capture_output=True, text=True, encoding='utf-8')
    try:
        out = json.loads(p.stdout)
    except ValueError:
        out = {'verdict': 'INVALID' if p.returncode == 2 else '?', 'invalid_reasons': [p.stderr.strip().splitlines()[-1] if p.stderr.strip() else 'no output']}
    return out, p.returncode


def main(analysis, jsonl, json_out):
    lengths = recorded_lengths(jsonl)
    files = {}
    for k, label in RUNS.items():
        base = os.path.join(analysis, slug(label))
        files[k] = (base + '.precommit.journal', base + '.before.sqlite3', lengths[label], base + '.journal')
    tmp = tempfile.mkdtemp(prefix='jr-selftest-')
    cases = []

    def scrub(text):                                  # no path of this machine in the output
        for raw, token in ((tmp, '<tmp>'), (analysis, '<analysis>'), (os.path.expanduser('~'), '<user>')):
            for form in {raw, raw.replace('\\', '/'), raw.replace('/', '\\'), raw.replace('\\', '\\\\')}:
                text = text.replace(form, token)
        return text

    def case(name, expected, args, reason=None):
        out, code = run(args)
        reasons = [scrub(r) for r in out.get('invalid_reasons', [])]
        ok = out.get('verdict') == expected and code == EXIT[expected] and (reason is None or any(reason in r for r in reasons))
        cases.append({'case': name, 'expected': expected, 'verdict': out.get('verdict'), 'exit': code,
                      'reason_required': reason, 'reasons': reasons[:4], 'ok': ok})
        print(f"{'ok ' if ok else 'BAD'} {name}: {out.get('verdict')} (exit {code})" + (f" -- {reasons[0]}" if reasons else ''))

    def altered(src, name, fn):
        data = bytearray(open(src, 'rb').read())
        fn(data)
        path = os.path.join(tmp, name)
        open(path, 'wb').write(bytes(data))
        return path

    try:
        fj, fc, fn_, fearly = files['F']
        rj, rc, rn, rearly = files['R']
        gj, gc, gn, _ = files['G']
        rjd = open(rj, 'rb').read()
        sector = struct.unpack('>I', rjd[20:24])[0]
        ps = struct.unpack('>I', rjd[24:28])[0]
        size = ps + 8
        nrec = (len(rjd) - sector) // size
        last = sector + (nrec - 1) * size

        # baselines
        case('valid per-source journal, new source (F)', 'PASS', [fj, fc, '--new-source', '--expect-journal-bytes', str(fn_)])
        case('valid per-source journal, existing source (R)', 'PASS', [rj, rc, '--source', '1', '--expect-journal-bytes', str(rn)])
        case('valid Library-wide negative control (G)', 'FAIL', [gj, gc, '--source', '1', '--layout', 'global', '--expect-journal-bytes', str(gn)])

        # empty and zero journals
        empty = altered(rj, 'empty.journal', lambda d: d.clear())
        case('empty journal (a copy taken after COMMIT), recorded length given', 'INVALID', [empty, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'empty')
        case('empty journal, recorded length 0', 'INVALID', [empty, rc, '--source', '1', '--expect-journal-bytes', '0'], 'empty')
        case('empty journal declared separately as expected (--expect-no-records)', 'PASS', [empty, rc, '--source', '1', '--expect-journal-bytes', '0', '--expect-no-records'])
        case('records present although declared empty', 'INVALID', [rj, rc, '--source', '1', '--expect-journal-bytes', str(rn), '--expect-no-records'], 'declared to journal no page')
        zeros = altered(rj, 'zeros.journal', lambda d: d.__setitem__(slice(0, len(d)), bytes(len(d))))
        case('journal of zeros, same length', 'INVALID', [zeros, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'sector size')

        # headers
        th = altered(rj, 'trunc-header.journal', lambda d: d.__delitem__(slice(20, len(d))))
        case('truncated header (20 B)', 'INVALID', [th, rc, '--source', '1', '--expect-journal-bytes', '20'], 'truncated header')
        th2 = altered(rj, 'trunc-header-sector.journal', lambda d: d.__delitem__(slice(300, len(d))))
        case('header sector truncated (300 of 512 B)', 'INVALID', [th2, rc, '--source', '1', '--expect-journal-bytes', '300'], 'truncated')
        bad_magic = altered(rj, 'bad-magic.journal', lambda d: d.__setitem__(0, 0xFF))
        case('corrupted header magic', 'INVALID', [bad_magic, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'magic')
        ps8k = altered(rj, 'ps8k.journal', lambda d: d.__setitem__(slice(24, 28), struct.pack('>I', 8192)))
        case('journal page size 8192 against a 4096-byte copy', 'INVALID', [ps8k, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'page size')
        ps1000 = altered(rj, 'ps1000.journal', lambda d: d.__setitem__(slice(24, 28), struct.pack('>I', 1000)))
        case('journal page size not a power of two', 'INVALID', [ps1000, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'invalid page size')
        nOrig = altered(rj, 'norig.journal', lambda d: d.__setitem__(slice(16, 20), struct.pack('>I', struct.unpack('>I', d[16:20])[0] - 1)))
        case('original database size unlike the copy\'s page count', 'INVALID', [nOrig, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'original database size')

        # truncation and timing
        half = altered(rj, 'half.journal', lambda d: d.__delitem__(slice(len(d) // 2, len(d))))
        case('journal truncated to half, recorded length given', 'INVALID', [half, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'recorded')
        tail = altered(rj, 'tail.journal', lambda d: d.__delitem__(slice(len(d) - 100, len(d))))
        case('truncated final record (100 B short), length taken from the file itself', 'INVALID', [tail, rc, '--source', '1', '--expect-journal-bytes', str(rn - 100)], 'record boundary')
        early = os.path.join(tmp, 'early.journal')
        shutil.copyfile(rearly, early)
        case('journal copied before the final statements (the end-of-rows copy)', 'INVALID', [early, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'recorded')

        # records
        ck = altered(rj, 'checksum.journal', lambda d: d.__setitem__(sector + size - 1, d[sector + size - 1] ^ 0x01))
        case('one record checksum corrupted', 'INVALID', [ck, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'checksum')
        img = altered(rj, 'image.journal', lambda d: d.__setitem__(sector + 4 + 1, d[sector + 4 + 1] ^ 0x01))
        case('one image byte the checksum does not sample', 'INVALID', [img, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'original image')
        beyond = altered(rj, 'beyond.journal', lambda d: d.__setitem__(slice(last, last + 4), struct.pack('>I', 10 ** 6)))
        case('record page number beyond the original size', 'INVALID', [beyond, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'beyond the original size')
        zero = altered(rj, 'pgzero.journal', lambda d: d.__setitem__(slice(last, last + 4), struct.pack('>I', 0)))
        case('record page number 0', 'INVALID', [zero, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'beyond the original size')
        rep = altered(rj, 'repeat.journal', lambda d: d.__setitem__(slice(last, last + 4), d[last - size:last - size + 4]))
        case('one page journalled twice', 'INVALID', [rep, rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'twice')

        # the database copy
        case('wrong copy: another run\'s database (F\'s copy with R\'s journal)', 'INVALID', [rj, fc, '--source', '1', '--expect-journal-bytes', str(rn)], 'original image')
        cc = altered(rc, 'counter.sqlite3', lambda d: [d.__setitem__(slice(o, o + 4), struct.pack('>I', struct.unpack('>I', d[o:o + 4])[0] + 1)) for o in (24, 92)])
        case('wrong copy: same Library one commit later (change counter differs)', 'INVALID', [rj, cc, '--source', '1', '--expect-journal-bytes', str(rn)], 'original image')
        fsha = hashlib.sha256(open(fc, 'rb').read()).hexdigest()
        case('copy unlike the SHA-256 recorded when it was taken', 'INVALID', [rj, rc, '--source', '1', '--expect-journal-bytes', str(rn), '--expect-copy-sha256', fsha], 'SHA-256')
        rsha = hashlib.sha256(open(rc, 'rb').read()).hexdigest()
        case('copy with the SHA-256 recorded when it was taken', 'PASS', [rj, rc, '--source', '1', '--expect-journal-bytes', str(rn), '--expect-copy-sha256', rsha])
        case('wrong copy: the negative control\'s Library with a per-source journal', 'INVALID', [rj, gc, '--source', '1', '--expect-journal-bytes', str(rn)], 'not as the declared')
        appid = altered(rc, 'appid.sqlite3', lambda d: d.__setitem__(slice(68, 72), struct.pack('>I', 0)))
        case('copy without StorageInventory\'s application_id', 'INVALID', [rj, appid, '--source', '1', '--expect-journal-bytes', str(rn)], 'application_id')
        notdb = altered(rc, 'notdb.sqlite3', lambda d: d.__setitem__(slice(0, 16), b'not a database!!'))
        case('copy that is not an SQLite database', 'INVALID', [rj, notdb, '--source', '1', '--expect-journal-bytes', str(rn)], 'not an SQLite database')

        # trees, keys and the target
        journalled = set()
        for r in range((len(rjd) - sector) // size):
            journalled.add(struct.unpack('>I', rjd[sector + r * size:sector + r * size + 4])[0])
        undecodable = None
        ren = None
        con = sqlite3.connect(f'file:{rc}?mode=ro&immutable=1', uri=True)
        root = con.execute("SELECT rootpage FROM sqlite_schema WHERE name = 'sqlite_autoindex_name_1'").fetchone()[0]
        con.close()
        data = open(rc, 'rb').read()
        # an interior page of the name index that is not journalled, and one of its leaves that is not journalled either
        stack = [root]
        while stack and undecodable is None:
            n = stack.pop()
            p = data[(n - 1) * ps:n * ps]
            if p[0] == 2:
                ncell = struct.unpack('>H', p[3:5])[0]
                kids = [struct.unpack('>I', p[struct.unpack('>H', p[12 + 2 * c:14 + 2 * c])[0]:][:4])[0] for c in range(ncell)]
                stack.extend(kids)
            elif p[0] == 10 and n not in journalled:
                undecodable = n
        def garble(d):
            off = (undecodable - 1) * ps
            first = struct.unpack('>H', d[off + 8:off + 10])[0]
            d[off + first:off + first + 3] = b'\xff\xff\xff'      # the first cell's payload size and record header
        und = altered(rc, 'undecodable.sqlite3', garble)
        case('undecodable key page in the copy (a name-index leaf not journalled)', 'INVALID', [rj, und, '--source', '1', '--expect-journal-bytes', str(rn)], 'cannot be decoded')
        def rename_index(d):                                        # its sqlite_schema row, wherever that page is
            i = d.find(b'sqlite_autoindex_name_1')
            while i >= 0:
                d[i:i + 23] = b'sqlite_autoindex_namX_1'
                i = d.find(b'sqlite_autoindex_name_1', i + 1)
        renamed = altered(rc, 'renamed-index.sqlite3', rename_index)
        case('copy whose name index entry is corrupt (SQLite rejects the schema)', 'INVALID', [rj, renamed, '--source', '1', '--expect-journal-bytes', str(rn)], 'schema')
        dropped = os.path.join(tmp, 'dropped-index.sqlite3')
        shutil.copyfile(rc, dropped)
        con = sqlite3.connect(dropped)
        con.execute('DROP INDEX folder_path_child')
        con.commit()
        con.close()
        case('copy without a dictionary index (folder_path_child dropped)', 'INVALID', [rj, dropped, '--source', '1', '--expect-journal-bytes', str(rn)], 'missing')
        case('per-source evidence declared as the negative control\'s layout', 'INVALID', [rj, rc, '--source', '1', '--layout', 'global', '--expect-journal-bytes', str(rn)], 'not as the declared')
        case('negative control declared as per-source', 'INVALID', [gj, gc, '--source', '1', '--expect-journal-bytes', str(gn)], 'not as the declared')
        case('target source that does not exist in the copy', 'INVALID', [rj, rc, '--source', '99', '--expect-journal-bytes', str(rn)], 'not an existing source')
        case('existing-source journal checked as a new source (valid evidence, wrong question)', 'FAIL', [rj, rc, '--new-source', '--expect-journal-bytes', str(rn)])
        # A limit, recorded on purpose: valid evidence checked against the wrong target can pass when that target's range borders
        # the true one (here the last existing source, beside the new source's right edge). The checker cannot know the run's
        # target; §15.4 therefore takes it from the run's record, never from the analyst.
        case('LIMIT: new-source journal checked as the bordering existing source 2 (wrong target, bound by the run record)', 'PASS', [fj, fc, '--source', '2', '--expect-journal-bytes', str(fn_)])

        # process status
        p = subprocess.run([sys.executable, CHECKER, rj, rc, '--source', '1'], capture_output=True, text=True, encoding='utf-8')
        ok = p.returncode == 2 and 'INVALID' in p.stderr
        cases.append({'case': 'usage error (no recorded journal length)', 'expected': 'INVALID', 'verdict': 'INVALID' if ok else '?', 'exit': p.returncode, 'reason_required': 'usage', 'reasons': [], 'ok': ok})
        print(f"{'ok ' if ok else 'BAD'} usage error: exit {p.returncode}")
        case('journal file missing', 'INVALID', [os.path.join(tmp, 'nothing.journal'), rc, '--source', '1', '--expect-journal-bytes', str(rn)], 'cannot be read')
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    bad = [c for c in cases if not c['ok']]
    summary = {'cases': len(cases), 'ok': len(cases) - len(bad), 'bad': len(bad),
               'by_expected': {v: sum(1 for c in cases if c['expected'] == v) for v in ('PASS', 'FAIL', 'INVALID')},
               'invalid_never_pass': all(c['verdict'] != 'PASS' for c in cases if c['expected'] != 'PASS')}
    print(json.dumps(summary))
    if json_out:
        with open(json_out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(json.dumps({'summary': summary, 'cases': cases}, indent=1, ensure_ascii=False) + '\n')
    return 0 if not bad else 1


if __name__ == '__main__':
    a = sys.argv[1:]
    sys.exit(main(a[0], a[1], a[a.index('--json') + 1] if '--json' in a else None))
