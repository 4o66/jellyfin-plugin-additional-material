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
}
