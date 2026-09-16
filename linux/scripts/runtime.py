"""Configuration and ordinary source staging shared by build/run entry points."""
import hashlib
import json
import os
from pathlib import Path
import sys
import uuid
from prepare import ordinary, copy
from work_materials import require_space

REPO = Path(__file__).resolve().parents[1]

def configured(path):
    cfg = json.loads(ordinary(path).read_text())
    root = ordinary(Path(cfg['workspace']), True)
    private = root / '.private'
    ordinary(private, True)
    if private.stat().st_mode & 0o777 != 0o700:
        raise ValueError('.private mode must be 0700')
    return cfg, root, private

def stage(cfg, private):
    # One ordinary game copy, build/runtime outputs and a 1 GiB safety reserve.
    require_space(private, 3 * 1024 ** 3)
    source = private / ('source-' + uuid.uuid4().hex[:12])
    source.mkdir(mode=0o700)
    names = ['original-project.binary', 'expected-script-map.json', 'game-assemblies-pinned.json', 'native-libraries-pinned.json', 'game-input-contract.json', 'guard-source-manifest.json', 'profile.save', 'prefs.save', 'progress.save']
    if cfg['start'] == 'continue':
        names.append('current_run.save')
    for name in names:
        copy(Path(cfg['source']) / name, source / name)
    for p in (REPO / 'src/host').iterdir():
        if p.is_file():
            copy(p, source / p.name)
    return source

def command(cfg, root, source, seed, root_run, decisions, tutorials, build_only=False):
    manifest = Path(cfg['userland_manifest'])
    argv = [sys.executable, '-B', str(source / 'run-normal-boot.py'), '--target-root', str(root), '--source-dir', str(source), '--game-baseline', cfg['game'], '--dotnet-root', cfg['dotnet_root'], '--godot-root', cfg['godot_root'], '--runtime-feed', cfg['runtime_feed'], '--userland-root', cfg['userland_root'], '--userland-manifest', str(manifest), '--userland-manifest-sha256', hashlib.sha256(manifest.read_bytes()).hexdigest(), '--executable-working-copy', str(root / '.private/executable-input/SlayTheSpire2'), '--executable-sha256', '0b0ae3859c4c26d352b6483dc1cb11601a07bbc9890d97bcebae0d5d0bdd99ff', '--seed', seed, '--root-run-id', root_run, '--decision-limit', str(decisions), '--tutorials', tutorials, '--start', cfg['start']]
    if build_only:
        argv.append('--build-only')
    return argv
