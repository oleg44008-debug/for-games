from __future__ import annotations
import os
from pathlib import Path
import stat
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from build_macos import APP_NAME, ASSEMBLY_NAME, macho_architectures, zip_bundle
from verify_macos import extract_owned_archive, inspect_dependencies, inspect_rendered_image, run_owned_process, validate_service_report, validate_ui_report
from macho_minimums import inspect_macho, version_tuple


class PackagingBoundaryChecks(unittest.TestCase):
    def test_archive_preserves_apphost_execute_permission(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            bundle = root / APP_NAME
            executable = bundle / "Contents" / "MacOS" / ASSEMBLY_NAME
            executable.parent.mkdir(parents=True)
            executable.write_bytes(b"\xcf\xfa\xed\xfe" + struct.pack("<I", 0x0100000C) + bytes(24))
            archive = root / "app.zip"
            zip_bundle(bundle, archive)
            with zipfile.ZipFile(archive) as source:
                entry = source.getinfo(f"{APP_NAME}/Contents/MacOS/{ASSEMBLY_NAME}")
                self.assertEqual(entry.create_system, 3)
                self.assertTrue((entry.external_attr >> 16) & stat.S_IXUSR)
            copy = extract_owned_archive(archive, root / "copy")
            self.assertEqual((copy / "Contents" / "MacOS" / ASSEMBLY_NAME).read_bytes(), executable.read_bytes())
            if os.name == "posix":
                self.assertTrue(os.access(copy / "Contents" / "MacOS" / ASSEMBLY_NAME, os.X_OK))

    def test_rejects_traversal_and_unexpected_top_level_content(self):
        for name in ("../outside", f"{APP_NAME}/../../outside", "/outside", "other.app/executable", f"{APP_NAME}\\..\\outside"):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                archive = root / "unsafe.zip"
                with zipfile.ZipFile(archive, "w") as source:
                    source.writestr(name, b"unsafe")
                with self.assertRaises(ValueError):
                    extract_owned_archive(archive, root / "copy")
                self.assertFalse((root / "outside").exists())

    def test_rejects_symlink_outside_app(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); archive = root / "unsafe.zip"
            info = zipfile.ZipInfo(f"{APP_NAME}/Contents/MacOS/link")
            info.create_system = 3; info.external_attr = (stat.S_IFLNK | 0o777) << 16
            with zipfile.ZipFile(archive, "w") as source:
                source.writestr(info, "../../../../outside")
            with self.assertRaises(ValueError):
                extract_owned_archive(archive, root / "copy")

    def test_macho_reports_native_architecture_not_pe(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for cpu, expected in ((0x01000007, "x64"), (0x0100000C, "arm64")):
                native = root / expected
                native.write_bytes(b"\xcf\xfa\xed\xfe" + struct.pack("<I", cpu) + bytes(24))
                self.assertEqual(macho_architectures(native), [expected])
            windows = root / "windows.exe"; windows.write_bytes(b"MZ" + bytes(100))
            self.assertEqual(macho_architectures(windows), [])

    def test_timeout_stops_only_the_owned_process(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(TimeoutError):
                run_owned_process([sys.executable, "-c", "import time; time.sleep(20)"], root, os.environ.copy(), root / "timeout.log", .2)
            self.assertTrue((root / "timeout.log").exists())

    def test_service_success_requires_preserved_input_and_owned_profile(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); profile = root / "profile"; profile.mkdir()
            prepared = profile / "managed" / "game.app"; prepared.mkdir(parents=True)
            report = {"status": "Pass", "originalLogoUnchanged": True, "report": {
                "Success": True, "ProfileDirectory": str(profile), "SourcePreserved": True, "PreparedMacAppPath": str(prepared)}}
            validate_service_report(report, root, True)
            report["report"]["SourcePreserved"] = False
            with self.assertRaises(ValueError):
                validate_service_report(report, root, True)
            report["report"]["SourcePreserved"] = True
            report["report"]["ProfileDirectory"] = str(root.parent)
            with self.assertRaises(ValueError):
                validate_service_report(report, root, True)

    def test_ui_success_requires_open_window_and_actual_game_analysis(self):
        report = {"status": "Pass", "windowOpened": True, "viewModelLoaded": True,
                  "originalLogoUnchanged": True, "clientWidth": 1180, "clientHeight": 780,
                  "libraryEntryCount": 1, "exAnalysisReady": True,
                  "embeddedStore": {"webViewCreated": True, "url": "https://dustore.ru/explore", "failedLoadShowsError": True,
                                    "storeDownload": {"addedToLibrary": True, "readyToLaunch": True}}}
        validate_ui_report(report, True)
        for field, value in (("windowOpened", False), ("libraryEntryCount", 0), ("exAnalysisReady", False),
                             ("embeddedStore", None), ("embeddedStore", {"webViewCreated": False, "url": "https://dustore.ru/explore"}),
                             ("embeddedStore", {"webViewCreated": True, "url": "about:blank", "failedLoadShowsError": True}),
                             ("embeddedStore", {"webViewCreated": True, "url": "https://dustore.ru/explore", "failedLoadShowsError": False}),
                             ("embeddedStore", {"webViewCreated": True, "url": "https://dustore.ru/explore", "failedLoadShowsError": True,
                                                "storeDownload": {"addedToLibrary": True, "readyToLaunch": False}})):
            invalid = {**report, field: value}
            with self.subTest(field=field), self.assertRaises(ValueError):
                validate_ui_report(invalid, True)

    def test_render_evidence_must_stay_in_owned_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); output = root / "verification"; output.mkdir()
            image = root / "outside.png"
            image.write_bytes(b"\x89PNG\r\n\x1a\n" + bytes(8) + struct.pack(">II", 1180, 780))
            with self.assertRaises(ValueError):
                inspect_rendered_image(image, output)

    def test_bundle_refuses_developer_machine_library_dependency(self):
        with tempfile.TemporaryDirectory() as temporary:
            bundle = Path(temporary) / APP_NAME
            library = bundle / "Contents" / "MacOS" / ASSEMBLY_NAME
            output = type("OtoolOutput", (), {"stdout": "launcher:\nLoad command 1\n cmd LC_LOAD_DYLIB\n name /opt/homebrew/lib/missing.dylib (offset 24)\n"})()
            with patch("verify_macos.native_files", return_value=[library]), patch("verify_macos.subprocess.run", return_value=output):
                with self.assertRaises(ValueError):
                    inspect_dependencies(bundle, "osx-arm64")

    def test_dylib_own_id_is_not_a_foreign_library_dependency(self):
        with tempfile.TemporaryDirectory() as temporary:
            bundle = Path(temporary) / APP_NAME
            library = bundle / "Contents" / "MacOS" / "libAvaloniaNative.dylib"
            output = type("OtoolOutput", (), {"stdout": "dylib:\nLoad command 1\n cmd LC_ID_DYLIB\n name /usr/local/lib/libAvalonia.Native.OSX.dylib (offset 24)\nLoad command 2\n cmd LC_LOAD_DYLIB\n name /usr/lib/libSystem.B.dylib (offset 24)\n"})()
            with patch("verify_macos.native_files", return_value=[library]), patch("verify_macos.subprocess.run", return_value=output):
                report = inspect_dependencies(bundle, "osx-arm64")
            self.assertEqual(report[0]["dependencies"], ["/usr/lib/libSystem.B.dylib"])
            self.assertEqual(report[0]["installName"], "/usr/local/lib/libAvalonia.Native.OSX.dylib")

    def test_deployment_minimum_uses_selected_universal_slice(self):
        def image(cpu, minimum):
            header = b"\xcf\xfa\xed\xfe" + struct.pack("<IIIIIII", cpu, 0, 6, 1, 24, 0, 0)
            return header + struct.pack("<IIIIII", 0x32, 24, 1, minimum, 0x1A0000, 0)
        intel = image(0x01000007, 0x000A0F00)
        arm = image(0x0100000C, 0x000B0000)
        offset = 48
        universal = b"\xca\xfe\xba\xbe" + struct.pack(">I", 2)
        universal += struct.pack(">IIIII", 0x01000007, 0, offset, len(intel), 0)
        universal += struct.pack(">IIIII", 0x0100000C, 0, offset + len(intel), len(arm), 0)
        universal += intel + arm
        self.assertEqual(inspect_macho(universal, "x64")["minimumOS"], "10.15.0")
        self.assertEqual(inspect_macho(universal, "arm64")["minimumOS"], "11.0.0")
        self.assertLess(version_tuple("10.15"), version_tuple("11.0"))

    def test_invalid_native_load_command_does_not_claim_old_os_compatibility(self):
        header = b"\xcf\xfa\xed\xfe" + struct.pack("<IIIIIII", 0x01000007, 0, 6, 1, 8, 0, 0)
        with self.assertRaises(ValueError):
            inspect_macho(header + struct.pack("<II", 0x32, 99999), "x64")


if __name__ == "__main__":
    unittest.main()
