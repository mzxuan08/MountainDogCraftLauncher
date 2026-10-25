import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import source_info


class ProvenanceTests(unittest.TestCase):
    def test_export_without_git_and_reject_changes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / 'example.cs').write_bytes(b'original')
            (root / 'SOURCE-REVISION').write_text('a' * 40)
            (root / 'SOURCE-FILES-SHA256.json').write_text(json.dumps({
                'example.cs': hashlib.sha256(b'original').hexdigest()}))
            original_path = os.environ.get('PATH', '')
            try:
                os.environ['PATH'] = ''
                self.assertEqual(source_info.revision(root), 'a' * 40)
                (root / 'Injected.cs').write_bytes(b'new compiler input')
                with self.assertRaisesRegex(ValueError, 'inventory changed'):
                    source_info.revision(root)
                (root / 'Injected.cs').unlink()
                (root / 'src/artifacts').mkdir(parents=True)
                (root / 'src/artifacts/Injected.cs').write_bytes(b'nested source')
                with self.assertRaisesRegex(ValueError, 'inventory changed'):
                    source_info.revision(root)
                (root / 'src/artifacts/Injected.cs').unlink()
                (root / 'example.cs').write_bytes(b'changed')
                with self.assertRaisesRegex(ValueError, 'Source changed'):
                    source_info.revision(root)
            finally:
                os.environ['PATH'] = original_path

    def test_checkout_rejects_dirty_and_untracked_sources(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            def git(*args):
                return subprocess.run(['git', *args], cwd=root, check=True, capture_output=True)
            git('init', '-q')
            git('config', 'user.name', 'Packaging Fixture')
            git('config', 'user.email', 'fixture@example.invalid')
            (root / 'example.cs').write_text('original')
            git('add', '.')
            git('commit', '-qm', 'fixture')
            self.assertEqual(len(source_info.revision(root)), 40)
            (root / 'example.cs').write_text('changed')
            with self.assertRaisesRegex(ValueError, 'clean committed'):
                source_info.revision(root)
            git('checkout', '--', 'example.cs')
            (root / 'untracked.cs').write_text('new source')
            with self.assertRaisesRegex(ValueError, 'clean committed'):
                source_info.revision(root)


if __name__ == '__main__':
    unittest.main()
