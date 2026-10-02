# Build archives from the plugin (scheduled task)

*Filed as [#1](https://github.com/4o66/jellyfin-plugin-additional-material/issues/1). Considered for a future version; not in 1.0.*

## Idea

Run the archive builder from inside Jellyfin as a scheduled task ("Build additional material"),
with the helper script's options on the plugin's settings page: levels, lesson matching,
excludes, the executable policy and the VirusTotal key.

## Why not just call `tools/make_additional_material.py`

Jellyfin's Docker images have no Python, so the plugin cannot rely on running the script. The
builder rules would be ported to C#. `System.IO.Compression` covers zip with no extra
dependency.

## Constraints to design for

- **Write access:** it can only build if Jellyfin's media folders are mounted writable. Many
  installs mount them read-only. The task must detect that and say so, not fail silently.
- **One implementation of the rules:** the Python script and the C# task must match lessons,
  sections and courses identically. Share test fixtures (a sample course tree plus the expected
  archives) between the two test suites.
- **Executable handling:** the same default (replace with a `.REMOVED.txt` note) and the same
  opt-outs. VirusTotal lookups must respect the free API's rate limit from a background task.
- **Scheduling:** run after library scans (`ILibraryPostScanTask`) and/or on a schedule, only on
  the enabled libraries, and only touch archives the plugin created.
- **Ownership:** archives are written as the Jellyfin user. Note how this interacts with
  `--chown`-style expectations (e.g. 99:100 on Unraid).
