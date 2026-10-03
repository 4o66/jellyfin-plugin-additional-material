# Changelog

## 1.6.0 (unreleased)

- **Rules in the settings** (Settings → Rules): every rule building uses is listed, and can be
  turned off, edited (an edited built-in rule replaces the original until reset) or added, in the
  rule file format. **Check** validates a rule and runs its own test cases; one that fails cannot
  be used. **Download the rules** gives the rules that are on as a zip for the helper script
  (`--no-default-rules --rules DIR`). Saving changed rules plans the courses again.
- **VirusTotal** (Settings → VirusTotal, optional): with an API key, files replaced by a note are
  looked up by SHA-256 (nothing is uploaded), in the background at the free API's 4 a minute, and
  the answer goes in the note, as the helper script writes it, and in the contents view (a flagged
  file is marked). Answers are remembered; when one arrives the course is planned and built again.
  Optionally, programs VirusTotal knows and no engine flags are included. The page says how to get
  a free key, with links, and has a **Test key** button. A used-up quota pauses lookups for an hour.

## 1.5.4 (2026-10-02)

- Grid and list icons appear about 5–10 times sooner (measured from a card or row appearing to
  its icon: grid 250–290 ms to 26–62 ms, list 320–420 ms to 19–100 ms, on a local test server).
  The display settings now come inside the script instead of a separate request; translations
  load alongside instead of being waited for; each answer is drawn the moment it arrives; new
  cards are looked at within 40 ms.
- Return visits draw icons at once from what the browser remembers (up to a week, per user),
  then confirm with the server.
- On grid cards the icon sits in the image's bottom-left corner, clear of Jellyfin's own
  indicators and hover buttons, and outside the indicator row Jellyfin rebuilds.

## 1.5.3 (2026-10-02)

- One Additional Material button on every page, and it always opens the picker. On course and
  section pages it used to download that level's archive at once, with the picker behind a
  separate list button that was easy to miss; that archive now downloads from the picker's foot
  ("Download the course archive").
- "1 archive", not "1 archives".

## 1.5.2 (2026-10-02)

- A course Jellyfin adds between scans (its file watcher, or a scan of just that folder) is planned
  and, with background building on, built about 30 seconds after the last of its videos is added,
  instead of waiting for the next full library scan.

## 1.5.1 (2026-10-02)

- Redirect placeholders (Udemy quiz and assignment pages that only send the browser to the
  website) are no longer just "left out": the contents view offers **Open link ↗** to the site
  they point at, in a new tab. The address is read from the file (meta refresh, or a script
  setting the location); only http and https become links. Rules turn this on with
  `show_link = true` (the Udemy redirect rule does); the helper script's report lists the address.

## 1.5.0 (2026-10-02)

- **The plugin can build archives itself** (Settings → Building, off by default), with the helper
  script's rules: icons and contents show at once, archives are built in the background after
  scans or on first download, beside the videos (the plugin's cache where that folder cannot be
  written, or always the cache), rebuilt when their files change, removed when no longer needed.
  Courses that already have archives the plugin did not build are left as they are.
- One-file material is handed out as that file, unless large (100 MB) and compressible: a sample
  is compressed to decide, and formats that are compressed already are never zipped.
- Each file in a built archive is stored or deflated depending on whether compressing it pays.
- The contents view lists files a rule left out, with the reason (everyone, admins only, or nobody).
- Tools → **Rebuild built archives**.
- Downloads are opened so the file can still be replaced or deleted while it downloads (on Windows
  a download used to block rebuilding or moving that archive). A copy that cannot be removed yet is
  kept as the plugin's and removed on a later run, never mistaken for someone else's archive.
- The planner is a C# port of the helper script's, held to identical results by CI; it reads the
  same rule files, with Tomlyn (BSD-2-Clause), now shipped beside the plugin's DLL. On a real
  library: 9 courses, 242 archives, 307 files, 284 skips, no differences.
- Helper script: an archive 7z cannot read (corrupt, or with an encrypted listing) is now replaced
  by a note, like any content that cannot be checked; it used to be included.

## 1.4.0 (2026-10-02)

- **Settings, reorganized** (modeled on Intro Skipper's): sections in a sidebar (General,
  Appearance, Contents, Tools, About), one shown at a time; on narrow screens the sections become
  tabs. One Save bar stays at the bottom and shows when there are unsaved changes. The open
  section is remembered.
- **Reset to default** for the accent color (back to Jellyfin's `#00A4DC`).
- About: the installed version, links, and the repository address with a Copy button.
- README: installing now covers both repositories (this one and File Transformation's).

## 1.3.0 (2026-10-02)

- **Contents view.** A lesson's button now opens a file tree of its archive, with a Download
  button on every file and **Download all (.zip)** below. Course and section pages keep their
  direct download and gain a **Contents** button listing every archive below them; each one
  expands to its files.
- Zips inside the material (lab packs and the like) are listed and their files downloadable one
  by one (setting: "List the files inside zips that are inside the material", on by default).
- Files the helper script replaced by a note show under their own name, marked as removed.
- Settings: **Re-read folders** re-reads every enabled library's folders now, with progress and
  the result.
- The listing closes when you leave the page.
- Files are listed from the zip's own directory (nothing unpacked) and served straight out of
  it, as attachments, with the same signed links and permission checks as whole archives.

## 1.2.3 (2026-10-02)

- Fixed: grid and list icons could vanish until a reload. Jellyfin re-builds a card's indicator
  row (and list rows' buttons) when their data refreshes, for example on returning to the tab, and
  the script had marked those elements done. It now checks every pass whether the icon is there.
- Security (Windows): the download-link signing key was readable by every local user, because it
  inherited `C:\ProgramData`'s permissions. It is now created with only the service account,
  SYSTEM and Administrators allowed (#5). On Linux it was already owner-only.
- A key readable by other accounts (on any OS) is replaced at startup. Links issued in the last
  few minutes stop working once.
- If the key cannot be read or written, the plugin uses a temporary key until restart instead of
  failing to issue links.

## 1.2.2 (2026-10-02)

- Fixed: grid and list icons sometimes never appeared until the page was reloaded. If Jellyfin
  re-drew the cards while the status request was still on its way, the new cards read "still
  asking" as "no material". Cards now wait for the answer. A failed status request is retried
  (2 s, backing off to 30 s) instead of waiting for the page to change.

## 1.2.1 (2026-10-02)

- Security: material reached through a symbolic link or junction to a folder (below the library's
  own folder) is now refused, as the README always said. Before, a linked folder pointing outside
  the library had its zips served. Exploiting it needed write access to the library. Found in
  cross-platform testing (#3).

## 1.2.0 (2026-10-02)

- Icons appear at once. The plugin keeps an index of which folders hold material, built at
  startup and after each library scan, instead of reading folders on demand (a sleeping disk took
  seconds per course to answer). Entries older than 10 minutes are re-checked in the background
  when used (`IndexRefreshMinutes`); no timer wakes the disks.
- New scheduled task "Refresh additional material" to re-read the folders by hand.
- Downloads still check the actual file.
- The page script no longer waits for translations before drawing icons.

## 1.1.1 (2026-10-02)

- Downloads get descriptive names: "Course - S10E01 - Lesson - Additional Material.zip".
- Browsers always get a fresh index.html, so plugin updates show up at once. (File
  Transformation rewrites the page, but the server answered "not modified" from the file on disk.)
- The web script is no longer cached for an hour.

## 1.1.0 (unreleased)

- Course and section pages list the material below them, grouped by section, with Go to lesson
  and Download. Grid cards and list rows show the icon (each can be turned off).
- New icon (design A6), two-color by default with a selectable accent; dashboard sidebar entry.
- Text is translatable (Web/i18n, tools/i18n).
- The web script's address changes with every build, so browsers never keep a stale copy.

## 1.0.0 (unreleased)

First version.

- An "Additional material" download button on series, seasons, episodes and movies that have a
  matching `.zip` (`additional-material.zip`, or `<video>.material.zip`).
- Opt-in per library. Downloads honor library access and, by default, the "Allow media
  downloading" permission.
- Short-lived signed download links. Paths are confined to the library, and symlinks are
  refused.
- The web button is added through File Transformation.
- `tools/make_additional_material.py` builds the archives from the files stored with the videos.
  Executable content is replaced by notes by default, with optional VirusTotal hash lookups.
