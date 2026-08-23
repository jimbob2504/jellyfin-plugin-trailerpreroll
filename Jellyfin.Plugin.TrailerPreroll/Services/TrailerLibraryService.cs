using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrailerPreroll.Model;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerPreroll.Services
{
    /// <summary>
    /// Owns the two on-disk folders that hold the cached trailers and the two Jellyfin libraries
    /// that expose them (one for library trailers, one for upcoming trailers). The libraries are
    /// created automatically; the user can hide or delete them from Dashboard - Libraries.
    /// </summary>
    public class TrailerLibraryService
    {
        /// <summary>Display name of the library-trailers library.</summary>
        public const string LibraryName = "Trailer Preroll (Library)";

        /// <summary>Display name of the upcoming-trailers library.</summary>
        public const string UpcomingName = "Trailer Preroll (Upcoming)";

        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<TrailerLibraryService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrailerLibraryService"/> class.
        /// </summary>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="appPaths">Application paths.</param>
        /// <param name="logger">Logger.</param>
        public TrailerLibraryService(ILibraryManager libraryManager, IApplicationPaths appPaths, ILogger<TrailerLibraryService> logger)
        {
            _libraryManager = libraryManager;
            _appPaths = appPaths;
            _logger = logger;
        }

        private string BaseDir => Path.Combine(_appPaths.DataPath, "trailer-preroll");

        /// <summary>Gets the folder holding cached library trailers.</summary>
        public string LibraryDir => Path.Combine(BaseDir, "library");

        /// <summary>Gets the folder holding cached upcoming trailers.</summary>
        public string UpcomingDir => Path.Combine(BaseDir, "upcoming");

        /// <summary>Gets the on-disk folder for a category.</summary>
        /// <param name="category">The category.</param>
        /// <returns>The folder path.</returns>
        public string DirFor(PrerollCategory category)
            => category == PrerollCategory.Upcoming ? UpcomingDir : LibraryDir;

        /// <summary>
        /// Ensures the folders and the two libraries exist.
        /// </summary>
        /// <returns>A task.</returns>
        public async Task EnsureLibrariesAsync()
        {
            Directory.CreateDirectory(LibraryDir);
            Directory.CreateDirectory(UpcomingDir);

            await EnsureLibraryAsync(LibraryName, LibraryDir).ConfigureAwait(false);
            await EnsureLibraryAsync(UpcomingName, UpcomingDir).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the internal item id of a library by its display name, or <see cref="Guid.Empty"/>.
        /// </summary>
        /// <param name="name">The library display name.</param>
        /// <returns>The library's folder id.</returns>
        public Guid GetLibraryId(string name)
        {
            try
            {
                var vf = _libraryManager.GetVirtualFolders()
                    .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
                if (vf?.ItemId is not null && Guid.TryParse(vf.ItemId, out var id))
                {
                    return id;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Trailer Preroll could not resolve library id for {Name}", name);
            }

            return Guid.Empty;
        }

        private async Task EnsureLibraryAsync(string name, string dir)
        {
            try
            {
                var exists = _libraryManager.GetVirtualFolders()
                    .Any(f => string.Equals(f.Name, name, StringComparison.Ordinal));
                if (exists)
                {
                    return;
                }

                var options = new LibraryOptions
                {
                    EnableRealtimeMonitor = true,
                    EnablePhotos = false,
                    PathInfos = new[] { new MediaPathInfo { Path = dir } }
                };

                _logger.LogInformation("Trailer Preroll creating library '{Name}' at {Dir}", name, dir);
                await _libraryManager.AddVirtualFolder(name, CollectionTypeOptions.homevideos, options, refreshLibrary: false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Trailer Preroll could not auto-create the '{Name}' library. You can add it manually: Dashboard - Libraries - Add Media Library (Home videos) pointing at {Dir}.",
                    name,
                    dir);
            }
        }
    }
}
