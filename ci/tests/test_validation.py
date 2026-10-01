"""Meaningful refusal and timeout checks for the CI validator; no game execution."""
import importlib.util
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import zipfile

MODULE = Path(__file__).resolve().parents[1] / "validate_macos.py"
SPEC = importlib.util.spec_from_file_location("validate_macos", MODULE)
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


class ValidationChecks(unittest.TestCase):
    def test_traversal_and_absolute_paths_are_rejected(self):
        for name in ("../escape", "/absolute", "C:/escape", "folder/../../escape", "folder\\escape", "a//b"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                VALIDATOR.validate_entry(zipfile.ZipInfo(name))

    def test_symbolic_link_is_rejected_before_native_extraction(self):
        entry = zipfile.ZipInfo("PODIEZD.app/Contents/Resources/link")
        entry.external_attr = 0o120777 << 16
        with self.assertRaises(ValueError):
            VALIDATOR.validate_entry(entry)

    def test_universal_header_architectures_are_read(self):
        header = struct.pack(">II", 0xCAFEBABE, 2)
        header += struct.pack(">IIIII", 0x01000007, 0, 128, 1024, 14)
        header += struct.pack(">IIIII", 0x0100000C, 0, 2048, 1024, 14)
        self.assertEqual(VALIDATOR.universal_architectures(header), ["x86_64", "arm64"])

    def test_corrupt_universal_table_is_rejected(self):
        for header in (b"\xca\xfe", struct.pack(">II", 0xCAFEBABE, 999), struct.pack(">II", 0xCAFEBABE, 2)):
            with self.subTest(header=header.hex()), self.assertRaises(ValueError):
                VALIDATOR.universal_architectures(header)

    def test_owned_process_timeout_produces_logs_and_failure(self):
        with tempfile.TemporaryDirectory(prefix="dustore-ci-validator-test-") as temporary:
            result, _, _ = VALIDATOR.run_process([sys.executable, "-c", "import time; time.sleep(10)"],
                                                 "owned-timeout", Path(temporary), timeout=0.15)
            self.assertTrue(result["timedOut"])
            self.assertNotEqual(result["exitCode"], 0)
            self.assertTrue((Path(temporary) / "owned-timeout.stdout.log").exists())
            self.assertTrue((Path(temporary) / "owned-timeout.stderr.log").exists())
            with self.assertRaises(ValueError):
                VALIDATOR.successful(result, "Synthetic owned process")


if __name__ == "__main__":
    unittest.main()
