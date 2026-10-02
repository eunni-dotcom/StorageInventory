"""C4 design repair: the PERF-15 (a) key-range confinement check of the repaired specification, as an independent reader of the
SQLite file format (sqlite.org/fileformat2.html). It needs no product code.

Usage:  python journal_ranges.py <journal copy> <database copy taken after T0, before BEGIN> (--source <id> | --new-source) [--json]

What it does
  1. Parses the rollback journal: segment headers, every record's page number and original image, the record checksum (nonce plus
     every 200th byte of the image from the end). It reports records whose checksum fails, repeated page numbers, pages beyond the
     database's original end, and records whose original image differs from the same page of the database copy (the copy must be
     the database as it was at BEGIN).
  2. Walks every B-tree of the database copy: for each page its tree, level (root = 0), position at its level (left to right) and,
     for the source-prefixed dictionary indexes, the first key column (source_id) of every entry and the key interval the page
     covers (from its ancestors' dividers).
  3. Classifies every journalled page:
       dictionary indexes (the name index, folder_path_child, folder_path_root):
         IN        its key interval intersects the target source's keys {source_id = s} (for a new source, s is past every key,
                   so the rightmost page of each level, where its keys are appended)
         ADJACENT  outside IN, at most W = 2 positions from the nearest IN page of its level (SQLite's balance window)
         REMOTE    further away: a defect
         SHARED    (counted separately) holds an entry of another source; at most 2 x (W + 1) = 6 per level
       append-shaped trees (file_obs, folder_obs, scan_error, snapshot_extension_total, the name and folder_path tables): the
         rightmost page of each level and at most W pages to its left; anything else is REMOTE
       catalogue trees, page 1, free-list pages: allowed
       any other page (another tree, unknown owner, beyond the original end): DEFECT
     For a dictionary index whose key does not start with source_id (the Library-wide dictionary of C4's blocked schema, used as the
     negative control), a page's sources are the sources whose snapshots or folder paths reference its names in the database copy;
     IN is the span of pages that hold, or whose subtree holds, a name of the target source, and a new source has no insertion point (every journalled page
     is REMOTE).
  4. PASS if there is no REMOTE page, no DEFECT page and no level with more than 6 SHARED pages; otherwise FAIL.
"""
import json
import sqlite3
import struct
import sys
from collections import defaultdict

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')

MAGIC = bytes([0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7])
W = 2
SHARED_CAP = 2 * (W + 1)
DICTIONARY = {'sqlite_autoindex_name_1', 'folder_path_child', 'folder_path_root'}
APPEND = {'file_obs', 'folder_obs', 'scan_error', 'snapshot_extension_total', 'name', 'folder_path'}
CATALOGUE = {'sqlite_schema', 'library_info', 'volume', 'volume_by_serial', 'source', 'source_local_key', 'source_network_key',
             'scan_attempt', 'scan_attempt_by_source', 'snapshot', 'snapshot_by_source', 'sqlite_autoindex_snapshot_1'}


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
    """Decodes a record into a list of values (int, float, bytes, str or None)."""
    hlen, pos = varint(payload, 0)
    types = []
    while pos < hlen:
        t, pos = varint(payload, pos)
        types.append(t)
    out = []
    at = hlen
    for t in types:
        if t == 0:
            out.append(None)
        elif 1 <= t <= 6:
            n = {1: 1, 2: 2, 3: 3, 4: 4, 5: 6, 6: 8}[t]
            out.append(int.from_bytes(payload[at:at + n], 'big', signed=True))
            at += n
        elif t == 7:
            out.append(struct.unpack('>d', payload[at:at + 8])[0])
            at += 8
        elif t in (8, 9):
            out.append(t - 8)
        elif t >= 12 and t % 2 == 0:
            n = (t - 12) // 2
            out.append(bytes(payload[at:at + n]))
            at += n
        elif t >= 13:
            n = (t - 13) // 2
            out.append(payload[at:at + n].decode('utf-8', 'replace'))
            at += n
        else:
            out.append(None)
    return out


class Db:
    def __init__(self, path):
        with open(path, 'rb') as f:
            self.data = f.read()
        raw = struct.unpack('>H', self.data[16:18])[0]
        self.page_size = 65536 if raw == 1 else raw
        self.usable = self.page_size - self.data[20]
        self.page_count = struct.unpack('>I', self.data[28:32])[0]

    def page(self, n):
        return self.data[(n - 1) * self.page_size:n * self.page_size]

    def payload(self, page, cell, total, is_table):
        """The cell's full payload, following overflow pages."""
        u = self.usable
        x = u - 35 if is_table else (u - 12) * 64 // 255 - 23
        m = (u - 12) * 32 // 255 - 23
        if total <= x:
            local = total
        else:
            k = m + (total - m) % (u - 4)
            local = k if k <= x else m
        out = bytearray(page[cell:cell + local])
        if total > local:
            ov = struct.unpack('>I', page[cell + local:cell + local + 4])[0]
            while ov and len(out) < total:
                p = self.page(ov)
                out += p[4:4 + min(u - 4, total - len(out))]
                ov = struct.unpack('>I', p[:4])[0]
        return bytes(out)

    def walk(self, root):
        """Yields (page, level, position, kind, cells) breadth first, children in key order. For an index page, cells are
        (left child or None, record); for a table page, (left child or None, rowid). Also yields each interior page's right child."""
        levels = defaultdict(int)
        frontier = [(root, 0)]
        nodes = []
        while frontier:
            nxt = []
            for n, lvl in frontier:
                p = self.page(n)
                h = 100 if n == 1 else 0
                kind = p[h]
                ncell = struct.unpack('>H', p[h + 3:h + 5])[0]
                interior = kind in (2, 5)
                hdr = 12 if interior else 8
                cells = []
                children = []
                for c in range(ncell):
                    off = struct.unpack('>H', p[h + hdr + 2 * c:h + hdr + 2 * c + 2])[0]
                    pos = off
                    child = None
                    if interior:
                        child = struct.unpack('>I', p[pos:pos + 4])[0]
                        children.append(child)
                        pos += 4
                    if kind == 5:   # table interior: rowid only
                        rowid, pos = varint(p, pos)
                        cells.append((child, rowid))
                        continue
                    total, pos = varint(p, pos)
                    if kind == 13:
                        rowid, pos = varint(p, pos)
                        cells.append((child, rowid))
                        continue
                    rec = record(self.payload(p, pos, total, False))
                    cells.append((child, rec))
                right = struct.unpack('>I', p[h + 8:h + 12])[0] if interior else None
                if interior:
                    children.append(right)
                nodes.append((n, lvl, levels[lvl], kind, cells, children))
                levels[lvl] += 1
                for ch in children:
                    nxt.append((ch, lvl + 1))
            frontier = nxt
        return nodes, dict(levels)


def parse_journal(path, page_size):
    with open(path, 'rb') as f:
        j = f.read()
    records = []
    off = 0
    segments = synced = 0
    while off + 28 <= len(j):
        hdr = j[off:off + 28]
        has_magic = hdr[:8] == MAGIC
        if not has_magic and any(hdr[:12]):
            break
        nrec, nonce, dbsize, sector, ps = struct.unpack('>IIIII', hdr[8:28])
        if sector == 0 or ps == 0:
            break
        segments += 1
        synced += 1 if has_magic else 0
        start = off + sector
        size = ps + 8
        count = (len(j) - start) // size if (not has_magic or nrec in (0, 0xFFFFFFFF)) else nrec
        for r in range(count):
            at = start + r * size
            if at + size > len(j):
                break
            pgno = struct.unpack('>I', j[at:at + 4])[0]
            image = j[at + 4:at + 4 + ps]
            stored = struct.unpack('>I', j[at + 4 + ps:at + 8 + ps])[0]
            ck = nonce
            i = ps - 200
            while i > 0:
                ck = (ck + image[i]) & 0xFFFFFFFF
                i -= 200
            records.append((pgno, image, ck == stored))
        end = start + count * size
        off = (end + sector - 1) // sector * sector
        if not has_magic:
            break
    return records, segments, synced, len(j)


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


def check(journal_path, db_path, source, new_source):
    db = Db(db_path)
    con = sqlite3.connect(f'file:{db_path}?mode=ro&immutable=1', uri=True)
    trees = [(name, root, sql or '') for name, root, sql in con.execute("SELECT name, rootpage, sql FROM sqlite_schema WHERE rootpage > 0")]
    max_source = con.execute('SELECT coalesce(max(source_id), 0) FROM source').fetchone()[0]
    con.close()
    if new_source:
        source = max_source + 1
    trees.append(('sqlite_schema', 1, ''))
    records, segments, synced, jbytes = parse_journal(journal_path, db.page_size)

    # page ownership and structure
    owner = {}
    info = {}           # page -> (tree, level, position)
    counts = {}         # tree -> {level: pages}
    first_col = {}      # dictionary page -> set of first-column values of its entries
    interval = {}       # dictionary page -> (lo, hi) bounds of the first column (None = unbounded)
    name_rowids = {}    # global name-index page -> list of name_ids
    children_of = {}    # global name-index page -> child pages
    global_name_index = False
    for name, root, sql in trees:
        nodes, levels = db.walk(root)
        counts[name] = levels
        bounds = {root: (None, None)}
        for n, lvl, pos, kind, cells, children in nodes:
            owner[n] = name
            info[n] = (name, lvl, pos)
            if name in DICTIONARY and kind in (2, 10):
                lo, hi = bounds.get(n, (None, None))
                keys = [c[1][0] if isinstance(c[1], list) and c[1] else None for c in cells]
                interval[n] = (lo, hi)
                first_col[n] = set(keys)
                if name == 'sqlite_autoindex_name_1' and keys and not isinstance(keys[0], int):
                    global_name_index = True
                    name_rowids[n] = [c[1][-1] for c in cells]
                if name == 'sqlite_autoindex_name_1':
                    children_of[n] = children
                if kind == 2:
                    prev = lo
                    for (child, rec), k in zip(cells, keys):
                        bounds[child] = (prev, k)
                        prev = k
                    bounds[children[-1]] = (prev, hi)
    # free list
    trunk = struct.unpack('>I', db.data[32:36])[0]
    while trunk:
        p = db.page(trunk)
        owner[trunk] = 'freelist'
        for i in range(struct.unpack('>I', p[4:8])[0]):
            owner[struct.unpack('>I', p[8 + 4 * i:12 + 4 * i])[0]] = 'freelist'
        trunk = struct.unpack('>I', p[:4])[0]

    own_global = owners_global(db_path) if global_name_index else None
    holds_target = {}   # global layout: a page holds, or its subtree holds, a name of the target source

    def subtree_holds(n):
        if n not in holds_target:
            mine = any(source in own_global.get(rid, ()) for rid in name_rowids.get(n, []))
            holds_target[n] = mine or any(subtree_holds(c) for c in children_of.get(n, []))
        return holds_target[n]

    def page_sources(n):
        name = info[n][0]
        if name == 'sqlite_autoindex_name_1' and global_name_index:
            s = set()
            for rid in name_rowids.get(n, []):
                s |= own_global.get(rid, set())
            return s
        return {k for k in first_col.get(n, set()) if isinstance(k, int)}

    def intersects(n):
        name = info[n][0]
        if name == 'sqlite_autoindex_name_1' and global_name_index:
            return subtree_holds(n)
        lo, hi = interval[n]
        lo_ok = lo is None or (isinstance(lo, int) and lo <= source)
        hi_ok = hi is None or (isinstance(hi, int) and hi >= source)
        return lo_ok and hi_ok

    # IN runs per dictionary tree and level
    in_run = {}
    for n, (name, lvl, pos) in info.items():
        if name in DICTIONARY and n in interval and intersects(n):
            lo, hi = in_run.get((name, lvl), (pos, pos))
            in_run[(name, lvl)] = (min(lo, pos), max(hi, pos))

    pages = {}
    integrity = {'records': len(records), 'checksum_failures': 0, 'repeats': 0, 'beyond_end': 0, 'image_mismatch': 0}
    seen = set()
    for pgno, image, ok in records:
        if not ok:
            integrity['checksum_failures'] += 1
        if pgno in seen:
            integrity['repeats'] += 1
        seen.add(pgno)
        if pgno > db.page_count:
            integrity['beyond_end'] += 1
        elif image != db.page(pgno):
            integrity['image_mismatch'] += 1
    result = defaultdict(lambda: defaultdict(lambda: defaultdict(int)))
    remote_examples = []
    defects = []
    for pgno in sorted(seen):
        own = owner.get(pgno)
        if pgno == 1:
            result['page 1']['-']['allowed'] += 1
            continue
        if own is None or pgno > db.page_count:
            defects.append((pgno, 'beyond the original end' if pgno > db.page_count else (own or 'unknown')))
            continue
        if own == 'freelist' or own in CATALOGUE:
            result[own]['-']['allowed'] += 1
            continue
        name, lvl, pos = info[pgno]
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
        if name in DICTIONARY:
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
            if page_sources(pgno) - {source}:
                cell['SHARED'] += 1
            continue
        defects.append((pgno, name))
    remote = sum(c.get('REMOTE', 0) for t in result.values() for c in t.values())
    shared_over = [(t, l, c['SHARED']) for t, lv in result.items() for l, c in lv.items() if c.get('SHARED', 0) > SHARED_CAP]
    verdict = 'PASS' if remote == 0 and not defects and not shared_over else 'FAIL'
    return {
        'verdict': verdict,
        'target_source': source, 'new_source': new_source, 'dictionary_layout': 'Library-wide (name only)' if global_name_index else 'per source (source_id first)',
        'journal_bytes': jbytes, 'segments': segments, 'synced_segments': synced, 'integrity': integrity,
        'database_pages': db.page_count, 'page_size': db.page_size,
        'remote_pages': remote, 'defect_pages': len(defects), 'levels_over_shared_cap': shared_over,
        'in_runs': {f'{t} level {l}': [lo, hi, counts[t][l]] for (t, l), (lo, hi) in sorted(in_run.items())},
        'by_tree': {t: {str(l): dict(c) for l, c in sorted(lv.items(), key=lambda kv: str(kv[0]))} for t, lv in sorted(result.items())},
        'remote_examples': remote_examples[:10], 'defects': defects[:10],
    }


if __name__ == '__main__':
    a = sys.argv[1:]
    src = int(a[a.index('--source') + 1]) if '--source' in a else 0
    out = check(a[0], a[1], src, '--new-source' in a)
    if '--json' in a:
        print(json.dumps(out, indent=1))
    else:
        print(f"{out['verdict']}: {out['dictionary_layout']} dictionary, target source {out['target_source']}{' (new)' if out['new_source'] else ''}; "
              f"journal {out['journal_bytes']:,} B, {out['integrity']['records']:,} records ({out['integrity']['checksum_failures']} checksum failures, "
              f"{out['integrity']['repeats']} repeats, {out['integrity']['beyond_end']} beyond the end, {out['integrity']['image_mismatch']} images unlike the copy); "
              f"REMOTE {out['remote_pages']}, DEFECT {out['defect_pages']}, levels over the SHARED cap {len(out['levels_over_shared_cap'])}")
        for t, lv in out['by_tree'].items():
            for l, c in lv.items():
                print(f"  {t} level {l}: {c}")
