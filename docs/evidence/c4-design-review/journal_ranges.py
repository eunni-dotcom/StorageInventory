"""C4 design repair (fail-closed since the final repair, C4DRR-M02): the PERF-15 (a) key-range confinement check of the repaired
specification, as an independent reader of the SQLite file format (sqlite.org/fileformat2.html). It needs no product code.

Usage:  python journal_ranges.py <journal copy> <database copy taken after T0, before BEGIN> (--source <id> | --new-source)
            --expect-journal-bytes <n> [--expect-copy-sha256 <hex>] [--layout persource|global] [--expect-no-records] [--json]

  --expect-journal-bytes   the journal's length read through a live handle immediately before COMMIT, from the run's record
                           (required: a copy of another length was taken at the wrong moment or is truncated)
  --expect-copy-sha256     the copy's SHA-256 recorded when the copy was taken (the specification requires it of the C4 harness)
  --layout                 the dictionary layout the copy must have: persource (the name index keyed by source_id first; the
                           default) or global (the name index keyed by the name alone: C4's blocked schema, the negative control)
  --expect-no-records      the run is declared, separately, to journal no page; then only a 0-byte journal is valid. No TEST-P1
                           cell declares this (every T-IMPORT of the matrix allocates pages, and SQLite journals page 1 first)

Outcomes and exit codes
  PASS (0)     the evidence is complete and valid, and every journalled page satisfies PERF-15 (a)
  FAIL (1)     the evidence is complete and valid, and a journalled page violates PERF-15 (a) (REMOTE, or a level over the SHARED cap)
  INVALID (2)  the evidence cannot be trusted or interpreted (every reason is listed); also any usage error or internal error.
               INVALID is never PASS: the cell's PERF-15 (a) has no result until the run is captured and analysed again.

What makes the evidence INVALID
  journal   its length differs from --expect-journal-bytes; it is empty, or holds no record, without --expect-no-records; a header
            is truncated, or its magic is neither SQLite's nor the zero form SQLite writes for a segment not synced yet, or its
            sector size or page size is not a valid SQLite value; its page size differs from the copy's; its original database size
            differs from the copy's page count (in any segment); a segment ends inside a record, or the unsynced last segment does not
            end on a record boundary; a record's page number is 0, beyond the original size, or repeated; a record's checksum fails
            (SQLite's pager_cksum: the segment's nonce plus every 200th byte of the image from the end); a record's original image
            differs from the copy's page; page 1 is not journalled (SQLite journals page 1 before the first page it allocates, to
            record the new database size or free-list count, and every T-IMPORT of the matrix allocates pages; its image ties the
            copy's change counter, size and free list to the journal)
  copy      not an SQLite database; length not a whole number of pages; in-header size not valid or unlike the file; auto-vacuum;
            text encoding not UTF-8; application_id not StorageInventory's (0x53494E56) or user_version not 1; SHA-256 unlike
            --expect-copy-sha256; PRAGMA quick_check not ok; its trees not exactly the Library schema's 21 (the dictionary indexes,
            append-shaped trees and catalogue trees below); the name index not keyed as --layout declares; a page of a tree that
            cannot be decoded (page type, cell, record, child or overflow pointer out of range, a page reached twice); a dictionary
            key whose first column is not what the layout needs; the free list's count unlike the header
  target    --source names a source with no key in the copy's dictionary (no existing range); --new-source while some key already
            sorts at or past the new source id; a journalled page the walk cannot place (no tree, no free list, no overflow chain)

What it checks on valid evidence (unchanged from the design repair)
  Every journalled page is placed by tree, level (root = 0) and position at its level (left to right); for the source-prefixed
  dictionary indexes the first key column (source_id) of every entry and the key interval each page covers (from its ancestors'
  dividers) are decoded, and each journalled page is
       IN        its key interval intersects the target source's keys {source_id = s} (for a new source, s is past every key,
                 so the rightmost page of each level, where its keys are appended)
       ADJACENT  outside IN, at most W = 2 positions from the nearest IN page of its level (SQLite's balance window)
       REMOTE    further away: a violation
       SHARED    (counted separately) holds an entry of another source; at most 2 x (W + 1) = 6 per level
  append-shaped trees (file_obs, folder_obs, scan_error, snapshot_extension_total, the name and folder_path tables): the
  rightmost page of each level and at most W pages to its left; anything else is REMOTE. Catalogue trees, page 1 and free-list
  pages are allowed. For a name index keyed by the name alone (the negative control), a page's sources are the sources whose
  snapshots or folder paths reference its names in the copy; IN is the span of pages that hold, or whose subtree holds, a name
  of the target source, and a new source has no insertion point (every journalled page is REMOTE).
  PASS if there is no REMOTE page and no level over the SHARED cap; otherwise FAIL.
"""
import argparse
import hashlib
import json
import sqlite3
import struct
import sys
from collections import defaultdict

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

PASS, FAIL, INVALID = 'PASS', 'FAIL', 'INVALID'
EXIT = {PASS: 0, FAIL: 1, INVALID: 2}
MAGIC = bytes([0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7])
W = 2
SHARED_CAP = 2 * (W + 1)
APPLICATION_ID = 0x53494E56     # "SINV", LibraryNames.cs
USER_VERSION = 1                # schema 1
DICTIONARY = {'sqlite_autoindex_name_1', 'folder_path_child', 'folder_path_root'}
APPEND = {'file_obs', 'folder_obs', 'scan_error', 'snapshot_extension_total', 'name', 'folder_path'}
CATALOGUE = {'sqlite_schema', 'library_info', 'volume', 'volume_by_serial', 'source', 'source_local_key', 'source_network_key',
             'scan_attempt', 'scan_attempt_by_source', 'snapshot', 'snapshot_by_source', 'sqlite_autoindex_snapshot_1'}
TREES = DICTIONARY | APPEND | CATALOGUE
NAME_INDEX = 'sqlite_autoindex_name_1'
LAYOUT_COLUMNS = {'persource': ['source_id', 'utf16'], 'global': ['utf16']}
SOURCE_FIRST = {'folder_path_child': 'source_id', 'folder_path_root': 'source_id'}


class Invalid(Exception):
    """The evidence cannot be interpreted any further."""


def power_of_two(v):
    return v > 0 and v & (v - 1) == 0


def varint(buf, pos):
    v = 0
    for i in range(8):
        b = buf[pos]
        pos += 1
        v = (v << 7) | (b & 0x7f)
        if not b & 0x80:
            return v, pos
    return (v << 8) | buf[pos], pos + 1


def record(payload):
    """Decodes a record into a list of values (int, float, bytes, str or None). Raises on a malformed record."""
    hlen, pos = varint(payload, 0)
    if hlen > len(payload) or hlen < pos:
        raise ValueError('record header longer than the payload')
    types = []
    while pos < hlen:
        t, pos = varint(payload, pos)
        types.append(t)
    out = []
    at = hlen
    for t in types:
        if t == 0:
            out.append(None)
            continue
        if t in (10, 11):
            raise ValueError('reserved serial type')
        if 1 <= t <= 6:
            n = {1: 1, 2: 2, 3: 3, 4: 4, 5: 6, 6: 8}[t]
        elif t == 7:
            n = 8
        elif t in (8, 9):
            out.append(t - 8)
            continue
        else:
            n = (t - 12) // 2 if t % 2 == 0 else (t - 13) // 2
        if at + n > len(payload):
            raise ValueError('record body shorter than its header says')
        raw = payload[at:at + n]
        at += n
        if 1 <= t <= 6:
            out.append(int.from_bytes(raw, 'big', signed=True))
        elif t == 7:
            out.append(struct.unpack('>d', raw)[0])
        elif t % 2 == 0:
            out.append(bytes(raw))
        else:
            out.append(raw.decode('utf-8', 'strict'))
    return out


class Db:
    def __init__(self, path, reasons, expect_sha256):
        with open(path, 'rb') as f:
            self.data = f.read()
        d = self.data
        if len(d) < 100 or d[:16] != b'SQLite format 3\x00':
            raise Invalid('copy: not an SQLite database')
        raw = struct.unpack('>H', d[16:18])[0]
        self.page_size = 65536 if raw == 1 else raw
        if not power_of_two(self.page_size) or not 512 <= self.page_size <= 65536:
            raise Invalid(f'copy: invalid page size {self.page_size}')
        if len(d) % self.page_size:
            raise Invalid('copy: length is not a whole number of pages')
        self.page_count = len(d) // self.page_size
        self.usable = self.page_size - d[20]
        change_counter, header_pages = struct.unpack('>II', d[24:32])
        self.free_trunk, self.free_count = struct.unpack('>II', d[32:40])
        if struct.unpack('>I', d[92:96])[0] != change_counter:
            reasons.append('copy: in-header database size not valid (version-valid-for differs from the change counter)')
        elif header_pages != self.page_count:
            reasons.append(f'copy: in-header size {header_pages} pages, file {self.page_count} pages')
        if struct.unpack('>I', d[52:56])[0]:
            raise Invalid('copy: auto-vacuum database (pointer-map pages are not supported)')
        if struct.unpack('>I', d[56:60])[0] != 1:
            reasons.append('copy: text encoding is not UTF-8')
        if struct.unpack('>I', d[68:72])[0] != APPLICATION_ID:
            reasons.append(f'copy: application_id 0x{struct.unpack(">I", d[68:72])[0]:08X} is not StorageInventory\'s 0x{APPLICATION_ID:08X}')
        if struct.unpack('>I', d[60:64])[0] != USER_VERSION:
            reasons.append(f'copy: user_version {struct.unpack(">I", d[60:64])[0]} is not schema {USER_VERSION}')
        self.sha256 = hashlib.sha256(d).hexdigest()
        if expect_sha256 and self.sha256 != expect_sha256.lower():
            reasons.append('copy: SHA-256 differs from the one recorded when the copy was taken')
        self.change_counter = change_counter

    def page(self, n):
        if not 1 <= n <= self.page_count:
            raise Invalid(f'copy: page number {n} out of range')
        return self.data[(n - 1) * self.page_size:n * self.page_size]

    def payload(self, page, cell, total, owner, overflow_of, holder, is_table=False):
        """The cell's full payload, following overflow pages (recorded in overflow_of as belonging to the holder page)."""
        u = self.usable
        x = u - 35 if is_table else (u - 12) * 64 // 255 - 23
        m = (u - 12) * 32 // 255 - 23
        if total <= x:
            local = total
        else:
            k = m + (total - m) % (u - 4)
            local = k if k <= x else m
        if cell + local > len(page):
            raise ValueError('cell runs past the end of its page')
        out = bytearray(page[cell:cell + local])
        if total > local:
            ov = struct.unpack('>I', page[cell + local:cell + local + 4])[0]
            while len(out) < total:
                if not 1 <= ov <= self.page_count or ov in owner or ov in overflow_of:
                    raise ValueError(f'overflow pointer {ov} out of range or shared')
                overflow_of[ov] = holder
                p = self.page(ov)
                out += p[4:4 + min(u - 4, total - len(out))]
                ov = struct.unpack('>I', p[:4])[0]
        return bytes(out)

    def walk(self, name, root, owner, overflow_of):
        """Breadth first, children in key order: [(page, level, position, kind, cells, children)] and pages per level. For an index
        page, cells are (left child or None, record); for a table page, (left child or None, rowid)."""
        levels = defaultdict(int)
        frontier = [(root, 0)]
        nodes = []
        while frontier:
            nxt = []
            for n, lvl in frontier:
                if n in owner or n in overflow_of:
                    raise Invalid(f'copy: page {n} reached twice (tree {name}): corrupt tree or wrong copy')
                owner[n] = name
                try:
                    p = self.page(n)
                    h = 100 if n == 1 else 0
                    kind = p[h]
                    if kind not in (2, 5, 10, 13):
                        raise ValueError(f'page type {kind}')
                    ncell = struct.unpack('>H', p[h + 3:h + 5])[0]
                    interior = kind in (2, 5)
                    hdr = 12 if interior else 8
                    if h + hdr + 2 * ncell > len(p):
                        raise ValueError('cell pointer array past the page')
                    cells, children = [], []
                    for c in range(ncell):
                        pos = struct.unpack('>H', p[h + hdr + 2 * c:h + hdr + 2 * c + 2])[0]
                        if pos >= len(p):
                            raise ValueError('cell offset past the page')
                        child = None
                        if interior:
                            child = struct.unpack('>I', p[pos:pos + 4])[0]
                            children.append(child)
                            pos += 4
                        if kind == 5:
                            rowid, pos = varint(p, pos)
                            cells.append((child, rowid))
                            continue
                        total, pos = varint(p, pos)
                        if kind == 13:      # table leaf: only the rowid is needed; the payload is read to place its overflow pages
                            rowid, pos = varint(p, pos)
                            self.payload(p, pos, total, owner, overflow_of, n, is_table=True)
                            cells.append((child, rowid))
                            continue
                        cells.append((child, record(self.payload(p, pos, total, owner, overflow_of, n))))
                    if interior:
                        children.append(struct.unpack('>I', p[h + 8:h + 12])[0])
                    for ch in children:
                        if not 1 <= ch <= self.page_count:
                            raise ValueError(f'child page {ch} out of range')
                except Invalid:
                    raise
                except (ValueError, IndexError, struct.error, UnicodeDecodeError) as e:
                    raise Invalid(f'copy: page {n} of tree {name} cannot be decoded ({e})')
                nodes.append((n, lvl, levels[lvl], kind, cells, children))
                levels[lvl] += 1
                nxt.extend((ch, lvl + 1) for ch in children)
            frontier = nxt
        return nodes, dict(levels)


def parse_journal(j, db, reasons):
    """Every record of a rollback journal: [(page, image, checksum ok)], and the segment count. Raises Invalid where the journal
    cannot be read further; appends to reasons what makes it untrustworthy but still readable."""
    records = []
    off = 0
    segments = synced = 0
    sector0 = None
    while off < len(j):
        if len(j) - off < 28:
            raise Invalid(f'journal: truncated header at offset {off}')
        hdr = j[off:off + 28]
        has_magic = hdr[:8] == MAGIC
        if not has_magic and any(hdr[:12]):
            raise Invalid(f'journal: header at offset {off} has neither SQLite\'s magic nor the unsynced zero form')
        nrec, nonce, dbsize, sector, ps = struct.unpack('>IIIII', hdr[8:28])
        if not power_of_two(sector) or not 32 <= sector <= 65536:
            raise Invalid(f'journal: header at offset {off} has an invalid sector size {sector}')
        if not power_of_two(ps) or not 512 <= ps <= 65536:
            raise Invalid(f'journal: header at offset {off} has an invalid page size {ps}')
        if ps != db.page_size:
            raise Invalid(f'journal: page size {ps} differs from the copy\'s {db.page_size}')
        if sector0 is None:
            sector0 = sector
        elif sector != sector0:
            raise Invalid('journal: segments disagree on the sector size')
        if dbsize != db.page_count:
            reasons.append(f'journal: original database size {dbsize} pages (segment {segments + 1}) differs from the copy\'s {db.page_count}')
        if len(j) - off < sector:
            raise Invalid(f'journal: header at offset {off} truncated (sector {sector} B)')
        segments += 1
        synced += 1 if has_magic else 0
        start = off + sector
        size = ps + 8
        if not has_magic or nrec == 0xFFFFFFFF:     # unsynced (or no-sync) segment: records run to the end of the file
            if (len(j) - start) % size:
                raise Invalid(f'journal: the last segment does not end on a record boundary ({(len(j) - start) % size} B left over)')
            count = (len(j) - start) // size
        else:
            count = nrec
            if start + count * size > len(j):
                raise Invalid(f'journal: segment {segments} declares {nrec} records but the file ends inside them')
        for r in range(count):
            at = start + r * size
            pgno = struct.unpack('>I', j[at:at + 4])[0]
            image = j[at + 4:at + 4 + ps]
            stored = struct.unpack('>I', j[at + 4 + ps:at + 8 + ps])[0]
            ck = nonce
            i = ps - 200
            while i > 0:
                ck = (ck + image[i]) & 0xFFFFFFFF
                i -= 200
            records.append((pgno, image, ck == stored))
        if not has_magic or nrec == 0xFFFFFFFF:
            break
        end = start + count * size
        off = (end + sector - 1) // sector * sector
    return records, segments, synced


def owners_global(db_path):
    """name_id -> set of source ids referencing it (for an index keyed by the name alone)."""
    con = sqlite3.connect(f'file:{db_path}?mode=ro&immutable=1', uri=True)
    own = defaultdict(set)
    for name_id, source_id in con.execute('SELECT DISTINCT f.name_id, s.source_id FROM file_obs f JOIN snapshot s ON s.snapshot_id = f.snapshot_id'):
        own[name_id].add(source_id)
    for name_id, source_id in con.execute('SELECT DISTINCT name_id, source_id FROM folder_path'):
        own[name_id].add(source_id)
    con.close()
    return own


def check(journal_path, db_path, source, new_source, expect_bytes, expect_sha256=None, layout='persource', expect_no_records=False):
    reasons = []
    out = {'verdict': INVALID, 'invalid_reasons': reasons, 'layout_declared': layout, 'new_source': new_source}
    try:
        return _check(journal_path, db_path, source, new_source, expect_bytes, expect_sha256, layout, expect_no_records, reasons, out)
    except Invalid as e:
        reasons.append(str(e))
    except (OSError, sqlite3.Error) as e:
        reasons.append(f'input cannot be read: {type(e).__name__}: {e}')
    out['verdict'] = INVALID
    return out


def _check(journal_path, db_path, source, new_source, expect_bytes, expect_sha256, layout, expect_no_records, reasons, out):
    db = Db(db_path, reasons, expect_sha256)
    out.update({'database_pages': db.page_count, 'page_size': db.page_size, 'copy_change_counter': db.change_counter})
    con = sqlite3.connect(f'file:{db_path}?mode=ro&immutable=1', uri=True)
    try:
        qc = [r[0] for r in con.execute('PRAGMA quick_check')]
        if qc != ['ok']:
            reasons.append(f'copy: PRAGMA quick_check reports {len(qc)} problem(s)')
        trees = [(name, root) for name, root in con.execute('SELECT name, rootpage FROM sqlite_schema WHERE rootpage > 0')]
        columns = {idx: [r[2] for r in con.execute(f'PRAGMA index_info("{idx}")')] for idx in DICTIONARY}
        max_source = con.execute('SELECT coalesce(max(source_id), 0) FROM source').fetchone()[0]
    finally:
        con.close()
    names = {n for n, _ in trees} | {'sqlite_schema'}
    if names != TREES:
        reasons.append(f'copy: not the Library schema (missing trees {sorted(TREES - names)}, unexpected trees {sorted(names - TREES)})')
    if columns.get(NAME_INDEX) != LAYOUT_COLUMNS[layout]:
        raise Invalid(f'copy: the name index is keyed by {columns.get(NAME_INDEX)}, not as the declared {layout} layout ({LAYOUT_COLUMNS[layout]})')
    for idx, first in SOURCE_FIRST.items():
        if not columns.get(idx) or columns[idx][0] != first:
            raise Invalid(f'copy: index {idx} is missing or not keyed by {first} first')
    global_name_index = layout == 'global'
    target = max_source + 1 if new_source else source
    out['target_source'] = target

    with open(journal_path, 'rb') as f:
        j = f.read()
    out['journal_bytes'] = len(j)
    if expect_bytes is not None and len(j) != expect_bytes:
        reasons.append(f'journal: {len(j)} B, but the run recorded {expect_bytes} B immediately before COMMIT')
    if len(j) == 0:
        if expect_no_records:
            out.update({'segments': 0, 'synced_segments': 0, 'integrity': {'records': 0}})
            if not reasons:
                out['verdict'] = PASS
                out['note'] = 'vacuous: no page journalled, as declared'
            return out
        raise Invalid('journal: empty (0 B), but the run is not declared to journal no page; a copy taken after COMMIT is empty')
    records, segments, synced = parse_journal(j, db, reasons)
    out.update({'segments': segments, 'synced_segments': synced})
    if expect_no_records and records:
        reasons.append('journal: records present, but the run was declared to journal no page')
    if not records and not expect_no_records:
        reasons.append('journal: no record, but the run is not declared to journal no page')

    integrity = {'records': len(records), 'checksum_failures': 0, 'repeats': 0, 'page_zero_or_beyond_original_size': 0,
                 'image_mismatch': 0}
    seen = set()
    for pgno, image, ok in records:
        if not ok:
            integrity['checksum_failures'] += 1
        if pgno in seen:
            integrity['repeats'] += 1
        seen.add(pgno)
        if not 1 <= pgno <= db.page_count:
            integrity['page_zero_or_beyond_original_size'] += 1
        elif image != db.page(pgno):
            integrity['image_mismatch'] += 1
    out['integrity'] = integrity
    for k, what in [('checksum_failures', 'record checksum(s) fail'), ('repeats', 'page(s) journalled twice'),
                    ('page_zero_or_beyond_original_size', 'record page number(s) 0 or beyond the original size'),
                    ('image_mismatch', 'original image(s) differ from the copy (wrong copy, or a journal of another run)')]:
        if integrity[k]:
            reasons.append(f'journal: {integrity[k]} {what}')
    if records and 1 not in seen:
        reasons.append('journal: page 1 is not journalled (every T-IMPORT that allocates a page journals it first)')

    # page ownership and structure of the copy
    owner, overflow_of = {}, {}
    info = {}           # page -> (tree, level, position)
    counts = {}         # tree -> {level: pages}
    first_col = {}      # dictionary page -> set of first-column values of its entries
    interval = {}       # dictionary page -> (lo, hi) bounds of the first column (None = unbounded)
    name_rowids = {}    # global name-index page -> list of name_ids
    children_of = {}    # name-index page -> child pages
    dictionary_sources = set()
    for name, root in sorted(trees + [('sqlite_schema', 1)], key=lambda t: t[1]):
        nodes, levels = db.walk(name, root, owner, overflow_of)
        counts[name] = levels
        bounds = {root: (None, None)}
        for n, lvl, pos, kind, cells, children in nodes:
            info[n] = (name, lvl, pos)
            if name in DICTIONARY and kind in (2, 10):
                lo, hi = bounds.get(n, (None, None))
                keys = [c[1][0] if isinstance(c[1], list) and c[1] else None for c in cells]
                if name == NAME_INDEX and global_name_index:
                    if any(not isinstance(k, (bytes, str)) for k in keys):
                        raise Invalid(f'copy: name-index page {n} holds a key that is not a name (layout global)')
                    name_rowids[n] = [c[1][-1] for c in cells]
                else:
                    if any(not isinstance(k, int) for k in keys):
                        raise Invalid(f'copy: dictionary page {n} ({name}) holds a key whose first column is not a source id')
                    dictionary_sources.update(keys)
                interval[n] = (lo, hi)
                first_col[n] = set(keys)
                if name == NAME_INDEX:
                    children_of[n] = children
                if kind == 2:
                    prev = lo
                    for (child, rec), k in zip(cells, keys):
                        bounds[child] = (prev, k)
                        prev = k
                    bounds[children[-1]] = (prev, hi)
    # free list
    trunk, free_seen = db.free_trunk, 0
    while trunk:
        if trunk in owner or not 1 <= trunk <= db.page_count:
            raise Invalid(f'copy: free-list trunk {trunk} out of range or reached twice')
        p = db.page(trunk)
        owner[trunk] = 'freelist'
        free_seen += 1
        leaves = struct.unpack('>I', p[4:8])[0]
        if 8 + 4 * leaves > len(p):
            raise Invalid(f'copy: free-list trunk {trunk} lists more leaves than fit')
        for i in range(leaves):
            leaf = struct.unpack('>I', p[8 + 4 * i:12 + 4 * i])[0]
            if leaf in owner or not 1 <= leaf <= db.page_count:
                raise Invalid(f'copy: free-list leaf {leaf} out of range or reached twice')
            owner[leaf] = 'freelist'
            free_seen += 1
        trunk = struct.unpack('>I', p[:4])[0]
    if free_seen != db.free_count:
        reasons.append(f'copy: free list holds {free_seen} pages, header says {db.free_count}')

    # the target
    own_global = owners_global(db_path) if global_name_index else None
    if global_name_index:
        referenced = set().union(*own_global.values()) if own_global else set()
        if not new_source and target not in referenced:
            reasons.append(f'target: source {target} references no name in the copy (not an existing source)')
        if new_source and any(s >= target for s in referenced):
            reasons.append(f'target: --new-source, but source {target} or later already references names in the copy')
    else:
        if not new_source and target not in dictionary_sources:
            reasons.append(f'target: source {target} has no key in the copy\'s dictionary (not an existing source)')
        if new_source and any(s >= target for s in dictionary_sources):
            reasons.append(f'target: --new-source, but a dictionary key already sorts at or past source {target}')

    holds_target = {}   # global layout: a page holds, or its subtree holds, a name of the target source

    def subtree_holds(n):
        if n not in holds_target:
            mine = any(target in own_global.get(rid, ()) for rid in name_rowids.get(n, []))
            holds_target[n] = mine or any(subtree_holds(c) for c in children_of.get(n, []))
        return holds_target[n]

    def page_sources(n):
        if info[n][0] == NAME_INDEX and global_name_index:
            s = set()
            for rid in name_rowids.get(n, []):
                s |= own_global.get(rid, set())
            return s
        return {k for k in first_col.get(n, set()) if isinstance(k, int)}

    def intersects(n):
        if info[n][0] == NAME_INDEX and global_name_index:
            return subtree_holds(n)
        lo, hi = interval[n]
        return (lo is None or lo <= target) and (hi is None or hi >= target)

    # IN runs per dictionary tree and level
    in_run = {}
    for n, (name, lvl, pos) in info.items():
        if name in DICTIONARY and n in interval and intersects(n):
            lo, hi = in_run.get((name, lvl), (pos, pos))
            in_run[(name, lvl)] = (min(lo, pos), max(hi, pos))

    result = defaultdict(lambda: defaultdict(lambda: defaultdict(int)))
    remote_examples = []
    unplaced = []
    for pgno in sorted(seen):
        if pgno == 1:
            result['page 1']['-']['allowed'] += 1
            continue
        if not 1 <= pgno <= db.page_count:
            continue                                 # already a reason above
        placed = pgno if pgno in owner else overflow_of.get(pgno)
        own = owner.get(placed) if placed else None
        if own is None:
            unplaced.append(pgno)
            continue
        if own == 'freelist' or own in CATALOGUE:
            result[own]['-']['allowed'] += 1
            continue
        name, lvl, pos = info[placed]
        cell = result[name][lvl]
        cell['journalled'] += 1
        if name in APPEND:
            last = counts[name][lvl] - 1
            if pos == last:
                cell['IN'] += 1
            elif last - pos <= W:
                cell['ADJACENT'] += 1
            else:
                cell['REMOTE'] += 1
                remote_examples.append((name, lvl, pos, last - pos))
            continue
        run = in_run.get((name, lvl))
        if run is None:
            dist = None
        elif run[0] <= pos <= run[1]:
            dist = 0
        else:
            dist = run[0] - pos if pos < run[0] else pos - run[1]
        if dist == 0:
            cell['IN'] += 1
        elif dist is not None and dist <= W:
            cell['ADJACENT'] += 1
        else:
            cell['REMOTE'] += 1
            if len(remote_examples) < 10:
                remote_examples.append((name, lvl, pos, dist))
        if page_sources(placed) - {target}:
            cell['SHARED'] += 1
    if unplaced:
        reasons.append(f'target: {len(unplaced)} journalled page(s) the walk cannot place (no tree, free list or overflow chain), e.g. {unplaced[:5]}')
    remote = sum(c.get('REMOTE', 0) for t in result.values() for c in t.values())
    shared_over = [(t, l, c['SHARED']) for t, lv in result.items() for l, c in lv.items() if c.get('SHARED', 0) > SHARED_CAP]
    out.update({
        'dictionary_layout': 'Library-wide (name only)' if global_name_index else 'per source (source_id first)',
        'remote_pages': remote, 'levels_over_shared_cap': shared_over,
        'in_runs': {f'{t} level {l}': [lo, hi, counts[t][l]] for (t, l), (lo, hi) in sorted(in_run.items())},
        'by_tree': {t: {str(l): dict(c) for l, c in sorted(lv.items(), key=lambda kv: str(kv[0]))} for t, lv in sorted(result.items())},
        'remote_examples': remote_examples[:10],
    })
    if reasons:
        out['verdict'] = INVALID
    else:
        out['verdict'] = PASS if remote == 0 and not shared_over else FAIL
    return out


class Parser(argparse.ArgumentParser):
    def error(self, message):                     # a usage error is INVALID too (exit 2), never PASS or FAIL
        self.print_usage(sys.stderr)
        print(f'INVALID: usage: {message}', file=sys.stderr)
        sys.exit(EXIT[INVALID])


def main(argv):
    ap = Parser(description='PERF-15 (a) key-range confinement check (fail-closed)')
    ap.add_argument('journal')
    ap.add_argument('copy')
    target = ap.add_mutually_exclusive_group(required=True)
    target.add_argument('--source', type=int)
    target.add_argument('--new-source', action='store_true')
    ap.add_argument('--expect-journal-bytes', type=int, required=True)
    ap.add_argument('--expect-copy-sha256')
    ap.add_argument('--layout', choices=sorted(LAYOUT_COLUMNS), default='persource')
    ap.add_argument('--expect-no-records', action='store_true')
    ap.add_argument('--json', action='store_true')
    a = ap.parse_args(argv)
    out = check(a.journal, a.copy, a.source, a.new_source, a.expect_journal_bytes, a.expect_copy_sha256, a.layout, a.expect_no_records)
    if a.json:
        print(json.dumps(out, indent=1))
    else:
        integ = out.get('integrity', {})
        print(f"{out['verdict']}: layout {out['layout_declared']}, target source {out.get('target_source')}{' (new)' if out['new_source'] else ''}; "
              f"journal {out.get('journal_bytes', '?'):,} B, {integ.get('records', 0):,} records; REMOTE {out.get('remote_pages', '-')}, "
              f"levels over the SHARED cap {len(out.get('levels_over_shared_cap', []))}")
        for r in out['invalid_reasons']:
            print(f'  INVALID because {r}')
        for t, lv in out.get('by_tree', {}).items():
            for l, c in lv.items():
                print(f'  {t} level {l}: {c}')
    return EXIT[out['verdict']]


if __name__ == '__main__':
    try:
        sys.exit(main(sys.argv[1:]))
    except SystemExit:
        raise
    except BaseException as e:                     # an internal error is INVALID (exit 2), never PASS or FAIL
        print(f'INVALID: internal error: {type(e).__name__}: {e}', file=sys.stderr)
        sys.exit(EXIT[INVALID])
