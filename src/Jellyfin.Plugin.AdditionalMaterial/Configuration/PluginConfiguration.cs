using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AdditionalMaterial.Configuration;

/// <summary>Administrator settings for the plugin.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the IDs of the libraries the plugin looks in. Empty means none: the plugin is
    /// opt-in per library.
    /// </summary>
    public string[] EnabledLibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets a value indicating whether downloading needs the user's "Allow media downloading"
    /// permission, the same permission Jellyfin's own Download button uses.
    /// </summary>
    public bool RequireDownloadPermission { get; set; } = true;

    /// <summary>Gets or sets how long a download link stays valid, in minutes.</summary>
    public int LinkLifetimeMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets the button style: <c>mono</c> (one color, following the theme like the buttons
    /// beside it) or <c>color</c> (the plus badge in <see cref="AccentColor"/>).
    /// </summary>
    public string ButtonStyle { get; set; } = "color";

    /// <summary>Gets or sets the badge color for the two-color style, as <c>#RRGGBB</c>. Default: Jellyfin's accent blue.</summary>
    public string AccentColor { get; set; } = "#00A4DC";

    /// <summary>
    /// Gets or sets how far up material is shown: <c>item</c> (only on the item that has it),
    /// <c>section</c> (lessons' material also on their season page), or <c>all</c> (also on the
    /// course/series page).
    /// </summary>
    public string ShowOnParents { get; set; } = "all";

    /// <summary>Gets or sets a value indicating whether the icon appears on cards in grid views (top right, beside the unwatched count).</summary>
    public bool ShowOnCards { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the icon appears on rows in list views (beside the favorite heart).</summary>
    public bool ShowInLists { get; set; } = true;

    /// <summary>
    /// Gets or sets how old, in minutes, a folder's entry in the index may be before a page that
    /// uses it triggers a background re-check of that folder. The stored answer is still shown at once.
    /// </summary>
    public int IndexRefreshMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether a zip inside a material archive has its own files
    /// listed (and downloadable) in the Contents view, rather than appearing as one file.
    /// </summary>
    public bool ListNestedZips { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin builds archives itself, from the files
    /// stored with the videos, using the helper script's rules. A course that already has archives
    /// the plugin did not build (made by hand or by the script) is left as it is.
    /// </summary>
    public bool BuildArchives { get; set; }

    /// <summary>Gets or sets a value indicating whether missing or outdated archives are built in the background after library scans, rather than on first download.</summary>
    public bool BuildInBackground { get; set; } = true;

    /// <summary>
    /// Gets or sets where built archives go: <c>beside</c> (next to the videos, where the plugin
    /// and the script look for them; the plugin's cache when that folder cannot be written) or
    /// <c>cache</c> (always the plugin's own data folder, never touching the media folders).
    /// </summary>
    public string BuiltArchiveLocation { get; set; } = "beside";

    /// <summary>
    /// Gets or sets a value indicating whether material that is a single file is downloaded as
    /// that file, with no archive around it, unless it is large and compresses well.
    /// </summary>
    public bool ServeSingleFiles { get; set; } = true;

    /// <summary>Gets or sets the size, in MB, from which a single file is zipped anyway when zipping makes it noticeably smaller.</summary>
    public int SingleFileZipFromMegabytes { get; set; } = 100;

    /// <summary>
    /// Gets or sets who sees the files that were left out of a built archive (adverts, shortcuts,
    /// links outside the library), with the reason: <c>everyone</c>, <c>admins</c> or <c>nobody</c>.
    /// </summary>
    public string ShowLeftOutFiles { get; set; } = "everyone";

    /// <summary>Gets or sets a value indicating whether course and section pages offer "Download everything": all the material the picker lists, in one zip.</summary>
    public bool OfferDownloadEverything { get; set; } = true;

    /// <summary>
    /// Gets or sets the size, in MB, below which "Download everything" is streamed as it is put
    /// together. From this size it is prepared on disk first, and its download can be resumed.
    /// </summary>
    public int StreamBundlesBelowMegabytes { get; set; } = 150;

    /// <summary>Gets or sets a value indicating whether a streamed bundle is also kept on disk, so the next download of the same material is served from there.</summary>
    public bool KeepStreamedBundles { get; set; } = true;

    /// <summary>Gets or sets the ids of rules that are turned off (built-in or the administrator's own).</summary>
    public string[] DisabledRules { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the administrator's rules, in the rule file format: new ones, and edited copies
    /// of built-in ones, which replace the built-in rule with the same id.
    /// </summary>
    public CustomRule[] CustomRules { get; set; } = Array.Empty<CustomRule>();

    /// <summary>
    /// Gets or sets the VirusTotal API key. When set, blocked files are looked up by SHA-256
    /// (only the fingerprint is sent; nothing is uploaded) and the result goes in their note.
    /// </summary>
    public string VirusTotalApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets how many VirusTotal requests are made per minute (the free API allows 4).</summary>
    public int VirusTotalRequestsPerMinute { get; set; } = 4;

    /// <summary>Gets or sets a value indicating whether executable content VirusTotal knows and no engine flags is included as is, rather than replaced by a note.</summary>
    public bool AllowCleanExecutables { get; set; }
}

/// <summary>One of the administrator's rules.</summary>
public class CustomRule
{
    /// <summary>Gets or sets the rule's id, as in its text.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the rule file's text (TOML).</summary>
    public string Text { get; set; } = string.Empty;
}
