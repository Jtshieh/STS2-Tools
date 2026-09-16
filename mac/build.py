#!/usr/bin/env python3
"""Offline build and deploy only into this recorder's independent app."""
import argparse
import datetime
from pathlib import Path
import shutil
import subprocess
import zipfile
from common import ROOT, check_game, config, digest, locked, native_mac, private_root, sdk_environment, write_json

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--workspace', type=Path, default=ROOT)
    p.add_argument('--dotnet', type=Path, required=True, help='Native arm64 .NET 9 SDK executable')
    p.add_argument('--game-app', type=Path, help='Read-only clean game app; build a drop-in ZIP without deploying')
    a = p.parse_args(); native_mac()
    private = private_root(a.workspace) if a.game_app else config(a.workspace)[0]
    with locked(private):
        app = a.game_app.expanduser().absolute() if a.game_app else private / 'work/SlayTheSpire2.app'
        check_game(app, patched=not bool(a.game_app))
        sdk = a.dotnet.expanduser().resolve()
        arch = subprocess.check_output(['/usr/bin/file', str(sdk)], text=True)
        if 'arm64' not in arch: raise ValueError('SDK must support native arm64')
        env = sdk_environment(private)
        version = subprocess.check_output([str(sdk), '--version'], env=env, text=True).strip()
        if not version.startswith('9.'): raise ValueError('Expected .NET SDK 9.x')
        build = private / 'build'; build.mkdir(exist_ok=True)
        for f in (ROOT / 'src').iterdir():
            if f.suffix in ('.cs', '.csproj') or f.name == 'NuGet.Config':
                shutil.copyfile(f, build / f.name)
        logs = private / 'build-logs'; logs.mkdir(exist_ok=True)
        log = logs / (datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.log')
        cmd = [str(sdk), 'build', str(build / 'Recorder.csproj'), '-c', 'Release', '--nologo',
               '-v:minimal', '-p:NuGetAudit=false', '-p:DebugType=None', '-p:DebugSymbols=false',
               '-p:Deterministic=true', '-p:GameData=' + str(app / 'Contents/Resources/data_sts2_macos_arm64')]
        with log.open('w') as out: r = subprocess.run(cmd, env=env, cwd=build, stdout=out, stderr=subprocess.STDOUT)
        print(log.read_text())
        if r.returncode: raise SystemExit(r.returncode)
        mods = private / 'dist/Sts2Recorder'; mods.mkdir(parents=True, exist_ok=True)
        dll = build / 'bin/Release/net9.0/Sts2Recorder.dll'
        shutil.copyfile(dll, mods / dll.name)
        write_json(mods / 'Sts2Recorder.json', dict(id='Sts2Recorder', name='STS2 Passive Recorder',
            author='Jtshieh; hooks derived from boardengineer/RunReplays (MIT)', version='0.2.3',
            description='Passive semantic recorder; no automated decisions.', has_pck=False,
            has_dll=True, affects_gameplay=False, dependencies=[]))
        write_json(private / 'build.json', dict(sdkVersion=version, recorderSha256=digest(dll),
            sourceSha256={f.name: digest(f) for f in (ROOT / 'src').glob('*.cs')}))
        shutil.copyfile(ROOT.parent / 'LICENSE', mods / 'LICENSE.txt')
        shutil.copyfile(ROOT / 'src/RunReplays-LICENSE', mods / 'RunReplays-LICENSE.txt')
        shutil.copyfile(ROOT.parent / 'linux/LICENSES/divine-sts2.txt', mods / 'divine-sts2-LICENSE.txt')
        shutil.copyfile(ROOT.parent / 'NOTICE.md', mods / 'NOTICE.md')
        (mods / 'README.txt').write_text(
            'STS2 passive recorder, macOS arm64 v0.111.0 / 41cef1ea only.\n'
            'Put Sts2Recorder.dll and Sts2Recorder.json in the game mods directory\n'
            '(SlayTheSpire2.app/Contents/MacOS/mods). Keep other mods disabled.\n'
            'Start the game normally and accept its original mod prompt if shown.\n'
            'RECORDER READY displays the log directory; recording starts automatically.\n'
            'Logs: user://sts2-recorder/logs/<process-id>/actions.jsonl.\n'
            'Drop-in mode uses the game\'s existing modded profile and saves.\n'
            'No progression is copied or unlocked by this mod. Full-run fidelity and SL unverified.\n'
            'Source and optional isolated launcher: https://github.com/Jtshieh/STS2-Tools\n')
        archive = private / 'dist/Sts2Recorder-macos-arm64-v0.1.0-alpha.zip'
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
            for name in ['Sts2Recorder.dll', 'Sts2Recorder.json', 'LICENSE.txt', 'RunReplays-LICENSE.txt', 'divine-sts2-LICENSE.txt', 'NOTICE.md', 'README.txt']:
                z.write(mods / name, name)
        if not a.game_app:
            workmods = app / 'Contents/MacOS/mods'; workmods.mkdir(exist_ok=True)
            for name in ['Sts2Recorder.dll', 'Sts2Recorder.json']: shutil.copyfile(mods / name, workmods / name)
            print('Deployed only to independent workspace:', workmods)
        print('Drop-in mod ZIP (project DLL only):', archive)

if __name__ == '__main__': main()
