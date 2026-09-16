#!/usr/bin/env python3
"""Prepare private local inputs. No downloads, installation or game execution."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import stat
import struct
import subprocess
import sys

from pe_metadata import Metadata
from work_materials import require_space

REPO = Path(__file__).resolve().parents[1]

def sha(path):
    with path.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()

def ordinary(path, directory=False):
    path = path.absolute()
    if '..' in path.parts or any(p.is_symlink() for p in [path, *path.parents]):
        raise ValueError(f'Symlink/traversal forbidden: {path}')
    st = path.stat()
    if not (stat.S_ISDIR(st.st_mode) if directory else stat.S_ISREG(st.st_mode) and st.st_nlink == 1):
        raise ValueError(f'Ordinary independent input required: {path}')
    return path

def write(path, obj):
    with path.open('x') as f:
        json.dump(obj, f, indent=2)
        f.write('\n')

def copy(src, dst, mode=0o400):
    ordinary(src)
    before = src.stat()
    require_space(dst.parent, before.st_size)
    dst.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with src.open('rb') as a, dst.open('xb') as b:
        shutil.copyfileobj(a, b, 1024 * 1024)
    dst.chmod(mode)
    after = src.stat()
    if (before.st_ino, before.st_size, before.st_mtime_ns) != (after.st_ino, after.st_size, after.st_mtime_ns) or sha(src) != sha(dst):
        raise ValueError(f'Input changed during copy: {src}')
    ordinary(dst)
    return dict(source=str(src), destination=str(dst), sha256=sha(dst), bytes=dst.stat().st_size)

def project_binary(pack):
    # Fixed Godot PCK v3 table; extract only project.binary, never executable code.
    with pack.open('rb') as f:
        h = f.read(40)
        if h[:4] != b'GDPC' or struct.unpack_from('<I', h, 4)[0] != 3:
            raise ValueError('Expected compatible PCK v3')
        base, directory = struct.unpack_from('<QQ', h, 24)
        f.seek(directory)
        count, = struct.unpack('<I', f.read(4))
        if not 1 <= count <= 100000:
            raise ValueError('PCK table bound exceeded')
        found = []
        for _ in range(count):
            length, = struct.unpack('<I', f.read(4))
            if not 1 <= length <= 4096:
                raise ValueError('PCK path bound exceeded')
            name = f.read(length).rstrip(b'\0')
            offset, size = struct.unpack('<QQ', f.read(16))
            md5 = f.read(16)
            flags, = struct.unpack('<I', f.read(4))
            if name in (b'project.binary', b'res://project.binary'):
                found.append((base + offset, size, md5, flags))
        if len(found) != 1 or found[0][1] != 24693 or found[0][3] != 0:
            raise ValueError('Expected one unencrypted fixed project.binary')
        offset, size, md5, _ = found[0]
        f.seek(offset)
        raw = f.read(size)
        if hashlib.md5(raw).digest() != md5:
            raise ValueError('PCK member digest mismatch')
        return raw

def metadata_inputs(game, out, compat):
    m = Metadata(game / 'data_sts2_linuxbsd_x86_64/sts2.dll')
    scripts = {}
    for i in range(1, m.counts.get(12, 0) + 1):
        parent, ctor, blob = m.row(12, i)
        t, r = m.coded('CustomAttributeType', ctor)
        if m.ref(t << 24 | r).get('owner') == 'Godot.ScriptPathAttribute':
            t, r = m.coded('HasCustomAttribute', parent)
            raw = m.blob(blob)
            n, p = m.compressed(raw, 2)
            scripts[m.type_name(t, r)] = raw[p:p+n].decode()
    write(out / 'expected-script-map.json', dict(schema='e004b-original-script-map-v1', gameSha256=m.sha256, scripts=scripts))
    if sha(out / 'expected-script-map.json') != 'd7d55364f4b4e442267953eb89d5ce2e9bcacde478058f98dfb99777f15c858c':
        raise ValueError('Script metadata differs from supported build')
    raw = project_binary(game / 'SlayTheSpire2.pck')
    if hashlib.sha256(raw).hexdigest() != compat['projectBinary']['sha256']:
        raise ValueError('Project configuration differs from supported build')
    (out / 'original-project.binary').write_bytes(raw)
    assemblies, native = {}, {}
    for row in compat['files']:
        name = row['path']
        pinned = {k: row[k] for k in ('bytes', 'sha256')}
        pinned['path'] = '/game/' + name
        if name.endswith('.dll') and Path(name).stem != 'GodotSharp':
            md = Metadata(game / name)
            a = md.row(32, 1)
            key = md.blob(a[6])
            token = hashlib.sha1(key).digest()[-8:][::-1].hex() if key else 'null'
            full = f"{md.s(a[7])}, Version={'.'.join(map(str, a[1:5]))}, Culture={md.s(a[8]) or 'neutral'}, PublicKeyToken={token}"
            assemblies[Path(name).stem] = dict(pinned, fullName=full)
        if name.endswith('.so') or '.so.' in name or name == 'crashpad_handler':
            native[pinned['path']] = pinned
            if '/' not in name:
                extra = dict(pinned, path='/work/native-host/' + name)
                native[extra['path']] = extra
    write(out / 'game-assemblies-pinned.json', assemblies)
    write(out / 'native-libraries-pinned.json', dict(nativeLibraryAliases={}, allowedNativeFiles=native))

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--workspace', type=Path, required=True, help='Existing ordinary directory; inputs/output under its .private')
    p.add_argument('--game', type=Path, required=True)
    p.add_argument('--readonly-game', action='store_true', help='Use an already immutable ordinary game baseline without another baseline copy')
    p.add_argument('--profile', type=Path, required=True, help='Directory containing original prefs.save and progress.save')
    p.add_argument('--selector', type=Path, required=True, help='Original account profile.save; selected id must match --profile-id')
    p.add_argument('--profile-id', type=int, choices=[1, 2, 3], required=True)
    p.add_argument('--current-run', type=Path, help='Explicit archived current_run.save for Continue; otherwise start a new run')
    p.add_argument('--dotnet-root', type=Path, required=True)
    p.add_argument('--godot-root', type=Path, required=True)
    p.add_argument('--runtime-feed', type=Path, required=True)
    p.add_argument('--userland-root', type=Path, required=True)
    p.add_argument('--userland-manifest', type=Path, required=True)
    a = p.parse_args()
    if platform.system() != 'Linux' or platform.machine() != 'x86_64':
        p.error('Only Linux x86_64 is supported by this host')
    os.umask(0o077)
    root = ordinary(a.workspace, True)
    private = root / '.private'
    private.mkdir(mode=0o700, exist_ok=True)
    ordinary(private, True)
    if stat.S_IMODE(private.stat().st_mode) != 0o700:
        raise ValueError('.private must have mode 0700')
    out = private / 'prepared'
    out.mkdir(mode=0o700)  # Never overwrite a preparation/failure.
    try:
        compat = json.loads((REPO / 'config/game-linux.json').read_text())
        game = ordinary(a.game, True)
        release = json.loads((game / 'release_info.json').read_text())
        if (release.get('version'), release.get('commit')) != (compat['release'], compat['commit']):
            raise ValueError(f"Version mismatch: actual {release}; expected {compat['release']}/{compat['commit']} build {compat['sourceBuild']}. Supply that legal build; no upgrade is performed.")
        rows = [compat['executable'], *compat['files']]
        records = []
        for row in rows:
            path = ordinary(game / row['path'])
            if path.stat().st_size != row['bytes'] or sha(path) != row['sha256']:
                raise ValueError('Fixed Linux build file mismatch: ' + row['path'])
            records.append(dict(path=str(path), **{k: row[k] for k in ('bytes', 'sha256')}))
        if a.readonly_game:
            if any((game / row['path']).stat().st_mode & 0o222 for row in rows):
                raise ValueError('--readonly-game requires immutable input files')
        else:
            baseline = private / 'game-baseline'
            baseline.mkdir(mode=0o700)
            for row in rows:
                copy(game / row['path'], baseline / row['path'])
            for directory in sorted((x for x in baseline.rglob('*') if x.is_dir()), reverse=True):
                directory.chmod(0o500)
            baseline.chmod(0o500)
            game = baseline
        for source in sorted((REPO / 'src/host').iterdir()):
            if source.is_file():
                copy(source, out / source.name)
        copy(game / 'SlayTheSpire2', private / 'executable-input/SlayTheSpire2')
        metadata_inputs(game, out, compat)
        selector = json.loads(ordinary(a.selector).read_text())
        if selector.get('last_profile_id') != a.profile_id or selector.get('schema_version') != 2:
            raise ValueError('Supply the original selector for the requested profile; no selector rewrite')
        profile_rows = []
        inputs = [('profile.save', a.selector, 'profile.save'), ('prefs.save', a.profile / 'prefs.save', f'profile{a.profile_id}/saves/prefs.save'), ('progress.save', a.profile / 'progress.save', f'profile{a.profile_id}/saves/progress.save')]
        if a.current_run:
            inputs.append(('current_run.save', a.current_run, f'profile{a.profile_id}/saves/current_run.save'))
        for name, source, relative in inputs:
            record = copy(source.absolute(), out / name)
            profile_rows.append(dict(name=name, originalRelativePath=relative, sha256=record['sha256'], bytes=record['bytes']))
            records.append(record)
        contract = dict(compat, schema='e004b-normal-boot-game-inputs-v1', profileId=a.profile_id, profileInputs=profile_rows)
        write(out / 'game-input-contract.json', contract)
        guard = private / 'guard-build'
        guard.mkdir(mode=0o700)
        guard_records = [copy(REPO / 'src/guard' / name, guard / name, 0o600) for name in ('early_guard.c', 'accepted_filter.h', 'e004b_common.h')]
        # gcc is explicitly local; build sees only compiler runtime and new guard directory.
        flags = ['-m64', '-std=c11', '-O2', '-fno-lto', '-fno-builtin', '-march=x86-64', '-mtune=generic', '-Wall', '-Wextra', '-Werror', '-Wconversion', '-Wshadow', '-Wformat=2', '-Wstrict-prototypes', '-fstack-protector-strong', '-D_GNU_SOURCE=1', '-fPIC', '-fvisibility=hidden', '-shared', '-nostdlib', '-Wl,-z,defs', '-Wl,--no-undefined', '-Wl,-z,relro', '-Wl,-z,now', '-Wl,-z,noexecstack', '-Wl,--build-id=sha1', '-Wl,-soname,libE004EarlyGuard.so', '-Wl,--no-as-needed', '/work/early_guard.c', '-lc', '-o', '/work/guard.so']
        cmd = ['/usr/bin/bwrap', '--unshare-all', '--die-with-parent', '--ro-bind', '/usr', '/usr', '--symlink', 'usr/lib', '/lib', '--symlink', 'usr/lib64', '/lib64', '--symlink', 'usr/bin', '/bin', '--proc', '/proc', '--dev', '/dev', '--bind', str(guard), '/work', '--tmpfs', '/tmp', '--chdir', '/work', '--', '/usr/bin/gcc', *flags]
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
        write(guard / 'build.json', dict(argv=cmd, returncode=result.returncode, stdout=result.stdout, stderr=result.stderr))
        if result.returncode or sha(guard / 'guard.so') != 'e6a4317547a27b6a5733e0c1825f02ad12a19ddf1d2d4974a2a7ab74bd016902':
            raise ValueError('Guard build/hash differs; compiler output needs review, never silently relax identity')
        write(out / 'guard-source-manifest.json', dict(records=[dict(path=Path(x['source']).name, sha256=x['sha256']) for x in guard_records]))
        config = dict(workspace=str(root), source=str(out), game=str(game), start='continue' if a.current_run else 'new', profileId=a.profile_id)
        for name in ('dotnet_root', 'godot_root', 'runtime_feed', 'userland_root', 'userland_manifest'):
            config[name] = str(ordinary(getattr(a, name), name != 'userland_manifest'))
        local_feed = private / 'runtime-feed'
        local_feed.mkdir(mode=0o700)
        for name in ('microsoft.netcore.app.runtime.linux-x64.9.0.7.nupkg', 'microsoft.aspnetcore.app.runtime.linux-x64.9.0.7.nupkg'):
            copy(Path(config['runtime_feed']) / name, local_feed / name)
        config['runtime_feed'] = str(local_feed)
        write(private / 'config.json', config)
        write(out / 'preparation.json', dict(platform=platform.system(), architecture=platform.machine(), version=release, build=compat['sourceBuild'], inputs=records, gameExecuted=False))
        print(json.dumps(dict(prepared=True, config=str(private / 'config.json'))))
    except BaseException as e:
        write(out / 'failure.json', dict(error=repr(e)))
        raise

if __name__ == '__main__':
    main()
