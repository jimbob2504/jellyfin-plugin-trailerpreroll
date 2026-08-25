using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TrailerPreroll.Model;
using Jellyfin.Plugin.TrailerPreroll.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerPreroll.Api
{
    /// <summary>
    /// API for the Trailer Preroll plugin: admin actions used by the settings page, the client
    /// script served to the web player, and the per-user "Watch Later" action.
    /// </summary>
    [ApiController]
    [Route("TrailerPreroll")]
    public class TrailerPrerollController : ControllerBase
    {
        private const string WatchLaterName = "Watch Later";

        private readonly TrailerLibraryService _libraries;
        private readonly TrailerCatalogService _catalog;
        private readonly TrailerCacheService _downloader;
        private readonly PrerollHealth _health;
        private readonly PrerollPlayTracker _playTracker;
        private readonly ILibraryManager _libraryManager;
        private readonly IPlaylistManager _playlistManager;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<TrailerPrerollController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrailerPrerollController"/> class.
        /// </summary>
        /// <param name="libraries">Library/folder service.</param>
        /// <param name="catalog">Rotation service.</param>
        /// <param name="downloader">Trailer downloader (for tool-status checks).</param>
        /// <param name="health">Download health tracker.</param>
        /// <param name="playTracker">Play-count tracker.</param>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="playlistManager">Playlist manager.</param>
        /// <param name="authContext">Authorization context (to identify the calling user).</param>
        /// <param name="logger">Logger.</param>
        public TrailerPrerollController(
            TrailerLibraryService libraries,
            TrailerCatalogService catalog,
            TrailerCacheService downloader,
            PrerollHealth health,
            PrerollPlayTracker playTracker,
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            IAuthorizationContext authContext,
            ILogger<TrailerPrerollController> logger)
        {
            _libraries = libraries;
            _catalog = catalog;
            _downloader = downloader;
            _health = health;
            _playTracker = playTracker;
            _libraryManager = libraryManager;
            _playlistManager = playlistManager;
            _authContext = authContext;
            _logger = logger;
        }

        /// <summary>
        /// Triggers a trailer download/rotation now (runs in the background).
        /// </summary>
        /// <returns>No content.</returns>
        [HttpPost("Refresh")]
        [Authorize(Policy = "RequiresElevation")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult Refresh()
        {
            _logger.LogInformation("Trailer Preroll manual refresh requested.");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _libraries.EnsureLibrariesAsync().ConfigureAwait(false);
                    await _catalog.RotateIfNeededAsync(force: true, CancellationToken.None).ConfigureAwait(false);
                    await _catalog.RollReplaceAsync(maxPerRun: 3, CancellationToken.None).ConfigureAwait(false);
                    await _catalog.CleanupItemNamesAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Trailer Preroll manual refresh failed.");
                }
            });

            return NoContent();
        }

        /// <summary>
        /// Re-types the already-cached trailer files as Trailer items (files are kept; no re-download).
        /// </summary>
        /// <returns>No content.</returns>
        [HttpPost("ConvertTypes")]
        [Authorize(Policy = "RequiresElevation")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult ConvertTypes()
        {
            _logger.LogInformation("Trailer Preroll type-conversion requested.");
            _ = Task.Run(() =>
            {
                try
                {
                    _catalog.ConvertLegacyVideos(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Trailer Preroll type conversion failed.");
                }
            });

            return NoContent();
        }

        /// <summary>
        /// Downloads (or updates) yt-dlp and deno for this server's OS into the data folder, in the
        /// background. Runs from the settings page; check the status panel afterwards to confirm.
        /// </summary>
        /// <returns>No content.</returns>
        [HttpPost("InstallTools")]
        [Authorize(Policy = "RequiresElevation")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult InstallTools()
        {
            _logger.LogInformation("Trailer Preroll tool download requested.");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _downloader.InstallToolsAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Trailer Preroll tool download failed.");
                }
            });

            return NoContent();
        }

        /// <summary>
        /// Returns download-pipeline health for the settings page.
        /// </summary>
        /// <returns>A health summary.</returns>
        [HttpGet("Health")]
        [Authorize(Policy = "RequiresElevation")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> Health()
        {
            var config = Plugin.Instance?.Config;
            var cookiesPath = config?.YtDlpCookiesPath ?? string.Empty;
            var cookiesExists = !string.IsNullOrWhiteSpace(cookiesPath) && System.IO.File.Exists(cookiesPath);
            DateTime? cookiesModifiedUtc = cookiesExists ? System.IO.File.GetLastWriteTimeUtc(cookiesPath) : null;

            var cookieWarning = _health.CycleAuthFailed >= 5
                || (_health.CycleAttempts >= 4 && (_health.CycleAuthFailed * 2) >= _health.CycleAttempts);

            var tools = _downloader.GetToolStatus();

            return new
            {
                ytDlpFound = tools.YtDlp,
                denoFound = tools.Deno,
                ffmpegFound = tools.Ffmpeg,
                lastCycleUtc = _health.LastCycleUtc,
                cycleAttempts = _health.CycleAttempts,
                cycleSucceeded = _health.CycleSucceeded,
                cycleFailed = _health.CycleFailed,
                cycleAuthFailed = _health.CycleAuthFailed,
                libraryPoolSize = _health.LibraryPoolSize,
                upcomingPoolSize = _health.UpcomingPoolSize,
                libraryCached = CountCached(_libraries.LibraryDir),
                upcomingCached = CountCached(_libraries.UpcomingDir),
                totalAttempts = _health.Attempts,
                totalSucceeded = _health.Succeeded,
                totalFailed = _health.Failed,
                totalAuthFailed = _health.AuthFailed,
                lastSuccessUtc = _health.LastSuccessUtc,
                lastFailureUtc = _health.LastFailureUtc,
                lastAuthFailureUtc = _health.LastAuthFailureUtc,
                lastError = _health.LastError,
                cookiesPath,
                cookiesExists,
                cookiesModifiedUtc,
                cookieWarning
            };
        }

        /// <summary>
        /// Adds the film behind the currently-playing trailer to the calling user's "Watch Later"
        /// playlist. For library trailers the actual movie is added; for upcoming trailers (not in the
        /// library) the trailer item itself is added as a placeholder.
        /// </summary>
        /// <param name="itemId">The playing trailer item's id.</param>
        /// <returns>A small JSON result describing what was added.</returns>
        [HttpPost("WatchLater")]
        [Authorize]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult> WatchLater([FromQuery] Guid itemId)
        {
            if (itemId.Equals(Guid.Empty))
            {
                return BadRequest();
            }

            var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            var userId = auth.UserId;
            if (userId.Equals(Guid.Empty))
            {
                return Unauthorized();
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                return NotFound();
            }

            var targetId = item.Id;
            var title = item.Name;
            var isMovie = false;

            var key = PrerollItem.KeyFromFileName(item.Path);
            if (!string.IsNullOrEmpty(key))
            {
                var movie = FindMovieByTrailerKey(key);
                if (movie is not null)
                {
                    targetId = movie.Id;
                    title = movie.Name;
                    isMovie = true;
                }
            }

            bool added;
            try
            {
                added = await AddToWatchLaterAsync(userId, targetId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trailer Preroll Watch Later add failed for user {User}, item {Item}", userId, targetId);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (added)
            {
                _logger.LogInformation("Trailer Preroll added '{Title}' to Watch Later for user {User}.", title, userId);
            }

            return Ok(new { added, alreadyPresent = !added, title, kind = isMovie ? "movie" : "trailer" });
        }

        /// <summary>
        /// Returns how many times each cached trailer has played, most-played first, for the settings page.
        /// </summary>
        /// <returns>A list of play-count rows.</returns>
        [HttpGet("PlayCounts")]
        [Authorize(Policy = "RequiresElevation")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> PlayCounts()
        {
            var counts = _playTracker.GetAll();
            var titles = BuildKeyTitleMap();

            var items = counts
                .Select(kv => new
                {
                    count = kv.Value,
                    title = titles.TryGetValue(kv.Key, out var t) ? t : kv.Key
                })
                .OrderByDescending(r => r.count)
                .ThenBy(r => r.title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new { total = items.Count, items };
        }

        /// <summary>
        /// Serves the client script injected into the web player (loaded by a plain script tag, so it
        /// must be anonymous).
        /// </summary>
        /// <returns>The JavaScript.</returns>
        [HttpGet("ClientScript")]
        [AllowAnonymous]
        public ActionResult ClientScript()
        {
            var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.TrailerPreroll.Web.clientScript.js");
            if (stream is null)
            {
                return NotFound();
            }

            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd(), "application/javascript");
        }

        /// <summary>
        /// Adds <paramref name="itemId"/> to the user's Watch Later playlist (creating it if needed).
        /// Returns <c>false</c> if the item was already in the playlist (no duplicate added).
        /// </summary>
        private async Task<bool> AddToWatchLaterAsync(Guid userId, Guid itemId)
        {
            var existing = _playlistManager.GetPlaylists(userId)
                .FirstOrDefault(p => string.Equals(p.Name, WatchLaterName, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                var contents = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = existing.Id,
                    Recursive = true
                });

                if (contents.Any(i => i.Id.Equals(itemId)))
                {
                    return false; // already saved - don't add a duplicate
                }

                await _playlistManager.AddItemToPlaylistAsync(existing.Id, new[] { itemId }, userId).ConfigureAwait(false);
                return true;
            }

            await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = WatchLaterName,
                UserId = userId,
                MediaType = MediaType.Video,
                ItemIdList = new[] { itemId }
            }).ConfigureAwait(false);
            return true;
        }

        private BaseItem? FindMovieByTrailerKey(string key)
        {
            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false
            });

            foreach (var movie in movies)
            {
                var trailers = movie.RemoteTrailers;
                if (trailers is null)
                {
                    continue;
                }

                foreach (var trailer in trailers)
                {
                    if (string.Equals(YoutubeId.TryExtract(trailer.Url), key, StringComparison.Ordinal))
                    {
                        return movie;
                    }
                }
            }

            return null;
        }

        private Dictionary<string, string> BuildKeyTitleMap()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var dir in new[] { _libraries.LibraryDir, _libraries.UpcomingDir })
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var f in Directory.GetFiles(dir, "*.mp4"))
                {
                    if (Path.GetFileName(f).StartsWith("dl_", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var key = PrerollItem.KeyFromFileName(f);
                    if (string.IsNullOrEmpty(key) || map.ContainsKey(key))
                    {
                        continue;
                    }

                    var title = System.Text.RegularExpressions.Regex
                        .Replace(Path.GetFileNameWithoutExtension(f), @"\s*\[[A-Za-z0-9_-]{11}\]\s*", " ")
                        .Trim();
                    map[key] = string.IsNullOrEmpty(title) ? key : title;
                }
            }

            return map;
        }

        private static int CountCached(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    return 0;
                }

                return Directory.GetFiles(dir, "*.mp4")
                    .Count(f => !Path.GetFileName(f).StartsWith("dl_", StringComparison.Ordinal));
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }
}
