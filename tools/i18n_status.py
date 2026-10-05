#!/usr/bin/env python3
"""
How complete each translation is, and what is wrong with it.

  python3 tools/i18n_status.py              every language, both catalogs
  python3 tools/i18n_status.py de           one language, listing each missing key with its English
  python3 tools/i18n_status.py de --json    the same, as JSON

For each language file it reports keys still missing (they show in English), keys whose text is
the same as English (fine for "OK" or a product name, worth a look otherwise), and errors: keys
English does not have, placeholders that differ from English, or a file name that is not a
lower-case language tag. Exit status 1 when there are errors, as the tests would fail.

Standard library only. See docs/i18n.md.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CATALOGS = {
    "web": ROOT / "src/Jellyfin.Plugin.AdditionalMaterial/Web/i18n",   # button, contents view, settings page
    "notes": ROOT / "tools/i18n",                                      # the .REMOVED.txt notes (helper script)
}
TAG = re.compile(r"[a-z]{2,3}(-[a-z0-9]{2,8})*")
PLACEHOLDER = re.compile(r"\{(\w+)\}")


def check(english: dict[str, str], name: str, data: dict) -> dict:
    errors = []
    if not TAG.fullmatch(name):
        errors.append(f"file name '{name}.json' is not a lower-case language tag (de.json, pt-br.json)")
    for key in sorted(set(data) - set(english)):
        errors.append(f"{key}: not a key English has (a typo, or a string that was removed)")
    for key in sorted(set(data) & set(english)):
        value = data[key]
        if not isinstance(value, str):
            errors.append(f"{key}: must be text")
            continue
        want, got = set(PLACEHOLDER.findall(english[key])), set(PLACEHOLDER.findall(value))
        if want != got:
            errors.append(f"{key}: placeholders {sorted(got)} differ from English {sorted(want)}")
    return {
        "translated": len(set(data) & set(english)),
        "total": len(english),
        "missing": sorted(set(english) - set(data)),
        "same_as_english": sorted(k for k in set(data) & set(english) if data[k] == english[k]),
        "errors": errors,
    }


def main(argv: list[str]) -> int:
    as_json = "--json" in argv
    wanted = [a.lower() for a in argv if not a.startswith("--")]
    report: dict[str, dict] = {}
    for part, folder in CATALOGS.items():
        english = json.loads((folder / "en.json").read_text(encoding="utf-8"))
        names = sorted(f.stem for f in folder.glob("*.json") if f.stem != "en")
        for lang in wanted:
            if lang not in [n.lower() for n in names]:
                names.append(lang)   # not started yet: everything is missing
        for name in names:
            if wanted and name.lower() not in wanted:
                continue
            f = folder / f"{name}.json"
            try:
                data = json.loads(f.read_text(encoding="utf-8")) if f.exists() else {}
            except ValueError as e:
                report.setdefault(name, {})[part] = {"errors": [f"{f.name}: not valid JSON: {e}"], "missing": [], "same_as_english": [], "translated": 0, "total": len(english)}
                continue
            report.setdefault(name, {})[part] = check(english, name, data) | ({"english": english} if wanted else {})

    failed = any(r["errors"] for parts in report.values() for r in parts.values())
    if as_json:
        print(json.dumps({lang: {p: {k: v for k, v in r.items() if k != "english"} for p, r in parts.items()} for lang, parts in report.items()}, ensure_ascii=False, indent=2))
        return 1 if failed else 0
    if not report:
        print("No translations yet. Start one with: python3 tools/i18n_status.py <language>  (see docs/i18n.md)")
        return 0
    for lang, parts in sorted(report.items()):
        print(lang)
        for part, r in parts.items():
            print(f"  {part:<6} {r['translated']}/{r['total']} translated, {len(r['missing'])} missing, "
                  f"{len(r['same_as_english'])} same as English, {len(r['errors'])} error(s)")
            for e in r["errors"]:
                print(f"         ERROR {e}")
            if wanted:
                for key in r["missing"]:
                    print(f"         missing {key}: {r['english'][key]!r}")
                for key in r["same_as_english"]:
                    print(f"         same    {key}: {r['english'][key]!r}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
