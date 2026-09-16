#!/usr/bin/env python3
"""Audit an explicit source allowlist; optionally create a source-only local archive."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[1]
FORBIDDEN = {'.dll', '.exe', '.so', '.pck', '.save', '.binary', '.il', '.log', '.nupkg', '.zip'}
PRIVATE_MARKERS = [b'/' + b'home' + b'/', b'/' + b'Users' + b'/', b'/' + b'sfs' + b'/gpfs', b'BEGIN ' + b'OPENSSH PRIVATE KEY', b'BEGIN ' + b'RSA PRIVATE KEY']

def audit(staged=False):
    names = (ROOT / 'RELEASE_FILES.txt').read_text().splitlines()
    if names != sorted(set(names)) or not names:
        raise ValueError('Release allowlist must be sorted and unique')
    rows = []
    for name in names:
        p = Path(name)
        if p.is_absolute() or '..' in p.parts or '.private' in p.parts or p.suffix.lower() in FORBIDDEN:
            raise ValueError('Forbidden release path: ' + name)
        path = ROOT / p
        st = path.lstat()
        if not stat.S_ISREG(st.st_mode) or st.st_nlink != 1 or st.st_size > 300000:
            raise ValueError('Nonordinary or oversized release file: ' + name)
        data = path.read_bytes()
        if b'\0' in data or any(x in data for x in PRIVATE_MARKERS):
            raise ValueError('Binary/private material in source: ' + name)
        data.decode('utf-8')
        rows.append(dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
    if staged:
        tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode().strip('\0').split('\0')
        if sorted(tracked) != names:
            raise ValueError('Actual Git index differs from explicit release allowlist')
        for row in rows:
            prefix = subprocess.check_output(['git', 'rev-parse', '--show-prefix'], cwd=ROOT).decode().strip()
            content = subprocess.check_output(['git', 'show', ':' + prefix + row['path']], cwd=ROOT)
            if hashlib.sha256(content).hexdigest() != row['sha256']:
                raise ValueError('Staged bytes differ from audited file: ' + row['path'])
    return rows

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--staged', action='store_true')
    p.add_argument('--output', type=Path)
    a = p.parse_args()
    rows = audit(a.staged)
    if a.output:
        with a.output.open('xb') as stream:
            with tarfile.open(fileobj=stream, mode='w:gz') as archive:
                for row in rows:
                    path = ROOT / row['path']
                    info = archive.gettarinfo(str(path), arcname='sts2-tools-v0.1.0-alpha/' + row['path'])
                    info.uid = info.gid = 0; info.uname = info.gname = ''; info.mtime = 0; info.mode = 0o644
                    with path.open('rb') as f: archive.addfile(info, f)
        with tarfile.open(a.output) as archive:
            members = archive.getmembers()
            if len(members) != len(rows) or any(not m.isfile() for m in members):
                raise ValueError('Archive member audit failed')
            for member, row in zip(members, rows):
                if member.name != 'sts2-tools-v0.1.0-alpha/' + row['path'] or hashlib.sha256(archive.extractfile(member).read()).hexdigest() != row['sha256']:
                    raise ValueError('Archive bytes differ from reviewed allowlist')
    print(json.dumps(dict(passed=True, files=len(rows), bytes=sum(x['bytes'] for x in rows), archive=str(a.output) if a.output else None, archiveSha256=hashlib.sha256(a.output.read_bytes()).hexdigest() if a.output else None)))

if __name__ == '__main__': main()
