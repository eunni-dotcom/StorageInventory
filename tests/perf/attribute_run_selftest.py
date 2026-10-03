"""Self-tests of attribute_run.py (the fail-closed PERF-15 (a) attribution gate), runnable with plain `python`.

Usage:  python attribute_run_selftest.py [--fixtures fixtures/gate-fixtures.zip] [--json <out.json>]

Every case runs attribute_run.py as a SEPARATE PROCESS on a run record and its analysis directory, and asserts the verdict printed on stdout,
the exit code (PASS 0, FAIL 1, INVALID 2) and, for INVALID, a reason. The fixtures are small REAL captures made with the harness at smoke
scale (fixtures/make_fixtures.py): a first save into a new source (F), a re-scan into an existing source (R), and the negative control
(name index keyed by the name alone, real SQLite journaling) importing into a new and into an existing source.

Contents:
  1. the reference's cases that apply to a record-bound checker (journalled-page, header, checksum, image, copy and target cases of
     docs/evidence/c4-design-review/journal_ranges_selftest.py), with the record kept consistent with the altered artefact so that the
     CHECKER, not a length comparison, is what refuses it;
  2. the new cases of the implementation repair (final recheck C4DRRR-O02):
       (a) a record without the copy hash                                           -> INVALID
       (b) a wrong copy differing from the true one ONLY in an unjournalled page    -> INVALID through the whole-copy SHA binding
           (and the demonstration that the reference checker, run without the hash, passes it: the limit the mandatory hash closes)
       (c) a wrong copy differing in a journalled page                              -> INVALID
       (d) a wrong target in the record                                             -> FAIL or INVALID, never PASS, except the one documented limit
       (e) an altered journal length in the record                                  -> INVALID
       (f) a 0-byte journal                                                         -> INVALID
       (g) truncated and bad-checksum journals                                      -> INVALID
  3. the interface: no argument but the record and the analysis directory (no --source, no --expect-*), `--help` is a usage INVALID that
     exits 2, strict record fields, the verdict printed agrees with the exit code.
"""
import copy
import hashlib
import importlib.util
import json
import os
import shutil
import sqlite3
import struct
import subprocess
import sys
import tempfile
import zipfile

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.join(HERE, 'attribute_run.py')
EXIT = {'PASS': 0, 'FAIL': 1, 'INVALID': 2}


def load_reference():
    spec = importlib.util.spec_from_file_location('journal_ranges_ref', os.path.join(HERE, 'vendor', 'journal_ranges.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def sha256(path):
    return hashlib.sha256(open(path, 'rb').read()).hexdigest()


class Lab:
    def __init__(self, fixtures, json_out):
        self.tmp = tempfile.mkdtemp(prefix='attribute-selftest-')
        self.dir = os.path.join(self.tmp, 'analysis')
        os.makedirs(self.dir)
        self.records = {}
        with zipfile.ZipFile(fixtures) as z:
            for info in z.infolist():
                data = z.read(info)
                if info.filename.startswith('records/'):
                    self.records[os.path.basename(info.filename)[:-5]] = json.loads(data)
                else:
                    with open(os.path.join(self.dir, os.path.basename(info.filename)), 'wb') as f:
                        f.write(data)
        self.cases = []
        self.json_out = json_out
        self.n = 0

    def path(self, name):
        return os.path.join(self.dir, name)

    def scrub(self, text):
        for raw, token in ((self.tmp, '<tmp>'), (os.path.expanduser('~'), '<user>')):
            for form in {raw, raw.replace('\\', '/'), raw.replace('/', '\\'), raw.replace('\\', '\\\\')}:
                text = text.replace(form, token)
        return text

    def rec(self, label):
        return copy.deepcopy(self.records[label])

    def altered(self, source, name, fn):
        data = bytearray(open(self.path(source), 'rb').read())
        fn(data)
        with open(self.path(name), 'wb') as f:
            f.write(bytes(data))
        return name

    def bind(self, rec, journal=None, database=None, sha=True, lengths=True):
        """The record kept CONSISTENT with an altered artefact (a harness that recorded what it saw): its names, lengths and the hash."""
        a = rec['attribution']
        if journal:
            a['journalCopy'] = journal
            if lengths:
                a['journalCopyLength'] = a['journalLengthBeforeCommit'] = os.path.getsize(self.path(journal))
        if database:
            a['databaseCopy'] = database
            if lengths:
                a['databaseCopyLength'] = os.path.getsize(self.path(database))
            if sha:
                a['databaseCopySha256'] = sha256(self.path(database))
        return rec

    def run(self, args):
        p = subprocess.run([sys.executable, TOOL] + args, capture_output=True, text=True, encoding='utf-8')
        try:
            out = json.loads(p.stdout.strip().splitlines()[-1])
        except (ValueError, IndexError):
            out = {'verdict': '?', 'reasons': [p.stdout[-200:], p.stderr[-200:]]}
        return out, p.returncode, p

    def case(self, name, expected, rec, reason=None, analysis=None, raw=None, args=None):
        """expected: PASS/FAIL/INVALID; reason: a substring (or a tuple of alternatives) that one of the reasons must contain."""
        self.n += 1
        if raw is not None:
            path = self.path(f'record-{self.n}.json')
            open(path, 'w', encoding='utf-8').write(raw)
        else:
            path = self.path(f'record-{self.n}.json')
            json.dump(rec, open(path, 'w', encoding='utf-8'))
        argv = args if args is not None else [path, '--analysis-dir', analysis or self.dir]
        out, code, _ = self.run(argv)
        reasons = [self.scrub(r) for r in out.get('reasons', [])]
        alternatives = (reason,) if isinstance(reason, str) else (reason or ())
        reason_ok = not alternatives or any(any(alt in r for alt in alternatives) for r in reasons)
        ok = out.get('verdict') == expected and code == EXIT[expected] and reason_ok
        self.cases.append({'case': name, 'expected': expected, 'verdict': out.get('verdict'), 'exit': code, 'reason_required': list(alternatives) or None,
                           'reasons': reasons[:3], 'ok': ok})
        print(f"{'ok ' if ok else 'BAD'} {self.n:2d} {name}: {out.get('verdict')} (exit {code})" + (f' -- {reasons[0][:150]}' if reasons else ''))
        return out

    def limit(self, name, rec, expected_verdicts):
        """A case documented as a LIMIT or as 'FAIL or INVALID, never PASS': any verdict in expected_verdicts is accepted."""
        self.n += 1
        path = self.path(f'record-{self.n}.json')
        json.dump(rec, open(path, 'w', encoding='utf-8'))
        out, code, _ = self.run([path, '--analysis-dir', self.dir])
        ok = out.get('verdict') in expected_verdicts and code == EXIT.get(out.get('verdict'), -1)
        self.cases.append({'case': name, 'expected': '/'.join(expected_verdicts), 'verdict': out.get('verdict'), 'exit': code, 'reason_required': None,
                           'reasons': [self.scrub(r) for r in out.get('reasons', [])[:2]], 'ok': ok})
        print(f"{'ok ' if ok else 'BAD'} {self.n:2d} {name}: {out.get('verdict')} (exit {code})")
        return out

    def finish(self):
        bad = [c for c in self.cases if not c['ok']]
        never = [c for c in self.cases if c['expected'] not in ('PASS',) and '/' not in c['expected']]
        summary = {'cases': len(self.cases), 'ok': len(self.cases) - len(bad), 'bad': len(bad),
                   'by_expected': {v: sum(1 for c in self.cases if c['expected'] == v) for v in ('PASS', 'FAIL', 'INVALID')},
                   'invalid_never_pass': all(c['verdict'] != 'PASS' for c in never)}
        print(json.dumps(summary))
        if self.json_out:
            with open(self.json_out, 'w', encoding='utf-8', newline='\n') as f:
                f.write(json.dumps({'summary': summary, 'cases': self.cases}, indent=1, ensure_ascii=False) + '\n')
        shutil.rmtree(self.tmp, ignore_errors=True)
        return 0 if not bad else 1


def journal_pages(ref, lab, journal_name, db_name):
    """The page numbers a journal holds, through the reference's own parser."""
    reasons = []
    db = ref.Db(lab.path(db_name), reasons, None)
    records, _, _ = ref.parse_journal(open(lab.path(journal_name), 'rb').read(), db, reasons)
    return db, [r[0] for r in records]


def main(fixtures, json_out):
    ref = load_reference()
    lab = Lab(fixtures, json_out)
    try:
        F, R, CN, CE = (lab.rec(k) for k in ('fix-F', 'fix-R', 'fix-control-new', 'fix-control-exist'))
        fj, fd = F['attribution']['journalCopy'], F['attribution']['databaseCopy']
        rj, rd = R['attribution']['journalCopy'], R['attribution']['databaseCopy']
        cj, cd = CN['attribution']['journalCopy'], CN['attribution']['databaseCopy']

        # ---------------------------------------------------------------- 1. baselines (a verdict is a verdict, not an exit code)
        lab.case('valid per-source journal, new source (F)', 'PASS', F)
        lab.case('valid per-source journal, existing source (R)', 'PASS', R)
        lab.case('valid Library-wide negative control, new source', 'FAIL', CN)
        lab.case('valid Library-wide negative control, existing source', 'FAIL', CE)

        # ---------------------------------------------------------------- (a) the copy hash is mandatory
        no_hash = lab.rec('fix-R'); del no_hash['attribution']['databaseCopySha256']
        lab.case('(a) record without the copy hash', 'INVALID', no_hash, 'databaseCopySha256')
        for label, value in (('null', None), ('empty', ''), ('short', 'abc123'), ('upper case', R['attribution']['databaseCopySha256'].upper()), ('a number', 5)):
            r = lab.rec('fix-R'); r['attribution']['databaseCopySha256'] = value
            lab.case(f'(a) copy hash {label}', 'INVALID', r, 'databaseCopySha256')
        wrong_hash = lab.rec('fix-R'); wrong_hash['attribution']['databaseCopySha256'] = sha256(lab.path(fd))
        lab.case('copy unlike the SHA-256 recorded when it was taken (another run\'s hash)', 'INVALID', wrong_hash, 'SHA-256')

        # ---------------------------------------------------------------- (b) a wrong copy differing only in an unjournalled page
        db, pages = journal_pages(ref, lab, rj, rd)
        journalled = set(pages)
        victim = None
        for pg in range(db.page_count, 1, -1):
            page = db.page(pg)
            if pg not in journalled and page[0] == 0x0D:
                victim = pg
                break
        assert victim, 'no unjournalled table leaf in the fixture'
        unj = lab.altered(rd, 'unjournalled.sqlite3', lambda d: d.__setitem__(victim * 4096 - 1, d[victim * 4096 - 1] ^ 0x01))
        r = lab.bind(lab.rec('fix-R'), database=unj, sha=False)         # the record keeps the TRUE copy's hash
        lab.case('(b) wrong copy differing only in an unjournalled page, record holds the true copy\'s SHA-256', 'INVALID', r, 'SHA-256')
        # the limit the mandatory hash closes: the reference checker, run WITHOUT the hash, passes that copy
        reference = ref.check(lab.path(rj), lab.path(unj), 1, False, R['attribution']['journalLengthBeforeCommit'], None, 'persource', False)
        ok = reference['verdict'] == 'PASS'
        lab.cases.append({'case': 'REFERENCE LIMIT (not through attribute_run): the reference checker without a hash passes that wrong copy', 'expected': 'PASS',
                          'verdict': reference['verdict'], 'exit': EXIT[reference['verdict']], 'reason_required': None, 'reasons': [], 'ok': ok})
        print(f"{'ok ' if ok else 'BAD'} -- REFERENCE LIMIT: the reference checker without a hash on that wrong copy: {reference['verdict']}")
        # a harness that recorded the altered copy's own hash binds that copy: the binding is the record's (it cannot detect a lying harness)
        forged = lab.bind(lab.rec('fix-R'), database=unj)
        lab.case('LIMIT: a record forged to carry the wrong copy\'s own SHA-256 binds that copy (the record is the binding)', 'PASS', forged)

        # ---------------------------------------------------------------- (c) a wrong copy differing in a journalled page
        jp = next(p for p in pages if p != 1 and db.page(p)[0] in (0x0D, 0x0A))
        jr_copy = lab.altered(rd, 'journalled.sqlite3', lambda d: d.__setitem__(jp * 4096 - 1, d[jp * 4096 - 1] ^ 0x01))
        r = lab.bind(lab.rec('fix-R'), database=jr_copy, sha=False)
        lab.case('(c) wrong copy differing in a journalled page, record holds the true copy\'s SHA-256', 'INVALID', r, 'SHA-256')
        r = lab.bind(lab.rec('fix-R'), database=jr_copy)
        lab.case('(c) wrong copy differing in a journalled page, record holds its own SHA-256', 'INVALID', r, 'original image')
        lab.case('wrong copy: another run\'s database (F\'s copy with R\'s journal), record bound to it', 'INVALID', lab.bind(lab.rec('fix-R'), database=fd), 'original image')
        cc = lab.altered(rd, 'counter.sqlite3', lambda d: [d.__setitem__(slice(o, o + 4), struct.pack('>I', struct.unpack('>I', d[o:o + 4])[0] + 1)) for o in (24, 92)])
        lab.case('wrong copy: same Library one commit later (change counter differs)', 'INVALID', lab.bind(lab.rec('fix-R'), database=cc), 'original image')

        # ---------------------------------------------------------------- (d) a wrong target in the record
        def target(rec, source, source_id=None):
            rec['attribution']['target'] = {'source': source, 'sourceId': source_id}
            return rec
        out = lab.case('(d) F (a new source) recorded as existing source 1', 'FAIL', target(lab.rec('fix-F'), 'existing', 1))
        lab.case('(d) R (existing source 1) recorded as existing source 2', 'FAIL', target(lab.rec('fix-R'), 'existing', 2))
        lab.case('(d) R (existing source 1) recorded as a NEW source', 'FAIL', target(lab.rec('fix-R'), 'new'))
        lab.case('(d) a target that does not exist in the copy (source 99)', 'INVALID', target(lab.rec('fix-R'), 'existing', 99), 'not an existing source')
        lab.case('(d) control (global layout) recorded against a source nothing references', 'INVALID', target(lab.rec('fix-control-exist'), 'existing', 99), 'references no name')
        lab.limit('(d) LIMIT, documented: F recorded as the BORDERING existing source 2 (the new source\'s left neighbour) is valid evidence '
                  'for that question and passes: the checker cannot know the run\'s target, so the harness writes it into the record and the record is the binding',
                  target(lab.rec('fix-F'), 'existing', 2), ('PASS', 'FAIL'))

        # ---------------------------------------------------------------- (e) altered journal length in the record
        r = lab.rec('fix-R'); r['attribution']['journalLengthBeforeCommit'] += 1
        lab.case('(e) recorded journal length before COMMIT one byte longer than the copy', 'INVALID', r, 'immediately before COMMIT')
        r = lab.rec('fix-R'); r['attribution']['journalCopyLength'] += 4096
        lab.case('(e) recorded copy length unlike the file', 'INVALID', r, 'on disk')
        r = lab.rec('fix-R'); r['attribution']['journalCopyLength'] = r['attribution']['journalLengthBeforeCommit'] = r['attribution']['journalLengthBeforeCommit'] - 100
        lab.case('(e) both recorded lengths shortened while the file is whole', 'INVALID', r, 'on disk')
        r = lab.rec('fix-R'); r['attribution']['journalLengthBeforeCommit'] = 0
        lab.case('(e) recorded journal length 0', 'INVALID', r, 'immediately before COMMIT')
        r = lab.rec('fix-R'); r['attribution']['databaseCopyLength'] += 1
        lab.case('(e) recorded database copy length unlike the file', 'INVALID', r, 'database copy')

        # ---------------------------------------------------------------- (f) 0-byte journal
        empty = lab.altered(rj, 'empty.journal', lambda d: d.clear())
        lab.case('(f) empty journal (a copy taken after COMMIT), record bound to it', 'INVALID', lab.bind(lab.rec('fix-R'), journal=empty), 'empty')
        r = lab.rec('fix-R'); r['attribution']['journalCopy'] = empty; r['attribution']['journalCopyLength'] = 0
        lab.case('(f) empty journal, recorded length before COMMIT unchanged', 'INVALID', r, 'immediately before COMMIT')
        zero_len = lab.altered(rj, 'zeros.journal', lambda d: d.__setitem__(slice(0, len(d)), bytes(len(d))))
        lab.case('(f) a journal of zeros, same length', 'INVALID', lab.bind(lab.rec('fix-R'), journal=zero_len), 'sector size')

        # ---------------------------------------------------------------- (g) truncated and bad-checksum journals, headers, records
        rjd = open(lab.path(rj), 'rb').read()
        sector = struct.unpack('>I', rjd[20:24])[0]
        ps = struct.unpack('>I', rjd[24:28])[0]
        size = ps + 8
        nrec = (len(rjd) - sector) // size
        last = sector + (nrec - 1) * size
        bound = lambda name: lab.bind(lab.rec('fix-R'), journal=name)
        lab.case('(g) truncated header (20 B)', 'INVALID', bound(lab.altered(rj, 'th.journal', lambda d: d.__delitem__(slice(20, len(d))))), 'truncated header')
        lab.case('(g) header sector truncated (300 of 512 B)', 'INVALID', bound(lab.altered(rj, 'th2.journal', lambda d: d.__delitem__(slice(300, len(d))))), 'truncated')
        lab.case('(g) corrupted header magic', 'INVALID', bound(lab.altered(rj, 'magic.journal', lambda d: d.__setitem__(0, 0xFF))), 'magic')
        lab.case('(g) journal truncated to half', 'INVALID', bound(lab.altered(rj, 'half.journal', lambda d: d.__delitem__(slice(len(d) // 2, len(d))))), ('ends inside', 'record boundary'))
        lab.case('(g) final record 100 B short', 'INVALID', bound(lab.altered(rj, 'tail.journal', lambda d: d.__delitem__(slice(len(d) - 100, len(d))))), ('record boundary', 'ends inside'))
        lab.case('(g) one byte appended after the last record', 'INVALID', bound(lab.altered(rj, 'plus1.journal', lambda d: d.extend(b'\x00'))), 'record boundary')
        lab.case('(g) one record checksum corrupted', 'INVALID', bound(lab.altered(rj, 'ck.journal', lambda d: d.__setitem__(sector + size - 1, d[sector + size - 1] ^ 0x01))), 'checksum')
        lab.case('(g) one image byte the checksum does not sample', 'INVALID', bound(lab.altered(rj, 'img.journal', lambda d: d.__setitem__(sector + 4 + 1, d[sector + 4 + 1] ^ 0x01))), 'original image')
        lab.case('(g) journal page size 8192 against a 4096-byte copy', 'INVALID', bound(lab.altered(rj, 'ps8k.journal', lambda d: d.__setitem__(slice(24, 28), struct.pack('>I', 8192)))), 'page size')
        lab.case('(g) journal page size not a power of two', 'INVALID', bound(lab.altered(rj, 'ps1000.journal', lambda d: d.__setitem__(slice(24, 28), struct.pack('>I', 1000)))), 'invalid page size')
        lab.case('(g) original database size unlike the copy\'s page count', 'INVALID',
                 bound(lab.altered(rj, 'norig.journal', lambda d: d.__setitem__(slice(16, 20), struct.pack('>I', struct.unpack('>I', d[16:20])[0] - 1)))), 'original database size')
        lab.case('(g) record page number beyond the original size', 'INVALID', bound(lab.altered(rj, 'beyond.journal', lambda d: d.__setitem__(slice(last, last + 4), struct.pack('>I', 10 ** 6)))), 'beyond the original size')
        lab.case('(g) record page number 0', 'INVALID', bound(lab.altered(rj, 'pgzero.journal', lambda d: d.__setitem__(slice(last, last + 4), struct.pack('>I', 0)))), 'beyond the original size')
        lab.case('(g) one page journalled twice', 'INVALID', bound(lab.altered(rj, 'repeat.journal', lambda d: d.__setitem__(slice(last, last + 4), d[last - size:last - size + 4]))), 'twice')
        # page 1 removed from F's journal, lengths adjusted (the first record of the first segment is page 1 or not: find it)
        p1 = [i for i in range(nrec) if struct.unpack('>I', rjd[sector + i * size:sector + i * size + 4])[0] == 1]
        if p1 and nrec > 1 and len(p1) == 1:
            def drop_page_one(d, i=p1[0]):
                # the record sits inside a synced segment whose count would change: zero the page number instead (a record for page 0)
                d[sector + i * size:sector + i * size + 4] = struct.pack('>I', 0)
            lab.case('(g) page 1 not journalled (its record renumbered)', 'INVALID', bound(lab.altered(rj, 'nopage1.journal', drop_page_one)), ('page 1', 'beyond the original size'))

        # ---------------------------------------------------------------- the database copy and the layout
        lab.case('wrong copy: the negative control\'s Library with a per-source record', 'INVALID', lab.bind(lab.rec('fix-R'), database=cd), 'not as the declared')
        r = lab.rec('fix-R'); r['attribution']['layout'] = 'global'
        lab.case('a production run declaring the Library-wide layout', 'INVALID', r, 'only a negative control')
        r = lab.rec('fix-control-exist'); r['attribution']['layout'] = 'persource'
        lab.case('a negative control declaring the per-source layout', 'INVALID', r, 'negative control declares')
        appid = lab.altered(rd, 'appid.sqlite3', lambda d: d.__setitem__(slice(68, 72), struct.pack('>I', 0)))
        lab.case('copy without StorageInventory\'s application_id', 'INVALID', lab.bind(lab.rec('fix-R'), database=appid), 'application_id')
        lab.case('copy that is not an SQLite database', 'INVALID', lab.bind(lab.rec('fix-R'), database=lab.altered(rd, 'notdb.sqlite3', lambda d: d.__setitem__(slice(0, 16), b'not a database!!'))), 'not an SQLite database')
        con = sqlite3.connect(f'file:{lab.path(rd)}?mode=ro&immutable=1', uri=True)
        name_root = con.execute("SELECT rootpage FROM sqlite_schema WHERE name = 'sqlite_autoindex_name_1'").fetchone()[0]
        con.close()
        data = open(lab.path(rd), 'rb').read()
        undecodable = None
        stack = [name_root]
        while stack and undecodable is None:
            n = stack.pop()
            page = data[(n - 1) * 4096:n * 4096]
            if page[0] == 2:
                cells = struct.unpack('>H', page[3:5])[0]
                stack.extend(struct.unpack('>I', page[struct.unpack('>H', page[12 + 2 * c:14 + 2 * c])[0]:][:4])[0] for c in range(cells))
            elif page[0] == 10 and n not in journalled:
                undecodable = n
        if undecodable:
            def garble(d):
                off = (undecodable - 1) * 4096
                first = struct.unpack('>H', d[off + 8:off + 10])[0]
                d[off + first:off + first + 3] = b'\xff\xff\xff'
            lab.case('undecodable key page in the copy (a name-index leaf not journalled)', 'INVALID', lab.bind(lab.rec('fix-R'), database=lab.altered(rd, 'undecodable.sqlite3', garble)), 'cannot be decoded')

        def rename_index(d):
            i = d.find(b'sqlite_autoindex_name_1')
            while i >= 0:
                d[i:i + 23] = b'sqlite_autoindex_namX_1'
                i = d.find(b'sqlite_autoindex_name_1', i + 1)
        lab.case('copy whose name-index entry is corrupt (SQLite rejects the schema)', 'INVALID', lab.bind(lab.rec('fix-R'), database=lab.altered(rd, 'renamed.sqlite3', rename_index)), 'schema')
        shutil.copyfile(lab.path(rd), lab.path('dropped.sqlite3'))
        c = sqlite3.connect(lab.path('dropped.sqlite3')); c.execute('DROP INDEX folder_path_child'); c.commit(); c.close()
        lab.case('copy without a dictionary index (folder_path_child dropped)', 'INVALID', lab.bind(lab.rec('fix-R'), database='dropped.sqlite3'), 'missing')

        # ---------------------------------------------------------------- the interface and the record's fields
        lab.case('no arguments', 'INVALID', None, 'usage', args=[])
        out = lab.case('--help is a usage INVALID, not an exit 0', 'INVALID', None, 'usage', args=['--help'])
        lab.case('a free-form --source is not an option', 'INVALID', None, 'unknown option', args=[lab.path('record-1.json'), '--analysis-dir', lab.dir, '--source', '1'])
        lab.case('a free-form --expect-copy-sha256 is not an option', 'INVALID', None, 'unknown option', args=[lab.path('record-1.json'), '--analysis-dir', lab.dir, '--expect-copy-sha256', 'a' * 64])
        lab.case('a free-form --layout is not an option', 'INVALID', None, 'unknown option', args=[lab.path('record-1.json'), '--analysis-dir', lab.dir, '--layout', 'global'])
        lab.case('no analysis directory', 'INVALID', None, 'required', args=[lab.path('record-1.json')])
        lab.case('record file missing', 'INVALID', None, 'cannot read', args=[lab.path('nothing.json'), '--analysis-dir', lab.dir])
        lab.case('analysis directory missing', 'INVALID', F, 'not a directory', analysis=lab.path('nodir'))
        lab.case('a record that is not JSON', 'INVALID', None, 'record', raw='this is not json')
        lab.case('two records in one file, none named', 'INVALID', None, 'exactly one', raw=json.dumps(F) + '\n' + json.dumps(R) + '\n')
        two = json.dumps(F) + '\n' + json.dumps(R) + '\n'
        path = lab.path('two.jsonl'); open(path, 'w', encoding='utf-8').write(two)
        out, code, _ = lab.run([path, '--analysis-dir', lab.dir, '--label', 'fix-R'])
        ok = out.get('verdict') == 'PASS' and code == 0
        lab.cases.append({'case': 'one record chosen by its label from a JSON-lines file', 'expected': 'PASS', 'verdict': out.get('verdict'), 'exit': code, 'reason_required': None, 'reasons': [], 'ok': ok})
        print(f"{'ok ' if ok else 'BAD'} one record chosen by label: {out.get('verdict')}")
        r = lab.rec('fix-R'); r['attribution']['extra'] = 1
        lab.case('an unknown attribution field', 'INVALID', r, 'unknown field')
        r = lab.rec('fix-R'); r['attribution']['target']['extra'] = 1
        lab.case('an unknown target field', 'INVALID', r, 'unknown field')
        r = lab.rec('fix-R'); r['attribution']['journalCopy'] = '..\\x.journal'
        lab.case('a journal name that is a path', 'INVALID', r, 'bare file name')
        r = lab.rec('fix-R'); r['attribution']['databaseCopy'] = 'sub/x.sqlite3'
        lab.case('a database name that is a path', 'INVALID', r, 'bare file name')
        r = lab.rec('fix-R'); r['attribution']['target'] = {'source': 'existing'}
        lab.case('an existing target without its id', 'INVALID', r, 'source id')
        r = lab.rec('fix-R'); r['attribution']['target'] = {'source': 'existing', 'sourceId': True}
        lab.case('a boolean as source id', 'INVALID', r, 'source id')
        r = lab.rec('fix-F'); r['attribution']['target'] = {'source': 'new', 'sourceId': 4}
        lab.case('a new source with an id', 'INVALID', r, 'no id')
        r = lab.rec('fix-R'); r['kind'] = 'timed'
        lab.case('a timed run\'s record is not an attribution', 'INVALID', r, 'neither')
        r = lab.rec('fix-R'); del r['attribution']
        lab.case('a record without an attribution section', 'INVALID', r, 'no attribution section')
        r = lab.rec('fix-R'); r['attribution']['journalCopy'] = 'absent.journal'
        lab.case('an artefact the record names but the directory lacks', 'INVALID', r, 'not in the analysis directory')

        # the checker is the reference, vendored unmodified
        docs = os.path.join(HERE, '..', '..', 'docs', 'evidence', 'c4-design-review', 'journal_ranges.py')
        if os.path.exists(docs):
            same = sha256(docs) == sha256(os.path.join(HERE, 'vendor', 'journal_ranges.py'))
            lab.cases.append({'case': 'the vendored checker is byte-identical to docs/evidence/c4-design-review/journal_ranges.py', 'expected': 'PASS', 'verdict': 'PASS' if same else 'FAIL',
                              'exit': 0 if same else 1, 'reason_required': None, 'reasons': [], 'ok': same})
            print(f"{'ok ' if same else 'BAD'} the vendored checker equals the reference")
        return lab.finish()
    except BaseException:
        shutil.rmtree(lab.tmp, ignore_errors=True)
        raise


if __name__ == '__main__':
    a = sys.argv[1:]
    fx = a[a.index('--fixtures') + 1] if '--fixtures' in a else os.path.join(HERE, 'fixtures', 'gate-fixtures.zip')
    sys.exit(main(fx, a[a.index('--json') + 1] if '--json' in a else None))
