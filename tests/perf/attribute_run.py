"""TEST-P1's PERF-15 (a) attribution gate: ONE run record, fail-closed (C4 implementation repair; §15.4 "Binding" and "Validity").

Usage:  python attribute_run.py <run record file> --analysis-dir <dir> [--label <run label>] [--json]

  <run record file>   a file holding exactly ONE run record: a JSON object (a whole file, or one line); or a JSON-lines file with
                      --label naming the one record to check. The record is the immutable raw result the harness wrote
                      (tests/StorageInventory.Library.Tests/PerfGate/RunRecord.cs, kind "attribution", or "control" for the negative control).
  --analysis-dir      the directory holding the artefacts the record names: the journal copy taken immediately before COMMIT and the
                      database copy taken after T0 commits and before BEGIN. Only bare file names inside it are accepted.

There is NO other input. The target source, the journal's length before COMMIT, the copies' lengths and the database copy's SHA-256 are
read ONLY from the record, never from arguments (§15.4: "the checker takes them from the record, never from the analyst"), so the tool has
no --source, --new-source, --expect-* or --layout option and rejects any unknown argument as a usage error.

Output: a machine-readable verdict on stdout, one JSON object:
    {"verdict": "PASS" | "FAIL" | "INVALID", "reasons": [...], "record": {...}, "checker": {...}, "details": {...}}
and the exit code PASS 0, FAIL 1, INVALID 2. CALLERS DECIDE ON THE VERDICT, not only on the exit code: `--help` and every usage error
print a verdict INVALID and exit 2 (a help text must never look like a pass), so the one outcome of this tool that exits 0 is PASS.

What it adds to the reference checker (docs/evidence/c4-design-review/journal_ranges.py, vendored unmodified under vendor/ and pinned by
SHA-256; it refuses to run if the vendored file changed):
  * the record's fields are required and strictly typed (a missing, malformed or unknown field, or a missing hash, is INVALID);
  * the database copy's SHA-256 is MANDATORY: it is recomputed here from the file and compared with the record's, and handed to the
    checker as its --expect-copy-sha256 (the whole-copy binding: a copy that differs from the true one only in a page the journal does not
    hold passes the reference checker WITHOUT the hash; C4DRRR-O02);
  * the journal copy's and the database copy's lengths must equal the record's, and the journal copy must equal the length read through
    a live handle immediately before COMMIT (the checker compares it again);
  * the layout is declared by the run: a production attribution is per-source; only a negative control declares the Library-wide layout.
"""
import hashlib
import importlib.util
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
VENDORED = os.path.join(HERE, 'vendor', 'journal_ranges.py')
# SHA-256 of docs/evidence/c4-design-review/journal_ranges.py (docs/evidence/c4-design-review/MANIFEST.md), the reference that §15.4 names
REFERENCE_SHA256 = 'df179422b1c4ba34edf86ee7228d41103fea1c94b9ea36fb4c67141dc847d0ef'

PASS, FAIL, INVALID = 'PASS', 'FAIL', 'INVALID'
EXIT = {PASS: 0, FAIL: 1, INVALID: 2}
ATTRIBUTION_FIELDS = {'target', 'layout', 'journalLengthBeforeCommit', 'journalCopy', 'journalCopyLength', 'databaseCopy',
                      'databaseCopyLength', 'databaseCopySha256'}
TARGET_FIELDS = {'source', 'sourceId'}
BARE_NAME = re.compile(r'^[A-Za-z0-9][A-Za-z0-9._-]{0,200}$')
SHA256 = re.compile(r'^[0-9a-f]{64}$')

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')


class Refused(Exception):
    """The evidence cannot be used: INVALID."""


def load_checker():
    """The reference checker, imported from the vendored copy after its hash is verified."""
    with open(VENDORED, 'rb') as f:
        data = f.read()
    digest = hashlib.sha256(data).hexdigest()
    if digest != REFERENCE_SHA256:
        raise Refused(f'checker: the vendored journal_ranges.py has SHA-256 {digest}, not the reference\'s {REFERENCE_SHA256}')
    spec = importlib.util.spec_from_file_location('journal_ranges_reference', VENDORED)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module, digest


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def read_record(path, label):
    """The one record of the file. Raises Refused unless there is exactly one."""
    try:
        with open(path, 'r', encoding='utf-8-sig') as f:
            text = f.read()
    except OSError as e:
        raise Refused(f'record: cannot read {os.path.basename(path)}: {e.strerror or e}')
    records = []
    stripped = text.strip()
    try:
        whole = json.loads(stripped)
        records = [whole]
    except ValueError:
        for number, line in enumerate(text.splitlines(), 1):
            if not line.strip():
                continue
            try:
                records.append(json.loads(line))
            except ValueError as e:
                raise Refused(f'record: line {number} is not JSON ({e})')
    if label is not None:
        records = [r for r in records if isinstance(r, dict) and r.get('label') == label]
        if len(records) != 1:
            raise Refused(f'record: {len(records)} records carry the label "{label}" (exactly one is required)')
    if len(records) != 1:
        raise Refused(f'record: the file holds {len(records)} records; exactly one is required (name it with --label)')
    if not isinstance(records[0], dict):
        raise Refused('record: not a JSON object')
    return records[0]


def is_int(v):
    return isinstance(v, int) and not isinstance(v, bool)


def validate_record(rec):
    """Strict validation of the record's attribution fields. Returns the cleaned values or raises Refused with every problem."""
    problems = []
    kind = rec.get('kind')
    if kind not in ('attribution', 'control'):
        problems.append(f'record: kind {kind!r} is neither "attribution" nor "control"')
    if rec.get('schema') != 1:
        problems.append(f'record: unknown schema {rec.get("schema")!r}')
    if not isinstance(rec.get('label'), str) or not rec.get('label'):
        problems.append('record: no label')
    a = rec.get('attribution')
    if not isinstance(a, dict):
        raise Refused('; '.join(problems + ['record: no attribution section (the target, the journal length and the copy hash are mandatory fields)']))
    missing = sorted(ATTRIBUTION_FIELDS - a.keys())
    unknown = sorted(a.keys() - ATTRIBUTION_FIELDS)
    if missing:
        problems.append('attribution: missing field(s) ' + ', '.join(missing))
    if unknown:
        problems.append('attribution: unknown field(s) ' + ', '.join(unknown))
    t = a.get('target')
    if not isinstance(t, dict):
        problems.append('attribution.target: missing or not an object')
    else:
        if t.keys() - TARGET_FIELDS:
            problems.append('attribution.target: unknown field(s) ' + ', '.join(sorted(t.keys() - TARGET_FIELDS)))
        source = t.get('source')
        if source not in ('existing', 'new'):
            problems.append(f'attribution.target.source: {source!r} is neither "existing" nor "new"')
        elif source == 'existing' and not (is_int(t.get('sourceId')) and t['sourceId'] >= 1):
            problems.append('attribution.target.sourceId: an existing target needs a source id of at least 1')
        elif source == 'new' and t.get('sourceId') is not None:
            problems.append('attribution.target.sourceId: a new source has no id')
    layout = a.get('layout')
    if layout not in ('persource', 'global'):
        problems.append(f'attribution.layout: {layout!r} is neither "persource" nor "global"')
    elif kind == 'attribution' and layout != 'persource':
        problems.append('attribution.layout: a run of the production schema is per-source; only a negative control declares the Library-wide layout')
    elif kind == 'control' and layout != 'global':
        problems.append('attribution.layout: the negative control declares the Library-wide layout')
    for field in ('journalLengthBeforeCommit', 'journalCopyLength', 'databaseCopyLength'):
        if field in a and not (is_int(a[field]) and a[field] >= 0):
            problems.append(f'attribution.{field}: not a non-negative integer')
    for field in ('journalCopy', 'databaseCopy'):
        if field in a and not (isinstance(a[field], str) and BARE_NAME.match(a[field]) and '..' not in a[field]):
            problems.append(f'attribution.{field}: not a bare file name')
    sha = a.get('databaseCopySha256')
    if 'databaseCopySha256' in a and not (isinstance(sha, str) and SHA256.match(sha)):
        problems.append('attribution.databaseCopySha256: not a lower-case hexadecimal SHA-256')
    if problems:
        raise Refused('; '.join(problems))
    return kind, a


def attribute(record_path, analysis_dir, label=None):
    """The verdict of one record: a dict {verdict, reasons, record, checker, details}."""
    out = {'verdict': INVALID, 'reasons': [], 'record': {}, 'checker': {}, 'details': {}}
    try:
        checker, checker_sha = load_checker()
        out['checker'] = {'file': 'journal_ranges.py (vendored, reference)', 'sha256': checker_sha}
        rec = read_record(record_path, label)
        out['record'] = {'label': rec.get('label'), 'kind': rec.get('kind')}
        kind, a = validate_record(rec)
        if not os.path.isdir(analysis_dir):
            raise Refused('analysis directory: not a directory')
        journal = os.path.join(analysis_dir, a['journalCopy'])
        database = os.path.join(analysis_dir, a['databaseCopy'])
        for what, path in (('journal copy', journal), ('database copy', database)):
            if not os.path.isfile(path):
                raise Refused(f'{what}: the record names {os.path.basename(path)}, which is not in the analysis directory')
        reasons = []
        if os.path.getsize(journal) != a['journalCopyLength']:
            reasons.append(f'journal copy: {os.path.getsize(journal)} B on disk, the record says {a["journalCopyLength"]} B')
        if a['journalCopyLength'] != a['journalLengthBeforeCommit']:
            reasons.append(f'journal copy: {a["journalCopyLength"]} B, but the journal read through the live handle immediately before COMMIT was '
                           f'{a["journalLengthBeforeCommit"]} B')
        if os.path.getsize(database) != a['databaseCopyLength']:
            reasons.append(f'database copy: {os.path.getsize(database)} B on disk, the record says {a["databaseCopyLength"]} B')
        actual = sha256_file(database)
        if actual != a['databaseCopySha256']:
            reasons.append('database copy: SHA-256 differs from the one recorded when the copy was taken')
        target = a['target']
        result = checker.check(journal, database, target.get('sourceId'), target['source'] == 'new', a['journalLengthBeforeCommit'],
                               a['databaseCopySha256'], a['layout'], False)
        details = {k: v for k, v in result.items() if k not in ('invalid_reasons', 'verdict')}
        out['details'] = details
        out['reasons'] = reasons + list(result.get('invalid_reasons', []))
        if reasons or result['verdict'] == INVALID:
            out['verdict'] = INVALID
            if result['verdict'] != INVALID and not out['reasons']:
                out['reasons'].append('checker: no reason given for an INVALID outcome')
        elif result['verdict'] in (PASS, FAIL):
            out['verdict'] = result['verdict']
            if result['verdict'] == FAIL:
                out['reasons'] = [f'REMOTE pages {result.get("remote_pages")}, levels over the SHARED cap {len(result.get("levels_over_shared_cap", []))}']
        else:
            out['verdict'] = INVALID
            out['reasons'].append(f'checker: unknown verdict {result["verdict"]!r}')
    except Refused as e:
        out['verdict'] = INVALID
        out['reasons'].append(str(e))
    except (OSError, ValueError, KeyError, TypeError) as e:
        out['verdict'] = INVALID
        out['reasons'].append(f'internal error: {type(e).__name__}: {e}')
    return out


def usage_invalid(message):
    print(json.dumps({'verdict': INVALID, 'reasons': ['usage: ' + message, 'nothing was checked; ' + __doc__.strip().splitlines()[0]],
                      'record': {}, 'checker': {}, 'details': {}}))
    print('usage: attribute_run.py <run record file> --analysis-dir <dir> [--label <run label>] [--json]', file=sys.stderr)
    return EXIT[INVALID]


def main(argv):
    args = list(argv)
    if not args or any(a in ('-h', '--help') for a in args):
        return usage_invalid('help or no arguments: this tool takes a run record and an analysis directory and nothing else')
    record = None
    analysis = None
    label = None
    i = 0
    while i < len(args):
        a = args[i]
        if a == '--analysis-dir' and i + 1 < len(args):
            analysis = args[i + 1]
            i += 2
        elif a == '--label' and i + 1 < len(args):
            label = args[i + 1]
            i += 2
        elif a == '--json':
            i += 1
        elif a.startswith('-'):
            return usage_invalid(f'unknown option {a} (the target, lengths and hashes come from the run record only)')
        elif record is None:
            record = a
            i += 1
        else:
            return usage_invalid(f'unexpected argument {a}')
    if record is None or analysis is None:
        return usage_invalid('a run record file and --analysis-dir are required')
    out = attribute(record, analysis, label)
    print(json.dumps(out))
    return EXIT[out['verdict']]


if __name__ == '__main__':
    try:
        code = main(sys.argv[1:])
    except SystemExit:
        raise
    except BaseException as e:      # an internal error is INVALID (exit 2), never PASS or FAIL
        print(json.dumps({'verdict': INVALID, 'reasons': [f'internal error: {type(e).__name__}: {e}'], 'record': {}, 'checker': {}, 'details': {}}))
        code = EXIT[INVALID]
    sys.exit(code)
