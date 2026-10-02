"""Tests for tools/make_additional_material.py. Standard library only: python3 -m unittest discover -s tests"""

import hashlib
import http.server
import io
import json
import os
import sys
import tempfile
import threading
import unittest
import zipfile
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "tools"))
import make_additional_material as mam  # noqa: E402


def touch(path: Path, data: bytes = b"x") -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def make_zip(path: Path, members: dict) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w") as z:
        for name, data in members.items():
            z.writestr(name, data)
    return path


def run(*args: str) -> tuple[int, str, dict]:
    out, err = io.StringIO(), io.StringIO()
    with tempfile.NamedTemporaryFile("r", suffix=".json", delete=False) as rep:
        report_path = rep.name
    try:
        with redirect_stdout(out), redirect_stderr(err):
            code = mam.main([*args, "--json", report_path])
        report = json.loads(Path(report_path).read_text())
    finally:
        os.unlink(report_path)
    return code, out.getvalue() + err.getvalue(), report


def names(zip_path: Path) -> list[str]:
    with zipfile.ZipFile(zip_path) as z:
        return sorted(z.namelist())


class CourseLayout(unittest.TestCase):
    """A course shaped like the real ones: Udemy numbering, attached_files, a video-less folder."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        c = self.course = Path(self.tmp.name, "My Course")
        s = self.section = c / "1 - Intro"
        touch(s / "1 - Welcome.mp4")
        touch(s / "1 - Welcome.pdf", b"same-name pdf")                      # lesson 1, same name
        touch(s / "2 - Reading Notes.html", b"<p>text lesson</p>")         # no video 2: section
        touch(s / "3 - Lab.mp4")
        make_zip(s / "3 - Lab Files.zip", {"lab.pkt": b"pkt"})            # lone zip for lesson 3: reused
        touch(s / "attached_files" / "3 - Lab" / "diagram.png", b"png")    # attached_files: lesson 3
        touch(s / "3 - Lab.srt", b"subs")                                  # subtitles: ignored
        touch(s / ".chapters" / "3 - Lab.xml", b"<x/>")                    # Jellyfin data: ignored
        touch(s / "4 - Tooling.mp4")
        touch(s / "4 - Tooling Setup.exe", b"MZ fake")                     # executable: note
        touch(s / "5 - Pack.mp4")
        make_zip(s / "5 - Pack Extras.zip", {"docs/readme.txt": b"hi", "bin/evil.exe": b"MZ"})  # hides an exe
        touch(s / "Get Bonus Downloads Here.url", b"[InternetShortcut]")   # default exclude
        touch(c / "poster.jpg", b"jpg")                                    # Jellyfin artwork: ignored
        touch(c / "2 - Extras" / "Cheat Sheet.pdf", b"extra")              # video-less folder: course
        touch(c / "additional-material.7z", b"old")                        # a trigger name: ignored
        os.symlink("/etc/hostname", s / "6 - Link.pdf")                    # symlink: skipped

    def tearDown(self):
        self.tmp.cleanup()

    def test_dry_run_writes_nothing(self):
        before = sorted(p for p in self.course.rglob("*"))
        code, out, report = run(str(self.course))
        self.assertEqual(code, 0)
        self.assertIn("dry run", out)
        self.assertEqual(before, sorted(p for p in self.course.rglob("*")))
        self.assertTrue(all(a["status"] == "would write" for c in report["courses"] for a in c["archives"]))

    def test_grouping(self):
        code, out, report = run(str(self.course), "--apply")
        self.assertEqual(code, 0, out)
        s = self.section
        self.assertEqual(names(s / "1 - Welcome.material.zip"), ["1 - Welcome.pdf"])
        self.assertEqual(names(s / "3 - Lab.material.zip"), ["3 - Lab Files.zip", "attached_files/3 - Lab/diagram.png"])
        self.assertEqual(names(s / "additional-material.zip"), ["2 - Reading Notes.html"])
        self.assertEqual(names(self.course / "additional-material.zip"), ["2 - Extras/Cheat Sheet.pdf"])
        skipped = {Path(x["file"]).name: x["reason"] for x in report["skipped"]}
        self.assertIn("6 - Link.pdf", skipped)
        self.assertIn("Get Bonus Downloads Here.url", skipped)
        for f in ("3 - Lab.srt", "poster.jpg", "3 - Lab.xml"):
            self.assertFalse(any(f in n for z in self.course.rglob("*.zip") for n in names(z) if z.name != "3 - Lab Files.zip"))

    def test_executables_become_notes(self):
        run(str(self.course), "--apply")
        z4 = self.section / "4 - Tooling.material.zip"
        self.assertEqual(names(z4), ["4 - Tooling Setup.exe.REMOVED.txt"])
        with zipfile.ZipFile(z4) as z:
            note = z.read("4 - Tooling Setup.exe.REMOVED.txt").decode()
        self.assertIn(hashlib.sha256(b"MZ fake").hexdigest(), note)
        self.assertIn("--allow-executables", note)
        z5 = self.section / "5 - Pack.material.zip"
        self.assertEqual(names(z5), ["5 - Pack Extras.zip.REMOVED.txt"])
        with zipfile.ZipFile(z5) as z:
            self.assertIn("bin/evil.exe", z.read("5 - Pack Extras.zip.REMOVED.txt").decode())

    def test_allow_executables(self):
        run(str(self.course), "--apply", "--allow-executables")
        self.assertEqual(names(self.section / "4 - Tooling.material.zip"), ["4 - Tooling Setup.exe"])

    def test_lone_zip_is_reused_not_rezipped(self):
        touch(self.section / "7 - Solo.mp4")
        src = make_zip(self.section / "7 - Solo Labs.zip", {"a.txt": b"a"})
        run(str(self.course), "--apply")
        out = self.section / "7 - Solo.material.zip"
        self.assertEqual(out.read_bytes(), src.read_bytes())
        self.assertEqual(out.stat().st_ino, src.stat().st_ino)  # hard link: no extra space

    def test_levels_roll_up(self):
        run(str(self.course), "--apply", "--levels", "course")
        self.assertFalse(list(self.section.glob("*.material.zip")))
        got = names(self.course / "additional-material.zip")
        self.assertIn("1 - Intro/1 - Welcome.pdf", got)
        self.assertIn("1 - Intro/2 - Reading Notes.html", got)

    def test_match_name_only(self):
        run(str(self.course), "--apply", "--match", "name")
        self.assertTrue((self.section / "1 - Welcome.material.zip").exists())
        self.assertIn("3 - Lab Files.zip", names(self.section / "additional-material.zip"))

    def test_existing_archives_kept_then_updated(self):
        run(str(self.course), "--apply")
        target = self.section / "1 - Welcome.material.zip"
        first = target.stat().st_mtime_ns
        _, _, report = run(str(self.course), "--apply")
        self.assertEqual(target.stat().st_mtime_ns, first)
        self.assertTrue(any(a["status"] == "kept" for c in report["courses"] for a in c["archives"]))
        future = first / 1e9 + 100
        os.utime(self.section / "1 - Welcome.pdf", (future, future))
        run(str(self.course), "--apply", "--update")
        self.assertNotEqual(target.stat().st_mtime_ns, first)

    def test_exclude_option(self):
        run(str(self.course), "--apply", "--exclude", "*.html")
        self.assertFalse((self.section / "additional-material.zip").exists())


class LibraryDetection(unittest.TestCase):
    def test_library_and_course_shapes(self):
        with tempfile.TemporaryDirectory() as tmp:
            lib = Path(tmp)
            touch(lib / "Course A" / "Section 1" / "1 - a.mp4")
            touch(lib / "Course A" / "Section 1" / "1 - a.pdf")
            touch(lib / "Flat Course" / "1 - b.mp4")
            touch(lib / "Flat Course" / "1 - b.pdf")
            touch(lib / "No Videos" / "notes.pdf")
            self.assertEqual([c.name for c in mam.courses_in(lib, "auto")], ["Course A", "Flat Course"])
            self.assertEqual(mam.courses_in(lib / "Course A", "auto"), [lib / "Course A"])
            code, _, report = run(str(lib), "--apply", "--only", "Flat*")
            self.assertEqual(code, 0)
            self.assertEqual(len(report["courses"]), 1)
            self.assertTrue((lib / "Flat Course" / "1 - b.material.zip").exists())


class FakeVirusTotal(http.server.BaseHTTPRequestHandler):
    known: dict = {}

    def do_GET(self):  # noqa: N802
        digest = self.path.rsplit("/", 1)[-1]
        if self.headers.get("x-apikey") != "test-key":
            self.send_response(401); self.end_headers(); return
        if digest not in self.known:
            self.send_response(404); self.end_headers(); self.wfile.write(b'{"error":{"code":"NotFoundError"}}'); return
        body = json.dumps({"data": {"attributes": {"last_analysis_stats": self.known[digest]}}}).encode()
        self.send_response(200); self.send_header("content-type", "application/json"); self.end_headers(); self.wfile.write(body)

    def log_message(self, *args):
        pass


class VirusTotalLookups(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        c = self.course = Path(self.tmp.name, "C")
        touch(c / "1 - a.mp4"); touch(c / "1 - a tool.exe", b"clean tool")
        touch(c / "2 - b.mp4"); touch(c / "2 - b tool.exe", b"bad tool")
        touch(c / "3 - c.mp4"); touch(c / "3 - c tool.exe", b"never seen")
        FakeVirusTotal.known = {
            hashlib.sha256(b"clean tool").hexdigest(): {"malicious": 0, "suspicious": 0, "harmless": 0, "undetected": 70},
            hashlib.sha256(b"bad tool").hexdigest(): {"malicious": 12, "suspicious": 1, "harmless": 0, "undetected": 57},
        }
        self.server = http.server.HTTPServer(("127.0.0.1", 0), FakeVirusTotal)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        os.environ["AM_VT_BASE"] = f"http://127.0.0.1:{self.server.server_port}"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        os.environ.pop("AM_VT_BASE", None)
        self.tmp.cleanup()

    def note(self, archive: str) -> str:
        with zipfile.ZipFile(self.course / archive) as z:
            return z.read(z.namelist()[0]).decode()

    def test_lookup_results_in_notes(self):
        code, out, report = run(str(self.course), "--apply", "--virustotal-key", "test-key", "--virustotal-rate", "6000")
        self.assertEqual(code, 0, out)
        self.assertIn("flagged by 0 of 70", self.note("1 - a.material.zip"))
        self.assertIn("FLAGGED by 12", self.note("2 - b.material.zip"))
        self.assertIn("not known", self.note("3 - c.material.zip"))
        self.assertIn("WARNING: VirusTotal flags 1 file", out)

    def test_allow_clean_executables(self):
        run(str(self.course), "--apply", "--virustotal-key", "test-key", "--virustotal-rate", "6000", "--allow-clean-executables")
        self.assertEqual(names(self.course / "1 - a.material.zip"), ["1 - a tool.exe"])
        self.assertTrue(names(self.course / "2 - b.material.zip")[0].endswith(".REMOVED.txt"))
        self.assertTrue(names(self.course / "3 - c.material.zip")[0].endswith(".REMOVED.txt"))

    def test_options_need_a_key(self):
        os.environ.pop("VT_API_KEY", None)
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as e:
            mam.main([str(self.course), "--allow-clean-executables"])
        self.assertEqual(e.exception.code, 2)


if __name__ == "__main__":
    unittest.main()
