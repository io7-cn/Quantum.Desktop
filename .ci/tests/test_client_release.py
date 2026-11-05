import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('client_release', Path(__file__).parents[1] / 'publish-client-release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ClientReleaseTests(unittest.TestCase):
    def test_matching_tag_is_required(self):
        class Api:
            def request(self, method, path):
                return {'object': {'type': 'commit', 'sha': 'a' * 40}}
        release.verify_tag(Api(), 'example-v1.0.0', 'a' * 40)
        with self.assertRaisesRegex(ValueError, 'differs'):
            release.verify_tag(Api(), 'example-v1.0.0', 'b' * 40)

    def bundle(self, root, channel):
        directory = root / channel
        directory.mkdir()
        asset = directory / f'client-{channel}.zip'
        asset.write_bytes(b'package')
        digest = hashlib.sha256(asset.read_bytes()).hexdigest()
        (directory / f'SHA256SUMS.{channel}.txt').write_text(f'{digest}  {asset.name}\n', encoding='utf-8-sig')
        return asset

    def test_windows_only_desktop_checksums(self):
        with tempfile.TemporaryDirectory() as path:
            root = Path(path)
            self.bundle(root, 'win-x64')
            self.assertEqual(len(release.collect_assets(root, 'desktop', False)), 2)

    def test_missing_mobile_platform_rejected(self):
        with tempfile.TemporaryDirectory() as path:
            root = Path(path)
            self.bundle(root, 'win-x64')
            with self.assertRaisesRegex(ValueError, 'Missing'):
                release.collect_assets(root, 'mobile')

    def test_corrupted_download_rejected(self):
        with tempfile.TemporaryDirectory() as path:
            root = Path(path)
            self.bundle(root, 'win-x64').write_bytes(b'corrupted')
            with self.assertRaisesRegex(ValueError, 'Checksum mismatch'):
                release.collect_assets(root, 'desktop', False)

    def test_unlisted_asset_rejected(self):
        with tempfile.TemporaryDirectory() as path:
            root = Path(path)
            self.bundle(root, 'win-x64')
            (root / 'win-x64' / 'extra.zip').write_bytes(b'extra')
            with self.assertRaisesRegex(ValueError, 'Unexpected'):
                release.collect_assets(root, 'desktop', False)


if __name__ == '__main__':
    unittest.main()
