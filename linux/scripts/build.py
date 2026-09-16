#!/usr/bin/env python3
"""Offline isolated build. Does not start the game or deploy to Steam."""
import argparse
import fcntl
import os
from pathlib import Path
import subprocess
from runtime import configured, stage, command
from work_materials import finish_materials

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--config', type=Path, required=True)
    a = p.parse_args()
    os.umask(0o077)
    cfg, root, private = configured(a.config)
    with (private / 'runtime.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        source = stage(cfg, private)
        before = set((private / 'evidence').glob('vm-*'))
        result = subprocess.call(command(cfg, root, source, 'BUILD_ONLY', 'build-only', 1, 'no', True))
        # The supervisor has exited and written its protection/exit checks.
        finish_materials(cfg, private, before)
        return result

if __name__ == '__main__':
    raise SystemExit(main())
