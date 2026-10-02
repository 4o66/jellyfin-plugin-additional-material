# Additional Material for Jellyfin

Adds an **Additional material** download button to courses, sections and lessons (series,
seasons and episodes, and movies) that have a matching `.zip` stored beside them. It's for
training and coursework libraries, where the videos come with PDFs, lab files, slides and notes
that Jellyfin otherwise ignores.

- Jellyfin **12.1** (built against `Jellyfin.Controller` 12.1.0, .NET 10)
- Opt-in per library
- Downloads honor Jellyfin's own **Allow media downloading** permission and library access
- A helper script builds the archives from the files already stored with your videos

> **Status:** in testing, not yet released.

## How it finds material

The plugin looks for a `.zip` with a fixed name. Nothing is indexed and nothing is written to
Jellyfin's database: it checks the disk when an item page asks.

| Where the zip is | Button appears on |
|---|---|
| `additional-material.zip` in a series (course) folder | the series |
| `additional-material.zip` in a season (section) folder | that season |
| `<video file name>.material.zip` beside a video | that episode or movie |

For example:

```
Training/
  CCNA 200-301 Course/
    additional-material.zip                   ← course
    5 - OSPF/
      additional-material.zip                 ← section
      1 - Introducing OSPF.mp4
      1 - Introducing OSPF.material.zip       ← lesson
```

Only `.zip` is recognized in this version.

## Installing

Until a release is published: build it (see below), copy
`Jellyfin.Plugin.AdditionalMaterial.dll` into a folder named `Additional Material_1.0.0.0` in
Jellyfin's `plugins` directory, and restart Jellyfin.

The **web button** needs the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
plugin, which many web-UI plugins already use. Without it everything else still works, and the
download API remains available, but no button is shown.

## Configuring

Dashboard → Plugins → **Additional Material**:

- **Libraries:** tick the libraries the plugin should look in. None are enabled by default.
- **Require the "Allow media downloading" permission** (on by default): users without it see
  the button disabled.
- **Download link lifetime:** how long a download has to start after the button is pressed
  (default 10 minutes).

## Security

- The client only ever sends an **item ID**. The file path is derived from the item, must stay
  inside the library's folders, and symbolic links are refused.
- Every request checks that the user can see the item, the same check Jellyfin uses for its
  own downloads.
- The browser downloads through a **short-lived signed link** (HMAC-SHA256, per user and item),
  so the user's access token never appears in a URL, a proxy log or browser history. Access is
  re-checked when the download starts, so revoking a permission takes effect at once.
- Archives are always sent as attachments (`Content-Disposition: attachment`) with `nosniff`.
  Nothing from an archive is ever displayed in the browser.

## Limits

- **Web client only.** Native apps (Android TV, Roku, Swiftfin, Kodi and so on) don't show the
  button. Clients that wrap the web client (Jellyfin Desktop, Android, webOS) probably do.
- The button is added by script, not by patching the web client's bundles, so a web-client
  update can at worst hide it until the plugin is updated. Downloads keep working.

## The helper script

`tools/make_additional_material.py` builds the archives from the files stored with your
videos. It needs Python 3.11 or newer and nothing else.

```zsh
# See what it would do (nothing is written without --apply):
python3 tools/make_additional_material.py /path/to/Training

# Build them, owned by Jellyfin's user:
python3 tools/make_additional_material.py /path/to/Training --apply --chown 1000:1000
```

Point it at a course folder or at a library folder that holds courses. It follows the structure
of your videos:

1. **Lesson:** a file with the same name as a video, or the same leading lesson number as
   exactly one video in that folder (`4 - Study Plan.pdf` goes with `4 - Welcome.mp4`), or one
   inside `attached_files/<lesson>/`.
2. **Section:** anything else in a season folder.
3. **Course:** files in the course folder itself.

**One archive for the whole course** when it has subfolders that hold material but no videos.
Jellyfin shows no page for those folders, so instead of splitting the course between section
zips and a course zip, everything goes into a single `additional-material.zip` on the course
page, with the folders kept inside. `--package always` does this for every course, and
`--package never` keeps the split.

Videos, subtitles, `.nfo` files, Jellyfin artwork, dot-folders such as `.chapters` and `.url`
shortcuts are always left out. A lone `.zip` that is all of a lesson's material is reused as is
(hard-linked, so it takes no extra space) rather than zipped again.

**Executable and active content is left out by default.** Each such file is replaced in the zip
by `<name>.REMOVED.txt`, a note saying why it was removed, with its SHA-256. Course downloads are
a common way malware spreads.

- **Programs and scripts:** `.exe`, `.dll`, `.msi`, `.bat`, `.ps1`, `.vbs` and similar,
  macro-enabled Office files (`.docm`, `.xlsm`, `.pptm`…), and archives that contain any of them.
  `--allow-executables` includes them.
- **Documents with active content:**
  - Office files with macros, embedded OLE objects, ActiveX controls, external templates, frames
    or OLE links (the route used by CVE-2022-30190 "Follina"), or DDE fields;
  - PDFs with JavaScript, Launch actions, embedded files or RichMedia, including inside
    compressed object streams and `#xx`-obfuscated names;
  - RTF files with embedded objects.

  Ordinary links, embedded charts and images are fine. `--allow-active-documents` includes them.

**Rules for particular sites and downloaders** are kept as small files in `tools/rules/`, one
per rule, each with its own test cases. The built-in rules skip Udemy redirect placeholders
(tiny HTML pages that only send the browser to the website), release-group adverts (by name,
and any text file that is nothing but links), web shortcuts and system files, and chapter
sidecars that Jellyfin plugins write (`*_chapters.xml`). One rule recognizes downloaders'
per-lesson folders (`attached_files/<lesson>/`). `--list-rules` shows them, `--rules DIR` adds
your own, and `--disable-rule ID` turns one off. See [CONTRIBUTING.md](CONTRIBUTING.md) to add
one.

A file that lost its extension (`StudyPlan200301docx`) is named from its content inside the
zip.

**Optional VirusTotal lookups:** with `--virustotal-key KEY` (or `VT_API_KEY`), blocked files
are looked up by SHA-256 and the result is written into the note. Only fingerprints are sent.
`--allow-clean-executables` includes executables that VirusTotal knows and no engine flags.
`--virustotal-upload` also uploads unknown files. **Uploaded files are shared with VirusTotal's
community, so only use it for files you're allowed to share.** The free API allows 4 requests a
minute; the script waits accordingly.

Other options include `--levels lesson,section,course`, `--match name`, `--exclude GLOB`,
`--only COURSE`, `--update`, `--force`, `--compression store` and `--json report.json` for
automation. See `--help`.

On a host without Python (Unraid, for example), run it in a throwaway container:

```zsh
docker run --rm -v /mnt/user/media/Training:/data -v "$PWD/tools:/t:ro" python:3-alpine \
  python /t/make_additional_material.py /data --apply --chown 99:100
```

## Building

```zsh
dotnet publish src/Jellyfin.Plugin.AdditionalMaterial -c Release -o out
```

The output is a single `Jellyfin.Plugin.AdditionalMaterial.dll`. Without a local .NET 10 SDK:

```zsh
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish src/Jellyfin.Plugin.AdditionalMaterial -c Release -o out
```

## Testing

- `python3 -m unittest discover -s tests`: the helper script (13 tests, including a fake
  VirusTotal server).
- `tests/integration.sh out [file-transformation-plugin-dir]`: starts a throwaway Jellyfin
  12.1 container with sample media and checks lookups, permissions, path safety, signed links,
  downloads and the web injection (25 checks). `tests/integration.sh --cleanup` removes it.
- `tests/browser_test.py`: drives the web client in headless Chromium against that server
  (8 checks). The command is in the file's header.

## License

[GPL-3.0](LICENSE), like Jellyfin's own plugins.

Copyright (C) 2026 4o66
