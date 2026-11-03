"""Offline synthetic local-filesystem exercises, not .NET/EuroOffice E2E."""
import errno
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import savecopy_fs_probe as probe


class ProbeTests(unittest.TestCase):
    def test_no_explicit_sandbox_is_never_touched(self):
        with tempfile.TemporaryDirectory(prefix='normal-documents-') as dirname:
            root = Path(dirname)
            with self.assertRaises(probe.ProbeError):
                probe.observe(root)
            self.assertEqual([], list(root.iterdir()))

    def test_preexisting_files_are_never_modified_or_cleaned(self):
        with tempfile.TemporaryDirectory(prefix=probe.SANDBOX_PREFIX) as dirname:
            root = Path(dirname)
            original = root / 'existing-document.txt'
            original.write_text('DO_NOT_TOUCH')
            with self.assertRaises(probe.ProbeError):
                probe.observe(root)
            self.assertEqual('DO_NOT_TOUCH', original.read_text())

    def test_reject_noncanonical_dotdot_alias_to_approved_sandbox(self):
        with tempfile.TemporaryDirectory(prefix=probe.SANDBOX_PREFIX) as dirname:
            root = Path(dirname)
            aliased = root / '..' / root.name
            with self.assertRaises(probe.ProbeError):
                probe.observe(aliased)
            self.assertEqual([], list(root.iterdir()))

    def test_reject_symlinked_root_without_following_target(self):
        with tempfile.TemporaryDirectory(prefix=probe.SANDBOX_PREFIX) as dirname:
            root = Path(dirname)
            alias = root.parent / (probe.SANDBOX_PREFIX + 'alias-' + root.name)
            try:
                alias.symlink_to(root, target_is_directory=True)
            except (OSError, NotImplementedError):
                self.fail('Required symlink security test unavailable; configure disposable VM')
            try:
                with self.assertRaises(probe.ProbeError):
                    probe.observe(alias)
                self.assertEqual([], list(root.iterdir()))
            finally:
                alias.unlink()

    def test_genuine_filesystem_no_replace_observation_leaves_root_empty(self):
        with tempfile.TemporaryDirectory(prefix=probe.SANDBOX_PREFIX) as dirname:
            root = Path(dirname)
            report = probe.observe(root)
            self.assertEqual('observed_not_attested', report['state'])
            self.assertTrue(report['collisionPreserved'])
            self.assertTrue(report['newCopyBytesVerified'])
            self.assertFalse(report['nativeSaveProven'])
            self.assertFalse(report['releaseReady'])
            self.assertEqual([], list(root.iterdir()))

    def test_link_unavailable_is_not_reported_as_success(self):
        with tempfile.TemporaryDirectory(prefix=probe.SANDBOX_PREFIX) as dirname:
            root = Path(dirname)
            with patch.object(probe.os, 'link', side_effect=OSError(errno.EOPNOTSUPP, 'unsupported')):
                report = probe.observe(root)
            self.assertEqual('unsupported_not_attested', report['state'])
            self.assertFalse(report['collisionPreserved'])
            self.assertFalse(report['newCopyBytesVerified'])
            self.assertEqual([], list(root.iterdir()))

    def test_cli_without_vm_ack_has_no_side_effects(self):
        with tempfile.TemporaryDirectory(prefix=probe.SANDBOX_PREFIX) as dirname:
            root = Path(dirname)
            with patch.dict(os.environ, {probe.ENV_NAME: ''}):
                self.assertEqual(2, probe.main(['--sandbox', str(root), probe.ACK]))
            self.assertEqual([], list(root.iterdir()))


if __name__ == '__main__':
    unittest.main()
