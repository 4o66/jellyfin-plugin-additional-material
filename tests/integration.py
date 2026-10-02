#!/usr/bin/env python3
"""Integration test against a Jellyfin 12.1 server that is already installed: Windows, a native
Linux install (.deb or tarball under systemd), or a container. The cross-platform counterpart of
tests/integration.sh, which also starts its own container.

Run it on the server's own machine, as a user who can write the media folder (on Windows, an
elevated local admin, so it can create symbolic links). The server must be freshly installed: the
first-run wizard not yet done, with this plugin (and optionally File Transformation) already in its
plugins folder.

  python3 tests/integration.py --base http://127.0.0.1:8096 --work C:\\am-test \\
      --log-dir C:\\ProgramData\\Jellyfin\\Server\\log --data-dir C:\\ProgramData\\Jellyfin\\Server

  The .deb install: --work /var/tmp/am-test --log-dir /var/log/jellyfin --data-dir /var/lib/jellyfin
  (both folders are group adm, so a member of adm can run it without root).

  --work      scratch folder: sample media, creds and run.json are written here (deleted first)
  --log-dir   the server's log folder (checks the index was built at startup)
  --data-dir  the server's data folder, holding plugins/ (checks signing.key's permissions). Unless
              a key is already there, a deliberately readable one is planted before the first link
              is issued, and must be replaced (as 1.2.2 and earlier wrote it on Windows)
  --server-work  the --work folder as the server sees it, if different (a container's mount)
  --ffmpeg / --sample-video  how to make the sample video (default: the server's bundled ffmpeg)
  --ft        expect the File Transformation plugin, and check index.html carries the script

Standard library only. Test-only credentials are generated per run into <work>/creds.
"""

import argparse
import glob
import hashlib
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from email.utils import formatdate

GUID = "10121f36-d2e1-4b8d-96c4-b2cc720880f3"
CLIENT = 'MediaBrowser Client="am-test", Device="am-test", DeviceId="am-test-1", Version="1.0"'
WINDOWS = os.name == "nt"
TASK_KEY = "Jellyfin.Plugin.AdditionalMaterial.IndexRefresh"

ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
ap.add_argument("--base", default="http://127.0.0.1:8096")
ap.add_argument("--work", required=True)
ap.add_argument("--server-work")
ap.add_argument("--log-dir")
ap.add_argument("--data-dir")
ap.add_argument("--ffmpeg")
ap.add_argument("--sample-video")
ap.add_argument("--ft", action="store_true")
ap.add_argument("--server-sep", help="path separator the server uses (default: this machine's)")
args = ap.parse_args()

BASE = args.base.rstrip("/")
WORK = os.path.abspath(args.work)
SWORK = args.server_work or WORK
SEP = args.server_sep or os.sep
passed = failed = 0
notes = []


def ok(name):
    global passed
    passed += 1
    print("PASS  " + name, flush=True)


def bad(name):
    global failed
    failed += 1
    print("FAIL  " + name, flush=True)


def info(name):
    notes.append(name)
    print("INFO  " + name, flush=True)


def check(name, got, want):
    if got == want:
        ok(name)
    else:
        bad(f"{name} (expected {want!r}, got {got!r})")


def http(method, path, token=None, body=None, headers=None, auth=True):
    """Returns (status, headers, bytes) without raising on HTTP errors."""
    h = {"Content-Type": "application/json"}
    if auth:
        h["Authorization"] = CLIENT + (f', Token="{token}"' if token else "")
    h.update(headers or {})
    data = None if body is None else (body if isinstance(body, bytes) else json.dumps(body).encode())
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, r.headers, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.headers, e.read()
    except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
        return 0, {}, str(e).encode()


def get(path, token=None, **kw):
    return http("GET", path, token, **kw)


def post(path, token=None, body=None, **kw):
    return http("POST", path, token, body if body is not None else b"", **kw)


def js(resp):
    try:
        return json.loads(resp[2] or b"null")
    except ValueError:
        return None


def spath(*parts):
    """A path under the work folder, as the server sees it."""
    return SEP.join([SWORK.rstrip("/\\"), *parts])


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


# ---- sample media -----------------------------------------------------------------
if os.path.lexists(WORK):
    shutil.rmtree(WORK)
media = os.path.join(WORK, "media")
T = os.path.join(media, "training", "Course A")
X = os.path.join(media, "training", "Course X")
outside = os.path.join(WORK, "outside")
for d in [os.path.join(T, "Season 1"), os.path.join(T, "Season 2"), os.path.join(media, "other", "Course B", "Season 1"),
          os.path.join(X, "Season 1"), os.path.join(outside, "Season 2"), os.path.join(outside, "Season 3")]:
    os.makedirs(d)

sample = os.path.join(WORK, "sample.mp4")
if args.sample_video:
    shutil.copy(args.sample_video, sample)
else:
    ff = args.ffmpeg or next((p for p in [r"C:\Program Files\Jellyfin\Server\ffmpeg.exe", "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                                          shutil.which("ffmpeg") or ""] if p and os.path.exists(p)), None)
    if not ff:
        sys.exit("no ffmpeg found: pass --ffmpeg or --sample-video")
    subprocess.run([ff, "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=10",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", sample], check=True)


def video(folder, name):
    shutil.copy(sample, os.path.join(folder, name + ".mp4"))


def zipbytes(entries):
    """A zip in memory. Names are written exactly as given: ZipInfo would turn a backslash into
    "/" on Windows, so the name is set after construction (some Windows zip tools write "\\")."""
    import io
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, data in entries:
            zi = zipfile.ZipInfo("placeholder", date_time=(2026, 1, 1, 0, 0, 0))
            zi.filename = name
            zi.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(zi, data)
    return buf.getvalue()


def mk(folder, name, text=None):
    with zipfile.ZipFile(os.path.join(folder, name), "w") as z:
        z.writestr("notes.txt", f"material for {text or name}\n")


S1, S2 = os.path.join(T, "Season 1"), os.path.join(T, "Season 2")
video(S1, "S01E01 - Lesson One"); video(S1, "S01E02 - Lesson Two"); video(S2, "S02E01 - Sneaky")
video(os.path.join(media, "other", "Course B", "Season 1"), "S01E01 - Other")
mk(T, "additional-material.zip", "course")                       # course
mk(S1, "additional-material.zip", "section")                     # section
# lesson: a folder, a nested zip and a removal note, for the Contents view
LESSON_NOTES = b"material for S01E01\n"
with zipfile.ZipFile(os.path.join(S1, "S01E01 - Lesson One.material.zip"), "w") as z:
    z.writestr("notes.txt", LESSON_NOTES)
    z.writestr("slides/", b"")
    z.writestr("slides/intro.txt", b"intro slides\n")
    z.writestr("tool.exe.REMOVED.txt", b"tool.exe was removed\n")
    z.writestr("labs.zip", zipbytes([("lab1.txt", b"lab one\n")]))
with open(os.path.join(S1, "S01E02 - Lesson Two.material.7z"), "wb") as f:   # .7z is not recognized in this version
    f.write(b"7z\xbc\xaf\x27\x1c" + bytes(26))
link_target = r"C:\Windows\win.ini" if WINDOWS else ("/etc/hostname" if os.path.exists("/etc/hostname") else "/etc/passwd")
try:                                                              # link escaping the library: refused
    os.symlink(link_target, os.path.join(S2, "S02E01 - Sneaky.material.zip"))
except OSError as e:
    sys.exit(f"cannot create a symbolic link ({e}); on Windows run elevated or enable Developer Mode")
mk(os.path.join(media, "other", "Course B"), "additional-material.zip")   # library not enabled: ignored

# Course X: platform cases, kept out of Course A so the browser test's counts stay the same.
XS1 = os.path.join(X, "Season 1")
video(XS1, "S01E01 - Case"); mk(XS1, "S01E01 - Case.MATERIAL.ZIP")                        # name cased differently
video(XS1, "S01E02 - Hard"); os.link(os.path.join(S1, "S01E01 - Lesson One.material.zip"),  # hard link: an ordinary file
                                     os.path.join(XS1, "S01E02 - Hard.material.zip"))
video(XS1, "S01E03 - Backslash")                                                            # entry names written with "\\"
BS_INNER = zipbytes([("sub\\lab2.txt", b"lab two\n")])
BS_ZIP = zipbytes([("docs\\guide.txt", b"guide\n"), ("top.txt", b"top\n"), ("labs.zip", BS_INNER)])
assert b"docs\\guide.txt" in BS_ZIP and b"sub\\lab2.txt" in BS_INNER, "zip was not written with backslash names"
with open(os.path.join(XS1, "S01E03 - Backslash.material.zip"), "wb") as f:
    f.write(BS_ZIP)
video(os.path.join(outside, "Season 2"), "S02E01 - Linked"); mk(os.path.join(outside, "Season 2"), "S02E01 - Linked.material.zip")
os.symlink(os.path.join(outside, "Season 2"), os.path.join(X, "Season 2"), target_is_directory=True)   # folder symlink out of the library
links = {"E_LINKED": ("Season 2", "S02E01 - Linked", "folder symlink")}
if WINDOWS:
    video(os.path.join(outside, "Season 3"), "S03E01 - Junction"); mk(os.path.join(outside, "Season 3"), "S03E01 - Junction.material.zip")
    subprocess.run(["cmd", "/c", "mklink", "/J", os.path.join(X, "Season 3"), os.path.join(outside, "Season 3")],
                   check=True, stdout=subprocess.DEVNULL)
    links["E_JUNCTION"] = ("Season 3", "S03E01 - Junction", "directory junction")
os.remove(sample)

# ---- signing key: a pre-existing key readable by others must be replaced ---------------------
KEYFILE = os.path.join(args.data_dir, "plugins", "Jellyfin.Plugin.AdditionalMaterial", "signing.key") if args.data_dir else None
SEED = None


def keyprint(path):
    """Identifies the key file. The replacement may be unreadable to this account (0600, another owner)."""
    st_ = os.stat(path)
    try:
        digest = sha(path)
    except PermissionError:
        digest = None
    return (st_.st_ino, st_.st_mtime_ns, st_.st_mode, digest)


if KEYFILE:
    if os.path.exists(KEYFILE):
        print(f"      signing.key already present (planted before first start): {KEYFILE}")
    else:
        # Links are signed with a key loaded on first use, so planting it before the first link works.
        try:
            os.makedirs(os.path.dirname(KEYFILE), exist_ok=True)
            with open(KEYFILE, "wb") as f:
                f.write(secrets.token_bytes(32))
            if not WINDOWS:
                os.chmod(KEYFILE, 0o644)
                if os.geteuid() == 0:   # the server must be able to delete it, as it could a key it wrote itself
                    st0 = os.stat(os.path.join(args.data_dir, "plugins"))
                    for p in (os.path.dirname(KEYFILE), KEYFILE):
                        os.chown(p, st0.st_uid, st0.st_gid)
            print(f"      planted a readable signing.key: {KEYFILE}")
        except PermissionError as e:
            info(f"signing key: could not plant a readable key ({e}); plant one before first start to test its replacement")
            KEYFILE = None
if KEYFILE:
    SEED = keyprint(KEYFILE)
    if WINDOWS:
        print("      " + subprocess.run(["icacls", KEYFILE], capture_output=True, text=True).stdout.strip().replace("\n", "\n      "))
    else:
        print(f"      seed mode {oct(os.stat(KEYFILE).st_mode & 0o777)}")

# ---- first-run setup ----------------------------------------------------------------
for _ in range(90):
    if get("/health", auth=False)[2] == b"Healthy":
        break
    time.sleep(2)
check("server healthy", get("/health", auth=False)[2].decode(errors="replace"), "Healthy")
public = js(get("/System/Info/Public", auth=False)) or {}
print(f"      server {public.get('Version')} on {public.get('OperatingSystem')}, id {public.get('Id')}")

PW_ADMIN, PW_USER = secrets.token_urlsafe(18), secrets.token_urlsafe(18)
creds = os.path.join(WORK, "creds")
with open(creds, "w") as f:
    f.write(f"admin {PW_ADMIN}\nusers {PW_USER}\n")
os.chmod(creds, 0o600)
post("/Startup/Configuration", body={"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
get("/Startup/User")
post("/Startup/User", body={"Name": "admin", "Password": PW_ADMIN})
post("/Startup/RemoteAccess", body={"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
post("/Startup/Complete")


def login(user, pw):
    return (js(post("/Users/AuthenticateByName", body={"Username": user, "Pw": pw})) or {}).get("AccessToken") or ""


ADMIN = login("admin", PW_ADMIN)
if len(ADMIN) > 10:
    ok("admin login")
else:
    bad("admin login")
    sys.exit(1)

for lib in ["training", "other"]:
    q = urllib.parse.urlencode({"name": lib, "collectionType": "tvshows", "paths": spath("media", lib), "refreshLibrary": "false"})
    post(f"/Library/VirtualFolders?{q}", ADMIN, {"LibraryOptions": {"EnableRealtimeMonitor": False, "EnableInternetProviders": False}})
post("/Library/Refresh", ADMIN)
expected_eps = 7 + len(links)
n, last_change, prev = 0, time.time(), -1
for _ in range(90):
    n = (js(get("/Items?Recursive=true&IncludeItemTypes=Episode", ADMIN)) or {}).get("TotalRecordCount", 0)
    if n != prev:
        prev, last_change = n, time.time()
    if n >= expected_eps or (n >= 7 and time.time() - last_change > 20):
        break
    time.sleep(2)
check("library scanned (episodes outside linked folders)", min(n, 7), 7)

libs = js(get("/Library/VirtualFolders", ADMIN)) or []
lib_id = {v["Name"]: v["ItemId"] for v in libs}
TRAINING, OTHER = lib_id.get("training"), lib_id.get("other")
print("      library locations as stored: " + "; ".join(f"{v['Name']}={v.get('Locations')}" for v in libs))


def plugin_cfg():
    return js(get(f"/Plugins/{GUID}/Configuration", ADMIN))


def cfg(**changes):
    c = plugin_cfg()
    c.update(changes)
    post(f"/Plugins/{GUID}/Configuration", ADMIN, c)


c0 = plugin_cfg()
if c0:
    ok("plugin loaded (configuration readable)")
else:
    bad("plugin loaded")
    sys.exit(1)
cfg(EnabledLibraryIds=[TRAINING])


def newuser(name, downloads, all_folders, folder=None):
    uid = js(post("/Users/New", ADMIN, {"Name": name, "Password": PW_USER}))["Id"]
    policy = js(get(f"/Users/{uid}", ADMIN))["Policy"]
    policy["EnableContentDownloading"] = downloads
    policy["EnableAllFolders"] = all_folders
    if not all_folders:
        policy["EnabledFolders"] = [folder]
    post(f"/Users/{uid}/Policy", ADMIN, policy)
    return uid


READER_ID = newuser("reader", False, True)
DL_ID = newuser("downloader", True, True)
OUTSIDE_ID = newuser("outsider", True, False, OTHER)
READER, DL, OUTSIDE = login("reader", PW_USER), login("downloader", PW_USER), login("outsider", PW_USER)

norm = (lambda p: p.lower()) if SEP == "\\" else (lambda p: p)
all_items = (js(get("/Items?Recursive=true&Fields=Path", ADMIN)) or {}).get("Items", [])


def item(kind, *parts):
    want = norm(spath("media", *parts))
    return next((i["Id"] for i in all_items if i["Type"] == kind and norm(i.get("Path") or "") == want), None)


ids = {
    "SERIES": item("Series", "training", "Course A"), "SERIESB": item("Series", "other", "Course B"),
    "SEASON1": item("Season", "training", "Course A", "Season 1"),
    "E1": item("Episode", "training", "Course A", "Season 1", "S01E01 - Lesson One.mp4"),
    "E2": item("Episode", "training", "Course A", "Season 1", "S01E02 - Lesson Two.mp4"),
    "E3": item("Episode", "training", "Course A", "Season 2", "S02E01 - Sneaky.mp4"),
    "E_CASE": item("Episode", "training", "Course X", "Season 1", "S01E01 - Case.mp4"),
    "E_HARD": item("Episode", "training", "Course X", "Season 1", "S01E02 - Hard.mp4"),
    "E_BS": item("Episode", "training", "Course X", "Season 1", "S01E03 - Backslash.mp4"),
}
for k, (season, name, _) in links.items():
    ids[k] = item("Episode", "training", "Course X", season, name + ".mp4")
for k, v in ids.items():
    if not v and k not in links:
        bad(f"test item {k} not found")
SERIES, SERIESB, SEASON1, E1, E2, E3 = (ids[k] for k in ["SERIES", "SERIESB", "SEASON1", "E1", "E2", "E3"])


def iinfo(token, item_id):
    return js(get(f"/AdditionalMaterial/Items/{item_id}", token)) or {}


def code(method, path, token=None, **kw):
    return http(method, path, token, b"" if method == "POST" else None, **kw)[0]


# ---- lookups ---------------------------------------------------------------------
check("course archive found", iinfo(ADMIN, SERIES).get("FileName"), "additional-material.zip")
check("section archive found", iinfo(ADMIN, SEASON1).get("FileName"), "additional-material.zip")
check("lesson archive found", iinfo(ADMIN, E1).get("FileName"), "S01E01 - Lesson One.material.zip")
check(".7z is ignored", iinfo(ADMIN, E2).get("Available"), False)
check("link escaping library refused", iinfo(ADMIN, E3).get("Available"), False)
check("library not enabled: ignored", iinfo(ADMIN, SERIESB).get("Available"), False)
i = iinfo(ADMIN, SERIES)
check("style defaults to two colors", f"{i.get('ButtonStyle')} {i.get('AccentColor')}", "color #00A4DC")
s = js(get("/AdditionalMaterial/web/settings", ADMIN)) or {}
check("display settings defaults", [s.get("ButtonStyle"), s.get("ShowOnParents"), s.get("ShowOnCards"), s.get("ShowInLists")], ["color", "all", True, True])
cfg(ButtonStyle="mono")
check("one-color style saved", iinfo(ADMIN, SERIES).get("ButtonStyle"), "mono")
cfg(ButtonStyle="color", AccentColor="#DB781B")
i = iinfo(ADMIN, SERIES)
check("two-color style and accent saved", f"{i.get('ButtonStyle')} {i.get('AccentColor')}", "color #DB781B")


# ---- listings (tree) and batch status ------------------------------------------------
def tree(token, item_id):
    return js(get(f"/AdditionalMaterial/Items/{item_id}/Tree", token)) or {}


def shape(t):
    return [t.get("Self") is not None, [r.get("Level") for g in t.get("Groups", []) for r in g.get("Items", [])]]


check("tree: course lists self + section + lesson", shape(tree(ADMIN, SERIES)), [True, ["section", "lesson"]])
t = tree(ADMIN, SERIES)
check("tree: rows carry names and item ids", (t.get("Groups") or [{}])[0].get("Items", [{}, {}])[1].get("ItemId"), E1)
check("tree: section lists its lesson", shape(tree(ADMIN, SEASON1)), [True, ["lesson"]])
t = tree(ADMIN, E1)
check("tree: lesson has only itself", [t.get("Self") is not None, len(t.get("Groups", []))], [True, 0])
check("tree: type and name", tree(ADMIN, SERIES).get("Type"), "Series")


def st(token):
    return js(post("/AdditionalMaterial/Items/Status", token, {"Ids": [SERIES, SEASON1, E1, E3, SERIESB]})) or {}


r = st(ADMIN)
check("status: own and below counts", [r.get(SERIES, {}).get("Own"), r.get(SERIES, {}).get("Below"), r.get(SEASON1, {}).get("Below"), r.get(E1, {}).get("Below")], [True, 2, 1, 0])
check("status: items without material omitted", [E3 in r, SERIESB in r], [False, False])
check("status: no access, nothing returned", len(st(OUTSIDE)), 0)
cfg(ShowOnParents="section"); time.sleep(1)
check("levels=section: course shows only its own", len(tree(ADMIN, SERIES).get("Groups", [])), 0)
check("levels=section: section still lists lesson", len(shape(tree(ADMIN, SEASON1))[1]), 1)
cfg(ShowOnParents="item"); time.sleep(1)
check("levels=item: section shows only its own", len(tree(ADMIN, SEASON1).get("Groups", [])), 0)
cfg(ShowOnParents="all")
check("outsider cannot list the course", code("GET", f"/AdditionalMaterial/Items/{SERIES}/Tree", OUTSIDE), 404)
cfg(AccentColor="red;}<script>")
check("invalid accent falls back", iinfo(ADMIN, SERIES).get("AccentColor"), "#00A4DC")
cfg(AccentColor="#DB781B")
check("admin may download", iinfo(ADMIN, SERIES).get("CanDownload"), True)
check("no-download user: CanDownload false", iinfo(READER, SERIES).get("CanDownload"), False)
check("no-download user: link refused", code("POST", f"/AdditionalMaterial/Items/{SERIES}/Link", READER), 403)
check("user without library access: 404", code("GET", f"/AdditionalMaterial/Items/{SERIES}", OUTSIDE), 404)
check("unauthenticated lookup: 401", code("GET", f"/AdditionalMaterial/Items/{SERIES}", auth=False), 401)

# ---- downloads ---------------------------------------------------------------------
L1 = os.path.join(S1, "S01E01 - Lesson One.material.zip")
TOKEN = (js(post(f"/AdditionalMaterial/Items/{E1}/Link", DL)) or {}).get("Token", "")
status, hdrs, body = get(f"/AdditionalMaterial/Download/{TOKEN}", auth=False)
check("download: 200", status, 200)
if KEYFILE:
    check("signing key: readable-by-others key replaced", os.path.exists(KEYFILE) and keyprint(KEYFILE) != SEED, True)
check("download: bytes match the file", hashlib.sha256(body).hexdigest(), sha(L1))
cd = hdrs.get("Content-Disposition", "") if hdrs else ""
check("download: sent as attachment", cd.lower().startswith("attachment"), True)
if re.match(r'attachment; filename="?[^"]+ - S01E01 - [^"]+ - Additional Material\.zip', cd, re.I):
    ok("download: descriptive file name")
else:
    bad(f"download: descriptive file name ({cd})")
check("index.html is never answered 'not modified'",
      get("/web/index.html", auth=False, headers={"If-Modified-Since": formatdate(usegmt=True)})[0], 200)
check("download: content type zip", (hdrs.get("Content-Type", "") if hdrs else "").lower().startswith("application/zip"), True)
check("download: range request 206", get(f"/AdditionalMaterial/Download/{TOKEN}", auth=False, headers={"Range": "bytes=0-9"})[0], 206)
ch = TOKEN[20]
r_ = "B" if ch == "A" else "A"
check("tampered token: 404", code("GET", f"/AdditionalMaterial/Download/{TOKEN[:20]}{r_}{TOKEN[21:]}", auth=False), 404)
swap = str.maketrans("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-",
                     "BADCFEHGJILKNMPORQTSVUXWZYbadcfehgjilknmporqtsvuxwzy1032547698-_")
check("non-canonical token: 404", code("GET", f"/AdditionalMaterial/Download/{TOKEN[:-1]}{TOKEN[-1].translate(swap)}", auth=False), 404)
check("garbage token: 404", code("GET", "/AdditionalMaterial/Download/not-a-token", auth=False), 404)


# ---- contents ----------------------------------------------------------------------------
def cont(token, item_id):
    return js(get(f"/AdditionalMaterial/Items/{item_id}/Contents", token)) or {}


def paths(c):
    return sorted(e.get("Path") for e in c.get("Entries", []))


def children(c, path):
    return [[k.get("Path") for k in (e.get("Children") or [])] if e.get("Children") is not None else None
            for e in c.get("Entries", []) if e.get("Path") == path]


def entry(tok, name, **kw):
    return get(f"/AdditionalMaterial/Download/{tok}?entry=" + urllib.parse.quote(name, safe=""), auth=False, **kw)


c = cont(ADMIN, E1)
check("contents: lesson files listed", paths(c), ["labs.zip", "notes.txt", "slides/intro.txt", "tool.exe.REMOVED.txt"])
check("contents: nested zip listed", children(c, "labs.zip"), [["lab1.txt"]])
check("contents: sizes are real", next((e.get("Size") for e in c.get("Entries", []) if e.get("Path") == "notes.txt"), None), len(LESSON_NOTES))
check("contents: course archive", [e.get("Path") for e in cont(ADMIN, SERIES).get("Entries", [])], ["notes.txt"])
check("contents: no material: 404", code("GET", f"/AdditionalMaterial/Items/{E3}/Contents", ADMIN), 404)
check("contents: no access: 404", code("GET", f"/AdditionalMaterial/Items/{E1}/Contents", OUTSIDE), 404)
check("contents: unauthenticated 401", code("GET", f"/AdditionalMaterial/Items/{E1}/Contents", auth=False), 401)
c = cont(READER, E1)
check("contents: reader may list", [c.get("CanDownload"), len(c.get("Entries", []))], [False, 4])
TOKEN2 = (js(post(f"/AdditionalMaterial/Items/{E1}/Link", DL)) or {}).get("Token", "")
status, hdrs, body = entry(TOKEN2, "slides/intro.txt")
check("entry download: 200", status, 200)
check("entry download: bytes match", body, b"intro slides\n")
cd = hdrs.get("Content-Disposition", "") if hdrs else ""
if re.match(r'attachment; filename="?intro\.txt', cd, re.I):
    ok("entry download: attachment named after the file")
else:
    bad(f"entry download: attachment named after the file ({cd})")
check("entry download: opaque content type", (hdrs.get("Content-Type", "") if hdrs else "").lower().startswith("application/octet-stream"), True)
check("entry download: file inside the nested zip", entry(TOKEN2, "labs.zip!/lab1.txt")[2], b"lab one\n")
check("entry download: no such entry 404", entry(TOKEN2, "nope.txt")[0], 404)
check("entry download: folder is not a file", entry(TOKEN2, "slides")[0], 404)
check("entry download: folder entry is not a file", entry(TOKEN2, "slides/")[0], 404)
check("entry download: path tricks 404", entry(TOKEN2, "../../etc/passwd")[0], 404)
check("entry download: needs a valid token", entry("not-a-token", "notes.txt")[0], 404)

# Entry names a Windows zip tool wrote with "\\" are listed and served with "/".
E_BS = ids["E_BS"]
c = cont(ADMIN, E_BS)
check("backslash names: listed with /", paths(c), ["docs/guide.txt", "labs.zip", "top.txt"])
check("backslash names: inside a nested zip, listed with /", children(c, "labs.zip"), [["sub/lab2.txt"]])
tok_bs = (js(post(f"/AdditionalMaterial/Items/{E_BS}/Link", ADMIN)) or {}).get("Token", "")
status, hdrs, body = entry(tok_bs, "docs/guide.txt")
check("backslash names: entry download by its / path", [status, body], [200, b"guide\n"])
cd = hdrs.get("Content-Disposition", "") if hdrs else ""
if re.match(r'attachment; filename="?guide\.txt', cd, re.I):
    ok("backslash names: attachment named after the file")
else:
    bad(f"backslash names: attachment named after the file ({cd})")
check("backslash names: nested entry download by its / path", entry(tok_bs, "labs.zip!/sub/lab2.txt")[2], b"lab two\n")
check("backslash names: the raw \\ spelling is not a second path", entry(tok_bs, "docs\\guide.txt")[0], 404)

cfg(ListNestedZips=False)
check("nested listing off: shown as a plain file", children(cont(ADMIN, E1), "labs.zip"), [None])
check("nested listing off: inner file not served", entry(TOKEN2, "labs.zip!/lab1.txt")[0], 404)
cfg(ListNestedZips=True)
check("index refresh: readers refused", code("POST", "/AdditionalMaterial/Index/Refresh", READER), 403)
check("index refresh: admin accepted", code("POST", "/AdditionalMaterial/Index/Refresh", ADMIN), 202)
for _ in range(20):
    if (js(get("/AdditionalMaterial/Index/Status", ADMIN)) or {}).get("Running") is False:
        break
    time.sleep(1)
s = js(get("/AdditionalMaterial/Index/Status", ADMIN)) or {}
check("index status: counts folders and zips", [(s.get("Folders") or 0) > 0, (s.get("Zips") or 0) > 0, s.get("FinishedUtc") is not None], [True, True, True])

pol = js(get(f"/Users/{DL_ID}", ADMIN))["Policy"]
pol["EnableContentDownloading"] = False
post(f"/Users/{DL_ID}/Policy", ADMIN, pol)
check("revoked permission: existing link dies", code("GET", f"/AdditionalMaterial/Download/{TOKEN}", auth=False), 404)
check("revoked permission: entry links die too", entry(TOKEN2, "notes.txt")[0], 404)


# ---- folder index --------------------------------------------------------------------
def refresh():
    tid = next((t["Id"] for t in js(get("/ScheduledTasks", ADMIN)) or [] if t.get("Key") == TASK_KEY), None)
    post(f"/ScheduledTasks/Running/{tid}", ADMIN)
    for _ in range(30):
        time.sleep(1)
        if (js(get(f"/ScheduledTasks/{tid}", ADMIN)) or {}).get("State") == "Idle":
            break


if args.log_dir:
    # log_<date>.log by default; the .deb's /etc/jellyfin/logging.json names them jellyfin<date>.log.
    logs = sorted(glob.glob(os.path.join(args.log_dir, "*.log")), key=os.path.getmtime)
    text = open(logs[-1], encoding="utf-8", errors="replace").read() if logs else ""
    check("index built at startup", "Additional Material: indexed" in text, True)
    if KEYFILE:
        line = next((ln.strip() for ln in text.splitlines() if "Additional Material: replacing" in ln), "")
        print("      " + (line or "(no replacement line logged)"))
        check("signing key: replacement logged", "readable by other accounts" in line, True)
else:
    info("index built at startup: not checked (no --log-dir)")
check("refresh task listed", next((t["Name"] for t in js(get("/ScheduledTasks", ADMIN)) or [] if t.get("Key") == TASK_KEY), None), "Refresh additional material")
L2 = os.path.join(S1, "S01E02 - Lesson Two.material.zip")
held = os.path.join(WORK, "held.zip")
shutil.copy(L1, L2)
check("new zip: not seen before a refresh", iinfo(ADMIN, E2).get("Available"), False)
refresh()
check("new zip: seen after the refresh task", iinfo(ADMIN, E2).get("Available"), True)
shutil.move(L1, held)
check("deleted zip: page still shows it", iinfo(ADMIN, E1).get("Available"), True)
check("deleted zip: link refused (disk checked)", code("POST", f"/AdditionalMaterial/Items/{E1}/Link", ADMIN), 404)
cfg(IndexRefreshMinutes=0)
iinfo(ADMIN, E1); time.sleep(2)
check("stale entry re-checked in the background", iinfo(ADMIN, E1).get("Available"), False)
shutil.move(held, L1); os.remove(L2)
cfg(IndexRefreshMinutes=10); refresh()
check("index restored", [iinfo(ADMIN, E1).get("Available"), iinfo(ADMIN, E2).get("Available")], [True, False])

# ---- web client ------------------------------------------------------------------------
check("script served", code("GET", "/AdditionalMaterial/web/additional-material.js", auth=False), 200)
check("strings: English", (js(get("/AdditionalMaterial/web/strings", auth=False)) or {}).get("config.save"), "Save")
check("strings: unknown language falls back", (js(get("/AdditionalMaterial/web/strings?lang=xx-YY", auth=False)) or {}).get("config.save"), "Save")
check("strings: bad tag ignored", code("GET", "/AdditionalMaterial/web/strings?lang=../../etc", auth=False), 200)
if args.ft:
    time.sleep(5)
    check("index.html carries the script (File Transformation)", b'plugin="AdditionalMaterial"' in get("/web/index.html", auth=False)[2], True)

# ---- platform cases ---------------------------------------------------------------------
check("name cased differently on disk: found", iinfo(ADMIN, ids["E_CASE"]).get("FileName"), "S01E01 - Case.MATERIAL.ZIP")
tok = (js(post(f"/AdditionalMaterial/Items/{ids['E_CASE']}/Link", ADMIN)) or {}).get("Token", "")
check("name cased differently on disk: downloads", hashlib.sha256(get(f"/AdditionalMaterial/Download/{tok}", auth=False)[2]).hexdigest(),
      sha(os.path.join(XS1, "S01E01 - Case.MATERIAL.ZIP")))
check("hard link: served like an ordinary file", iinfo(ADMIN, ids["E_HARD"]).get("Available"), True)
for k, (_, _, what) in links.items():
    if not ids.get(k):
        info(f"{what} out of the library: Jellyfin did not scan the episode inside it, so nothing to serve")
        continue
    info(f"{what} out of the library: Jellyfin scanned the episode inside it")
    check(f"{what} out of the library: material refused (lookup)", iinfo(ADMIN, ids[k]).get("Available"), False)
    check(f"{what} out of the library: link refused", code("POST", f"/AdditionalMaterial/Items/{ids[k]}/Link", ADMIN), 404)

# signing.key exists once a link has been issued.
if args.data_dir:
    keys = glob.glob(os.path.join(args.data_dir, "plugins", "**", "signing.key"), recursive=True)
    if not keys:
        bad("signing.key found under <data-dir>/plugins")
    elif WINDOWS:
        acl = subprocess.run(["icacls", keys[0]], capture_output=True, text=True).stdout
        print("      " + acl.strip().replace("\n", "\n      "))
        broad = [ln.strip() for ln in acl.splitlines() if re.search(r"BUILTIN\\Users|Everyone|Authenticated Users|\\Users:", ln)]
        check("signing.key: not readable by ordinary local users", broad, [])
        check("signing.key: no inherited entries", [ln.strip() for ln in acl.splitlines() if "(I)" in ln], [])
        prot = subprocess.run(["powershell", "-NoProfile", "-Command", f"(Get-Acl -LiteralPath '{keys[0]}').AreAccessRulesProtected"],
                              capture_output=True, text=True).stdout.strip()
        check("signing.key: inheritance off", prot, "True")
    else:
        print("      " + keys[0])
        st_ = os.stat(keys[0])
        check("signing.key: mode 600", oct(st_.st_mode & 0o777), "0o600")
else:
    info("signing.key: not checked (no --data-dir)")

# ---- what the browser test needs ------------------------------------------------------
rel = {"course": ["training", "Course A", "additional-material.zip"],
       "lesson": ["training", "Course A", "Season 1", "S01E01 - Lesson One.material.zip"]}
with open(os.path.join(WORK, "run.json"), "w") as f:
    json.dump({"base": BASE, "media": spath("media"), "sep": SEP,
               "sha256": {k: sha(os.path.join(media, *v)) for k, v in rel.items()}}, f, indent=1)

print(f"\npassed {passed}, failed {failed}" + (f", {len(notes)} notes" if notes else ""))
print(f"test server left running on {BASE}; uninstall it, or reset it, before running this again")
sys.exit(0 if failed == 0 else 1)
