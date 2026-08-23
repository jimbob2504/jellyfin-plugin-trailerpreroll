using Jellyfin.Plugin.TrailerPreroll.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TrailerPreroll
{
    /// <summary>
    /// Registers the plugin's services with the host DI container.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<PrerollHealth>();
            serviceCollection.AddSingleton<TrailerCacheService>();
            serviceCollection.AddSingleton<TrailerLibraryService>();
            serviceCollection.AddSingleton<PrerollPlayTracker>();
            serviceCollection.AddSingleton<TrailerCatalogService>();
            serviceCollection.AddSingleton<PrerollState>();

            serviceCollection.AddHostedService<PrerollHostedService>();

            // PrerollIntroProvider is discovered via GetExports<IIntroProvider>(); its deps resolve from DI.
        }
    }
}
