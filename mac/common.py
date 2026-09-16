"""Local-only Mac setup helpers. Game inputs never belong in the source repository."""
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import stat
import subprocess

ROOT = Path(__file__).resolve().parent

def digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''): h.update(block)
    return h.hexdigest()

def ordinary(path):
    p = Path(path).absolute()
    if any(x.is_symlink() for x in (p, *p.parents)):
        raise ValueError('Symlink input refused: ' + str(p))
    s = p.stat()
    if not stat.S_ISREG(s.st_mode) or s.st_nlink != 1:
        raise ValueError('Expected an independent ordinary file: ' + str(p))
    return p

def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')

def private_root(workspace):
    os.umask(0o077)
    workspace = Path(workspace).expanduser().absolute()
    if any(x.is_symlink() for x in (workspace, *workspace.parents)):
        raise ValueError('Workspace must not be a symlink')
    workspace.mkdir(parents=True, exist_ok=True)
    p = workspace / '.private'
    if p.is_symlink(): raise ValueError('Private directory is a symlink')
    p.mkdir(mode=0o700, exist_ok=True)
    p.chmod(0o700)
    return p

@contextlib.contextmanager
def locked(p):
    with (p / 'game.lock').open('a') as f:
        try: fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError: raise RuntimeError('Workspace game/build is already running')
        yield

def native_mac():
    if platform.system() != 'Darwin' or platform.machine() != 'arm64':
        raise RuntimeError('This recorder currently supports native macOS arm64 only')

def check_game(app, patched=False):
    expected = json.loads((ROOT / 'config/game-macos-arm64.json').read_text())
    release = json.loads(ordinary(app / 'Contents/Resources/release_info.json').read_text())
    if any(release.get(k) != expected[k] for k in ('version', 'commit')):
        raise ValueError('Unsupported game version; supply your own matching Mac build. No upgrade performed.')
    checked = {}
    for name, sha in expected['files'].items():
        actual = digest(ordinary(app / name))
        if patched and name.endswith('/0Harmony.dll'):
            sha = expected['patchedHarmonySha256']
        if actual != sha: raise ValueError('Unsupported/modified game input: ' + name)
        checked[name] = actual
    return dict(platform='macos-arm64', version=release['version'], commit=release['commit'],
                referenceBuild=expected['build'], sha256=checked)

def copy_file(source, dest):
    source = ordinary(source)
    dest = Path(dest)
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists() or dest.is_symlink(): raise FileExistsError(dest)
    if shutil.disk_usage(dest.parent).free < source.stat().st_size + 1024**3:
        raise RuntimeError('Insufficient disk space; retain at least 1 GiB free')
    before = digest(source)
    # Streaming copy creates an independent inode; never symlink/hardlink.
    with source.open('rb') as src, dest.open('xb') as out: shutil.copyfileobj(src, out)
    dest.chmod(stat.S_IMODE(source.stat().st_mode) | stat.S_IWUSR)
    if digest(dest) != before or digest(source) != before:
        raise RuntimeError('Copy/source verification failed: ' + str(source))
    return dict(source=str(source), destination=str(dest), bytes=dest.stat().st_size, sha256=before)

def config(workspace):
    p = private_root(workspace)
    c = json.loads((p / 'config.json').read_text())
    if c.get('schema') != 'sts2-mac-workspace-v1' or Path(c['workspace']) != p.parent:
        raise ValueError('Workspace config mismatch')
    return p, c

def sdk_environment(p):
    return dict(PATH='/usr/bin:/bin', LANG='en_US.UTF-8',
        DOTNET_CLI_HOME=str(p / 'build-state'), NUGET_PACKAGES=str(p / 'packages'),
        DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
        DOTNET_GENERATE_ASPNET_CERTIFICATE='false', DOTNET_NOLOGO='1',
        DOTNET_EnableDiagnostics='0', MSBUILDDISABLENODEREUSE='1')
