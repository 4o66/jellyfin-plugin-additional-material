#!/usr/bin/env python3
"""
Build Additional Material archives for Jellyfin from the files stored with your videos.

Point it at a course folder, or at a library folder holding several courses. It walks the
videos and their folders and gathers everything that is not video, subtitles or Jellyfin's own
metadata into .zip archives the Additional Material plugin recognizes:

  lesson   <video name>.material.zip   beside the video, for files that belong to one lesson
  section  additional-material.zip     in a section (season) folder, for the section's other files
  course   additional-material.zip     in the course (series) folder, for everything else

Which lesson a file belongs to, in order:
  1. it has the same name as the video (only the extension differs);
  2. it starts with the same lesson number as exactly one video in the same folder
     ("4 - Study Plan.pdf" belongs to "4 - Welcome.mp4");
  3. it sits in attached_files/<lesson>/, where <lesson> matches a video in the parent folder.
Files matching no lesson go to their section.

If a course has subfolders that hold material but no videos (Jellyfin shows no page for them),
the whole course is packaged as one additional-material.zip instead, folders kept inside, so the
material is not split between the course page and section pages (--package, default auto).

Executable content (.exe, .dll, scripts, macro-enabled Office files, archives that contain them) and
documents with active content (macros, embedded objects, external templates or OLE links, DDE fields,
PDF JavaScript, launch actions or embedded files, RTF objects) are left out by default
and replaced in the archive by "<name>.REMOVED.txt", a note saying why, with the file's SHA-256 so
it can be looked up on a malware scanner. --allow-executables and --allow-active-documents include them. With a
VirusTotal API key, blocked files are looked up by SHA-256 (fingerprints only, nothing uploaded
unless --virustotal-upload).

Nothing is changed unless you pass --apply. Original files are never moved or deleted.
Exit status: 0 success, 1 some archives failed, 2 bad arguments.
"""

from __future__ import annotations

import argparse
import fnmatch
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

VERSION = "1.0.0"

VIDEO_EXT = {".mp4", ".mkv", ".avi", ".m4v", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m2ts",
             ".mpg", ".mpeg", ".3gp", ".ogv", ".vob"}
SUBTITLE_EXT = {".srt", ".vtt", ".ass", ".ssa", ".sub", ".idx", ".sup", ".smi"}
EXECUTABLE_EXT = {".exe", ".dll", ".msi", ".msp", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl",
                  ".hta", ".lnk", ".vbs", ".vbe", ".wsf", ".jar", ".reg", ".apk", ".dmg", ".pkg",
                  ".ps1", ".psm1", ".sys", ".ocx", ".appx", ".msix", ".deb", ".rpm", ".app",
                  # macro-enabled Office files are code
                  ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppsm", ".ppam", ".sldm"}
NESTED_ARCHIVE_EXT = {".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz"}
DEFAULT_EXCLUDES = ["*.url", "*.torrent", "Thumbs.db", "desktop.ini",
                    # release-group adverts that come with course downloads
                    "Bonus Resources.txt", "Get Bonus Downloads Here*",
                    # fnmatch reads [...] as a character set: "[[]" is a literal "["
                    "[[] FreeCourseWeb.com ]*", "[[] FreeCourseLab.com ]*", "[[] DevCourseWeb.com ]*",
                    "[[] WebToolTip.com ]*", "[[] CourseBoat.com ]*", "[[] FreeTutorials*"]
# Chapter and edit-list sidecars that Jellyfin plugins write beside videos.
SIDECAR = re.compile(r"(_chapters\.xml|\.edl|-chapters\.xml)$", re.IGNORECASE)
REDIRECT = re.compile(rb"(window\.)?location(\.href)?\s*=|location\.replace\(|http-equiv\s*=\s*[\"']?refresh", re.IGNORECASE)
URL_LINE = re.compile(r"^\s*(https?://|www\.)\S+\s*$", re.IGNORECASE)
JELLYFIN_IMAGE = re.compile(
    r"^(folder|poster|cover|fanart|backdrop\d*|banner|logo|clearart|clearlogo|landscape|thumb|disc|art|"
    r"season\d*-(poster|banner|fanart|landscape)|season-specials-poster|.*-(thumb|poster|fanart|landscape))"
    r"\.(jpe?g|png|webp|gif|tbn)$", re.IGNORECASE)
JELLYFIN_DIRS = {"extrafanart", "extrathumbs", "metadata"}
ATTACHMENT_DIRS = {"attached_files", "attachments", "resources"}
TRIGGER_FOLDER = "additional-material.zip"
TRIGGER_SUFFIX = ".material.zip"
LESSON_NUMBER = re.compile(r"^\s*0*(\d{1,4})(?=[\s._\-)\]]|$)")
QUIET_SKIPS = {"video, subtitle or nfo", "Jellyfin artwork", "hidden or Jellyfin data (dot) file", "Jellyfin chapter data"}


@dataclass
class Group:
    level: str                      # lesson / section / course
    archive: Path                   # the .zip to write
    base: Path                      # entries are stored relative to this folder
    files: list[Path] = field(default_factory=list)
    removed: dict[Path, str] = field(default_factory=dict)  # file -> why it becomes a note
    scans: dict[Path, dict] = field(default_factory=dict)   # file -> VirusTotal result


def lesson_number(name: str) -> str | None:
    m = LESSON_NUMBER.match(name)
    return m.group(1) if m else None


def is_trigger(name: str) -> bool:
    low = name.lower()
    return low == TRIGGER_FOLDER or low.endswith(TRIGGER_SUFFIX) or low == "additional-material.7z" or low.endswith(".material.7z")


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def is_redirect_stub(path: Path) -> bool:
    """A tiny HTML page whose only job is to send the browser elsewhere (Udemy quiz placeholders)."""
    if path.suffix.lower() not in (".html", ".htm"):
        return False
    try:
        if path.stat().st_size > 4096:
            return False
        data = path.read_bytes()
    except OSError:
        return False
    if not REDIRECT.search(data):
        return False
    text = re.sub(rb"<script.*?</script>|<style.*?</style>|<[^>]+>", b" ", data, flags=re.S | re.I)
    return len(re.sub(rb"\s+", b"", text)) < 40


def is_link_only_text(path: Path) -> bool:
    """A small text file that is nothing but web links: the release group's advert."""
    if path.suffix.lower() not in (".txt", ".text", ""):
        return False
    try:
        if path.stat().st_size > 2048:
            return False
        lines = [line for line in path.read_text(encoding="utf-8", errors="replace").splitlines() if line.strip()]
    except OSError:
        return False
    if not lines or len(lines) > 40:
        return False
    other = [line for line in lines if not URL_LINE.match(line)]
    # Every line a link, or several links with one short caption ("Visit us for more courses").
    return not other or (len(lines) >= 3 and len(other) == 1 and len(other[0].strip()) <= 60)


# ---- active content in documents ---------------------------------------------------------------
OOXML_EXT = {".docx", ".dotx", ".xlsx", ".xltx", ".xlsb", ".pptx", ".potx", ".ppsx", ".vsdx"}
OLE2_EXT = {".doc", ".dot", ".xls", ".xlt", ".ppt", ".pot", ".pps", ".msg"}
# Relationship types that make a document fetch or run something when it is opened.
RISKY_REL = re.compile(r"/(attachedTemplate|oleObject|frame|subDocument|control|package)$")
PDF_TOKENS = {b"/JavaScript": "JavaScript", b"/JS": "JavaScript", b"/Launch": "a Launch action (runs a program)",
              b"/EmbeddedFile": "embedded files", b"/RichMedia": "RichMedia (Flash) content"}
PDF_NAME_HEX = re.compile(rb"/[A-Za-z0-9#]*#[0-9A-Fa-f]{2}[A-Za-z0-9#]*")
MAX_SCAN = 200 * 1024 * 1024


def _pdf_names_decoded(data: bytes) -> bytes:
    """PDF names may hide letters as #xx (/J#61vaScript); decode them so the tokens are visible."""
    return PDF_NAME_HEX.sub(lambda m: re.sub(rb"#([0-9A-Fa-f]{2})", lambda h: bytes([int(h.group(1), 16)]), m.group(0)), data)


def _looks_like_pdf_syntax(chunk: bytes) -> bool:
    sample = chunk[:4096]
    return bool(sample) and sum(32 <= b < 127 or b in (9, 10, 13) for b in sample) / len(sample) > 0.9


def pdf_active_content(data: bytes) -> str | None:
    import zlib
    chunks = [data]
    # Look inside compressed streams too, but only object streams or streams that decompress to
    # PDF syntax: image and font data is binary, where "/JS" can occur by chance.
    for m in re.finditer(rb"stream\r?\n", data):
        start = m.end()
        end = data.find(b"endstream", start)
        if end < 0 or end - start > 50 * 1024 * 1024:
            continue
        header = data[max(0, m.start() - 512):m.start()]
        header = header[header.rfind(b"<<"):] if b"<<" in header else header
        if re.search(rb"/(Image|Font|FontFile\d?|XObject)\b|/Subtype\s*/(Image|Type1C|CIDFontType0C|OpenType)", header):
            continue
        try:
            inflated = zlib.decompressobj().decompress(data[start:end], 50 * 1024 * 1024)
        except zlib.error:
            continue
        if b"/ObjStm" in header or _looks_like_pdf_syntax(inflated):
            chunks.append(inflated)
    found = []
    for chunk in chunks:
        text = _pdf_names_decoded(chunk)
        for token, label in PDF_TOKENS.items():
            # A name token is followed by PDF syntax: whitespace, a delimiter, or the end.
            if re.search(re.escape(token) + rb"(?=[\s/()<>\[\]{}%]|$)", text) and label not in found:
                found.append(label)
    return "the PDF contains " + ", ".join(found) if found else None


def ooxml_active_content(z: zipfile.ZipFile) -> str | None:
    found = []
    names = z.namelist()
    if any(n.lower().endswith("vbaproject.bin") for n in names):
        found.append("macros")
    if any(re.search(r"/embeddings/(oleObject[^/]*\.bin|[^/]*\.(bin|exe|dll|scr|js|vbs|bat|cmd|ps1|hta))$", n, re.I) for n in names):
        found.append("embedded OLE objects")
    if any("/activex/" in n.lower() for n in names):
        found.append("ActiveX controls")
    for n in names:
        if not n.endswith(".rels"):
            continue
        try:
            xml = z.read(n).decode("utf-8", "replace")
        except (KeyError, zipfile.BadZipFile, RuntimeError):
            continue
        for rel in re.finditer(r"<Relationship\b[^>]*>", xml):
            tag = rel.group(0)
            kind = re.search(r'Type="([^"]+)"', tag)
            if "TargetMode=\"External\"" in tag and kind and RISKY_REL.search(kind.group(1)):
                target = re.search(r'Target="([^"]+)"', tag)
                what = kind.group(1).rsplit("/", 1)[-1]
                label = f"an external {what} ({(target.group(1) if target else '?')[:80]})"
                if label not in found:
                    found.append(label)
    for n in names:
        if re.match(r"(word/(document|header\d*|footer\d*)\.xml)$", n):
            try:
                if re.search(rb"\bDDE(AUTO)?\b", z.read(n)):
                    found.append("a DDE field (asks to run a command)")
                    break
            except (KeyError, zipfile.BadZipFile, RuntimeError):
                pass
    return "the document contains " + ", ".join(found) if found else None


def ole2_active_content(data: bytes) -> str | None:
    found = []
    utf16 = lambda s: s.encode("utf-16-le")  # noqa: E731  (OLE directory entry names)
    if utf16("_VBA_PROJECT") in data or utf16("VBA") + b"\x00\x00" in data or utf16("Macros") in data:
        found.append("macros")
    if utf16("ObjectPool") in data or utf16("\x01Ole10Native") in data:
        found.append("embedded objects")
    return "the document contains " + ", ".join(found) if found else None


def active_content(name: str, data: bytes) -> str | None:
    """Why a document's content is active (macros, scripts, embedded objects), or None."""
    ext = Path(name).suffix.lower()
    if data.startswith(b"%PDF") or ext == ".pdf":
        return pdf_active_content(data)
    if data.startswith(b"{\\rtf") or ext == ".rtf":
        return "the RTF contains embedded objects" if re.search(rb"\\obj(data|emb|link|autlink|update)\b|\\object\b", data) else None
    if data.startswith(b"\xd0\xcf\x11\xe0") and (ext in OLE2_EXT or not ext):
        return ole2_active_content(data)
    if data.startswith(b"PK\x03\x04") and (ext in OOXML_EXT or not ext):
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as z:
                if "[Content_Types].xml" in z.namelist():
                    return ooxml_active_content(z)
        except (zipfile.BadZipFile, RuntimeError):
            return None
    return None


DOCUMENT_EXT = {".pdf", ".rtf"} | OOXML_EXT | OLE2_EXT


def file_active_content(path: Path) -> str | None:
    ext = path.suffix.lower()
    if ext not in DOCUMENT_EXT and ext:
        return None
    try:
        if path.stat().st_size > MAX_SCAN:
            return None
        data = path.read_bytes()
    except OSError:
        return None
    return active_content(path.name, data)


def zip_active_documents(path: Path) -> list[str]:
    """Documents inside a .zip (one level) that carry active content, as 'name: reason'."""
    out = []
    try:
        with zipfile.ZipFile(path) as z:
            for info in z.infolist():
                ext = Path(info.filename).suffix.lower()
                if ext in DOCUMENT_EXT and info.file_size <= MAX_SCAN:
                    try:
                        why = active_content(info.filename, z.read(info))
                    except (zipfile.BadZipFile, RuntimeError, OSError):
                        continue
                    if why:
                        out.append(f"{info.filename}: {why.replace('the document contains ', '').replace('the PDF contains ', '')}")
    except (zipfile.BadZipFile, OSError, RuntimeError):
        pass
    return out


OOXML_DIRS = {"word/": ".docx", "xl/": ".xlsx", "ppt/": ".pptx"}
EXT_HINTS = (".docx", ".xlsx", ".pptx", ".pdf", ".zip", ".doc", ".xls", ".ppt", ".txt", ".png", ".jpg")


def archive_name(path: Path, base: Path) -> str:
    """Name inside the archive. A file that lost its extension ("Plan200301docx") gets one back,
    detected from its content; the original on disk is left alone."""
    rel = path.relative_to(base).as_posix()
    if path.suffix and path.suffix.lower() not in (".", ""):
        return rel
    ext = None
    try:
        with open(path, "rb") as f:
            head = f.read(8)
        if head.startswith(b"%PDF"):
            ext = ".pdf"
        elif head.startswith(b"PK\x03\x04"):
            ext = ".zip"
            with zipfile.ZipFile(path) as z:
                listing = z.namelist()
            if "[Content_Types].xml" in listing:
                ext = next((e for d, e in OOXML_DIRS.items() if any(n.startswith(d) for n in listing)), ".zip")
        elif head.startswith(b"\xd0\xcf\x11\xe0"):
            ext = ".doc"
        elif head[:4] in (b"\x89PNG", b"\xff\xd8\xff\xe0", b"\xff\xd8\xff\xe1"):
            ext = ".png" if head.startswith(b"\x89PNG") else ".jpg"
    except (OSError, zipfile.BadZipFile):
        return rel
    if not ext:
        return rel
    hint = ext.lstrip(".")
    if rel.lower().endswith(hint):                       # "...200301docx" -> "...200301.docx"
        return rel[: -len(hint)].rstrip(".") + ext
    return rel + ext


def find_7z() -> str | None:
    return next((shutil.which(n) for n in ("7zz", "7z", "7za") if shutil.which(n)), None)


def archive_members(path: Path, seven: str | None) -> list[str]:
    """Names inside an archive (and inside zips nested in a zip), or [] if it is not one."""
    ext = path.suffix.lower()
    if ext not in NESTED_ARCHIVE_EXT:
        return []
    if ext == ".zip":
        names: list[str] = []
        try:
            with zipfile.ZipFile(path) as z:
                for info in z.infolist():
                    names.append(info.filename)
                    if info.filename.lower().endswith(".zip") and info.file_size < 512 * 1024 * 1024:
                        try:
                            with zipfile.ZipFile(io.BytesIO(z.read(info))) as inner:
                                names += [info.filename + "/" + n for n in inner.namelist()]
                        except (zipfile.BadZipFile, OSError, RuntimeError):
                            pass
        except (zipfile.BadZipFile, OSError, RuntimeError):
            pass
        return names
    if seven:
        r = subprocess.run([seven, "l", "-slt", "-ba", str(path)], capture_output=True, text=True)
        return [line[7:] for line in r.stdout.splitlines() if line.startswith("Path = ")]
    return ["(contents not checked: no 7z program to read this archive)"]


# ---- VirusTotal ------------------------------------------------------------------------------
class VirusTotal:
    """Looks files up on VirusTotal by SHA-256. Uploads only when explicitly enabled: an uploaded
    file is shared with VirusTotal's community, so never upload material you may not share."""

    def __init__(self, key: str, upload: bool, per_minute: int, base: str):
        self.key, self.upload, self.base = key, upload, base.rstrip("/")
        self.gap = 60.0 / max(1, per_minute)
        self.last = -1e9
        self.cache: dict[str, dict] = {}

    def _request(self, method: str, url: str, data: bytes | None = None, headers: dict | None = None) -> tuple[int, dict]:
        wait = self.last + self.gap - time.monotonic()
        if wait > 0:
            time.sleep(wait)
        self.last = time.monotonic()
        req = urllib.request.Request(url, data=data, method=method,
                                     headers={"x-apikey": self.key, "accept": "application/json", **(headers or {})})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return r.status, json.loads(r.read() or b"{}")
        except urllib.error.HTTPError as e:
            try:
                return e.code, json.loads(e.read() or b"{}")
            except ValueError:
                return e.code, {}
        except (urllib.error.URLError, TimeoutError, ValueError) as e:
            return 0, {"error": {"message": str(e)}}

    def check(self, path: Path, digest: str) -> dict:
        if digest in self.cache:
            return self.cache[digest]
        link = f"https://www.virustotal.com/gui/file/{digest}"
        code, body = self._request("GET", f"{self.base}/files/{digest}")
        attrs_key = "last_analysis_stats"
        if code == 404 and self.upload:
            code, body = self._upload(path)
            attrs_key = "stats"
        if code == 404:
            result = {"status": "unknown", "link": link}
        elif code != 200:
            msg = (body.get("error") or {}).get("message", "") if isinstance(body, dict) else ""
            result = {"status": "error", "error": f"HTTP {code} {msg}".strip(), "link": link}
        else:
            stats = (body.get("data") or {}).get("attributes", {}).get(attrs_key) or {}
            mal, sus = int(stats.get("malicious", 0)), int(stats.get("suspicious", 0))
            engines = sum(v for v in stats.values() if isinstance(v, int))
            result = {"status": "flagged" if mal or sus else ("clean" if engines else "unknown"),
                      "malicious": mal, "suspicious": sus, "engines": engines, "link": link}
        self.cache[digest] = result
        return result

    def _upload(self, path: Path) -> tuple[int, dict]:
        if path.stat().st_size > 32 * 1024 * 1024:
            return 413, {"error": {"message": "larger than the 32 MB upload limit"}}
        boundary = "----am" + os.urandom(8).hex()
        head = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{path.name}"\r\n'
                f"Content-Type: application/octet-stream\r\n\r\n").encode()
        code, body = self._request("POST", f"{self.base}/files", head + path.read_bytes() + f"\r\n--{boundary}--\r\n".encode(),
                                   {"content-type": f"multipart/form-data; boundary={boundary}"})
        analysis = (body.get("data") or {}).get("id") if code == 200 else None
        if not analysis:
            return code or 0, body
        for _ in range(20):  # the scan usually finishes within a few minutes
            code, body = self._request("GET", f"{self.base}/analyses/{analysis}")
            if code == 200 and (body.get("data") or {}).get("attributes", {}).get("status") == "completed":
                return 200, body
            time.sleep(15)
        return 0, {"error": {"message": "scan did not finish in time"}}


def describe_scan(scan: dict | None) -> str:
    if not scan:
        return "not checked"
    status = scan["status"]
    if status == "clean":
        return f"VirusTotal: known, flagged by 0 of {scan['engines']} engines"
    if status == "flagged":
        return (f"VirusTotal: FLAGGED by {scan['malicious']} engine(s) as malicious and "
                f"{scan['suspicious']} as suspicious, out of {scan['engines']}")
    if status == "unknown":
        return "VirusTotal: not known to VirusTotal"
    return f"VirusTotal: lookup failed ({scan.get('error', 'error')})"


def removal_note(path: Path, base: Path, why: str, scan: dict | None) -> str:
    digest = sha256_of(path)
    return (
        f"REMOVED: {path.name}\n\n"
        f"This file was left out of the Additional Material archive because {why}.\n"
        "Executable and active content in course downloads is a common way malware spreads, so it\n"
        "is not handed out automatically.\n\n"
        f"  File:    {path.relative_to(base).as_posix()}\n"
        f"  Size:    {path.stat().st_size} bytes\n"
        f"  SHA-256: {digest}\n"
        f"  Scan:    {describe_scan(scan)}\n\n"
        "The original is still on the server. You can look the SHA-256 up before deciding to trust it:\n"
        f"  https://www.virustotal.com/gui/search/{digest}\n\n"
        "An administrator can include such files by re-running make_additional_material.py with\n"
        "--allow-executables (programs and scripts) or --allow-active-documents (documents).\n")


# ---- planning --------------------------------------------------------------------------------
class Planner:
    def __init__(self, args: argparse.Namespace):
        self.args = args
        self.excludes = DEFAULT_EXCLUDES + (args.exclude or [])
        self.skipped: list[tuple[str, str]] = []
        self.blocked: dict[Path, str] = {}
        self.scans: dict[Path, dict] = {}
        self.allowed_clean: list[tuple[str, str]] = []
        self.packaged: dict[Path, list[str]] = {}
        self.seven = find_7z()
        key = args.virustotal_key or os.environ.get("VT_API_KEY")
        self.vt = VirusTotal(key, args.virustotal_upload, args.virustotal_rate,
                             os.environ.get("AM_VT_BASE", "https://www.virustotal.com/api/v3")) if key else None

    def excluded(self, path: Path, course: Path) -> str | None:
        rel = path.relative_to(course)
        if any(part.startswith(".") for part in rel.parts):
            return "hidden or Jellyfin data (dot) file"
        if any(part.lower() in JELLYFIN_DIRS or part.lower().endswith(".trickplay") for part in rel.parts[:-1]):
            return "Jellyfin artwork"
        ext = path.suffix.lower()
        if ext in VIDEO_EXT or ext in SUBTITLE_EXT or ext == ".nfo":
            return "video, subtitle or nfo"
        if JELLYFIN_IMAGE.match(path.name) or SIDECAR.search(path.name):
            return "Jellyfin artwork" if JELLYFIN_IMAGE.match(path.name) else "Jellyfin chapter data"
        if is_redirect_stub(path):
            return "redirect placeholder (only sends the browser to a website)"
        if is_link_only_text(path):
            return "link-only text file (advert)"
        if is_trigger(path.name):
            return "existing Additional Material archive"
        for pattern in self.excludes:
            if fnmatch.fnmatch(path.name, pattern) or fnmatch.fnmatch(rel.as_posix(), pattern):
                return f"excluded by {pattern}"
        return None

    def executable_reason(self, path: Path) -> str | None:
        if self.args.allow_executables:
            if self.args.allow_active_documents:
                return None
            return file_active_content(path) if path.suffix.lower() != ".zip" else None
        if path.suffix.lower() in EXECUTABLE_EXT:
            return f"{path.suffix.lower()} files are executable or script content"
        inner = archive_members(path, self.seven)
        bad = [m for m in inner if Path(m).suffix.lower() in EXECUTABLE_EXT]
        if bad:
            shown = ", ".join(bad[:5]) + (f" and {len(bad) - 5} more" if len(bad) > 5 else "")
            return f"the archive contains executable or script content ({shown})"
        if inner and inner[0].startswith("(contents not checked"):
            return "it is an archive whose contents could not be checked for executable content"
        if not self.args.allow_active_documents:
            why = file_active_content(path)
            if why:
                return why
            if path.suffix.lower() == ".zip":
                docs = zip_active_documents(path)
                if docs:
                    return "the archive holds documents with active content (" + "; ".join(docs[:3]) + (" and more" if len(docs) > 3 else "") + ")"
        return None

    def screen(self, path: Path) -> None:
        why = self.executable_reason(path)
        if not why:
            return
        scan = None
        if self.vt is not None:
            scan = self.vt.check(path, sha256_of(path))
            self.scans[path] = scan
            if not self.args.quiet:
                print(f"   scanned     {path.name}: {describe_scan(scan)}", file=sys.stderr)
        if self.args.allow_clean_executables and scan and scan["status"] == "clean":
            self.allowed_clean.append((str(path), describe_scan(scan)))
        else:
            self.blocked[path] = why

    def plan_course(self, course: Path) -> list[Group]:
        videos_by_dir: dict[Path, list[Path]] = {}
        material: list[Path] = []
        for root, dirs, files in os.walk(course, followlinks=False):
            dirs[:] = sorted(d for d in dirs if not d.startswith("."))
            for name in sorted(files):
                path = Path(root, name)
                if path.is_symlink():
                    self.skipped.append((str(path), "symbolic link"))
                    continue
                if path.suffix.lower() in VIDEO_EXT:
                    videos_by_dir.setdefault(path.parent, []).append(path)
                    continue
                reason = self.excluded(path, course)
                if reason:
                    if reason not in QUIET_SKIPS:
                        self.skipped.append((str(path), reason))
                    continue
                material.append(path)

        all_videos = [v for vids in videos_by_dir.values() for v in vids]
        sections = [d for d in subdirs(course) if any(v.is_relative_to(d) for v in all_videos)]
        # Subfolders that hold material but no videos: Jellyfin shows no page for them, so their
        # files could only ride on the course page while the rest sat in section and lesson zips.
        # When a course has any, it is packaged as one archive (--package auto, the default).
        video_less = sorted({d for d in subdirs(course) if d not in sections and any(m.is_relative_to(d) for m in material)})
        single = self.args.package == "always" or (self.args.package == "auto" and bool(video_less))
        self.packaged[course] = [d.name for d in video_less] if single else []
        levels = {"course"} if single else set(self.args.levels)
        groups: dict[Path, Group] = {}

        def add(level: str, archive: Path, base: Path, path: Path) -> None:
            g = groups.setdefault(archive, Group(level, archive, base))
            g.files.append(path)
            if path in self.blocked:
                g.removed[path] = self.blocked[path]
            if path in self.scans:
                g.scans[path] = self.scans[path]

        for path in material:
            self.screen(path)
            lesson = self.match_lesson(path, videos_by_dir) if "lesson" in levels else None
            if lesson is not None:
                add("lesson", lesson.with_name(lesson.stem + TRIGGER_SUFFIX), lesson.parent, path)
                continue
            section = next((s for s in sections if path.is_relative_to(s)), None)
            if section is not None and "section" in levels:
                add("section", section / TRIGGER_FOLDER, section, path)
            elif "course" in levels:
                add("course", course / TRIGGER_FOLDER, course, path)
            else:
                self.skipped.append((str(path), "its level is not enabled (--levels)"))
        return sorted(groups.values(), key=lambda g: str(g.archive))

    def match_lesson(self, path: Path, videos_by_dir: dict[Path, list[Path]]) -> Path | None:
        def in_folder(folder: Path, stem: str) -> Path | None:
            videos = videos_by_dir.get(folder, [])
            for v in videos:
                if v.stem.lower() == stem.lower():
                    return v
            if self.args.match == "name":
                return None
            num = lesson_number(stem)
            hits = [v for v in videos if num is not None and lesson_number(v.stem) == num]
            return hits[0] if len(hits) == 1 else None

        first_stem = path.name.split(".")[0]  # "x.pdf.pdf" -> "x"
        hit = in_folder(path.parent, path.stem) or in_folder(path.parent, first_stem)
        if hit:
            return hit
        parts = path.parts  # attached_files/<lesson>/...
        for i in range(len(parts) - 3, 0, -1):
            if parts[i].lower() in ATTACHMENT_DIRS:
                found = in_folder(Path(*parts[:i]), parts[i + 1])
                if found:
                    return found
        return None


# ---- writing ---------------------------------------------------------------------------------
def write_group(g: Group, args: argparse.Namespace) -> tuple[str, str]:
    out = g.archive
    if out.exists() and not args.force:
        newest = max(f.stat().st_mtime for f in g.files)
        if not args.update or out.stat().st_mtime >= newest:
            return "kept", "exists (use --update or --force)"
    if not args.apply:
        return "would write", ""

    # A lone .zip that passed screening is reused under the trigger name (hard link, so no extra space).
    if args.reuse and len(g.files) == 1 and not g.removed and g.files[0].suffix.lower() == ".zip":
        tmp = out.with_name("." + out.name + ".tmp")
        try:
            tmp.unlink(missing_ok=True)
            try:
                os.link(g.files[0], tmp)
                how = "linked"
            except OSError:
                shutil.copy2(g.files[0], tmp)
                how = "copied"
            finish(tmp, out, args, keep_mode=True)
            return how, f"from {g.files[0].name}"
        except OSError as e:
            tmp.unlink(missing_ok=True)
            return "failed", str(e)

    fd, name = tempfile.mkstemp(prefix=".am-", suffix=".zip", dir=out.parent)
    os.close(fd)
    tmp = Path(name)
    try:
        method = zipfile.ZIP_STORED if args.compression == "store" else zipfile.ZIP_DEFLATED
        with zipfile.ZipFile(tmp, "w", compression=method, compresslevel=None if method == zipfile.ZIP_STORED else 6) as z:
            for f in g.files:
                rel = archive_name(f, g.base)
                if f in g.removed:
                    z.writestr(rel + ".REMOVED.txt", removal_note(f, g.base, g.removed[f], g.scans.get(f)))
                else:
                    z.write(f, rel)
        finish(tmp, out, args)
        return "wrote", ""
    except (OSError, zipfile.BadZipFile, ValueError) as e:
        tmp.unlink(missing_ok=True)
        return "failed", str(e)


def finish(tmp: Path, out: Path, args: argparse.Namespace, keep_mode: bool = False) -> None:
    if args.chown and not keep_mode:
        uid, _, gid = args.chown.partition(":")
        os.chown(tmp, int(uid), int(gid) if gid else -1)
    if not keep_mode:
        os.chmod(tmp, 0o664)
    os.replace(tmp, out)


# ---- course discovery ------------------------------------------------------------------------
def subdirs(folder: Path) -> list[Path]:
    try:
        return sorted(d for d in folder.iterdir() if d.is_dir() and not d.name.startswith("."))
    except OSError:
        return []


def direct_videos(folder: Path) -> bool:
    try:
        return any(f.suffix.lower() in VIDEO_EXT and f.is_file() for f in folder.iterdir())
    except OSError:
        return False


def has_videos(folder: Path) -> bool:
    for root, dirs, files in os.walk(folder):
        dirs[:] = [d for d in dirs if not d.startswith(".")]
        if any(Path(f).suffix.lower() in VIDEO_EXT for f in files):
            return True
    return False


def looks_like_course(folder: Path) -> bool:
    """Videos in the folder or its sections, and no section that is itself course-shaped."""
    if direct_videos(folder):
        return True
    kids = subdirs(folder)
    return any(direct_videos(k) for k in kids) and not any(direct_videos(g) for k in kids for g in subdirs(k))


def courses_in(target: Path, mode: str) -> list[Path]:
    if mode == "course" or (mode == "auto" and looks_like_course(target)):
        return [target]
    return [d for d in subdirs(target) if has_videos(d)]


# ---- command line ----------------------------------------------------------------------------
def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="make_additional_material.py",
        description="Build Additional Material .zip archives (lesson, section and course) from the non-video files stored with your videos.",
        epilog="Dry run unless --apply is given. Original files are never moved or deleted.")
    p.add_argument("folder", type=Path, help="a course folder, or a library folder that holds courses")
    p.add_argument("--apply", action="store_true", help="write the archives (default: only show what would happen)")
    p.add_argument("--mode", choices=["auto", "course", "library"], default="auto",
                   help="treat FOLDER as one course, as a library of courses, or decide automatically (default)")
    p.add_argument("--only", action="append", metavar="NAME", help="only these course folders (repeatable, globs allowed)")
    p.add_argument("--levels", default="lesson,section,course",
                   help="comma-separated levels to build (default all). Files of a disabled level roll up to the next")
    p.add_argument("--package", choices=["auto", "always", "never"], default="auto",
                   help="one archive for the whole course: when it has subfolders with material but no videos (auto, default), "
                        "for every course (always), or never")
    p.add_argument("--match", choices=["number", "name"], default="number",
                   help="files join a lesson by same name or lesson number (default), or by same name only")
    p.add_argument("--exclude", action="append", metavar="GLOB", help="skip files matching GLOB (name or path within the course); repeatable")
    p.add_argument("--allow-executables", action="store_true",
                   help="include executable and script content as is (default: replace each with a .REMOVED.txt note)")
    p.add_argument("--allow-active-documents", action="store_true",
                   help="include documents with active content (macros, embedded objects, external templates, "
                        "PDF JavaScript or launch actions); by default each is replaced by a note")
    p.add_argument("--allow-clean-executables", action="store_true",
                   help="with a VirusTotal key: include executable content VirusTotal knows and no engine flags")
    p.add_argument("--virustotal-key", metavar="KEY",
                   help="look executable content up on VirusTotal by SHA-256 (or set VT_API_KEY). Only fingerprints are sent")
    p.add_argument("--virustotal-upload", action="store_true",
                   help="also upload files VirusTotal does not know (32 MB max). Uploads are shared with VirusTotal's community: "
                        "only for files you may share")
    p.add_argument("--virustotal-rate", type=int, default=4, metavar="N", help="VirusTotal requests per minute (default 4, the free limit)")
    p.add_argument("--compression", choices=["deflate", "store"], default="deflate", help="compress (default), or store only")
    p.add_argument("--no-reuse", dest="reuse", action="store_false", help="always build a new zip, even when the material is one zip already")
    p.add_argument("--update", action="store_true", help="rebuild archives older than any of their files")
    p.add_argument("--force", action="store_true", help="rebuild every archive")
    p.add_argument("--chown", metavar="UID:GID", help="owner for written archives, e.g. 99:100 on Unraid")
    p.add_argument("--json", metavar="FILE", help="also write a JSON report to FILE ('-' for standard output)")
    p.add_argument("-q", "--quiet", action="store_true", help="print only problems and the summary")
    p.add_argument("-v", "--verbose", action="store_true", help="list every file, and every skipped file with the reason")
    p.add_argument("--version", action="version", version=f"%(prog)s {VERSION}")
    return p


def main(argv: list[str] | None = None) -> int:
    p = build_parser()
    args = p.parse_args(argv)
    args.levels = [x.strip() for x in args.levels.split(",") if x.strip()]
    if not args.levels or not set(args.levels) <= {"lesson", "section", "course"}:
        p.error("--levels takes lesson, section and/or course")
    if args.chown and not re.fullmatch(r"\d+(:\d+)?", args.chown):
        p.error("--chown takes UID or UID:GID (numbers)")
    has_key = bool(args.virustotal_key or os.environ.get("VT_API_KEY"))
    if (args.allow_clean_executables or args.virustotal_upload) and not has_key:
        p.error("--allow-clean-executables and --virustotal-upload need --virustotal-key (or VT_API_KEY)")
    target = args.folder.resolve()
    if not target.is_dir():
        p.error(f"{target} is not a folder")

    planner = Planner(args)
    courses = courses_in(target, args.mode)
    if args.only:
        courses = [c for c in courses if any(fnmatch.fnmatch(c.name, o) for o in args.only)]
    report: dict = {"version": VERSION, "folder": str(target), "apply": args.apply, "courses": []}
    counts: dict[str, int] = {}
    say = (lambda *a, **k: None) if args.quiet else print

    for course in courses:
        say(f"== {course.name}")
        groups = planner.plan_course(course)
        why = planner.packaged.get(course)
        if why is not None and (why or args.package == "always"):
            shown = ", ".join(why[:3]) + (f" and {len(why) - 3} more" if len(why) > 3 else "")
            say("   one archive for the whole course" + (f": no videos in {shown}" if why else " (--package always)"))
        entry = {"course": str(course), "packaged_as_one": bool(why) or args.package == "always", "archives": []}
        for g in groups:
            status, detail = write_group(g, args)
            counts[status] = counts.get(status, 0) + 1
            note = f", {len(g.removed)} replaced by notes" if g.removed else ""
            line = f"   {status:<11} {g.level:<7} {g.archive.relative_to(course)}  ({len(g.files)} files{note})" + (f"  {detail}" if detail else "")
            print(line, file=sys.stderr) if status == "failed" else say(line)
            if args.verbose:
                for f in g.files:
                    mark = "x" if f in g.removed else "+"
                    say(f"                 {mark} {f.relative_to(g.base)}" + (f"  [{g.removed[f]}]" if f in g.removed else ""))
            entry["archives"].append({
                "level": g.level, "status": status, "archive": str(g.archive),
                "files": [str(f) for f in g.files if f not in g.removed],
                "removed": [{"file": str(f), "reason": r, "scan": g.scans.get(f)} for f, r in g.removed.items()]})
        if not groups:
            say("   (no additional material found)")
        report["courses"].append(entry)

    report["skipped"] = [{"file": f, "reason": r} for f, r in planner.skipped]
    report["included_clean_executables"] = [{"file": f, "scan": r} for f, r in planner.allowed_clean]
    flagged = [f for f, r in planner.scans.items() if r["status"] == "flagged"]
    if flagged:
        print(f"\nWARNING: VirusTotal flags {len(flagged)} file(s); each was left out and replaced by a note:", file=sys.stderr)
        for f in flagged:
            print(f"   {f}  {planner.scans[f]['link']}", file=sys.stderr)
    if planner.skipped and not args.quiet:
        if args.verbose:
            print(f"\nskipped {len(planner.skipped)} file(s):")
            for f, r in planner.skipped:
                print(f"   {r}: {f}")
        else:
            print(f"\nskipped {len(planner.skipped)} file(s); -v lists them")
    summary = ", ".join(f"{v} {k}" for k, v in sorted(counts.items())) or "nothing to do"
    print(f"\n{len(courses)} course(s): {summary}" + ("" if args.apply else "  [dry run: add --apply to write]"))
    if args.json:
        data = json.dumps(report, indent=2)
        if args.json == "-":
            print(data)
        else:
            Path(args.json).write_text(data + "\n", encoding="utf-8")
    return 1 if counts.get("failed") else 0


if __name__ == "__main__":
    sys.exit(main())
