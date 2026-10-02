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
        code, out, report = run(str(self.course), "--apply", "--package", "never")
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
        run(str(self.course), "--apply", "--package", "never")
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
        run(str(self.course), "--apply", "--package", "never", "--allow-executables")
        self.assertEqual(names(self.section / "4 - Tooling.material.zip"), ["4 - Tooling Setup.exe"])

    def test_lone_zip_is_reused_not_rezipped(self):
        touch(self.section / "7 - Solo.mp4")
        src = make_zip(self.section / "7 - Solo Labs.zip", {"a.txt": b"a"})
        run(str(self.course), "--apply", "--package", "never")
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
        run(str(self.course), "--apply", "--package", "never", "--match", "name")
        self.assertTrue((self.section / "1 - Welcome.material.zip").exists())
        self.assertIn("3 - Lab Files.zip", names(self.section / "additional-material.zip"))

    def test_existing_archives_kept_then_updated(self):
        run(str(self.course), "--apply", "--package", "never")
        target = self.section / "1 - Welcome.material.zip"
        first = target.stat().st_mtime_ns
        _, _, report = run(str(self.course), "--apply", "--package", "never")
        self.assertEqual(target.stat().st_mtime_ns, first)
        self.assertTrue(any(a["status"] == "kept" for c in report["courses"] for a in c["archives"]))
        future = first / 1e9 + 100
        os.utime(self.section / "1 - Welcome.pdf", (future, future))
        run(str(self.course), "--apply", "--package", "never", "--update")
        self.assertNotEqual(target.stat().st_mtime_ns, first)

    def test_video_less_folder_packages_whole_course(self):
        code, out, report = run(str(self.course), "--apply")
        self.assertEqual(code, 0, out)
        self.assertIn("one archive for the whole course: no videos in 2 - Extras", out)
        self.assertFalse(list(self.course.rglob("*.material.zip")))
        self.assertFalse((self.section / "additional-material.zip").exists())
        got = names(self.course / "additional-material.zip")
        for expected in ("1 - Intro/1 - Welcome.pdf", "1 - Intro/2 - Reading Notes.html", "1 - Intro/3 - Lab Files.zip",
                         "1 - Intro/attached_files/3 - Lab/diagram.png", "1 - Intro/4 - Tooling Setup.exe.REMOVED.txt",
                         "2 - Extras/Cheat Sheet.pdf"):
            self.assertIn(expected, got)
        self.assertTrue(report["courses"][0]["packaged_as_one"])

    def test_package_always_and_auto_without_video_less(self):
        (self.course / "2 - Extras" / "Cheat Sheet.pdf").unlink()
        (self.course / "2 - Extras").rmdir()
        _, out, _ = run(str(self.course), "--apply")                 # no video-less folder: split as usual
        self.assertTrue((self.section / "1 - Welcome.material.zip").exists())
        self.assertNotIn("one archive", out)
        _, out, _ = run(str(self.course), "--apply", "--force", "--package", "always")
        self.assertIn("one archive for the whole course (--package always)", out)
        self.assertIn("1 - Intro/1 - Welcome.pdf", names(self.course / "additional-material.zip"))

    def test_empty_video_less_folder_does_not_trigger(self):
        (self.course / "2 - Extras" / "Cheat Sheet.pdf").unlink()
        _, out, _ = run(str(self.course), "--apply")
        self.assertNotIn("one archive", out)
        self.assertTrue((self.section / "1 - Welcome.material.zip").exists())

    def test_exclude_option(self):
        run(str(self.course), "--apply", "--package", "never", "--exclude", "*.html")
        self.assertFalse((self.section / "additional-material.zip").exists())


class UnreadableArchives(unittest.TestCase):
    def test_archive_7z_cannot_read_is_replaced_by_a_note(self):
        """With a 7z program that fails to read an archive (corrupt, encrypted listing), the archive
        cannot be checked, so it is left out like any other unchecked content."""
        with tempfile.TemporaryDirectory() as t:
            t = Path(t)
            fake = t / "bin" / "7z"
            fake.parent.mkdir()
            fake.write_text("#!/bin/sh\necho 'ERROR: cannot open' >&2\nexit 2\n")
            fake.chmod(0o755)
            course = t / "Course"
            touch(course / "1 - Lesson.mp4")
            touch(course / "labs.7z", b"7z\xbc\xaf\x27\x1c broken")
            old = os.environ["PATH"]
            os.environ["PATH"] = f"{fake.parent}{os.pathsep}{old}"
            try:
                code, _, report = run(str(course), "--mode", "course")
            finally:
                os.environ["PATH"] = old
            self.assertEqual(code, 0)
            removed = [r for c in report["courses"] for a in c["archives"] for r in a["removed"]]
            self.assertEqual([Path(r["file"]).name for r in removed], ["labs.7z"])
            self.assertIn("could not be checked", removed[0]["reason"])


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


class DownloadJunk(unittest.TestCase):
    """Patterns found in real course downloads: Udemy redirect stubs, adverts, chapter sidecars,
    and a document that lost its extension."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        c = self.course = Path(self.tmp.name, "Course")
        s = c / "1. Basics"
        touch(s / "1. Intro.mp4")
        touch(s / "2. Quiz 1.html", b'<script type="text/javascript">window.location = "https://www.udemy.com/course/x/quiz/1";</script>')
        touch(s / "3. Real Lesson.html", b"<html><body><h1>Subnetting</h1><p>" + b"Real lesson text. " * 40 + b"</p></body></html>")
        touch(s / "1. Intro_chapters.xml", b"<Chapters/>")
        touch(c / "Bonus Resources.txt", b"https://freecourseweb.com\r\n\r\nhttps://example.net/more\r\n")
        touch(c / "Notes.txt", b"Remember to save your lab configs.\nhttps://example.com\n")
        with zipfile.ZipFile(c / "4 - StudyPlan200301docx", "w") as z:
            z.writestr("[Content_Types].xml", "<Types/>")
            z.writestr("word/document.xml", "<w:document/>")
        # a video-less folder holding only redirect stubs must not trigger whole-course packaging
        touch(c / "6. Practice Tests" / "1. Test 1.html", b'<script>window.location = "https://www.udemy.com/t/1";</script>')

    def tearDown(self):
        self.tmp.cleanup()

    def test_rules(self):
        code, out, report = run(str(self.course), "--apply")
        self.assertEqual(code, 0, out)
        self.assertNotIn("one archive", out)          # stubs-only folder is not material
        section = names(self.course / "1. Basics" / "additional-material.zip")
        self.assertEqual(section, ["3. Real Lesson.html"])
        course = names(self.course / "additional-material.zip")
        self.assertEqual(course, ["4 - StudyPlan200301.docx", "Notes.txt"])
        reasons = {Path(x["file"]).name: x["reason"] for x in report["skipped"]}
        self.assertIn("redirect placeholder", reasons["2. Quiz 1.html"])
        self.assertIn("redirect placeholder", reasons["1. Test 1.html"])
        self.assertIn("Bonus Resources.txt", reasons)  # caught as an advert, by content or by name
        self.assertNotIn("1. Intro_chapters.xml", reasons)   # Jellyfin data: skipped quietly
        self.assertTrue((self.course / "4 - StudyPlan200301docx").exists())  # original untouched

    def test_link_only_text_detected_without_name_list(self):
        touch(self.course / "Visit Us.txt", b"www.somecoursesite.example\nhttps://t.me/channel\n")
        _, _, report = run(str(self.course))
        reasons = {Path(x["file"]).name: x["reason"] for x in report["skipped"]}
        self.assertIn("advert", reasons["Visit Us.txt"])


def docx(path: Path, extra: dict | None = None, rels: str = "") -> Path:
    members = {"[Content_Types].xml": "<Types/>", "word/document.xml": "<w:document><w:t>hi</w:t></w:document>",
               "word/_rels/document.xml.rels": f'<Relationships>{rels}</Relationships>'}
    members.update(extra or {})
    return make_zip(path, members)


class ActiveContent(unittest.TestCase):
    """Rule 5: documents that can run or fetch something are replaced by notes."""

    EXTERNAL_TEMPLATE = ('<Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate" '
                         'Target="https://evil.example/t.dotm" TargetMode="External"/>')
    HYPERLINK = ('<Relationship Id="r2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" '
                 'Target="https://cisco.com" TargetMode="External"/>')

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        import zlib
        c = self.course = Path(self.tmp.name, "Docs")
        touch(c / "1 - a.mp4")
        docx(c / "2 - macro.docx", {"word/vbaProject.bin": b"\x00"})
        docx(c / "3 - template.docx", rels=self.EXTERNAL_TEMPLATE)
        docx(c / "4 - links only.docx", rels=self.HYPERLINK)                              # benign
        docx(c / "5 - chart.docx", {"word/embeddings/Microsoft_Excel_Worksheet.xlsx": b"PK"})  # benign
        docx(c / "6 - ole.docx", {"word/embeddings/oleObject1.bin": b"\xd0\xcf"})
        touch(c / "7 - js.pdf", b"%PDF-1.7\n1 0 obj << /OpenAction << /S /JavaScript /JS (app.alert(1)) >> >> endobj")
        touch(c / "8 - hidden.pdf", b"%PDF-1.7\n2 0 obj << /Length 9 /Filter /FlateDecode >> stream\n"
              + zlib.compress(b"<< /S /Launch /F (cmd.exe) >>") + b"\nendstream endobj")
        touch(c / "9 - obfuscated.pdf", b"%PDF-1.7\n3 0 obj << /S /J#61vaScript /JS (x) >> endobj")
        touch(c / "10 - plain.pdf", b"%PDF-1.7\n4 0 obj << /Type /Page >> endobj")              # benign
        # benign: "/JS" by chance inside compressed image bytes (seen in real course PDFs)
        touch(c / "15 - image.pdf", b"%PDF-1.7\n5 0 obj << /Type /XObject /Subtype /Image /Filter /FlateDecode >> stream\n"
              + zlib.compress(b"\x83/JS\x18!\xcd\x00\xff" * 50) + b"\nendstream endobj")
        touch(c / "11 - object.rtf", b"{\\rtf1 {\\object\\objemb {\\*\\objdata 0105}}}")
        touch(c / "12 - macros.xlsm", b"PK")
        make_zip(c / "13 - lab pack.zip", {"notes.txt": b"n"})
        with zipfile.ZipFile(c / "13 - lab pack.zip", "a") as z:
            buf = io.BytesIO()
            with zipfile.ZipFile(buf, "w") as inner:
                inner.writestr("[Content_Types].xml", "<Types/>"); inner.writestr("word/vbaProject.bin", "x")
            z.writestr("bad.docx", buf.getvalue())
        touch(c / "14 - legacy.doc", b"\xd0\xcf\x11\xe0" + b"\x00" * 60 + "_VBA_PROJECT".encode("utf-16-le"))

    def tearDown(self):
        self.tmp.cleanup()

    def all_entries(self):
        return {n for z in self.course.rglob("*.zip") if ".material" in z.name or z.name == "additional-material.zip" for n in names(z)}

    def test_blocked_and_allowed(self):
        code, out, _ = run(str(self.course), "--apply", "--levels", "course")
        self.assertEqual(code, 0, out)
        got = names(self.course / "additional-material.zip")
        for blocked in ("2 - macro.docx", "3 - template.docx", "6 - ole.docx", "7 - js.pdf", "8 - hidden.pdf",
                        "9 - obfuscated.pdf", "11 - object.rtf", "12 - macros.xlsm", "13 - lab pack.zip", "14 - legacy.doc"):
            self.assertIn(blocked + ".REMOVED.txt", got, blocked)
            self.assertNotIn(blocked, got)
        for kept in ("4 - links only.docx", "5 - chart.docx", "10 - plain.pdf", "15 - image.pdf"):
            self.assertIn(kept, got, kept)
        with zipfile.ZipFile(self.course / "additional-material.zip") as z:
            self.assertIn("external attachedTemplate (https://evil.example/t.dotm)", z.read("3 - template.docx.REMOVED.txt").decode())
            self.assertIn("a Launch action", z.read("8 - hidden.pdf.REMOVED.txt").decode())
            self.assertIn("JavaScript", z.read("9 - obfuscated.pdf.REMOVED.txt").decode())
            self.assertIn("bad.docx: macros", z.read("13 - lab pack.zip.REMOVED.txt").decode())

    def test_allow_active_documents(self):
        run(str(self.course), "--apply", "--levels", "course", "--allow-active-documents")
        got = names(self.course / "additional-material.zip")
        self.assertIn("2 - macro.docx", got)
        self.assertIn("7 - js.pdf", got)
        self.assertIn("12 - macros.xlsm.REMOVED.txt", got)     # macro-enabled types are executables
        run(str(self.course), "--apply", "--force", "--levels", "course", "--allow-executables")
        got = names(self.course / "additional-material.zip")
        self.assertIn("12 - macros.xlsm", got)
        self.assertIn("2 - macro.docx.REMOVED.txt", got)      # documents still need their own flag


class RuleFiles(unittest.TestCase):
    """Every rule file in tools/rules/ is valid and passes its own [[test]] cases."""

    def test_each_rule_file_passes_its_tests(self):
        rules = mam.load_rules([mam.DEFAULT_RULES])
        self.assertGreaterEqual(len(rules), 6)
        for rule in rules:
            with self.subTest(rule=rule.id):
                self.assertTrue(rule.tests, f"{rule.source.name} has no [[test]] cases")
                self.assertEqual(mam.run_rule_tests(rule), [])

    def write(self, folder: Path, name: str, text: str) -> Path:
        folder.mkdir(parents=True, exist_ok=True)
        (folder / name).write_text(text)
        return folder

    def test_invalid_rule_files_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            cases = {
                "typo.toml": 'id = "typo"\ndescription = "d"\naction = "skip"\n[match]\nnmes = ["x"]\n',
                "wrong-name.toml": 'id = "other"\ndescription = "d"\naction = "skip"\n[match]\nnames = ["x"]\n',
                "bad-regex.toml": 'id = "bad-regex"\ndescription = "d"\naction = "skip"\n[match]\nname_regex = "("\n',
                "no-match.toml": 'id = "no-match"\ndescription = "d"\naction = "skip"\n',
                "bad-action.toml": 'id = "bad-action"\ndescription = "d"\naction = "delete"\n[match]\nnames = ["x"]\n',
            }
            for name, text in cases.items():
                with self.subTest(file=name):
                    d = self.write(Path(tmp, name), name, text)
                    with self.assertRaises(mam.RuleError):
                        mam.load_rules([d])

    def test_user_rules_and_disabling(self):
        with tempfile.TemporaryDirectory() as tmp:
            rules = self.write(Path(tmp, "my-rules"), "skip-answer-keys.toml",
                               'id = "skip-answer-keys"\ndescription = "Instructor answer keys"\naction = "skip"\n'
                               'reason = "answer key"\n[match]\nnames = ["*answer key*"]\n')
            c = Path(tmp, "C")
            touch(c / "1 - a.mp4"); touch(c / "1 - Answer Key.pdf"); touch(c / "Bonus Resources.txt", b"https://x.example\n")
            _, _, report = run(str(c), "--rules", str(rules))
            reasons = {Path(x["file"]).name: x["reason"] for x in report["skipped"]}
            self.assertEqual(reasons["1 - Answer Key.pdf"], "answer key [rule skip-answer-keys]")
            self.assertIn("Bonus Resources.txt", reasons)
            _, _, report = run(str(c), "--disable-rule", "link-only-text", "--disable-rule", "release-group-adverts")
            self.assertNotIn("Bonus Resources.txt", {Path(x["file"]).name for x in report["skipped"]})
            with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                mam.main([str(c), "--disable-rule", "no-such-rule"])
            out = io.StringIO()
            with redirect_stdout(out):
                mam.main(["--list-rules", "--rules", str(rules)])
            self.assertIn("skip-answer-keys", out.getvalue())
            self.assertIn("udemy-redirect-placeholders", out.getvalue())


class PlacementAndLanguage(unittest.TestCase):
    def test_report_says_why_each_file_is_where_it_is(self):
        with tempfile.TemporaryDirectory() as tmp:
            c = Path(tmp, "C"); s = c / "1 - S"
            touch(s / "1 - a.mp4"); touch(s / "1 - a.pdf"); touch(s / "2 - b.mp4"); touch(s / "2 - b notes.txt", b"notes for b")
            touch(s / "attached_files" / "1 - a" / "x.png"); touch(s / "9 - text lesson.html", b"<p>" + b"words " * 20 + b"</p>")
            _, out, report = run(str(c), "-v")
            how = {Path(k).name: v for a in report["courses"][0]["archives"] for k, v in a["placement"].items()}
            self.assertEqual(how["1 - a.pdf"], "same name as the video 1 - a.mp4")
            self.assertEqual(how["2 - b notes.txt"], "lesson number 2, the only video 2 - b.mp4")
            self.assertIn("[rule lesson-attachment-folders]", how["x.png"])
            self.assertEqual(how["9 - text lesson.html"], "in the section, not tied to one lesson")
            self.assertIn("<- same name as the video", out)

    def test_note_language_falls_back_to_english(self):
        with tempfile.TemporaryDirectory() as tmp:
            c = Path(tmp, "C"); touch(c / "1 - a.mp4"); touch(c / "1 - a tool.exe", b"MZ")
            run(str(c), "--apply", "--language", "xx-YY")       # no such translation: English
            with zipfile.ZipFile(c / "1 - a.material.zip") as z:
                note = z.read("1 - a tool.exe.REMOVED.txt").decode()
            self.assertTrue(note.startswith("REMOVED: 1 - a tool.exe"))
            self.assertIn("SHA-256:", note)
            self.assertEqual(mam.set_language("de_DE.UTF-8"), "en")   # only English ships today
            mam.set_language("en")

    def test_every_translation_has_only_known_keys(self):
        for folder in (mam.I18N_DIR, Path(__file__).resolve().parent.parent / "src/Jellyfin.Plugin.AdditionalMaterial/Web/i18n"):
            english = json.loads((folder / "en.json").read_text())
            for f in folder.glob("*.json"):
                with self.subTest(file=str(f)):
                    data = json.loads(f.read_text())
                    self.assertEqual(set(data) - set(english), set(), f"{f.name} has keys English lacks")
                    for k, v in data.items():
                        placeholders = set(__import__("re").findall(r"\{(\w+)\}", english[k]))
                        self.assertEqual(set(__import__("re").findall(r"\{(\w+)\}", v)), placeholders, f"{f.name}: {k}")


if __name__ == "__main__":
    unittest.main()
