using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TrailerPreroll.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TrailerPreroll
{
    /// <summary>
    /// The Trailer Preroll plugin entry point.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Application paths.</param>
        /// <param name="xmlSerializer">XML serializer.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <summary>
        /// Gets the singleton instance of the plugin.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public override string Name => "Trailer Preroll";

        /// <inheritdoc />
        public override string Description =>
            "Plays a cinema-style pre-show of trailers (from your own library and upcoming releases) before movies and TV episodes. Trailers are fetched with yt-dlp into a small, rotating local cache and served from two auto-created libraries.";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("b7e9f2a1-4c3d-4e5f-9a8b-6c7d8e9f0a1b");

        /// <summary>
        /// Gets the current configuration.
        /// </summary>
        public PluginConfiguration Config => Configuration;

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                DisplayName = "Trailer Preroll",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "theaters"
            };
        }
    }
}
