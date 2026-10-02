using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.AdditionalMaterial.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AdditionalMaterial;

/// <summary>
/// Offers a downloadable archive of additional material (a .zip stored beside the media)
/// on courses, sections and lessons in the libraries an administrator enables.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's permanent ID. Never change it: it is how servers recognize the plugin.</summary>
    public const string PluginGuid = "10121f36-d2e1-4b8d-96c4-b2cc720880f3";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Server application paths.</param>
    /// <param name="xmlSerializer">Serializer for the plugin configuration.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Additional Material";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string Description =>
        "Adds an \"Additional material\" download to items that have a matching .zip archive stored beside them.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            },
        ];
    }
}
