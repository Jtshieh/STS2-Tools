#!/usr/bin/env python3
"""Prepare one independent native Mac game/profile from user-owned inputs."""
import argparse
import json
from pathlib import Path
import shutil
from common import ROOT, check_game, copy_file, digest, locked, native_mac, ordinary, private_root, write_json

def prepare(a):
    native_mac()
    app = a.game_app.expanduser().absolute()
    p = private_root(a.workspace)
    if p == app or p.is_relative_to(app) or app.is_relative_to(p):
        raise ValueError('Input app must be outside destination private workspace')
    selector = json.loads(ordinary(a.selector).read_text())
    if selector.get('last_profile_id') != a.profile_id:
        raise ValueError('Original profile selector differs from --profile-id; do not synthesize it')
    version = check_game(app)
    files = []
    for f in app.rglob('*'):
        if f.is_symlink(): raise ValueError('App contains symlink: ' + str(f))
        if f.is_file(): files.append(ordinary(f))
    if any('mods' in f.relative_to(app).parts for f in files):
        raise ValueError('Supply a clean app without preinstalled mods')
    if shutil.disk_usage(p).free < sum(f.stat().st_size for f in files) + 2 * 1024**3:
        raise RuntimeError('Insufficient space for independent app and 2 GiB working margin')
    inputs = [('profile.save', a.selector),
              (f'profile{a.profile_id}/saves/progress.save', a.profile / 'progress.save'),
              (f'profile{a.profile_id}/saves/prefs.save', a.profile / 'prefs.save')]
    if a.current_run: inputs.append((f'profile{a.profile_id}/saves/current_run.save', a.current_run))
    if a.settings: ordinary(a.settings)
    for _, f in inputs: ordinary(f)
    with locked(p):
        w = p / 'work'
        if w.exists() or (p / 'config.json').exists():
            raise FileExistsError('Workspace already prepared or incomplete; choose a fresh workspace')
        w.mkdir(); target = w / 'SlayTheSpire2.app'; manifest = []
        try:
            for f in files: manifest.append(copy_file(f, target / f.relative_to(app)))
            # User-supplied Harmony's native helper must stay inside the allowed cwd/tmp.
            # Exact, same-length ASCII path replacement only; no game method bodies.
            harmony = target / 'Contents/Resources/data_sts2_macos_arm64/0Harmony.dll'
            before = b'/tmp/mm-exhelper.dylib.XXXXXX'
            after = b'tmp/mm-exhelper_.dylib.XXXXXX'
            data = harmony.read_bytes()
            if data.count(before) != 1 or len(before) != len(after):
                raise ValueError('Unknown Harmony helper path layout')
            harmony.write_bytes(data.replace(before, after))
            check_game(target, patched=True)
            profile_rows = []
            for relative, source in inputs:
                profile_rows.append(copy_file(source, p / 'profile-input' / relative))
                for base in [w / 'profile/default/1', w / 'profile/default/1/modded']:
                    copy_file(p / 'profile-input' / relative, base / relative)
            if a.settings:
                profile_rows.append(copy_file(a.settings, p / 'profile-input/settings.save'))
                copy_file(a.settings, w / 'profile/default/1/settings.save')
            override = ('[application]\nconfig/use_custom_user_dir=true\n'
                        'config/custom_user_dir_name="profile"\n[display]\n'
                        'window/size/mode=0\nwindow/size/window_width_override=1280\n'
                        'window/size/window_height_override=800\n')
            for folder in [w, target / 'Contents/MacOS', target / 'Contents/Resources']:
                (folder / 'override.cfg').write_text(override)
            (w / 'tmp').mkdir()
            write_json(p / 'game-copy-manifest.json', manifest)
            write_json(p / 'profile-input-manifest.json', profile_rows)
            write_json(p / 'config.json', dict(schema='sts2-mac-workspace-v1', workspace=str(p.parent),
                sourceApp=str(app), sourceProfile=str(a.profile.absolute()), sourceSelector=str(a.selector.absolute()),
                profileId=a.profile_id, start='continue' if a.current_run else 'new', version=version))
        except Exception as e:
            write_json(p / 'prepare-failure.json', dict(error=repr(e), completedCopies=manifest))
            raise
    print('Prepared independent game/profile:', w)
    print('Build next; first launch may ask for original mod-loading consent.')

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--workspace', type=Path, default=ROOT)
    p.add_argument('--game-app', type=Path, required=True)
    p.add_argument('--profile', type=Path, required=True, help='Your selected profileN/saves directory')
    p.add_argument('--selector', type=Path, required=True, help='Your account profile.save')
    p.add_argument('--profile-id', type=int, choices=(1, 2, 3), required=True)
    p.add_argument('--current-run', type=Path, help='Explicit Continue input; omitted for a fresh run')
    p.add_argument('--settings', type=Path, help='Optional existing settings.save, copied unchanged')
    prepare(p.parse_args())

if __name__ == '__main__': main()
