# Contributing

## Adding a rule for the helper script

Patterns that belong to a particular course site, downloader or release group live in
`tools/rules/`, **one TOML file per rule**, so a pull request adds or changes exactly one file.
Generic behavior (videos, subtitles, Jellyfin artwork, executable and active-content checks,
lesson matching, packaging) is in the script itself, and rules can't weaken it.

A rule file:

```toml
id = "my-site-adverts"               # must match the file name: my-site-adverts.toml
description = "What it catches and where it comes from"
action = "skip"                      # "skip", or "attachment-folder" (see below)
reason = "advert from my-site"       # shown in the dry run and the JSON report (default: description)
quiet = false                        # true: skipped without being listed (for metadata noise)

[match]                              # every condition given must hold
names = ["Visit my-site*.txt"]       # file-name globs, case-insensitive; a literal "[" is "[[]"
name_regex = '...'                   # or a regular expression on the file name
extensions = [".txt", ""]            # "" means no extension
max_size = 2048                      # bytes
content_regex = '...'                # searched in the first 1 MB of the file
max_visible_text = 40                # HTML: characters left after removing tags and scripts
lines_regex = '...'                  # every non-empty line must match ...
max_lines = 40                       # ... the file has at most this many lines ...
allow_caption_lines = 1              # ... apart from this many short lines (when 3+ lines) ...
caption_max_length = 60              # ... of at most this length

[[test]]                             # at least one, ideally a match and a near miss
name = "Visit my-site.txt"
content = "https://my-site.example\n"
expect = "match"

[[test]]
name = "Lab notes.txt"
content = "Configure OSPF area 0 first.\n"
expect = "no-match"
```

An `attachment-folder` rule names folders that downloaders use for one lesson's files
(`<folder>/<lesson name or number>/…`):

```toml
id = "my-downloader-folders"
description = "Lesson folders written by my-downloader"
action = "attachment-folder"

[match]
folder_names = ["lesson_files"]

[[test]]
folder = "lesson_files"
expect = "match"
```

Check your rule:

```zsh
python3 tools/make_additional_material.py --list-rules
python3 -m unittest discover -s tests
python3 tools/make_additional_material.py /path/to/a/course -v     # dry run on real files
```

The test suite runs every rule file's `[[test]]` cases. Unknown keys, bad regular expressions,
a file name that doesn't match the `id`, and duplicate ids are errors, so a typo fails CI
instead of silently matching nothing.

Users can keep their own rules outside the repo with `--rules DIR` and turn any rule off with
`--disable-rule ID`.

## The planner exists twice: keep the two in step

What goes into which archive is decided by the helper script
(`tools/make_additional_material.py`) and, for archives the plugin builds itself, by its C# port
(`src/Jellyfin.Plugin.AdditionalMaterial/Planning/`). Both read the same `tools/rules/*.toml`.
A change to the decisions, not just to rule files, must be made in both. CI's `planner-parity`
job builds a library that exercises every check (`tools/planner-compare/fixtures.py`), plans it
with both, and fails on any difference in archives, files, order, reasons, entry names or skips.
To run it yourself:

```zsh
python3 tools/planner-compare/fixtures.py /tmp/am-lib
python3 tools/make_additional_material.py /tmp/am-lib --json /tmp/py.json -q
dotnet run --project tools/planner-compare -- /tmp/am-lib --rules tools/rules --json /tmp/cs.json
python3 tools/planner-compare/compare.py /tmp/py.json /tmp/cs.json
```

Run the script without a 7z program on `PATH` for this: the plugin has none, so it cannot
look inside `.7z` and `.rar` archives and replaces them with a note, where the script with 7z
lists them. That is the one intended difference.

A new check deserves a new fixture in `fixtures.py`, so both sides are held to it.

## Adding a translation

Text is kept in one JSON file per language, so a translation is one file per part:

- `src/Jellyfin.Plugin.AdditionalMaterial/Web/i18n/<lang>.json`: the web button and the
  settings page. The language follows the web client's display language (`de-DE`, then `de`,
  then English).
- `tools/i18n/<lang>.json`: the `.REMOVED.txt` notes the helper script puts in archives
  (`--language`, or `LANG`).

Copy `en.json`, translate the values, and keep every `{placeholder}` as is. Missing keys fall
back to English. The tests check that a translation has no keys English lacks and keeps the same
placeholders.

## Everything else

- The plugin targets Jellyfin 12.1 / .NET 10. Build it and run `tests/integration.sh` (see the
  README) before sending changes to the server side.
- Keep the web script free of build steps and of anything that patches jellyfin-web's bundles.
