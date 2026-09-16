"""Reclaim completed ordinary work files; never remove evidence or a baseline."""
import argparse
import fcntl
import json
import os
from pathlib import Path
import shutil
import stat
import time
import zipfile

GIB = 1024 ** 3

def ordinary(path, directory=False):
    path = Path(path).absolute()
    if '..' in path.parts:
        raise ValueError('Parent traversal forbidden')
    for part in [*reversed(path.parents), path]:
        info = part.lstat()
        if stat.S_ISLNK(info.st_mode) or (part != Path('/') and os.path.ismount(part)):
            raise ValueError('Symlink or mount boundary: ' + str(part))
    info = path.lstat()
    if not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode) and info.st_nlink == 1):
        raise ValueError('Ordinary independent material required: ' + str(path))
    return path

def require_space(destination, copy_bytes=0, reserve=GIB):
    destination = Path(destination)
    while not destination.exists():
        destination = destination.parent
    free = shutil.disk_usage(destination).free
    if free < copy_bytes + reserve:
        raise OSError(f'Insufficient disk space: need {copy_bytes + reserve} bytes, available {free}; reclaim completed work explicitly')

def append(path, row):
    ordinary(path.parent, True)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'a') as out:
        if os.fstat(out.fileno()).st_nlink != 1:
            raise ValueError('Linked cleanup journal')
        out.write(json.dumps(dict(time=time.time(), **row)) + '\n')
        out.flush()
        os.fsync(out.fileno())

def inactive(evidence):
    needle = str(evidence)
    for proc in Path('/proc').iterdir():
        if not proc.name.isdigit() or int(proc.name) == os.getpid():
            continue
        try:
            cmd = (proc / 'cmdline').read_bytes().replace(b'\0', b' ').decode(errors='replace')
            if needle in cmd:
                raise RuntimeError('Process references session: ' + proc.name)
            if proc.stat().st_uid != os.getuid():
                continue
            # Non-dumpable OS login/session daemons cannot be inspected without
            # elevation. Match their OS entry points, never a generic Python host.
            daemon = cmd.startswith(('/usr/lib/systemd/systemd --user', '(sd-pam)', 'sshd: '))
            for name in ('maps', 'mountinfo'):
                try:
                    if needle in (proc / name).read_text(errors='replace'):
                        raise RuntimeError('Mapped or mounted session: ' + proc.name)
                except PermissionError:
                    if not daemon:
                        raise RuntimeError('Process use unknown: ' + proc.name)
            try:
                refs = [proc / 'cwd', proc / 'exe', *list((proc / 'fd').iterdir())]
                for ref in refs:
                    try:
                        target = os.readlink(ref)
                        if target == needle or target.startswith(needle + '/'):
                            raise RuntimeError('Open session: ' + proc.name)
                    except FileNotFoundError:
                        pass
            except PermissionError:
                if not daemon:
                    raise RuntimeError('Open files unknown: ' + proc.name)
        except (FileNotFoundError, ProcessLookupError):
            pass

def safe_exit(report):
    if not report.get('finishedUtc') or not report.get('phases'):
        return False
    if any(p.get('remainingLiveOwnedProcesses') != [] for p in report['phases']):
        return False
    if report.get('parentAndNativeGuardUnchanged') is not True:
        return False
    for name in ('userlandHostVerification', 'identityHostVerification'):
        if report.get(name, {}).get('passed') is not True:
            return False
    if report.get('nativeGamePhaseAttempted'):
        for name in ('normalGameImmutableAfter', 'normalHostPayloadAfter'):
            if report.get(name, {}).get('passed') is not True:
                return False
    return True  # A failed gameplay result is eligible only if protection checks passed.

def binary(path):
    return path.suffix.lower() in {'.pck', '.dll', '.pdb', '.so', '.nupkg'} or '.so.' in path.name or path.name == 'crashpad_handler'

def package_cache(path):
    return binary(path) or path.suffix.lower() in {'.xml', '.mibc'}

def walk(root):
    if not root.exists():
        return
    ordinary(root, True)
    for base, dirs, names in os.walk(root, followlinks=False):
        for name in dirs:
            ordinary(Path(base) / name, True)
        for name in names:
            yield ordinary(Path(base) / name)

def remove_duplicate(evidence, path, source, size, journal, category):
    row = dict(path=str(path), bytes=size, retained=source, run=evidence.name,
               category=category, sha256=None, verification='name-size-retained-copy-recipe; not rehashed')
    try:
        # Explicit subtrees and binary filenames only. No profiles, settings,
        # text/JSON, source, diagnostic logs or whole-directory deletion.
        rel = path.relative_to(evidence)
        allowed = (rel.parts[0] == 'game-input' or
                   rel.parts[:2] in (('build-work', 'nuget-packages'), ('build-work', 'publish'), ('runtime-work', 'native-host')) or
                   rel.parts[:3] == ('build-work', 'project', '.godot') or rel.parts[0] == 'readonly')
        payload = binary(path) or (rel.parts[:2] == ('build-work', 'nuget-packages') and package_cache(path))
        if not allowed or not payload:
            raise ValueError('Protected path')
        ordinary(path)
        retained = ordinary(Path(source.split('!')[0]))
        if retained.is_relative_to(evidence) or retained == path:
            raise ValueError('Retained material must be outside this session')
        with retained.open('rb'):
            pass
        before = path.stat()
        if before.st_size != size:
            raise ValueError('Size changed')
        row['allocatedBytes'] = before.st_blocks * 512
        append(journal, dict(row, result='planned'))
        fd = os.open(path.parent, os.O_DIRECTORY | os.O_NOFOLLOW)
        mode = stat.S_IMODE(os.fstat(fd).st_mode)
        try:
            now = os.stat(path.name, dir_fd=fd, follow_symlinks=False)
            if (now.st_ino, now.st_size, now.st_mtime_ns) != (before.st_ino, before.st_size, before.st_mtime_ns):
                raise ValueError('File changed before unlink')
            os.fchmod(fd, mode | 0o200)
            os.unlink(path.name, dir_fd=fd)
        finally:
            os.fchmod(fd, mode)
            os.close(fd)
        append(journal, dict(row, result='deleted'))
        return size
    except Exception as error:
        append(journal, dict(row, result='retained-error', error=repr(error)))
        return 0

def reclaim(cfg, evidence):
    private = ordinary(Path(cfg['workspace']) / '.private', True)
    if stat.S_IMODE(private.stat().st_mode) != 0o700:
        raise ValueError('Private directory must be 0700')
    evidence = Path(evidence).absolute()
    if evidence.parent != private / 'evidence':
        raise ValueError('Not a direct evidence session')
    ordinary(evidence, True)
    journal = evidence / 'work-materials-cleanup.jsonl'
    try:
        inactive(evidence)
        report = json.loads(ordinary(evidence / 'supervisor-report.json').read_text())
        if not safe_exit(report):
            raise ValueError('Exit/protection checks incomplete or failed; preserve for diagnosis')
        game = ordinary(Path(cfg['game']), True)
        if game.is_relative_to(evidence):
            raise ValueError('Baseline inside session')
        candidates = []
        manifest = evidence / 'normal-game-input-copy-manifest.json'
        if manifest.exists():
            copies = json.loads(ordinary(manifest).read_text())
            for row in copies['gameFiles']:
                path = Path(row['destination'])
                rel = path.relative_to(evidence / 'game-input')
                source = ordinary(game / rel)
                if source.stat().st_size != row['bytes']:
                    raise ValueError('Retained game size mismatch')
                if binary(path) and path.exists():
                    candidates.append((path, str(source), row['bytes'], 'game-input'))
            for row in copies.get('nativeFallbackCopies', []):
                path = Path(row['destination'])
                rel = path.relative_to(evidence / 'runtime-work/native-host')
                source = ordinary(game / rel)
                if source.stat().st_size != row['bytes']:
                    raise ValueError('Retained native library size mismatch')
                if binary(path) and path.exists():
                    candidates.append((path, str(source), row['bytes'], 'runtime-work'))
        packages = list(Path(cfg['runtime_feed']).glob('*.nupkg'))
        packages += list((Path(cfg['godot_root']) / 'GodotSharp/Tools/nupkgs').glob('*.nupkg'))
        index = {}
        for package in packages:
            ordinary(package)
            if package.is_relative_to(evidence):
                raise ValueError('Dependency source inside session')
            index[(package.name.lower(), package.stat().st_size)] = str(package)
            with zipfile.ZipFile(package) as archive:
                for info in archive.infolist():
                    if package_cache(Path(info.filename)) and not info.is_dir():
                        index[(Path(info.filename).name.lower(), info.file_size)] = str(package) + '!' + info.filename
        for part in ('build-work/nuget-packages', 'build-work/publish', 'build-work/project/.godot', 'runtime-work/native-host', 'readonly'):
            for path in walk(evidence / part):
                key = (path.name.lower(), path.stat().st_size)
                eligible = package_cache(path) if part == 'build-work/nuget-packages' else binary(path)
                if eligible and key in index:
                    candidates.append((path, index[key], key[1], part.split('/')[0]))
        inactive(evidence)
        total = sum(remove_duplicate(evidence, p, src, size, journal, kind) for p, src, size, kind in candidates)
        append(journal, dict(result='completed', reclaimedBytes=total,
               state='evidence retained; run copies reclaimed; rebuild fresh independent copies to run again'))
        return total
    except Exception as error:
        append(journal, dict(result='retained', error=repr(error)))
        print('Work materials retained: ' + str(error), flush=True)
        return 0

def finish_materials(cfg, private, before):
    for evidence in sorted(set((private / 'evidence').glob('vm-*')) - before):
        try:
            reclaim(cfg, evidence)
        except Exception as error:
            # Cleanup must not rewrite the completed game/build outcome.
            print('Work materials retained: ' + str(error), flush=True)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    from runtime import configured
    cfg, root, private = configured(args.config)
    with (private / 'runtime.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        total = sum(reclaim(cfg, p) for p in sorted((private / 'evidence').glob('vm-*')))
    print(json.dumps(dict(reclaimedBytes=total)))

if __name__ == '__main__':
    main()
