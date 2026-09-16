#!/usr/bin/env python3
"""Audit exact source files/index before upload. Does not upload or inspect private files."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import subprocess

ROOT = Path(__file__).resolve().parents[1]
FORBIDDEN = {'.dll', '.pdb', '.exe', '.so', '.dylib', '.pck', '.save', '.binary', '.il', '.log', '.nupkg', '.zip', '.pem', '.key'}
MARKERS = [b'/' + b'Users/', b'/' + b'home/', b'BEGIN ' + b'OPENSSH PRIVATE KEY',
           b'BEGIN ' + b'RSA PRIVATE KEY']
SECRETS = re.compile(rb'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|sk-proj-[A-Za-z0-9_-]{20,}')

def audit(staged=False):
    names = (ROOT / 'RELEASE_FILES.txt').read_text().splitlines()
    if names != sorted(set(names)) or not names: raise ValueError('Invalid source allowlist')
    rows = []
    for name in names:
        relative = Path(name); p = ROOT / relative
        if relative.is_absolute() or any(x in relative.parts for x in ('..', '.private', '.git', 'bin', 'obj')) or relative.suffix in FORBIDDEN:
            raise ValueError('Forbidden source path: ' + name)
        s = p.lstat()
        if not stat.S_ISREG(s.st_mode) or s.st_nlink != 1 or s.st_size > 500000:
            raise ValueError('Nonordinary/oversized source: ' + name)
        data = p.read_bytes(); data.decode('utf-8')
        if b'\0' in data or any(m in data for m in MARKERS) or SECRETS.search(data):
            raise ValueError('Binary/private marker in: ' + name)
        rows.append(dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
    if staged:
        tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode().strip('\0').split('\0')
        if sorted(tracked) != names: raise ValueError('Git index differs from source allowlist')
        for row in rows:
            b = subprocess.check_output(['git', 'show', ':' + row['path']], cwd=ROOT)
            if hashlib.sha256(b).hexdigest() != row['sha256']: raise ValueError('Staged bytes differ: ' + row['path'])
    return rows

if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__); p.add_argument('--staged', action='store_true')
    rows = audit(p.parse_args().staged)
    print(json.dumps(dict(passed=True, files=len(rows), bytes=sum(r['bytes'] for r in rows))))
