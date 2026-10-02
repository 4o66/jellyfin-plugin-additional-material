# Changelog

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
