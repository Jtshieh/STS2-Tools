"""One fixed release ELF derivative; pure Python preparation and static QA only.

Only SHA-256 0b0ae3859c4c26d352b6483dc1cb11601a07bbc9890d97bcebae0d5d0bdd99ff
is supported. No ELF execution, native build, subprocess, network, retry, overwrite,
or general ELF repair is provided. Failed/partial outputs are retained as evidence.
CLI paths are relocation inputs only; the parent hash and entire layout stay fixed.
The caller must supply an existing private evidence directory and a fresh child path.
"""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import struct
import sys
import traceback

sys.dont_write_bytecode = True
OUT = CHILD = READER = None
READER_SHA = 'bab541bf6de0a72727b5b82a668d8aeab4b3db7d475c1db0c14222dcf4420442'
PARENT_SHA = '0b0ae3859c4c26d352b6483dc1cb11601a07bbc9890d97bcebae0d5d0bdd99ff'
PARENT_SIZE = 70349168
NEEDED = b'/guard/libE004EarlyGuard.so\0'
PAGE = 0x1000
NEW_OFFSET, NEW_VADDR, NEW_SIZE = 0x4318000, 0x48f5000, 0x103e
OUTPUTS = {'operation-diff.json', 'elf-comparison.json',
           'static-qa.json', 'ordinary-copy.json', 'manifest.json', 'failure.json'}
CHECKS = []
STAGE = 'initial'
EVIDENCE_READY = False


def digest(data):
    return hashlib.sha256(data).hexdigest()


def require(condition, name):
    if not condition:
        raise ValueError(name)
    CHECKS.append(name)


def receipt(path):
    s = path.lstat()
    return {'path': str(path), 'regular': stat.S_ISREG(s.st_mode), 'mode': oct(stat.S_IMODE(s.st_mode)),
            'nlink': s.st_nlink, 'device': s.st_dev, 'inode': s.st_ino, 'bytes': s.st_size,
            'mtimeNs': s.st_mtime_ns, 'ctimeNs': s.st_ctime_ns, 'uid': s.st_uid, 'gid': s.st_gid}


def fresh_bytes(path, payload, mode=0o400):
    require(path == CHILD or (path.parent == OUT and path.name in OUTPUTS), 'write destination is an explicit fresh output: ' + str(path))
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW
    fd = os.open(path, flags, 0o600)
    with os.fdopen(fd, 'wb') as f:
        f.write(payload)
        f.flush()
        os.fsync(f.fileno())
        os.fchmod(f.fileno(), mode)


def save_json(name, data):
    fresh_bytes(OUT / name, (json.dumps(data, ensure_ascii=False, indent=2) + '\n').encode())


def overlaps(a, b, c, d):
    return a < d and c < b


def pages(elf):
    """Static 4 KiB PT_LOAD/RELRO model for this fixed x86_64 ELF only."""
    result = {}
    for p in elf['programHeaders']:
        if p[0] != 1:
            continue
        first, file_end = p[3] & -PAGE, (p[3] + p[5] + PAGE - 1) & -PAGE
        memory_end = (p[3] + p[6] + PAGE - 1) & -PAGE
        for va in range(first, memory_end, PAGE):
            if va in result:
                raise ValueError('Overlapping PT_LOAD pages')
            file_offset = (p[2] & -PAGE) + va - first if va < file_end else None
            result[va] = {'initialFlags': p[1], 'effectiveFlagsAfterRelro': p[1], 'fileOffset': file_offset}
    for p in elf['programHeaders']:
        if p[0] == 0x6474e552:
            for va in range(p[3] & -PAGE, (p[3] + p[6]) & -PAGE, PAGE):
                if va not in result or result[va]['initialFlags'] != 6:
                    raise ValueError('Unexpected fixed-ELF RELRO page')
                result[va]['effectiveFlagsAfterRelro'] = 4
    return result


def main():
    global STAGE, OUT, CHILD, READER, EVIDENCE_READY
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--source', required=True, type=Path)
    cli.add_argument('--destination', required=True, type=Path)
    cli.add_argument('--hq-elf', required=True, type=Path)
    cli.add_argument('--evidence-dir', required=True, type=Path)
    args = cli.parse_args()
    original, CHILD, READER, OUT = (p.absolute() for p in (args.source, args.destination, args.hq_elf, args.evidence_dir))
    require(sys.flags.optimize == 0, 'assertions enabled')
    require(OUT.is_dir() and OUT.resolve() == OUT, 'explicit evidence directory exists without path aliases')
    require(stat.S_IMODE(OUT.lstat().st_mode) == 0o700, 'output directory is private mode 0700')
    require(not any(os.path.lexists(OUT / n) for n in OUTPUTS), 'all result names absent before the single attempt')
    require(CHILD.parent.is_dir() and CHILD.resolve() == CHILD and not os.path.lexists(CHILD), 'explicit child is a fresh non-aliased path with an existing parent')
    require(CHILD.parent != OUT or CHILD.name not in OUTPUTS, 'child destination cannot collide with QA report paths')
    EVIDENCE_READY = True
    source_sha = digest(Path(__file__).read_bytes())
    before = receipt(original)
    require(before['regular'] and before['nlink'] == 1 and not (int(before['mode'], 8) & 0o222),
            'original is a read-only ordinary file with one link')
    require(before['bytes'] == PARENT_SIZE, 'fixed original size')
    require(digest(READER.read_bytes()) == READER_SHA, 'fixed complete HQ read-only comparator SHA')
    spec = importlib.util.spec_from_file_location('e004b_fixed_hq_elf', READER)
    elf = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(elf)
    a = elf.parse(original)
    require(a['sha256'] == PARENT_SHA and a['bytes'] == PARENT_SIZE, 'actual original bytes match fixed SHA and size')
    require(a['elf'][1:4] == (2, 62, 1) and a['elf'][5] == 0x40 and a['elf'][9:] == (56, 12, 64, 35, 34),
            'fixed ELF64 ET_EXEC machine and table dimensions')
    elf.mapped_layout(a)
    by, ph, raw = a['byName'], a['programHeaders'], a['data']
    interp, strings, dynamic = by['.interp'], by['.dynstr'], by['.dynamic']
    require(ph[0] == (6, 4, 0x40, 0x400040, 0x400040, 0x2a0, 0x2a0, 8), 'fixed original PT_PHDR')
    require(ph[3] == (3, 4, 0x2e0, 0x4002e0, 0x4002e0, 28, 28, 1), 'fixed original PT_INTERP')
    require((strings['offset'], strings['vaddr'], strings['bytes']) == (0x3470, 0x403470, 4130), 'fixed original dynstr')
    require((dynamic['offset'], dynamic['vaddr'], dynamic['bytes']) == (0x430e9f8, 0x470e9f8, 576), 'fixed original dynamic')
    require(interp['payload'] == b'/lib64/ld-linux-x86-64.so.2\0' and interp['align'] == 8, 'unchanged interpreter payload and section alignment')
    require(raw[0x2fc:0x368] == b'X' * 106 + b'\0\0', 'exact known 106-X and 2-NUL header fill, not zero padding')
    for s in a['sections']:
        require(s['type'] == 8 or s['name'] == '.interp' or not overlaps(0x2e0, 0x334, s['offset'], s['offset'] + s['bytes']),
                'header edit does not overlap another section: ' + s['name'])
    for i, p in enumerate(ph):
        require(p[0] in (1, 3, 6) or not overlaps(0x2e0, 0x334, p[2], p[2] + p[5]),
                'header edit does not overlap another semantic segment: ' + str(i))
    for s in a['sections']:
        if s['type'] == 4:
            for offset, info, addend in struct.iter_unpack('<QQq', s['payload']):
                require(not (0x4002fc <= offset < 0x400368 or 0x4002fc <= addend < 0x400368), 'no relocation targets known header fill')
        if s['type'] in (2, 11):
            for name, info, other, index, value, size in struct.iter_unpack('<IBBHQQ', s['payload']):
                require(not (0x4002fc <= value < 0x400368), 'no symbol address targets known header fill')
                require(not (info & 15 == 3 and index in (1, 7)), 'no moved-section symbol requires an extra rewrite')
    require(len(NEEDED) == 28 and len(strings['payload'] + NEEDED) == NEW_SIZE, 'complete dynstr plus exact 28-byte dependency')
    active = b''.join(struct.pack('<qQ', *r) for r in a['dynamic'])
    require(len(a['dynamic']) == 30 and dynamic['payload'] == active + bytes(6 * 16), '30 active dynamic entries and six all-zero slots')
    rows = [[1, 4130]] + [[tag, NEW_VADDR if tag == 5 else NEW_SIZE if tag == 10 else value] for tag, value in a['dynamic']]
    require(sum(t == 5 for t, v in rows) == sum(t == 10 for t, v in rows) == 1, 'unique DT_STRTAB and DT_STRSZ')
    new_dynamic = b''.join(struct.pack('<qQ', *r) for r in rows) + bytes(5 * 16)
    require(len(new_dynamic) == dynamic['bytes'], 'dynamic remains fixed length with five terminating slots')
    require((len(raw) + PAGE - 1) & -PAGE == NEW_OFFSET, 'new segment begins after all original file pages')
    original_loads = [p for p in ph if p[0] == 1]
    original_end = max(p[3] + p[6] for p in original_loads)
    require((original_end + PAGE - 1) & -PAGE == NEW_VADDR, 'new segment starts after all original memory pages')
    new_load = (1, 4, NEW_OFFSET, NEW_VADDR, NEW_VADDR, NEW_SIZE, NEW_SIZE, PAGE)
    result, operations = bytearray(raw), []

    def replace(offset, payload, reason):
        require(0 <= offset < offset + len(payload) <= len(raw), 'bounded original-byte edit: ' + reason)
        require(all(not overlaps(offset, offset + len(payload), x['offset'], x['offset'] + x['bytes']) for x in operations),
                'disjoint byte whitelist: ' + reason)
        old = bytes(result[offset:offset + len(payload)])
        result[offset:offset + len(payload)] = payload
        operations.append({'offset': offset, 'bytes': len(payload), 'reason': reason,
                           'beforeHex': old.hex(), 'afterHex': payload.hex(), 'beforeSha256': digest(old), 'afterSha256': digest(payload)})

    STAGE = 'build-in-memory'
    replace(56, struct.pack('<H', 13), 'e_phnum 12 to 13')
    replace(0x40 + 32, struct.pack('<QQ', 0x2d8, 0x2d8), 'PT_PHDR file/memory length plus 56')
    replace(0x40 + 3 * 56 + 8, struct.pack('<QQQ', 0x318, 0x400318, 0x400318), 'PT_INTERP file/virtual/physical location')
    replace(0x2e0, struct.pack('<IIQQQQQQ', *new_load), 'thirteenth program header replaces old interp and 28 known X bytes')
    replace(0x318, interp['payload'], 'relocated unchanged interpreter replaces 28 known X bytes')
    replace(dynamic['offset'], new_dynamic, 'in-place DT_NEEDED with updated string-table address and size')
    replace(a['elf'][6] + 1 * 64 + 16, struct.pack('<QQ', 0x400318, 0x318), 'interp section location only')
    replace(a['elf'][6] + 7 * 64 + 16, struct.pack('<QQQ', NEW_VADDR, NEW_OFFSET, NEW_SIZE), 'dynstr section location and size')
    alignment = bytes(NEW_OFFSET - len(raw))
    appended = alignment + strings['payload'] + NEEDED
    result.extend(appended)
    allowed = {i for r in operations for i in range(r['offset'], r['offset'] + r['bytes'])}
    changed = [i for i, (old, new) in enumerate(zip(raw, result)) if old != new]
    require(set(changed) <= allowed and len(result) == NEW_OFFSET + NEW_SIZE, 'every changed original byte is whitelisted and append length is exact')
    require(result[0x334:0x368] == raw[0x334:0x368], 'remaining 50 X and two NUL header bytes unchanged')
    require(result[strings['offset']:strings['offset'] + strings['bytes']] == strings['payload'], 'old dynstr physical bytes remain unchanged')
    STAGE = 'single-exclusive-ordinary-write'
    require(receipt(original) == before and digest(original.read_bytes()) == PARENT_SHA, 'original unchanged immediately before exclusive output creation')
    fresh_bytes(CHILD, result, 0o500)
    del result
    STAGE = 'static-validation'
    comparison = elf.compare_needed_derivative(original, CHILD, NEEDED[:-1].decode('ascii'))
    b = elf.parse(CHILD)
    require(comparison['passed'] and b['sha256'] != PARENT_SHA, 'full unmodified HQ comparator passes')
    require(b['programHeaders'][-1] == new_load and [p for p in b['programHeaders'] if p[0] == 1][:-1] == original_loads,
            'all original LOAD entries unchanged and exactly one final R LOAD added')
    expected_ph = list(ph)
    expected_ph[0] = (6, 4, 0x40, 0x400040, 0x400040, 0x2d8, 0x2d8, 8)
    expected_ph[3] = (3, 4, 0x318, 0x400318, 0x400318, 28, 28, 1)
    require(b['programHeaders'] == expected_ph + [new_load], 'all program headers exactly match the fixed authorized edits')
    expected_header = list(a['elf']); expected_header[10] = 13
    require(b['elf'] == tuple(expected_header), 'only ELF header phnum changed')
    require(b['dynamic'] == rows and b['byName']['.dynamic']['payload'] == new_dynamic, 'exact dynamic entries including all terminal slots')
    require(b['byName']['.dynstr']['payload'] == strings['payload'] + NEEDED, 'exact complete dynstr prefix and only new dependency')
    for name, s in by.items():
        t = b['byName'][name]
        for field in s:
            allowed_fields = {'offset', 'vaddr'} if name == '.interp' else {'offset', 'vaddr', 'bytes', 'payload', 'sha256'} if name == '.dynstr' else {'payload', 'sha256'} if name == '.dynamic' else set()
            require(field in allowed_fields or s[field] == t[field], 'preserved section field: ' + name + ':' + field)
    require(by['pck'] == b['byName']['pck'] and by['pck']['payload'] == bytes(8), 'PCK offset section boundary and zero value unchanged')
    first_load = original_loads[0]
    require(0x40 + 13 * 56 == 0x318 and 0x318 + 28 <= 0x368 and 0x334 <= first_load[2] + first_load[5],
            'expanded PHDR and relocated INTERP fit first R LOAD before NOTE without overlap')
    require(0x400040 == first_load[3] + 0x40 - first_load[2], 'PT_PHDR virtual address matches its mapped file bytes')
    old_pages, new_pages = pages(a), pages(b)
    require(all(new_pages.get(va) == p for va, p in old_pages.items()), 'every original page retains initial/final permissions and file-or-BSS mapping')
    added = sorted(set(new_pages) - set(old_pages))
    require(added == [0x48f5000, 0x48f6000] and all(new_pages[va]['initialFlags'] == new_pages[va]['effectiveFlagsAfterRelro'] == 4 for va in added),
            'only two new read-only pages with no execute or write permission')
    old_file_pages = {p['fileOffset'] for p in old_pages.values() if p['fileOffset'] is not None}
    require(all(new_pages[va]['fileOffset'] >= NEW_OFFSET and new_pages[va]['fileOffset'] not in old_file_pages for va in added),
            'new R pages do not alias any original mapped or unmapped original file page')
    require(all(p['initialFlags'] & 3 != 3 and p['effectiveFlagsAfterRelro'] & 3 != 3 for p in new_pages.values()), 'no RWX page before or after RELRO')
    require(receipt(original) == before and digest(original.read_bytes()) == PARENT_SHA, 'original bytes identity metadata and protection unchanged after validation')
    child_receipt = receipt(CHILD)
    require(child_receipt['regular'] and child_receipt['nlink'] == 1 and child_receipt['mode'] == '0o500' and
            (child_receipt['device'], child_receipt['inode']) != (before['device'], before['inode']), 'derived file is ordinary independently written nlink1 mode0500 with distinct identity')
    require(digest(READER.read_bytes()) == READER_SHA and digest(Path(__file__).read_bytes()) == source_sha, 'conversion source and reused comparator unchanged')
    limitations = [
        'Static ELF acceptance only; executable, native guard and managed bootstrap were not run.',
        'Page model requires 4096-byte Linux pages; AT_PAGESZ and auxiliary-vector observations remain runtime checks.',
        'Expected auxiliary vector: AT_PHDR=0x400040, AT_PHENT=56, AT_PHNUM=13; not runtime-observed.',
        'New highest LOAD changes maximum mapped end and may change Linux initial brk/heap and address-space accounting; whole-process layout equivalence is not claimed.',
        'Known X-fill has no inspected ELF section, semantic segment, relocation or symbol use; arbitrary machine-code reads of it are not disproved.',
        'File EOF grows; original pck section offset, size, address and zero offset value remain exact. No external PCK or game assemblies were read or modified.',
        'Original build-id/debuglink bytes are preserved and do not identify the derivative; full parent/child SHA-256 supplies lineage.',
    ]
    save_json('operation-diff.json', {'schema': 'e004b-fixed-release-elf-byte-diff-v1', 'parentSha256': PARENT_SHA, 'childSha256': b['sha256'],
        'changedOriginalByteCount': len(changed), 'changedOriginalByteOffsets': changed, 'originalByteWhitelist': operations,
        'append': {'offset': len(raw), 'bytes': len(appended), 'alignmentZeroBytes': len(alignment), 'sha256': digest(appended),
                   'dynstrOffset': NEW_OFFSET, 'dynstrBytes': NEW_SIZE, 'dynstrSha256': digest(strings['payload'] + NEEDED)},
        'knownHeaderFill': {'offset': 0x2fc, 'bytes': 108, 'beforeHex': raw[0x2fc:0x368].hex(), 'afterHex': b['data'][0x2fc:0x368].hex(),
                            'description': '106 ASCII X plus two NUL; exactly the authorized 56 X-byte positions are eligible for replacement'}})
    save_json('elf-comparison.json', comparison)
    save_json('ordinary-copy.json', {'schema': 'e004b-ordinary-release-derivative-v1', 'originalBefore': before, 'originalAfter': receipt(original),
        'derived': child_receipt, 'parentSha256': PARENT_SHA, 'childSha256': b['sha256'], 'method': 'exclusive ordinary write from Python bytes; no link, clone, symlink or original write',
        'originalUntouched': True, 'outputDirectoryMode': '0o700'})
    save_json('static-qa.json', {'schema': 'e004b-fixed-release-elf-static-qa-v1', 'passed': True, 'checks': CHECKS[:], 'checkCount': len(CHECKS),
        'parent': {'sha256': PARENT_SHA, 'bytes': len(raw)}, 'child': {'sha256': b['sha256'], 'bytes': b['bytes']},
        'pageLayout': {'pageBytesAssumed': PAGE, 'originalPageCount': len(old_pages), 'derivedPageCount': len(new_pages),
                       'originalPageMappingsAndProtectionsPreserved': True, 'newRwxPages': 0, 'newFileAliases': 0,
                       'addedPages': [{'vaddr': va, **new_pages[va]} for va in added]},
        'auxvExpectedNotObserved': {'AT_PHDR': 0x400040, 'AT_PHENT': 56, 'AT_PHNUM': 13, 'AT_PAGESZ': PAGE},
        'mappedEnd': {'original': original_end, 'derived': NEW_VADDR + NEW_SIZE, 'originalRounded': NEW_VADDR, 'derivedRounded': NEW_VADDR + 2 * PAGE},
        'pckSection': {k: v for k, v in by['pck'].items() if k != 'payload'}, 'limitations': limitations,
        'runtimeExecuted': False, 'nativeCompilation': False, 'gameAcceptance': False})
    entries = []
    for p in sorted(OUT.iterdir()):
        r = receipt(p)
        require(r['regular'] and r['nlink'] == 1, 'manifest member is an ordinary single-link file: ' + p.name)
        entries.append({'name': p.name, 'bytes': r['bytes'], 'sha256': digest(p.read_bytes()), 'mode': r['mode']})
    save_json('manifest.json', {'schema': 'e004b-fixed-release-elf-preparation-manifest-v1', 'createdUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'passed': True, 'sourceSha256': source_sha, 'readOnlyComparator': {'path': str(READER), 'sha256': READER_SHA},
        'conversionSource': {'path': str(Path(__file__).absolute()), 'sha256': source_sha},
        'parentPath': str(original), 'parentSha256': PARENT_SHA, 'parentBytes': PARENT_SIZE,
        'childPath': str(CHILD), 'childSha256': b['sha256'], 'childBytes': b['bytes'],
        'filesExcludingThisManifest': entries, 'generationAttempts': 1, 'binaryExecutions': 0, 'nativeCompilations': 0,
        'scope': 'Fixed-hash release ELF preparation and static QA only; runtime and game acceptance remain separate.'})
    STAGE = 'complete'
    print(json.dumps({'passed': True, 'outputDirectory': str(OUT), 'parentSha256': PARENT_SHA, 'childSha256': b['sha256'],
                      'childBytes': b['bytes'], 'changedOriginalByteCount': len(changed), 'appendedBytes': len(appended), 'runtimeExecuted': False}, indent=2))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        if EVIDENCE_READY and not os.path.lexists(OUT / 'failure.json'):
            save_json('failure.json', {'passed': False, 'stage': STAGE, 'errorType': type(error).__name__, 'error': str(error),
                'traceback': traceback.format_exc(), 'completedChecks': CHECKS, 'outputsRetainedWithoutRetryOrOverwrite': True})
        raise
