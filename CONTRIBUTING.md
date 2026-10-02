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
