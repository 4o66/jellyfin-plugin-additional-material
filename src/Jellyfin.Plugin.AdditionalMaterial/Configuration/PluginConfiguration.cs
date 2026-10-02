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
}
