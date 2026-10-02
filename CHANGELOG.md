# Changelog

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
