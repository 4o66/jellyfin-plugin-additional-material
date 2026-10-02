using Jellyfin.Plugin.AdditionalMaterial.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AdditionalMaterial;

/// <summary>Registers the plugin's services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MaterialIndex>();
        serviceCollection.AddSingleton<MaterialLocator>();
        serviceCollection.AddSingleton<LinkSigner>();
        serviceCollection.AddTransient<IStartupFilter, FreshIndexStartupFilter>();
    }
}
