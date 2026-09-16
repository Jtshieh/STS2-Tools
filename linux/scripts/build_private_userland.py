#!/usr/bin/env python3
"""Materialize reviewed public Ubuntu archives as ordinary files, without install.

Runs on the local Linux host. Never executes archive contents.
zstd is an explicitly supplied decoder; archives and excluded members are recorded.
"""
from __future__ import annotations
import argparse
import datetime
import hashlib
import io
import json
import os
import platform
from pathlib import Path, PurePosixPath
import re
import resource
import signal
import stat
import subprocess
import tarfile
import time
from work_materials import require_space

LIMIT = 512 * 1024**2
TOP = {'bin': 'usr/bin', 'sbin': 'usr/sbin', 'lib': 'usr/lib', 'lib64': 'usr/lib64', 'lib32': 'usr/lib32', 'libx32': 'usr/libx32'}

def sha(data):
    return hashlib.sha256(data).hexdigest()

def write_json(path, data):
    with path.open('x') as stream:
        json.dump(data, stream, indent=2, sort_keys=True)
        stream.write('\n')

def norm(name, base=''):
    if '\x00' in name or '\\' in name:
        raise ValueError('Bad virtual name')
    parts = [] if name.startswith('/') else base.split('/') if base else []
    for part in name.split('/'):
        if part in ('', '.'):
            continue
        if part == '..':
            if not parts:
                raise ValueError('Virtual root escape')
            parts.pop()
        else:
            parts.append(part)
    return '/'.join(parts)

def canon(name):
    name = norm(name)
    head, sep, tail = name.partition('/')
    return TOP[head] + ('/' + tail if sep else '') if head in TOP else name

def ordinary(path, directory=False):
    info = path.lstat()
    assert info.st_uid == os.getuid()
    assert stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode) and info.st_nlink == 1
    return info

def decode(data, label, q, decoder):
    if label.endswith('.zst'):
        src = q / (label + '.input')
        dst = q / (label + '.decoded.tar')
        with src.open('xb') as stream:
            stream.write(data)
        src.chmod(0o400)
        def limits():
            resource.setrlimit(resource.RLIMIT_CPU, (15, 15))
            resource.setrlimit(resource.RLIMIT_FSIZE, (128 * 1024**2, 128 * 1024**2))
            if platform.system() == 'Linux':
                resource.setrlimit(resource.RLIMIT_AS, (512 * 1024**2, 512 * 1024**2))
            resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        start = time.monotonic()
        with dst.open('xb') as out, (q / (label + '.stderr')).open('xb') as err:
            process = subprocess.Popen([decoder, '-dc', str(src)], stdin=subprocess.DEVNULL,
                stdout=out, stderr=err, cwd=q, env={'PATH': '/usr/bin:/bin', 'LC_ALL': 'C'},
                start_new_session=True, preexec_fn=limits)
            try:
                code = process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()
                code = 'timeout'
        write_json(q / (label + '.result.json'), {'argv': [decoder, '-dc', str(src)], 'pid': process.pid,
            'returncode': code, 'elapsedSeconds': time.monotonic() - start, 'inputSha256': sha(data)})
        assert code == 0 and dst.stat().st_size < 128 * 1024**2, (label, code)
        dst.chmod(0o400)
        return tarfile.open(fileobj=io.BytesIO(dst.read_bytes()), mode='r:')
    return tarfile.open(fileobj=io.BytesIO(data), mode='r:*')

def ar(data):
    assert data[:8] == b'!<arch>\n'
    result = {}
    off = 8
    while off < len(data):
        header = data[off:off+60]
        assert header[58:60] == b'`\n'
        name = header[:16].decode('ascii').strip().rstrip('/')
        size = int(header[48:58])
        off += 60
        assert size >= 0 and off + size <= len(data) and name not in result
        assert name == 'debian-binary' or re.fullmatch(r'(control|data)\.tar\.(xz|zst)', name), name
        result[name] = data[off:off+size]
        off += size + size % 2
    assert off == len(data) and result['debian-binary'] == b'2.0\n' and len(result) == 3
    return result

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--inputs', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--zstd', required=True)
    args = parser.parse_args()
    args.host_mode = 'local-linux'
    if platform.system() != 'Linux': raise ValueError('Linux required')
    os.umask(0o077)
    for path in [args.inputs, args.output.parent]:
        assert path.is_absolute() and '.private' in path.parts and '..' not in path.parts
        ordinary(path, True)
        assert all(not parent.is_symlink() for parent in [path, *path.parents])
    assert stat.S_IMODE(Path(*args.output.parts[:args.output.parts.index('.private')+1]).stat().st_mode) == 0o700
    assert args.output.is_absolute() and not args.output.exists()
    decoder_path = Path(args.zstd)
    assert decoder_path.is_file() and not decoder_path.stat().st_mode & 0o002
    require_space(args.output.parent, 2 * LIMIT)
    args.output.mkdir(mode=0o700)
    q = args.output
    decoded = q / 'decoded'
    controls = q / 'package-controls'
    decoded.mkdir(mode=0o700)
    controls.mkdir(mode=0o700)
    manifest_path = args.inputs / 'archive-manifest.json'
    ordinary(manifest_path)
    manifest = json.loads(manifest_path.read_text())
    assert len(manifest['archives']) == 18 and sum(row['bytes'] for row in manifest['archives']) < 160 * 1024**2
    vfs = {}
    index = []
    overrides = []
    raw_total = 0
    source_metadata = []
    for row in manifest['archives']:
        rel = PurePosixPath(row['path'])
        assert not rel.is_absolute() and '..' not in rel.parts and len(rel.parts) == 1
        path = args.inputs / str(rel)
        before = ordinary(path)
        data = path.read_bytes()
        assert len(data) == row['bytes'] and sha(data) == row['sha256']
        source_metadata.append({'path': str(path), 'bytes': len(data), 'sha256': row['sha256'],
            'device': before.st_dev, 'inode': before.st_ino, 'mtimeNs': before.st_mtime_ns, 'ctimeNs': before.st_ctime_ns})
        if path.name.endswith('.deb'):
            members = ar(data)
            control_name = next(name for name in members if name.startswith('control.tar.'))
            with decode(members[control_name], row['package'] + '-' + control_name, decoded, args.zstd) as archive:
                out = controls / row['package']
                out.mkdir(mode=0o700)
                for member in archive:
                    if member.isdir():
                        assert norm(member.name) == ''
                        continue
                    assert member.isfile() and len(PurePosixPath(norm(member.name)).parts) == 1 and member.size < 1024**2
                    body = archive.extractfile(member).read()
                    target = out / norm(member.name)
                    with target.open('xb') as stream:
                        stream.write(body)
                    target.chmod(0o400)
            data_name = next(name for name in members if name.startswith('data.tar.'))
            archive = decode(members[data_name], row['package'] + '-' + data_name, decoded, args.zstd)
        else:
            assert path.name == 'ubuntu-base-22.04.5-base-amd64.tar.gz'
            archive = tarfile.open(fileobj=io.BytesIO(data), mode='r:gz')
        with archive:
            for member in archive:
                name = canon(member.name)
                if not name:
                    assert member.isdir()
                    continue
                assert not member.name.startswith('/') and '..' not in PurePosixPath(member.name).parts
                assert member.isfile() or member.isdir() or member.issym() or member.islnk(), member.name
                record = {'path': name, 'originalPath': member.name, 'source': path.name,
                    'kind': 'file' if member.isfile() else 'dir' if member.isdir() else 'symlink' if member.issym() else 'hardlink',
                    'bytes': member.size, 'archiveMode': member.mode, 'linkname': member.linkname}
                index.append(record)
                if norm(member.name) in TOP:
                    if member.issym():
                        assert norm(member.linkname) == TOP[norm(member.name)], record
                        continue
                    assert member.isdir(), record
                if member.isfile():
                    raw_total += member.size
                    assert raw_total <= LIMIT and member.size <= 64 * 1024**2
                    body = archive.extractfile(member).read()
                    assert len(body) == member.size
                    record['sha256'] = sha(body)
                    record['data'] = body
                elif member.issym():
                    parent = str(PurePosixPath(name).parent)
                    record['target'] = canon(norm(member.linkname, '' if parent == '.' else parent))
                elif member.islnk():
                    record['target'] = canon(member.linkname)
                if name in vfs:
                    old = vfs[name]
                    if old['kind'] == record['kind'] == 'dir':
                        continue
                    overrides.append({'path': name, 'fromSource': old['source'], 'toSource': record['source'],
                        'fromKind': old['kind'], 'toKind': record['kind'], 'fromSha256': old.get('sha256'), 'toSha256': record.get('sha256')})
                vfs[name] = record
        after = path.stat()
        assert all(getattr(before, key) == getattr(after, key) for key in ['st_dev', 'st_ino', 'st_size', 'st_mtime_ns', 'st_ctime_ns'])
    def public(record):
        return {key: value for key, value in record.items() if key != 'data'}
    write_json(q / 'virtual-member-index.json', [public(record) for record in index])
    write_json(q / 'virtual-overrides.json', overrides)

    def resolve(name, trail=()):
        name = canon(name)
        assert name not in trail and len(trail) <= 40, ('cycle', name)
        parts = name.split('/')
        for i in range(1, len(parts)+1):
            prefix = '/'.join(parts[:i])
            item = vfs.get(prefix)
            if item and item['kind'] in ('symlink', 'hardlink'):
                tail = '/'.join(parts[i:])
                return resolve(item['target'] + ('/' + tail if tail else ''), trail + (name,))
        if name not in vfs:
            raise FileNotFoundError(name)
        return name, vfs[name]

    excluded = []
    root = q / 'root'
    root.mkdir(mode=0o700)
    files = []
    written = set()
    total = 0
    def emit(dest, source, trail=()):
        nonlocal total
        assert dest == norm(dest) and (dest == 'usr' or dest.startswith('usr/') or dest == 'etc' or dest.startswith('etc/'))
        if dest in written:
            return
        if dest in {'etc/ld.so.cache', 'etc/passwd', 'etc/group', 'etc/nsswitch.conf', 'etc/hosts', 'etc/resolv.conf', 'etc/shadow', 'etc/gshadow', 'etc/machine-id', 'etc/mtab'}:
            excluded.append({'path': dest, 'reason': 'runtime_uses_explicit_private_projection_or_no_cache'})
            return
        try:
            actual, item = resolve(source)
        except FileNotFoundError as exc:
            if dest.startswith('usr/share/doc/') or re.fullmatch(r'usr/share/locale/[^/]+/LC_TIME/coreutils\.mo', dest):
                excluded.append({'path': dest, 'reason': 'published_dangling_nonruntime_link', 'target': str(exc)})
                return
            raise
        assert (dest, actual) not in trail and len(trail) < 40
        path = root / dest
        path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        if item['kind'] == 'dir':
            path.mkdir(mode=0o700, exist_ok=True)
            written.add(dest)
            children = sorted(n for n in vfs if n.startswith(actual + '/') and '/' not in n[len(actual)+1:])
            for child in children:
                emit(dest + '/' + child.rsplit('/', 1)[-1], child, trail + ((dest, actual),))
        else:
            assert item['kind'] == 'file'
            total += len(item['data'])
            assert total <= LIMIT
            with path.open('xb') as stream:
                stream.write(item['data'])
            mode = 0o500 if item['archiveMode'] & 0o111 else 0o400
            path.chmod(mode)
            info = ordinary(path)
            files.append({'path': dest, 'bytes': info.st_size, 'sha256': item['sha256'], 'mode': mode,
                'archiveSource': item['source'], 'resolvedVirtualPath': actual, 'materializedAlias': dest != actual,
                'strippedSpecialBits': item['archiveMode'] & 0o6000, 'nlink': info.st_nlink})
            written.add(dest)
    for top in ['usr', 'etc']:
        emit(top, top)
    required = ['usr/bin/true', 'usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2',
        'usr/lib64/ld-linux-x86-64.so.2', 'usr/lib/x86_64-linux-gnu/libc.so.6',
        'usr/lib/x86_64-linux-gnu/libicuuc.so.70', 'usr/lib/x86_64-linux-gnu/libicui18n.so.70',
        'usr/lib/x86_64-linux-gnu/libcurl.so.4', 'usr/lib/x86_64-linux-gnu/libfontconfig.so.1',
        'usr/lib/locale/C.utf8/LC_CTYPE', 'etc/fonts/fonts.conf', 'etc/os-release']
    for name in required:
        ordinary(root / name)
    for path in root.rglob('*'):
        ordinary(path, path.is_dir())
    for path in sorted((p for p in root.rglob('*') if p.is_dir()), key=lambda p:len(p.parts), reverse=True):
        path.chmod(0o500)
    root.chmod(0o500)
    write_json(q / 'runtime-manifest.json', {'schema': 'e004b-private-userland-v1', 'root': str(root),
        'createdUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'files': files,
        'fileCount': len(files), 'bytes': total, 'ordinaryFilesOnly': True, 'topAliases': TOP,
        'archives': manifest['archives'], 'sourceMetadata': source_metadata, 'excluded': excluded,
        'archiveOverrides': overrides, 'baseVersion': '22.04.5', 'glibcPackageVersion': '2.35-0ubuntu3.8',
        'requiredPaths': required, 'maintainerScriptsExecuted': False, 'gameExecuted': False,
        'preparationHostMode': args.host_mode, 'preparationPlatform': platform.system(),
        'decoder': args.zstd, 'archiveManifestSha256': sha(manifest_path.read_bytes())})
    print(json.dumps({'prepared': True, 'root': str(root), 'files': len(files), 'bytes': total,
        'exclusions': len(excluded), 'runtimeManifestSha256': sha((q/'runtime-manifest.json').read_bytes()), 'gameExecuted': False}))

if __name__ == '__main__':
    main()
