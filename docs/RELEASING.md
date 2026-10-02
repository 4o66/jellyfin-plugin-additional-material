# Releasing

## Plugin repository: "4o66"

The plugin is distributed the way danieladov's plugins are:

- each plugin lives in its own repo, `4o66/jellyfin-plugin-<name>` (this one is
  `jellyfin-plugin-additional-material`);
- one manifest repo, **`4o66/JellyfinPluginManifest`**, holds `manifest.json` listing every plugin
  and version, with the catalog images under `resources/`;
- users add it under Dashboard → Plugins → Repositories with the name **`4o66`** and the URL
  `https://raw.githubusercontent.com/4o66/JellyfinPluginManifest/main/manifest.json`.

Not a repo named `4o66/4o66`: GitHub reserves that name for the account's profile README.

## Steps

1. Bump `AssemblyVersion`/`FileVersion` in the csproj and `version` in `build.yaml`; update
   `CHANGELOG.md`.
2. Push a `v<version>` tag. CI builds the zip, runs the tests, and drafts a GitHub release with the zip and
   its MD5.
3. Publish the draft, then add a `versions` entry to `manifest.json` in the manifest repo:
   `version`, `changelog`, `targetAbi` (`12.1.0.0`), `sourceUrl` (the release zip),
   `checksum` (MD5), `timestamp`. The plugin entry's `imageUrl` points at
   `resources/additional-material.png` (a copy of `assets/catalog.png`).

Both repos must be public before Jellyfin can read the manifest or download the zip.
