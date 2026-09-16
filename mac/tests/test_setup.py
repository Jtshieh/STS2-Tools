"""Small synthetic input protection checks; no game needed."""
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from common import copy_file, ordinary, private_root
from export_trace import export

class Setup(unittest.TestCase):
    def test_copy_does_not_link_or_overwrite(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d).resolve(); source = p/'source'; target = p/'target'
            source.write_bytes(b'synthetic profile')
            copy_file(source, target)
            self.assertNotEqual(source.stat().st_ino, target.stat().st_ino)
            target.write_bytes(b'changed work')
            self.assertEqual(source.read_bytes(), b'synthetic profile')
            with self.assertRaises(FileExistsError): copy_file(source, target)

    def test_links_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d).resolve(); (p/'source').write_bytes(b'synthetic')
            (p/'link').symlink_to(p/'source')
            with self.assertRaises(ValueError): ordinary(p/'link')
            os.link(p/'source', p/'hard')
            with self.assertRaises(ValueError): ordinary(p/'hard')

    def test_private_directory_and_incomplete_export(self):
        with tempfile.TemporaryDirectory() as d:
            p = private_root(Path(d).resolve())
            self.assertEqual(p.stat().st_mode & 0o777, 0o700)
            session = p/'empty'; session.mkdir()
            result = export(session, p/'export')
            self.assertFalse(result['structuralReady'])
            self.assertIn('recorder did not initialize', result['issues'])

if __name__ == '__main__': unittest.main()
