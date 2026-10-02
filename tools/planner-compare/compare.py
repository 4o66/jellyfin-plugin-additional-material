#!/usr/bin/env python3
"""Compare the helper script's --json report with planner-compare's: same courses, archives,
files (in order), placement reasons, removals, entry names and skips. Exit 0 when identical.

  python tools/make_additional_material.py LIB --json py.json -q
  dotnet PlannerCompare.dll LIB --rules tools/rules --json cs.json
  python tools/planner-compare/compare.py py.json cs.json

Run both on the same path: the reports name files by absolute path."""
import json, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import make_additional_material as m  # noqa: E402  (for archive_name: the entry names inside each zip)

py, cs = (json.load(open(p, encoding="utf-8")) for p in sys.argv[1:3])
diffs = []
def d(msg): diffs.append(msg)

pc = {c["course"]: c for c in py["courses"]}; cc = {c["course"]: c for c in cs["courses"]}
for k in sorted(set(pc) ^ set(cc)): d(f"course only in {'python' if k in pc else 'c#'}: {k}")
for k in sorted(set(pc) & set(cc)):
    a, b = pc[k], cc[k]
    if a["packaged_as_one"] != b["packaged_as_one"]: d(f"{k}: packaged_as_one {a['packaged_as_one']} vs {b['packaged_as_one']}")
    aa = {x["archive"]: x for x in a["archives"]}; ba = {x["archive"]: x for x in b["archives"]}
    if list(aa) != list(ba): d(f"{k}: archives differ\n   py: {list(aa)}\n   c#: {list(ba)}")
    for z in aa.keys() & ba.keys():
        x, y = aa[z], ba[z]
        for f in ("level", "status", "files", "placement"):
            if x[f] != y[f]: d(f"{z}: {f} differs\n   py: {x[f]}\n   c#: {y[f]}")
        if [(r["file"], r["reason"]) for r in x["removed"]] != [(r["file"], r["reason"]) for r in y["removed"]]:
            d(f"{z}: removed differs\n   py: {x['removed']}\n   c#: {y['removed']}")
        # entry names: python's archive_name for each file, with .REMOVED.txt for removed ones
        removed = {r["file"] for r in x["removed"]}
        base = Path(z).parent
        files = list(x["placement"])
        want = [m.archive_name(Path(f), base) + (".REMOVED.txt" if f in removed else "") for f in files]
        if want != y.get("entries"): d(f"{z}: entry names differ\n   py: {want}\n   c#: {y.get('entries')}")
sp = [(s["file"], s["reason"]) for s in py["skipped"]]; sc = [(s["file"], s["reason"]) for s in cs["skipped"]]
if sp != sc:
    only_p = [s for s in sp if s not in sc]; only_c = [s for s in sc if s not in sp]
    d(f"skipped differs: {len(only_p)} only in python, {len(only_c)} only in c#" + ("" if only_p or only_c else " (order only)")
      + "".join(f"\n   py: {s}" for s in only_p[:10]) + "".join(f"\n   c#: {s}" for s in only_c[:10]))
narch = sum(len(c["archives"]) for c in py["courses"]); nfiles = sum(len(a["placement"]) for c in py["courses"] for a in c["archives"])
print(f"{len(pc)} courses, {narch} archives, {nfiles} files, {len(sp)} skips compared: {len(diffs)} difference(s)")
for x in diffs[:40]: print(" -", x)
sys.exit(1 if diffs else 0)
