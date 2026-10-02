# Changelog

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
