using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.TrailerPreroll.Configuration;
using Jellyfin.Plugin.TrailerPreroll.Model;
using Jellyfin.Plugin.TrailerPreroll.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerPreroll.Intros
{
    /// <summary>
    /// Supplies the cinema-style preroll before movies and TV episodes, choosing from the two
    /// trailer libraries. Discovered automatically by the server via GetExports&lt;IIntroProvider&gt;().
    /// </summary>
    public class PrerollIntroProvider : IIntroProvider
    {
        private readonly TrailerLibraryService _libraries;
        private readonly PrerollState _state;
        private readonly PrerollPlayTracker _playTracker;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<PrerollIntroProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PrerollIntroProvider"/> class.
        /// </summary>
        /// <param name="libraries">Library/folder service.</param>
        /// <param name="state">Session state.</param>
        /// <param name="playTracker">Play-count tracker.</param>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="logger">Logger.</param>
        public PrerollIntroProvider(
            TrailerLibraryService libraries,
            PrerollState state,
            PrerollPlayTracker playTracker,
            ILibraryManager libraryManager,
            ILogger<PrerollIntroProvider> logger)
        {
            _libraries = libraries;
            _state = state;
            _playTracker = playTracker;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Trailer Preroll";

        /// <inheritdoc />
        public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
        {
            try
            {
                return Task.FromResult(GetIntrosInternal(item, user));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trailer Preroll failed to build intros for {Item}", item?.Name);
                return Task.FromResult(Enumerable.Empty<IntroInfo>());
            }
        }

        private IEnumerable<IntroInfo> GetIntrosInternal(BaseItem item, User user)
        {
            var config = Plugin.Instance?.Config;
            if (config is null || item is null || user is null)
            {
                return Enumerable.Empty<IntroInfo>();
            }

            // Resolve settings for this specific user (per-user override, or global fallback).
            var settings = config.ResolveForUser(user.Id);

            var kind = item.GetBaseItemKind();
            if (kind == BaseItemKind.Movie)
            {
                if (!settings.EnableForMovies)
                {
                    return Enumerable.Empty<IntroInfo>();
                }
            }
            else if (kind == BaseItemKind.Episode)
            {
                if (!settings.EnableForTvShows)
                {
                    return Enumerable.Empty<IntroInfo>();
                }

                if (settings.EpisodeMode == EpisodeMode.FirstEpisodeOfSession
                    && !_state.ShouldServeEpisode(user.Id, config.SessionGapMinutes))
                {
                    return Enumerable.Empty<IntroInfo>();
                }
            }
            else
            {
                return Enumerable.Empty<IntroInfo>();
            }

            var orderedIds = new List<Guid>();
            var served = new List<KeyValuePair<string, string?>>();

            // --- Library trailers (honouring the per-user watched/unwatched filter) ---
            var libraryCount = Clamp(settings.LibraryTrailerCount);
            if (libraryCount > 0 && (config.LibrarySourceYoutube || config.LibrarySourceTmdb))
            {
                var eligibleKeys = GetEligibleLibraryKeys(user, settings.LibraryFilter);
                var libItems = GetLibraryItems(TrailerLibraryService.LibraryName);
                var candidates = libItems
                    .Where(kv => eligibleKeys.Contains(kv.Key))
                    .ToList();

                if (config.Randomize)
                {
                    Shuffle(candidates);
                }

                foreach (var kv in candidates.Take(libraryCount))
                {
                    orderedIds.Add(kv.Value);
                    served.Add(new KeyValuePair<string, string?>(kv.Key, _libraryManager.GetItemById(kv.Value)?.Name));
                }
            }

            // --- Upcoming trailers (+ optional bumper before them) ---
            var upcomingCount = Clamp(settings.UpcomingTrailerCount);
            if (upcomingCount > 0)
            {
                var bumperKey = config.EnableIntroVideo ? YoutubeId.TryExtract(config.IntroVideoUrl) : null;
                var upItems = GetLibraryItems(TrailerLibraryService.UpcomingName);

                var upcomingCandidates = upItems
                    .Where(kv => bumperKey is null || !string.Equals(kv.Key, bumperKey, StringComparison.Ordinal))
                    .ToList();

                if (config.Randomize)
                {
                    Shuffle(upcomingCandidates);
                }

                var chosenUpcoming = upcomingCandidates.Take(upcomingCount).ToList();

                if (chosenUpcoming.Count > 0 && bumperKey is not null && upItems.TryGetValue(bumperKey, out var bumperId))
                {
                    orderedIds.Add(bumperId);
                }

                foreach (var kv in chosenUpcoming)
                {
                    orderedIds.Add(kv.Value);
                    served.Add(new KeyValuePair<string, string?>(kv.Key, _libraryManager.GetItemById(kv.Value)?.Name));
                }
            }

            if (orderedIds.Count == 0)
            {
                return Enumerable.Empty<IntroInfo>();
            }

            // Count these as plays (drives the rolling "replace after N plays" rotation).
            _playTracker.RecordPlays(served);

            _logger.LogInformation("Trailer Preroll: serving {Count} trailer(s) before {Kind} '{Name}'", orderedIds.Count, kind, item.Name);
            return orderedIds.Select(id => new IntroInfo { ItemId = id });
        }

        /// <summary>
        /// Returns a map of YouTube key to item Guid for the video items in the named trailer library.
        /// </summary>
        private Dictionary<string, Guid> GetLibraryItems(string libraryName)
        {
            var map = new Dictionary<string, Guid>(StringComparer.Ordinal);

            var libId = _libraries.GetLibraryId(libraryName);
            if (libId.Equals(Guid.Empty))
            {
                return map;
            }

            // Query WITHOUT a user so it still finds the trailers even when the two libraries are
            // hidden/disabled for users (which is exactly what people do to keep them off the home screen).
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = libId,
                Recursive = true
            });

            foreach (var dbItem in items)
            {
                if (dbItem is not Video || string.IsNullOrEmpty(dbItem.Path))
                {
                    continue;
                }

                var key = PrerollItem.KeyFromFileName(dbItem.Path);
                if (!string.IsNullOrEmpty(key))
                {
                    map[key] = dbItem.Id;
                }
            }

            return map;
        }

        private List<string> GetEligibleLibraryKeys(User user, LibraryFilter filter)
        {
            bool? isPlayed = filter == LibraryFilter.UnwatchedOnly ? false : (bool?)null;

            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false,
                User = user,
                IsPlayed = isPlayed
            });

            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var movie in movies)
            {
                var trailers = movie.RemoteTrailers;
                if (trailers is null)
                {
                    continue;
                }

                foreach (var trailer in trailers)
                {
                    var key = YoutubeId.TryExtract(trailer.Url);
                    if (key is not null && seen.Add(key))
                    {
                        keys.Add(key);
                    }
                }
            }

            return keys;
        }

        private static int Clamp(int value) => value < 0 ? 0 : (value > 10 ? 10 : value);

        private static void Shuffle<T>(IList<T> list)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
