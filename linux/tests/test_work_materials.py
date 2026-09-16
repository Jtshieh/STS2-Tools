"""Small synthetic cleanup tests; no private game input or game execution."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
import work_materials as w

class WorkMaterials(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.private = self.root / '.private'
        self.private.mkdir(mode=0o700)
        self.e = self.private / 'evidence/vm-synthetic'
        self.e.mkdir(parents=True)
        self.game = self.root / 'baseline'
        self.game.mkdir()
        (self.game / 'SlayTheSpire2.pck').write_bytes(b'fixture')
        (self.e / 'game-input').mkdir()
        self.copy = self.e / 'game-input/SlayTheSpire2.pck'
        self.copy.write_bytes(b'fixture')
        self.profile = self.e / 'runtime-work/xdg-data/profile.save'
        self.profile.parent.mkdir(parents=True)
        self.profile.write_bytes(b'changed progress retained')
        self.log = self.e / 'error.log'
        self.log.write_bytes(b'original failed action')
        self.report = dict(finishedUtc='synthetic', passed=False,
                           phases=[dict(remainingLiveOwnedProcesses=[])],
                           parentAndNativeGuardUnchanged=True, nativeGamePhaseAttempted=True,
                           **{k: dict(passed=True) for k in ('userlandHostVerification', 'identityHostVerification', 'normalGameImmutableAfter', 'normalHostPayloadAfter')})
        self.write_report()
        native = self.e / 'runtime-work/native-host/libsynthetic.so'
        native.parent.mkdir(parents=True)
        native.write_bytes(b'native')
        (self.game / native.name).write_bytes(b'native')
        (self.e / 'normal-game-input-copy-manifest.json').write_text(json.dumps(dict(
            gameFiles=[dict(destination=str(self.copy), bytes=7)],
            nativeFallbackCopies=[dict(destination=str(native), bytes=6)])))
        self.feed = self.root / 'feed'
        self.feed.mkdir()
        with zipfile.ZipFile(self.feed / 'synthetic.1.nupkg', 'w') as z:
            z.writestr('lib/synthetic.dll', b'dependency')
            z.writestr('lib/synthetic.xml', b'API docs')
        self.dep = self.e / 'build-work/nuget-packages/synthetic/1/lib/synthetic.dll'
        self.dep.parent.mkdir(parents=True)
        self.dep.write_bytes(b'dependency')
        self.dep.with_suffix('.xml').write_bytes(b'API docs')
        self.cfg = dict(workspace=str(self.root), game=str(self.game), runtime_feed=str(self.feed), godot_root=str(self.root / 'godot'))

    def write_report(self):
        (self.e / 'supervisor-report.json').write_text(json.dumps(self.report))

    def test_success_failure_and_idempotence_preserve_evidence(self):
        report_bytes = (self.e / 'supervisor-report.json').read_bytes()
        self.assertEqual(w.reclaim(self.cfg, self.e), 31)
        self.assertFalse(self.copy.exists())
        self.assertFalse(self.dep.exists())
        self.assertEqual(w.reclaim(self.cfg, self.e), 0)
        self.assertEqual(self.profile.read_bytes(), b'changed progress retained')
        self.assertEqual(self.log.read_bytes(), b'original failed action')
        self.assertEqual((self.game / self.copy.name).read_bytes(), b'fixture')
        self.assertEqual((self.e / 'supervisor-report.json').read_bytes(), report_bytes)
        self.report['passed'] = True
        self.write_report()
        self.copy.write_bytes(b'fixture')
        self.assertEqual(w.reclaim(self.cfg, self.e), 7)

    def test_incomplete_and_failed_protection_retained(self):
        for key in ['finishedUtc', 'normalGameImmutableAfter', 'normalHostPayloadAfter']:
            report = dict(self.report)
            report.pop(key)
            (self.e / 'supervisor-report.json').write_text(json.dumps(report))
            self.assertEqual(w.reclaim(self.cfg, self.e), 0)
            self.assertTrue(self.copy.exists())

    def test_live_owned_process_report_retained(self):
        self.report['phases'][0]['remainingLiveOwnedProcesses'] = [dict(pid=123)]
        self.write_report()
        self.assertEqual(w.reclaim(self.cfg, self.e), 0)
        self.assertTrue(self.copy.exists())

    def test_actual_process_use_retained(self):
        proc = subprocess.Popen([sys.executable, '-c', 'import sys,time; f=open(sys.argv[1]); print("ready",flush=True); time.sleep(10)', str(self.copy)], stdout=subprocess.PIPE, text=True)
        try:
            self.assertEqual(proc.stdout.readline().strip(), 'ready')
            self.assertEqual(w.reclaim(self.cfg, self.e), 0)
            self.assertTrue(self.copy.exists())
        finally:
            proc.terminate()
            proc.wait(timeout=3)
            proc.stdout.close()

    def test_boundary_symlink_hardlink_and_mount(self):
        with self.assertRaises(ValueError):
            w.reclaim(self.cfg, self.game)
        link = self.root / 'link'
        link.symlink_to(self.game, target_is_directory=True)
        with self.assertRaises(ValueError):
            w.ordinary(link / self.copy.name)
        hard = self.root / 'hard'
        os.link(self.game / self.copy.name, hard)
        with self.assertRaises(ValueError):
            w.ordinary(hard)
        with patch.object(w.os.path, 'ismount', side_effect=lambda p: p == self.e):
            with self.assertRaises(ValueError):
                w.ordinary(self.copy)
        with self.assertRaises(ValueError):
            w.ordinary(self.e / '../vm-synthetic/game-input/SlayTheSpire2.pck')

    def test_protected_path_and_delete_failure_journal(self):
        journal = self.e / 'test-cleanup.jsonl'
        source = str(self.game / self.copy.name)
        self.assertEqual(w.remove_duplicate(self.e, self.profile, source, self.profile.stat().st_size, journal, 'test'), 0)
        with patch.object(w.os, 'unlink', side_effect=PermissionError('synthetic denied')):
            self.assertEqual(w.remove_duplicate(self.e, self.copy, source, 7, journal, 'test'), 0)
        rows = [json.loads(x) for x in journal.read_text().splitlines()]
        self.assertEqual([r['result'] for r in rows], ['retained-error', 'planned', 'retained-error'])
        self.assertTrue(self.profile.exists())
        self.assertTrue(self.copy.exists())

    def test_unknown_source_and_symlink_candidate_retained(self):
        (self.game / self.copy.name).unlink()
        self.assertEqual(w.reclaim(self.cfg, self.e), 0)
        self.assertTrue(self.copy.exists())
        (self.game / self.copy.name).write_bytes(b'fixture')
        self.copy.unlink()
        self.copy.symlink_to(self.game / self.copy.name)
        w.reclaim(self.cfg, self.e)
        self.assertTrue(self.copy.is_symlink())
        self.assertTrue((self.game / self.copy.name).exists())

    def test_space_check_before_copy(self):
        with patch.object(w.shutil, 'disk_usage') as usage:
            usage.return_value.free = w.GIB + 6
            with self.assertRaises(OSError):
                w.require_space(self.root, 7)
            usage.return_value.free += 1
            w.require_space(self.root, 7)

if __name__ == '__main__':
    unittest.main()
