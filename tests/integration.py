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
  --service-sid  Windows: the account the server runs as (default *S-1-5-20, NetworkService)
  --fake-vt PORT  run tests/fake_virustotal.py on 127.0.0.1:PORT and check VirusTotal lookups. The
              server must have been started with AM_VT_BASE=http://127.0.0.1:PORT in its
              environment (Windows: the service's Environment value in the registry; systemd: a
              drop-in with Environment=). Without it, the VirusTotal checks are skipped.

Folder watching (1.6.1) needs the training library's real-time monitoring, which the test turns on
near the end; the server must be able to watch the work folder (a local disk, not a share).

Building archives needs some course folders the server can write and some it cannot, which
integration.sh gets from a read-only bind mount. Here permissions do it. Windows: the training
library's folder stops inheriting (so C:\\'s Modify for Authenticated Users does not apply), the
service gets read, then Modify on Courses C, F and G; Course D gets a deny on creating files, and
Course E (Windows only) is never granted anything. Linux: C, F and G are made 0777 (chowned to the
server's account when run as root); D stays this account's, 0755.

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
ap.add_argument("--service-sid", help="Windows: the account the server runs as (default *S-1-5-20, NetworkService)")
ap.add_argument("--fake-vt", type=int, metavar="PORT", help="run a fake VirusTotal on 127.0.0.1:PORT (the server needs AM_VT_BASE)")
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


def put(path, data):
    with open(path, "wb") as f:
        f.write(data if isinstance(data, bytes) else data.encode())


# Course C has no archives at all: the plugin builds them when building is on.
TC = os.path.join(media, "training", "Course C"); TCS = os.path.join(TC, "Season 1")
os.makedirs(TCS)
video(TCS, "S01E01 - Build Lesson"); video(TCS, "S01E02 - Two Files")
put(os.path.join(TCS, "S01E01 - Build Lesson.pdf"), b"%PDF-1.4\nlesson one handout\n")    # one file: handed out as is
put(os.path.join(TCS, "S01E02 - Two Files.txt"), "lesson two notes\n")
put(os.path.join(TCS, "S01E02 - Two Files.docm"), b"PK\x03\x04 macro document")            # replaced by a note
put(os.path.join(TCS, "notes.txt"), "section notes\n"); put(os.path.join(TCS, "section-slides.md"), "# slides\n")
put(os.path.join(TCS, "Bonus Resources.txt"), "https://freecourseweb.com\nhttps://devcourseweb.com\n")   # advert: left out
put(os.path.join(TCS, "3. Practice Quiz.html"), '<script type="text/javascript">window.location = "https://www.udemy.com/course/x/quiz/3";</script>\n')   # redirect: a link
put(os.path.join(TC, "readme.txt"), "course readme\n")
# Courses the server cannot write: built archives go to the plugin's cache. A container mounts
# Course D read-only; a native server is kept out by permissions instead (set below). On Windows,
# D has an explicit deny and E simply never gets a grant: the usual case for a media share.
unwritable = {"D": ("Course D", "S01E01 - Read Only")}
if WINDOWS:
    unwritable["E"] = ("Course E", "S01E01 - Not Granted")
for _, (course, stem) in unwritable.items():
    d = os.path.join(media, "training", course, "Season 1"); os.makedirs(d)
    video(d, stem); put(os.path.join(d, stem + ".txt"), "read-only notes\n"); put(os.path.join(d, stem + ".md"), "more\n")
# Course F: links inside a course the plugin builds (never packed), and nested folders (entry names use "/").
TF = os.path.join(media, "training", "Course F"); TFS = os.path.join(TF, "Season 1")
os.makedirs(os.path.join(TFS, "handouts", "week 1"))
video(TFS, "S01E01 - Links")
put(os.path.join(TFS, "S01E01 - Links.txt"), "links notes\n"); put(os.path.join(TFS, "S01E01 - Links.md"), "# links\n")
put(os.path.join(TFS, "handouts", "week 1", "sheet.txt"), "sheet one\n"); put(os.path.join(TFS, "handouts", "week 1", "sheet2.md"), "sheet two\n")
secret = os.path.join(outside, "secret"); os.makedirs(secret)
put(os.path.join(secret, "secret.txt"), "outside the library\n"); put(os.path.join(secret, "S01E01 - Links.secret.txt"), "outside\n")
os.symlink(os.path.join(secret, "secret.txt"), os.path.join(TFS, "S01E01 - Links.pdf"))         # file link out of the course
os.symlink(secret, os.path.join(TFS, "linked"), target_is_directory=True)                      # folder link out of the course
if WINDOWS:
    secret2 = os.path.join(outside, "secret2"); os.makedirs(secret2); put(os.path.join(secret2, "secret2.txt"), "outside too\n")
    subprocess.run(["cmd", "/c", "mklink", "/J", os.path.join(TFS, "junction"), secret2], check=True, stdout=subprocess.DEVNULL)
os.remove(sample)

# Who may write where. Windows: the training library gets only read for the service (no inherited
# Modify from C:\), then Modify is granted on Courses C and F; D gets an explicit deny, E nothing.
# Linux: C and F are made writable for the server's account; D is left to this account (or root), 0755.
SERVICE_SID = "*S-1-5-20"   # NetworkService, the installer's default; another account: --service-sid
if WINDOWS:
    sid = args.service_sid or SERVICE_SID
    troot = os.path.join(media, "training")
    subprocess.run(["icacls", troot, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F",
                    "*S-1-5-32-545:(OI)(CI)RX", f"{sid}:(OI)(CI)RX", "/Q"], check=True, stdout=subprocess.DEVNULL)
    # No /T: Windows passes inheritable entries down itself. /T would also strip each file's
    # inherited entries and then fail to apply (OI)(CI) grants to files, leaving them unreadable.
    for d in (TC, TF):
        subprocess.run(["icacls", d, "/grant", f"{sid}:(OI)(CI)M", "/Q"], check=True, stdout=subprocess.DEVNULL)
    # Deny only creating files and folders (WD, AD). Plain W is FILE_GENERIC_WRITE, which includes
    # SYNCHRONIZE and READ_CONTROL: denying it stops the server opening the files at all.
    subprocess.run(["icacls", os.path.join(media, "training", "Course D"), "/deny", f"{sid}:(OI)(CI)(WD,AD)", "/Q"],
                   check=True, stdout=subprocess.DEVNULL)
    print("      a file below them: " + subprocess.run(["icacls", os.path.join(TCS, "notes.txt")], capture_output=True, text=True).stdout.splitlines()[0])
    for c in ("Course C", "Course D", "Course E"):
        acl = subprocess.run(["icacls", os.path.join(media, "training", c)], capture_output=True, text=True).stdout
        print("      " + "\n        ".join(ln.strip() for ln in acl.splitlines() if ln.strip() and "Successfully" not in ln))
else:
    server_uid = os.stat(os.path.join(args.data_dir, "plugins")).st_uid if args.data_dir and os.path.isdir(os.path.join(args.data_dir, "plugins")) else None
    for d in (TC, TF):
        for root, dirs, _ in os.walk(d):
            if os.geteuid() == 0 and server_uid is not None:
                os.chown(root, server_uid, -1)
            else:
                os.chmod(root, 0o777)
    for root, dirs, _ in os.walk(os.path.join(media, "training", "Course D")):
        os.chmod(root, 0o755)
    st_d = os.stat(os.path.join(media, "training", "Course D", "Season 1"))
    print(f"      Course D/Season 1: uid {st_d.st_uid}, mode {oct(st_d.st_mode & 0o777)}; the server's uid {server_uid}")

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
plain_eps = 7 + 2 + len(unwritable) + 1   # A, B, X; C; D (and E); F
expected_eps = plain_eps + len(links)
n, last_change, prev = 0, time.time(), -1
for _ in range(90):
    n = (js(get("/Items?Recursive=true&IncludeItemTypes=Episode", ADMIN)) or {}).get("TotalRecordCount", 0)
    if n != prev:
        prev, last_change = n, time.time()
    if n >= expected_eps or (n >= plain_eps and time.time() - last_change > 20):
        break
    time.sleep(2)
check("library scanned (episodes outside linked folders)", min(n, plain_eps), plain_eps)

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


# ---- building archives ----------------------------------------------------------------
def raw_names(data):
    """Entry names exactly as stored in the zip's central directory. zipfile turns a "\\" into "/"
    when it reads a zip on Windows, which would hide a backslash the plugin wrote."""
    import struct
    eocd = data.rfind(b"PK\x05\x06")
    if eocd < 0:
        return ["(not a zip)"]
    count, _, p = struct.unpack("<HII", data[eocd + 10:eocd + 20])
    names = []
    for _ in range(count):
        if data[p:p + 4] != b"PK\x01\x02":
            return names + ["(bad central directory)"]
        ln, le, lc = struct.unpack("<HHH", data[p + 28:p + 34])
        names.append(data[p + 46:p + 46 + ln].decode("utf-8", errors="replace"))
        p += 46 + ln + le + lc
    return sorted(names)


def readb(path):
    with open(path, "rb") as f:
        return f.read()


def zip_text(data, name):
    import io
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        return z.read(name).decode()


def manifest(*folders):
    """Every entry below the folders, links not followed: (path, size, mtime), or 'link'."""
    out = []
    for top in folders:
        for root, dirs, files in os.walk(top):
            for n in dirs + files:
                p = os.path.join(root, n)
                st_ = os.lstat(p)
                linked = os.path.islink(p) or bool(getattr(st_, "st_file_attributes", 0) & 0x400)   # 0x400: reparse point (junction)
                out.append((os.path.relpath(p, media), "link" if linked else ("dir" if n in dirs else (st_.st_size, st_.st_mtime_ns))))
    return sorted(out, key=str)


def litter(top):
    """The builder's probe and temporary files, if any were left behind."""
    return sorted(os.path.relpath(os.path.join(r, n), top) for r, _, fs in os.walk(top) for n in fs
                  if n.startswith(".am-probe-") or (n.startswith(".am-") and n.endswith(".zip.tmp")))


def bstat():
    return js(get("/AdditionalMaterial/Build/Status", ADMIN)) or {}


def wait_build(done):
    s = {}
    for _ in range(90):
        s = bstat()
        if s.get("Running") is False and done(s):
            break
        time.sleep(1)
    return s


def link(item_id, token=None):
    return (js(post(f"/AdditionalMaterial/Items/{item_id}/Link", token or ADMIN)) or {}).get("Token", "")


def kept(c):
    return sorted(e.get("Path") for e in c.get("Entries", []) if not e.get("LeftOut"))


LEFT_ALONE = [os.path.join(media, "training", "Course A"), os.path.join(media, "training", "Course X")]
before_left_alone = manifest(*LEFT_ALONE)
CACHE = os.path.join(args.data_dir, "plugins", "Jellyfin.Plugin.AdditionalMaterial", "archives") if args.data_dir else None
EC1 = item("Episode", "training", "Course C", "Season 1", "S01E01 - Build Lesson.mp4")
EC2 = item("Episode", "training", "Course C", "Season 1", "S01E02 - Two Files.mp4")
SEASONC, SERIESC = item("Season", "training", "Course C", "Season 1"), item("Series", "training", "Course C")
EF1 = item("Episode", "training", "Course F", "Season 1", "S01E01 - Links.mp4")
SEASONF, SERIESF = item("Season", "training", "Course F", "Season 1"), item("Series", "training", "Course F")
for k, v in {"EC1": EC1, "EC2": EC2, "SEASONC": SEASONC, "SERIESC": SERIESC, "EF1": EF1, "SEASONF": SEASONF}.items():
    if not v:
        bad(f"test item {k} not found")
check("building off: nothing offered for a course without archives", iinfo(ADMIN, EC1).get("Available"), False)
cfg(BuildArchives=True, BuildInBackground=False, BuiltArchiveLocation="beside", ShowLeftOutFiles="everyone")
post("/AdditionalMaterial/Index/Refresh", ADMIN); time.sleep(2)
s = wait_build(lambda s: (s.get("Planned") or 0) > 0)
print(f"      build status after planning: {json.dumps(s)}")
check("building on: archives planned", (s.get("Planned") or 0) >= 3, True)
check("a course with its own archives is left alone", (s.get("CoursesLeftAlone") or 0) >= 1, True)
check("left alone: no archive invented for Course A's Lesson Two", iinfo(ADMIN, E2).get("Available"), False)
i = iinfo(ADMIN, EC1)
check("one-file material: offered as the file itself", [i.get("FileName"), i.get("Format")], ["S01E01 - Build Lesson.pdf", "pdf"])
check("nothing written before a download", [n for n in os.listdir(TCS) if n.lower().endswith("material.zip")], [])
status, hdrs, body = get(f"/AdditionalMaterial/Download/{link(EC1)}", auth=False)
check("one-file download: the original file", hashlib.sha256(body).hexdigest(), sha(os.path.join(TCS, "S01E01 - Build Lesson.pdf")))
cd = hdrs.get("Content-Disposition", "") if hdrs else ""
if re.search(r'filename="?S01E01 - Build Lesson\.pdf', cd):
    ok("one-file download: under its own name")
else:
    bad(f"one-file download: under its own name ({cd})")
check("planned lesson archive offered", iinfo(ADMIN, EC2).get("FileName"), "S01E02 - Two Files.material.zip")
c = cont(ADMIN, EC2)
check("planned contents: files, the macro document as a note", kept(c), ["S01E02 - Two Files.docm.REMOVED.txt", "S01E02 - Two Files.txt"])
check("planned contents: the removal reason", [e.get("Reason") for e in c.get("Entries", []) if (e.get("Path") or "").endswith(".REMOVED.txt")],
      [".docm files are executable or script content"])
c = cont(ADMIN, SEASONC)
check("section: its file and the left-out advert", [[e.get("Path"), e.get("LeftOut")] for e in c.get("Entries", [])],
      [["notes.txt", False], ["section-slides.md", False], ["3. Practice Quiz.html", True], ["Bonus Resources.txt", True]])
check("left-out files carry the rule's reason", [e.get("Reason") for e in c.get("Entries", []) if e.get("Path") == "Bonus Resources.txt"],
      ["link-only text file (advert) [rule link-only-text]"])
check("redirect placeholder: offered as a link to the site", [e.get("Link") for e in c.get("Entries", []) if e.get("Path") == "3. Practice Quiz.html"],
      ["https://www.udemy.com/course/x/quiz/3"])
check("an advert carries no link", [e.get("Link") for e in c.get("Entries", []) if e.get("Path") == "Bonus Resources.txt"], [None])
cfg(ShowLeftOutFiles="admins")
check("left-out files: admins only (reader)", len([e for e in cont(READER, SEASONC).get("Entries", []) if e.get("LeftOut")]), 0)
check("left-out files: admins only (admin)", len([e for e in cont(ADMIN, SEASONC).get("Entries", []) if e.get("LeftOut")]), 2)
cfg(ShowLeftOutFiles="nobody")
check("left-out files: nobody", len([e for e in cont(ADMIN, SEASONC).get("Entries", []) if e.get("LeftOut")]), 0)
cfg(ShowLeftOutFiles="everyone")
check("course: the readme", [e.get("Path") for e in cont(ADMIN, SERIESC).get("Entries", [])], ["readme.txt"])
TC2 = link(EC2)
ZC = os.path.join(TCS, "S01E02 - Two Files.material.zip")
status, _, zc1 = get(f"/AdditionalMaterial/Download/{TC2}", auth=False)
check("first download builds the archive beside the video", [status, os.path.exists(ZC)], [200, True])
check("download is the built archive", hashlib.sha256(zc1).hexdigest(), sha(ZC) if os.path.exists(ZC) else None)
check("built archive: entry names", raw_names(zc1), ["S01E02 - Two Files.docm.REMOVED.txt", "S01E02 - Two Files.txt"])
try:
    note = zip_text(zc1, "S01E02 - Two Files.docm.REMOVED.txt")
except (KeyError, zipfile.BadZipFile) as e:
    note = str(e)
check("built archive: the note, as the script writes it", note.startswith("REMOVED: S01E02 - Two Files.docm"), True)
m1 = os.stat(ZC).st_mtime_ns if os.path.exists(ZC) else None
time.sleep(1); get(f"/AdditionalMaterial/Download/{TC2}", auth=False)
check("second download: not rebuilt", os.stat(ZC).st_mtime_ns if os.path.exists(ZC) else None, m1)
check("entry download from a built archive: the original file", entry(TC2, "S01E02 - Two Files.txt")[2], b"lesson two notes\n")
check("entry download: the note for a removed file", entry(TC2, "S01E02 - Two Files.docm.REMOVED.txt")[2].startswith(b"REMOVED: "), True)
check("entry download: a left-out file is not served", entry(TC2, "Bonus Resources.txt")[0], 404)
put(os.path.join(TCS, "S01E02 - Two Files.txt"), "lesson two notes, revised\n"); time.sleep(1)
get(f"/AdditionalMaterial/Download/{TC2}", auth=False)
try:
    revised = zip_text(readb(ZC), "S01E02 - Two Files.txt")
except (OSError, KeyError, zipfile.BadZipFile) as e:
    revised = str(e)
check("changed file: archive rebuilt", revised, "lesson two notes, revised\n")

# Built files beside the videos: who owns them, and with what permissions.
if os.path.exists(ZC):
    if WINDOWS:
        acl = subprocess.run(["icacls", ZC], capture_output=True, text=True).stdout
        owner = subprocess.run(["powershell", "-NoProfile", "-Command", f"(Get-Acl -LiteralPath '{ZC}').Owner"], capture_output=True, text=True).stdout.strip()
        info(f"built archive beside the video: owner {owner}; " + " | ".join(ln.replace(ZC, "").strip() for ln in acl.splitlines() if ln.strip() and "Successfully" not in ln))
    else:
        st_ = os.stat(ZC)
        info(f"built archive beside the video: uid {st_.st_uid} gid {st_.st_gid} mode {oct(st_.st_mode & 0o777)}")
        if server_uid is not None:
            check("built archive: owned by the server's account", st_.st_uid, server_uid)
        check("built archive: not writable by group or others", oct(st_.st_mode & 0o022), "0o0")

for key, (course, stem) in unwritable.items():
    ed = item("Episode", "training", course, "Season 1", stem + ".mp4")
    what = "denied" if key == "D" else "never granted"
    status, _, zd = get(f"/AdditionalMaterial/Download/{link(ed)}", auth=False)
    check(f"folder the server cannot write ({what}): still downloads", status, 200)
    check(f"folder the server cannot write ({what}): built in the cache", raw_names(zd) if status == 200 else None, [stem + ".md", stem + ".txt"])
    check(f"folder the server cannot write ({what}): nothing written there",
          [n for n in os.listdir(os.path.join(media, "training", course, "Season 1")) if n.lower().endswith("zip")], [])
check("rebuild: readers refused", code("POST", "/AdditionalMaterial/Build/Rebuild", READER), 403)
cfg(BuiltArchiveLocation="cache")
post("/AdditionalMaterial/Build/Rebuild", ADMIN); time.sleep(1)
wait_build(lambda s: True)
check("cache location: the copy beside the video is removed", os.path.exists(ZC), False)
check("cache location: still downloads", get(f"/AdditionalMaterial/Download/{TC2}", auth=False)[0], 200)
check("cache location: the media folder holds no built archives",
      [n for n in os.listdir(TC) + os.listdir(TCS) if n.lower().endswith("material.zip") or n.lower() == "additional-material.zip"], [])
cfg(BuiltArchiveLocation="beside", BuildInBackground=True)
post("/AdditionalMaterial/Index/Refresh", ADMIN); time.sleep(2)
s = wait_build(lambda s: s.get("UpToDate") == s.get("Planned"))
print(f"      build status after the background build: {json.dumps(s)}")
check("background: every planned archive built", [s.get("Planned") == s.get("UpToDate"), s.get("Failed")], [True, 0])
check("background: section archive beside its videos", os.path.exists(os.path.join(TCS, "additional-material.zip")), True)
check("background: no archive for one-file material (lesson, course)",
      os.path.exists(os.path.join(TCS, "S01E01 - Build Lesson.material.zip")) or os.path.exists(os.path.join(TC, "additional-material.zip")), False)
if CACHE:
    cached = sorted(os.listdir(CACHE)) if os.path.isdir(CACHE) else []
    check("background: only the unwritable folders' archives stay cached", len(cached), len(unwritable))

# Links inside a course the plugin builds are never packed, and entry names use "/" on every OS.
c = cont(ADMIN, EF1)
check("links: lesson packs only its real files", kept(c), ["S01E01 - Links.md", "S01E01 - Links.txt"])
check("links: a file link is left out", [[e.get("Path"), e.get("LeftOut")] for e in c.get("Entries", []) if e.get("Path") == "S01E01 - Links.pdf"],
      [["S01E01 - Links.pdf", True]])
cs = cont(ADMIN, SEASONF)
check("nested folders: section entry names use /", kept(cs), ["handouts/week 1/sheet.txt", "handouts/week 1/sheet2.md"])
everything = [e.get("Path") or "" for x in (c, cs, cont(ADMIN, SERIESF)) for e in x.get("Entries", [])]
check("links: nothing from a linked folder" + (" or junction" if WINDOWS else "") + " is listed", [p for p in everything if "secret" in p], [])
ZF, ZFS = os.path.join(TFS, "S01E01 - Links.material.zip"), os.path.join(TFS, "additional-material.zip")
check("links: built lesson archive holds only the real files", raw_names(readb(ZF)) if os.path.exists(ZF) else None, ["S01E01 - Links.md", "S01E01 - Links.txt"])
check("nested folders: built entry names use / (central directory)", raw_names(readb(ZFS)) if os.path.exists(ZFS) else None,
      ["handouts/week 1/sheet.txt", "handouts/week 1/sheet2.md"])
tok_f = link(SEASONF)
check("nested folders: entry download by its / path", entry(tok_f, "handouts/week 1/sheet.txt")[2], b"sheet one\n")
check("nested folders: the \\ spelling is not a second path", entry(tok_f, "handouts\\week 1\\sheet.txt")[0], 404)
check("links: a linked file is not served as an entry", entry(link(EF1), "S01E01 - Links.pdf")[0], 404)

check("courses with their own archives: untouched on disk", manifest(*LEFT_ALONE) == before_left_alone, True)
check("no probe or temporary files left in the media", litter(media), [])
if CACHE:
    check("no temporary files left in the cache", litter(CACHE) if os.path.isdir(CACHE) else [], [])
# Only files the plugin wrote and nobody changed are ever deleted: edit one, then move archives to the cache.
if os.path.exists(ZF):
    edited = readb(ZF) + b"edited by hand"   # replaced, not appended: the file is the server's, the folder is shared
    os.remove(ZF); put(ZF, edited)
    touched = sha(ZF)
    cfg(BuiltArchiveLocation="cache"); post("/AdditionalMaterial/Build/Rebuild", ADMIN); time.sleep(1)
    wait_build(lambda s: True)
    check("an archive someone changed is never deleted", os.path.exists(ZF) and sha(ZF) == touched, True)
    os.remove(ZF)
    cfg(BuiltArchiveLocation="beside"); post("/AdditionalMaterial/Index/Refresh", ADMIN); time.sleep(2)
    s = wait_build(lambda s: s.get("UpToDate") == s.get("Planned"))
    check("back beside the videos: every planned archive built", [s.get("Planned") == s.get("UpToDate"), s.get("Failed"), os.path.exists(ZF)], [True, 0, True])

# ---- helpers shared by the rules, VirusTotal and watching checks -------------------------------
def log_text():
    """The server's current log, or None without --log-dir. Quotes are dropped: some installs' file
    logs quote each value (the .deb writes rule "broken-rule" where the console shows rule broken-rule)."""
    if not args.log_dir:
        return None
    logs = sorted(glob.glob(os.path.join(args.log_dir, "*.log")), key=os.path.getmtime)
    return open(logs[-1], encoding="utf-8", errors="replace").read().replace('"', "") if logs else ""


def replan():
    """Re-read, plan and (background building is on) build; waits for a build run that ends after the request."""
    before = bstat().get("FinishedUtc")
    post("/AdditionalMaterial/Index/Refresh", ADMIN)
    for _ in range(90):
        time.sleep(1)
        s_ = bstat()
        if s_.get("Running") is False and s_.get("FinishedUtc") != before:
            return


def reason_of(name):
    e = next((e for e in cont(ADMIN, SEASONC).get("Entries", []) if e.get("Path") == name), None)
    return None if e is None else (e.get("Reason") if e.get("LeftOut") else "kept")


def wait_items(*paths_):
    """Item ids for media paths (each a tuple of parts below the work folder), scanning until Jellyfin has them all."""
    post("/Library/Refresh", ADMIN)
    found = {}
    for _ in range(120):
        time.sleep(2)
        items = (js(get("/Items?Recursive=true&IncludeItemTypes=Episode,Season,Series&Fields=Path", ADMIN)) or {}).get("Items", [])
        by_path = {norm(i.get("Path") or ""): i["Id"] for i in items}
        found = {p_: by_path.get(norm(spath("media", *p_))) for p_ in paths_}
        if all(found.values()):
            break
    return found


# ---- rules from the settings ------------------------------------------------------------------
def rules_list(token=ADMIN):
    return js(get("/AdditionalMaterial/Rules", token)) or []


def rcheck(text, original=""):
    return js(post("/AdditionalMaterial/Rules/Check", ADMIN, {"Text": text, "OriginalId": original, "OtherIds": []})) or {}


r0 = rules_list()
check("rules: the built-in ones listed, all on", [len(r0), all(r.get("Enabled") for r in r0), any(r.get("Custom") for r in r0)], [7, True, False])
check("rules: readers refused", code("GET", "/AdditionalMaterial/Rules", READER), 403)
check("rules: check refused to readers", http("POST", "/AdditionalMaterial/Rules/Check", READER, {"Text": ""})[0], 403)
MDRULE = ('id = "markdown-slides"\ndescription = "Markdown slide sources"\naction = "skip"\nreason = "slide source"\n\n'
          '[match]\nnames = ["*-slides.md"]\n\n[[test]]\nname = "section-slides.md"\nexpect = "match"\n\n'
          '[[test]]\nname = "notes.md"\nexpect = "no-match"\n')
r = rcheck(MDRULE)
check("rule check: a valid rule, its tests run", [r.get("Ok"), r.get("Id"), r.get("Tests")], [True, "markdown-slides", 2])
r = rcheck(MDRULE.replace("markdown-slides", "Markdown Slides"))
check("rule check: the file must be named after a valid id", [r.get("Ok"), "lowercase" in " ".join(r.get("Errors") or [])], [False, True])
r = rcheck(MDRULE.replace("notes.md", "notes-slides.md"))
check("rule check: a failing test case refuses it", [r.get("Ok"), "expected no-match" in " ".join(r.get("Errors") or [])], [False, True])
check("rule check: unknown keys refused", rcheck(MDRULE.replace("action", "acton", 1)).get("Ok"), False)
check("before: the advert is left out by link-only-text", reason_of("Bonus Resources.txt"), "link-only text file (advert) [rule link-only-text]")
check("before: the slides are kept", reason_of("section-slides.md"), "kept")
cfg(DisabledRules=["link-only-text"], CustomRules=[{"Id": "markdown-slides", "Text": MDRULE}])
replan()
check("a turned-off rule no longer applies (the next rule catches the advert)", reason_of("Bonus Resources.txt"), "release-group advert [rule release-group-advert-text]")
check("an added rule applies", reason_of("section-slides.md"), "slide source [rule markdown-slides]")
rl = {r_["Id"]: r_ for r_ in rules_list()}
check("rules: the added one listed as yours, the turned-off one off",
      [len(rl), rl.get("markdown-slides", {}).get("Custom"), rl.get("link-only-text", {}).get("Enabled")], [8, True, False])
check("downloads follow the rules (only notes.txt is left: handed out as that file)", iinfo(ADMIN, SEASONC).get("FileName"), "notes.txt")
status, _, zr = get("/AdditionalMaterial/Rules/Export", ADMIN)
try:
    import io
    exported = sorted(zipfile.ZipFile(io.BytesIO(zr)).namelist()) if status == 200 else [status]
except zipfile.BadZipFile as e:
    exported = [str(e)]
check("export: the rules that are on, as rule files", exported,
      ["jellyfin-chapter-sidecars.toml", "lesson-attachment-folders.toml", "markdown-slides.toml", "release-group-advert-text.toml",
       "release-group-adverts.toml", "shortcuts-and-system-files.toml", "udemy-redirect-placeholders.toml"])
check("export: readers refused", code("GET", "/AdditionalMaterial/Rules/Export", READER), 403)
EDITED = rl["release-group-adverts"]["Text"].replace('"Bonus Resources.txt",', '"Bonus Resources.txt", "Bonus*.txt",')
cfg(DisabledRules=[], CustomRules=[{"Id": "release-group-adverts", "Text": EDITED}, {"Id": "broken-rule", "Text": 'id = "broken-rule"\n'}])
replan()
rl = {r_["Id"]: r_ for r_ in rules_list()}
e_ = rl.get("release-group-adverts", {})
check("an edited built-in rule replaces the original", [e_.get("Custom"), e_.get("BuiltIn"), e_.get("BuiltInText") != e_.get("Text")], [True, True, True])
b_ = rl.get("broken-rule", {})
check("a broken rule from the settings is reported, not used", ["required" in (b_.get("Error") or ""), b_.get("Enabled")], [True, True])
check("a broken rule does not stop planning", reason_of("Bonus Resources.txt"), "link-only text file (advert) [rule link-only-text]")
lt = log_text()
if lt is None:
    info("a broken rule is logged: not checked (no --log-dir)")
else:
    check("a broken rule is logged", "rule broken-rule from the settings cannot be used" in lt, True)
cfg(DisabledRules=[], CustomRules=[])
replan()
check("rules back to the built-in ones", [reason_of("section-slides.md"), iinfo(ADMIN, SEASONC).get("FileName")], ["kept", "additional-material.zip"])

# ---- VirusTotal (a fake one, on this machine) ----------------------------------------------------
def vttest(key, token=ADMIN):
    return http("POST", "/AdditionalMaterial/VirusTotal/Test", token, {"Key": key})


check("VirusTotal key test: readers refused", vttest("test-key", READER)[0], 403)
if not args.fake_vt:
    info("VirusTotal lookups: not checked (no --fake-vt)")
else:
    import atexit
    fake = subprocess.Popen([sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), "fake_virustotal.py"),
                             str(args.fake_vt), "127.0.0.1"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    atexit.register(fake.kill)
    time.sleep(1)

    def vtstats():
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{args.fake_vt}/stats", timeout=5) as r_:
                return json.loads(r_.read())
        except (urllib.error.URLError, ConnectionError, TimeoutError, ValueError):
            return {}

    good = js(vttest("test-key")) or {}
    check("VirusTotal key test: a good key (the server reaches the fake)", good.get("Ok"), True)
    if good.get("Ok") is not True:
        bad("VirusTotal: the server is not pointed at the fake: start it with AM_VT_BASE=http://127.0.0.1:%d" % args.fake_vt)
    else:
        wrong = js(vttest("wrong-key")) or {}
        check("VirusTotal key test: a wrong key, with VirusTotal's reason", [wrong.get("Ok"), "401" in (wrong.get("Message") or "")], [False, True])
        TV = os.path.join(media, "training", "Course V"); TVS = os.path.join(TV, "Season 1"); os.makedirs(TVS)
        content = {"S01E01 - Clean": b"clean tool", "S01E02 - Bad": b"bad tool", "S01E03 - Unknown": b"never seen"}
        for stem, data in content.items():
            shutil.copy(os.path.join(TCS, "S01E01 - Build Lesson.mp4"), os.path.join(TVS, stem + ".mp4"))
            put(os.path.join(TVS, stem + ".txt"), f"notes for {stem}\n"); put(os.path.join(TVS, stem + ".exe"), data)
        ev = wait_items(*[("training", "Course V", "Season 1", stem + ".mp4") for stem in content])
        EV = {stem: ev[("training", "Course V", "Season 1", stem + ".mp4")] for stem in content}
        if not all(EV.values()):
            bad("VirusTotal: Course V not scanned")
        else:
            def scan_line(stem):
                status_, _, body_ = entry(link(EV[stem]), stem + ".exe.REMOVED.txt")
                m_ = re.search(r"^\s*Scan:\s*(.*?)\s*$", body_.decode("utf-8", "replace"), re.M) if status_ == 200 else None
                return m_.group(1) if m_ else f"(HTTP {status_})"

            cfg(VirusTotalApiKey="test-key", VirusTotalRequestsPerMinute=600, AllowCleanExecutables=False)
            replan()
            got = ""
            for _ in range(60):
                got = scan_line("S01E01 - Clean")
                if got.startswith("VirusTotal:") and all(scan_line(s_).startswith("VirusTotal:") for s_ in ("S01E02 - Bad", "S01E03 - Unknown")):
                    break
                time.sleep(2)
            check("VirusTotal: a known file's answer, in its note", got, "VirusTotal: known, flagged by 0 of 70 engines")
            check("VirusTotal: a flagged file", scan_line("S01E02 - Bad"), "VirusTotal: FLAGGED by 12 engine(s) as malicious and 1 as suspicious, out of 70")
            check("VirusTotal: a file it does not know", scan_line("S01E03 - Unknown"), "VirusTotal: not known to VirusTotal")
            looked = vtstats().get("lookups", [])
            mine = [hashlib.sha256(d).hexdigest() for d in content.values()]
            check("VirusTotal: each blocked file looked up once, by its SHA-256", [looked.count(d) for d in mine] + [len(looked) == len(set(looked))], [1, 1, 1, True])
            check("VirusTotal: nothing uploaded, nothing else asked", vtstats().get("other"), [])
            n_looked = len(looked)
            replan()
            check("VirusTotal: answers remembered (no second lookup)", len(vtstats().get("lookups", [])), n_looked)
            bad_e = next((e for e in cont(ADMIN, EV["S01E02 - Bad"]).get("Entries", []) if (e.get("Path") or "").endswith(".REMOVED.txt")), {})
            check("VirusTotal: the contents view marks a flagged file",
                  [bad_e.get("Flagged"), "FLAGGED by 12" in (bad_e.get("Scan") or ""), bad_e.get("ScanLink")],
                  [True, True, "https://www.virustotal.com/gui/file/" + hashlib.sha256(b"bad tool").hexdigest()])
            cfg(AllowCleanExecutables=True)
            replan()
            check("allow clean: a file no engine flags is included as is", kept(cont(ADMIN, EV["S01E01 - Clean"])), ["S01E01 - Clean.exe", "S01E01 - Clean.txt"])
            check("allow clean: a flagged file is still a note", kept(cont(ADMIN, EV["S01E02 - Bad"])), ["S01E02 - Bad.exe.REMOVED.txt", "S01E02 - Bad.txt"])
            check("allow clean: an unknown file is still a note", kept(cont(ADMIN, EV["S01E03 - Unknown"])), ["S01E03 - Unknown.exe.REMOVED.txt", "S01E03 - Unknown.txt"])
            cfg(VirusTotalApiKey="", AllowCleanExecutables=False, VirusTotalRequestsPerMinute=4)
            replan()
            check("no key: notes say not checked again", scan_line("S01E02 - Bad"), "not checked")
            check("no key: no lookups", len(vtstats().get("lookups", [])), n_looked)
        shutil.rmtree(TV)
        post("/Library/Refresh", ADMIN)
        replan()

cfg(BuildArchives=False)
check("building off: built archives still served as they are", iinfo(ADMIN, EC2).get("FileName"), "S01E02 - Two Files.material.zip")
cfg(BuildArchives=True)

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

# ---- downloads in progress ----------------------------------------------------------------
# A built archive is rebuilt in place, or moved to the cache, while someone is still downloading
# it. The download holds the file open; on Windows that blocks replacing or deleting it unless
# the server opened it to allow that. 48 MB of random data keeps the download from finishing.
G = os.path.join(media, "training", "Course G"); GS = os.path.join(G, "Season 1"); os.makedirs(GS)
shutil.copy(os.path.join(TCS, "S01E01 - Build Lesson.mp4"), os.path.join(GS, "S01E01 - Big.mp4"))
put(os.path.join(GS, "S01E01 - Big.dat"), os.urandom(48 * 1024 * 1024))   # .dat: Jellyfin takes .bin for a video
put(os.path.join(GS, "S01E01 - Big.txt"), "version 1\n")
if WINDOWS:
    subprocess.run(["icacls", G, "/grant", f"{args.service_sid or SERVICE_SID}:(OI)(CI)M", "/Q"], check=True, stdout=subprocess.DEVNULL)
else:
    for d in (G, GS):
        if os.geteuid() == 0 and server_uid is not None:
            os.chown(d, server_uid, -1)
        else:
            os.chmod(d, 0o777)
post("/Library/Refresh", ADMIN)
EG = None
for _ in range(120):
    time.sleep(2)
    EG = next((i["Id"] for i in (js(get("/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path", ADMIN)) or {}).get("Items", [])
               if norm(i.get("Path") or "") == norm(spath("media", "training", "Course G", "Season 1", "S01E01 - Big.mp4"))), None)
    if EG:
        break
ZG = os.path.join(GS, "S01E01 - Big.material.zip")


def big(tok):
    status, _, data = get(f"/AdditionalMaterial/Download/{tok}", auth=False)
    try:
        return [status, zip_text(data, "S01E01 - Big.txt").strip() if status == 200 else None]
    except (KeyError, zipfile.BadZipFile) as e:
        return [status, str(e)]


def hold(tok):
    r = urllib.request.urlopen(BASE + f"/AdditionalMaterial/Download/{tok}", timeout=120)
    r.read(65536)
    return r


if not EG:
    bad("downloads in progress: Course G not scanned")
else:
    cfg(BuildInBackground=False, BuiltArchiveLocation="beside")
    post("/AdditionalMaterial/Index/Refresh", ADMIN); time.sleep(2); wait_build(lambda s: True)
    tok_g = link(EG)
    check("downloads in progress: built beside the video", [big(tok_g), os.path.exists(ZG)], [[200, "version 1"], True])
    held = hold(tok_g); time.sleep(1)
    put(os.path.join(GS, "S01E01 - Big.txt"), "version 2, longer\n"); time.sleep(1)
    check("downloads in progress: a changed archive is rebuilt and served meanwhile", big(tok_g), [200, "version 2, longer"])
    held.close(); time.sleep(2)
    big(tok_g)
    held = hold(tok_g); time.sleep(1)
    cfg(BuiltArchiveLocation="cache"); post("/AdditionalMaterial/Build/Rebuild", ADMIN); time.sleep(1)
    wait_build(lambda s: True)
    held.close(); time.sleep(2)
    check("downloads in progress: moving to the cache removes the copy beside the video", os.path.exists(ZG), False)
    post("/AdditionalMaterial/Index/Refresh", ADMIN); time.sleep(2); wait_build(lambda s: True)
    put(os.path.join(GS, "S01E01 - Big.txt"), "version 3, the newest\n"); time.sleep(1)
    check("downloads in progress: after the move the course is still the plugin's (rebuilt on change)", big(link(EG)), [200, "version 3, the newest"])
    cfg(BuiltArchiveLocation="beside", BuildInBackground=True)
shutil.rmtree(G)
post("/Library/Refresh", ADMIN)
post("/AdditionalMaterial/Index/Refresh", ADMIN); time.sleep(2); wait_build(lambda s: True)


# ---- material added to an existing course, without a scan (folders watched) ------------------
# Last, so the real-time monitoring it turns on cannot affect the checks before it.
lt = log_text()
if lt is not None:
    check("real-time monitoring off: no folders watched", len(re.findall(r"watching \d+ folder\(s\) for changed material", lt)), 0)
vf = js(get("/Library/VirtualFolders", ADMIN)) or []
opts = next((v.get("LibraryOptions") for v in vf if v.get("ItemId") == TRAINING), None) or {}
opts["EnableRealtimeMonitor"] = True
post("/Library/VirtualFolders/LibraryOptions", ADMIN, {"Id": TRAINING, "LibraryOptions": opts})
if lt is None:
    info("watching: the folder list is not checked (no --log-dir); waiting a minute for the next sync")
    time.sleep(75)
else:
    want = "watching 1 folder(s) for changed material: " + spath("media", "training")
    got = False
    for _ in range(50):
        if any(norm(ln.split("Additional Material: ", 1)[-1].strip()) == norm(want) for ln in (log_text() or "").splitlines() if "watching" in ln):
            got = True
            break
        time.sleep(2)
    check("real-time monitoring on: the enabled library's folder is watched (the other is not enabled)", got, True)
full_plans = len(re.findall(r"Additional Material: planned \d+ archives in", log_text() or ""))
added = os.path.join(TCS, "S01E02 - Two Files.pdf")
put(added, b"%PDF-1.4\nadded later\n")
got = []
for _ in range(45):
    got = kept(cont(ADMIN, EC2))
    if "S01E02 - Two Files.pdf" in got:
        break
    time.sleep(2)
check("added material: in its lesson's contents within a minute, no re-read", got,
      ["S01E02 - Two Files.docm.REMOVED.txt", "S01E02 - Two Files.pdf", "S01E02 - Two Files.txt"])
status, _, zw = get(f"/AdditionalMaterial/Download/{link(EC2)}", auth=False)
check("added material: in the built archive", raw_names(zw) if status == 200 else status,
      ["S01E02 - Two Files.docm.REMOVED.txt", "S01E02 - Two Files.pdf", "S01E02 - Two Files.txt"])
if args.log_dir:
    check("added material: no full re-plan", len(re.findall(r"Additional Material: planned \d+ archives in", log_text() or "")), full_plans)
os.remove(added)
for _ in range(45):
    got = kept(cont(ADMIN, EC2))
    if "S01E02 - Two Files.pdf" not in got:
        break
    time.sleep(2)
check("removed material: gone from the contents within a minute", got, ["S01E02 - Two Files.docm.REMOVED.txt", "S01E02 - Two Files.txt"])
opts["EnableRealtimeMonitor"] = False
post("/Library/VirtualFolders/LibraryOptions", ADMIN, {"Id": TRAINING, "LibraryOptions": opts})

# ---- what the browser test needs ------------------------------------------------------
rel = {"course": ["training", "Course A", "additional-material.zip"],
       "lesson": ["training", "Course A", "Season 1", "S01E01 - Lesson One.material.zip"]}
with open(os.path.join(WORK, "run.json"), "w") as f:
    json.dump({"base": BASE, "media": spath("media"), "sep": SEP,
               "sha256": {k: sha(os.path.join(media, *v)) for k, v in rel.items()}}, f, indent=1)

print(f"\npassed {passed}, failed {failed}" + (f", {len(notes)} notes" if notes else ""))
print(f"test server left running on {BASE}; uninstall it, or reset it, before running this again")
sys.exit(0 if failed == 0 else 1)
