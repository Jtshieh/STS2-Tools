#!/usr/bin/env python3
"""Launch the independent native Mac game, with no human-thinking timeout."""
import argparse
import datetime
import json
from pathlib import Path
import signal
import subprocess
import uuid
from common import ROOT, check_game, config, digest, locked, native_mac, write_json

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--workspace', type=Path, default=ROOT)
    a = p.parse_args(); native_mac(); private, cfg = config(a.workspace)
    with locked(private):
        w = private / 'work'; app = w / 'SlayTheSpire2.app'
        version = check_game(app, patched=True)
        recorder = app / 'Contents/MacOS/mods/Sts2Recorder.dll'
        if not recorder.is_file(): raise RuntimeError('Build the recorder first')
        ident = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + uuid.uuid4().hex[:8]
        log = private / 'logs' / ident; log.mkdir(parents=True, mode=0o700)
        exe = app / 'Contents/MacOS/Slay the Spire 2'
        quote = lambda x: json.dumps(str(x))
        deny_reads = {cfg['sourceApp'], cfg['sourceProfile'], cfg['sourceSelector'],
                      str(Path.home() / 'Library/Application Support/Steam')}
        rules = ['(version 1)', '(allow default)', '(deny network*)',
            '(deny file-write* (require-all (require-not (subpath ' + quote(w) + ')) '
            '(require-not (subpath ' + quote(log) + ')) (require-not (literal "/dev/null"))))',
            '(deny process-exec (require-not (literal ' + quote(exe) + ')))']
        rules += ['(deny file-read* (subpath ' + quote(x) + '))' for x in sorted(deny_reads)]
        sb = log / 'sandbox.sb'; sb.write_text('\n'.join(rules) + '\n')
        env = dict(PATH='/usr/bin:/bin', LANG='en_US.UTF-8', TERM='dumb', TMPDIR=str(w / 'tmp'),
            DOTNET_EnableDiagnostics='0', DOTNET_CLI_TELEMETRY_OPTOUT='1',
            DOTNET_GENERATE_ASPNET_CERTIFICATE='false', STS2_RECORDER_LOG=str(log),
            STS2_PROCESS_ID=ident, STS2_EXPECTED_USERDATA=str(w / 'profile'))
        cmd = ['/usr/bin/sandbox-exec', '-f', str(sb), str(exe), '--path', str(w),
               '--log-file', str(log / 'godot.log'), '--force-steam=off']
        write_json(log / 'launch.json', dict(argv=cmd, cwd=str(w), environment=env,
            processRunId=ident, humanTimeout=None, recordingMode='normal', sl='not verified', **version))
        write_json(log / 'loaded-files.json', dict(game=version, recorderSha256=digest(recorder)))
        rows = []
        for f in (w / 'profile').rglob('*.save'):
            if f.is_symlink(): raise ValueError('Working profile contains symlink')
            relative = f.relative_to(w / 'profile')
            # Snapshot exact launch input, so Continue replay never uses an overwritten end save.
            target = log / 'initial-profile' / relative; target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(f.read_bytes())
            rows.append(dict(path=str(relative), sha256=digest(target)))
        write_json(log / 'profile-before-launch.json', rows)
        (private / 'latest-session.txt').write_text(str(log) + '\n')
        print('Independent game; private logs:', log, flush=True)
        with (log / 'stdout.log').open('w') as out:
            proc = subprocess.Popen(cmd, cwd=w, env=env, stdout=out, stderr=subprocess.STDOUT)
            (log / 'pid').write_text(str(proc.pid))
            try: code = proc.wait()
            except KeyboardInterrupt:
                proc.send_signal(signal.SIGTERM); code = proc.wait()
        write_json(log / 'launcher-exit.json', dict(exitCode=code,
            continuity='segment ended; resumed continuity remains unverified',
            utc=datetime.datetime.now(datetime.timezone.utc).isoformat()))
        print('Logs retained:', log)
        return code

if __name__ == '__main__': raise SystemExit(main())
