using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TrailerPreroll.Configuration;
using Jellyfin.Plugin.TrailerPreroll.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using TMDbLib.Client;

namespace Jellyfin.Plugin.TrailerPreroll.Services
{
    /// <summary>
    /// Decides which trailers should be cached (rotating on a schedule), downloads them into the two
    /// library folders, prunes stale files, and asks Jellyfin to pick up the changes.
    /// </summary>
    public class TrailerCatalogService
    {
        private const string TmdbProviderKey = "Tmdb";

        private readonly ILibraryManager _libraryManager;
        private readonly TrailerCacheService _downloader;
        private readonly TrailerLibraryService _libraries;
        private readonly PrerollPlayTracker _playTracker;
        private readonly PrerollHealth _health;
        private readonly IPlaylistManager _playlistManager;
        private readonly IUserManager _userManager;
        private readonly ILogger<TrailerCatalogService> _logger;

        private readonly SemaphoreSlim _rotationLock = new(1, 1);
        private string _lastEpoch = string.Empty;
        private List<PrerollItem> _libraryPool = new();
        private List<PrerollItem> _upcomingPool = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="TrailerCatalogService"/> class.
        /// </summary>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="downloader">Trailer downloader.</param>
        /// <param name="libraries">Library/folder service.</param>
        /// <param name="playTracker">Play-count tracker.</param>
        /// <param name="health">Download health tracker.</param>
        /// <param name="playlistManager">Playlist manager (to protect Watch Later trailers).</param>
        /// <param name="userManager">User manager.</param>
        /// <param name="logger">Logger.</param>
        public TrailerCatalogService(
            ILibraryManager libraryManager,
            TrailerCacheService downloader,
            TrailerLibraryService libraries,
            PrerollPlayTracker playTracker,
            PrerollHealth health,
            IPlaylistManager playlistManager,
            IUserManager userManager,
            ILogger<TrailerCatalogService> logger)
        {
            _libraryManager = libraryManager;
            _downloader = downloader;
            _libraries = libraries;
            _playTracker = playTracker;
            _health = health;
            _playlistManager = playlistManager;
            _userManager = userManager;
            _logger = logger;
        }

        private static PluginConfiguration Config => Plugin.Instance!.Config;

        /// <summary>
        /// Rotates the cached trailer set if the rotation epoch changed (or when forced).
        /// </summary>
        /// <param name="force">Force a rebuild of the pool selection regardless of epoch.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><c>true</c> if there are still trailers left to download (budget was hit).</returns>
        public async Task<bool> RotateIfNeededAsync(bool force, CancellationToken cancellationToken)
        {
            var config = Config;
            var epoch = CurrentEpoch(config.RotationInterval);

            if (!await _rotationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                var needRebuild = force
                    || !string.Equals(epoch, _lastEpoch, StringComparison.Ordinal)
                    || _libraryPool.Count == 0;

                if (needRebuild)
                {
                    _libraryPool = BuildLibraryPool(config, epoch);
                    _upcomingPool = await BuildUpcomingPoolAsync(config, cancellationToken).ConfigureAwait(false);
                    _lastEpoch = epoch;
                }

                return await SyncPoolsAsync(config, epoch, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trailer Preroll rotation failed");
                return false;
            }
            finally
            {
                _rotationLock.Release();
            }
        }

        /// <summary>
        /// Rolling rotation: for each trailer played at least "RotateAfterPlays" times, download ONE
        /// fresh replacement then retire the old one (up to <paramref name="maxPerRun"/> per call). A
        /// gentle trickle that keeps the pool fresh without bursts.
        /// </summary>
        /// <param name="maxPerRun">Maximum replacements to perform this call.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task RollReplaceAsync(int maxPerRun, CancellationToken cancellationToken)
        {
            var config = Config;
            var threshold = config.RotateAfterPlays;
            if (threshold <= 0)
            {
                return;
            }

            var overplayed = _playTracker.KeysAtOrOver(threshold);
            if (overplayed.Count == 0)
            {
                return;
            }

            if (!await _rotationLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                var libraryCandidates = BuildLibraryCandidates(config);
                var upcomingCandidates = await BuildUpcomingPoolAsync(config, cancellationToken).ConfigureAwait(false);
                var overSet = new HashSet<string>(overplayed, StringComparer.Ordinal);
                var protectedKeys = GetProtectedKeys();

                var changed = false;
                var replaced = 0;

                foreach (var key in overplayed)
                {
                    if (replaced >= maxPerRun)
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Keep trailers that are saved in someone's Watch Later, even if over-played.
                    if (protectedKeys.Contains(key))
                    {
                        continue;
                    }

                    var (dir, isUpcoming, oldPath) = LocateCached(key);
                    if (dir is null)
                    {
                        _playTracker.Forget(key);
                        continue;
                    }

                    var cachedKeys = GetCachedKeys(dir);
                    var pool = isUpcoming ? upcomingCandidates : libraryCandidates;
                    var freshPicks = pool
                        .Where(p => !cachedKeys.Contains(p.YoutubeKey) && !overSet.Contains(p.YoutubeKey))
                        .ToList();
                    if (freshPicks.Count == 0)
                    {
                        continue;
                    }

                    Shuffle(freshPicks);
                    var replacement = freshPicks[0];
                    var newPath = Path.Combine(dir, replacement.FileName);

                    // Download the replacement FIRST; only remove the old one if it succeeds.
                    if (await _downloader.DownloadAsync(replacement.YoutubeKey, newPath, cancellationToken).ConfigureAwait(false))
                    {
                        try
                        {
                            if (oldPath is not null && File.Exists(oldPath))
                            {
                                File.Delete(oldPath);
                            }
                        }
                        catch (IOException)
                        {
                        }

                        _playTracker.Forget(key);
                        overSet.Add(replacement.YoutubeKey);
                        replaced++;
                        changed = true;
                        _logger.LogInformation(
                            "Trailer Preroll rolled: replaced {Old} (played {N}+ times) with {New}",
                            key,
                            threshold,
                            replacement.YoutubeKey);
                    }
                }

                if (changed)
                {
                    _libraryManager.QueueLibraryScan();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trailer Preroll rolling replacement failed");
            }
            finally
            {
                _rotationLock.Release();
            }
        }

        private static readonly Regex KeyInName = new(@"\s*\[[A-Za-z0-9_-]{11}\]\s*", RegexOptions.Compiled);

        /// <summary>
        /// Re-types any cached trailer items that are still plain <see cref="Video"/> (not
        /// <see cref="Trailer"/>) by removing the DB item (files are KEPT on disk) and queuing a scan,
        /// so the plugin's resolver re-creates them as Trailers. No re-download occurs. Returns the
        /// number of items removed for re-resolution (0 = everything already a Trailer).
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of items converted.</returns>
        public int ConvertLegacyVideos(CancellationToken cancellationToken)
        {
            var converted = 0;
            try
            {
                foreach (var name in new[] { TrailerLibraryService.LibraryName, TrailerLibraryService.UpcomingName })
                {
                    var libId = _libraries.GetLibraryId(name);
                    if (libId.Equals(Guid.Empty))
                    {
                        continue;
                    }

                    var items = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        ParentId = libId,
                        Recursive = true
                    });

                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Convert only plain videos; leave any already-Trailer items alone.
                        if (item is Video && item is not Trailer && !string.IsNullOrEmpty(item.Path))
                        {
                            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                            converted++;
                        }
                    }
                }

                if (converted > 0)
                {
                    _logger.LogInformation("Trailer Preroll converting {Count} legacy Video item(s) to Trailer type; queuing a scan.", converted);
                    _libraryManager.QueueLibraryScan();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trailer Preroll legacy-to-Trailer conversion failed");
            }

            return converted;
        }

        /// <summary>
        /// Tidies the display metadata of already-cached trailer items WITHOUT re-downloading: strips
        /// the "[youtube-id]" suffix from the shown name and fills in the release year. The files on
        /// disk keep the "[key]" in their filename (that is how the key is mapped back), so nothing is
        /// re-downloaded. Idempotent - only touches items that still need it. The Name field is locked
        /// so a later metadata refresh won't put the id back.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of items updated.</returns>
        public async Task<int> CleanupItemNamesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var libraryYears = GetLibraryKeyYears();
                var upcomingYears = _upcomingPool
                    .Where(p => p.Year.HasValue)
                    .GroupBy(p => p.YoutubeKey, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Year!.Value, StringComparer.Ordinal);

                var updated = 0;
                updated += await CleanupFolderNamesAsync(TrailerLibraryService.LibraryName, libraryYears, cancellationToken).ConfigureAwait(false);
                updated += await CleanupFolderNamesAsync(TrailerLibraryService.UpcomingName, upcomingYears, cancellationToken).ConfigureAwait(false);

                if (updated > 0)
                {
                    _logger.LogInformation("Trailer Preroll tidied {Count} trailer name(s).", updated);
                }

                return updated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trailer Preroll name cleanup failed");
                return 0;
            }
        }

        private async Task<int> CleanupFolderNamesAsync(string libraryName, IReadOnlyDictionary<string, int> keyYears, CancellationToken cancellationToken)
        {
            var libId = _libraries.GetLibraryId(libraryName);
            if (libId.Equals(Guid.Empty))
            {
                return 0;
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = libId,
                Recursive = true
            });

            var updated = 0;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is not Video || string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                var key = PrerollItem.KeyFromFileName(item.Path);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var clean = StripKey(Path.GetFileNameWithoutExtension(item.Path));
                int? year = keyYears.TryGetValue(key, out var y) && y > 0 ? y : null;
                if (year.HasValue && !clean.EndsWith("(" + year.Value + ")", StringComparison.Ordinal))
                {
                    clean = clean + " (" + year.Value + ")";
                }

                // Only touch items whose visible name/year is actually wrong. LockedFields is not
                // hydrated on items from GetItemList, so we must NOT gate on it (that would re-update
                // all items every tick). We still (re)apply the Name lock whenever we do update, so a
                // later metadata refresh won't re-append the id.
                var needsName = !string.Equals(item.Name, clean, StringComparison.Ordinal);
                var needsYear = year.HasValue && item.ProductionYear != year.Value;
                if (!needsName && !needsYear)
                {
                    continue;
                }

                item.Name = clean;
                if (needsYear)
                {
                    item.ProductionYear = year;
                }

                var locked = (item.LockedFields ?? Array.Empty<MetadataField>()).ToList();
                if (!locked.Contains(MetadataField.Name))
                {
                    locked.Add(MetadataField.Name);
                    item.LockedFields = locked.ToArray();
                }

                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                updated++;
            }

            return updated;
        }

        private Dictionary<string, int> GetLibraryKeyYears()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false
            });

            foreach (var movie in movies)
            {
                if (!movie.ProductionYear.HasValue || movie.RemoteTrailers is null)
                {
                    continue;
                }

                foreach (var trailer in movie.RemoteTrailers)
                {
                    var key = YoutubeId.TryExtract(trailer.Url);
                    if (key is not null && !map.ContainsKey(key))
                    {
                        map[key] = movie.ProductionYear.Value;
                    }
                }
            }

            return map;
        }

        private static string StripKey(string name)
        {
            var s = KeyInName.Replace(name, " ").Trim();
            return string.IsNullOrEmpty(s) ? "Trailer" : s;
        }

        /// <summary>
        /// Builds the set of YouTube keys that must NOT be deleted because their film (or the trailer
        /// itself) is in some user's "Watch Later" playlist. For a library movie in the playlist, every
        /// key from its remote trailers is protected; for an upcoming trailer item, its own key is.
        /// </summary>
        private HashSet<string> GetProtectedKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var user in _userManager.GetUsers())
                {
                    foreach (var playlist in _playlistManager.GetPlaylists(user.Id))
                    {
                        if (!string.Equals(playlist.Name, "Watch Later", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var items = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            ParentId = playlist.Id,
                            Recursive = true
                        });

                        foreach (var item in items)
                        {
                            var fileKey = PrerollItem.KeyFromFileName(item.Path);
                            if (!string.IsNullOrEmpty(fileKey))
                            {
                                keys.Add(fileKey);
                            }

                            var trailers = item.RemoteTrailers;
                            if (trailers is not null)
                            {
                                foreach (var trailer in trailers)
                                {
                                    var mk = YoutubeId.TryExtract(trailer.Url);
                                    if (mk is not null)
                                    {
                                        keys.Add(mk);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trailer Preroll could not compute Watch Later protected keys");
            }

            return keys;
        }

        private (string? Dir, bool IsUpcoming, string? OldPath) LocateCached(string key)
        {
            foreach (var (dir, isUp) in new[] { (_libraries.LibraryDir, false), (_libraries.UpcomingDir, true) })
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var match = Directory.GetFiles(dir, "*.mp4")
                    .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("dl_", StringComparison.Ordinal)
                                         && string.Equals(PrerollItem.KeyFromFileName(f), key, StringComparison.Ordinal));
                if (match is not null)
                {
                    return (dir, isUp, match);
                }
            }

            return (null, false, null);
        }

        private static HashSet<string> GetCachedKeys(string dir)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.GetFiles(dir, "*.mp4"))
            {
                if (Path.GetFileName(f).StartsWith("dl_", StringComparison.Ordinal))
                {
                    continue;
                }

                var k = PrerollItem.KeyFromFileName(f);
                if (!string.IsNullOrEmpty(k))
                {
                    set.Add(k);
                }
            }

            return set;
        }

        private List<PrerollItem> BuildLibraryCandidates(PluginConfiguration config)
        {
            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false
            });

            var byKey = new Dictionary<string, PrerollItem>(StringComparer.Ordinal);
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
                    if (key is null || byKey.ContainsKey(key))
                    {
                        continue;
                    }

                    byKey[key] = new PrerollItem(key, movie.Name, PrerollCategory.Library) { Year = movie.ProductionYear };
                    break;
                }
            }

            return byKey.Values.ToList();
        }

        private async Task<bool> SyncPoolsAsync(PluginConfiguration config, string epoch, CancellationToken cancellationToken)
        {
            var bumper = BuildBumper(config);
            var upcomingPlus = new List<PrerollItem>(_upcomingPool);
            if (bumper is not null)
            {
                upcomingPlus.Add(bumper);
            }

            var keepAll = config.KeepAllLibraryTrailers;
            var budget = config.DownloadsPerCycle <= 0 ? int.MaxValue : config.DownloadsPerCycle;
            var libCap = keepAll ? int.MaxValue : (config.LibraryPoolSize <= 0 ? 60 : config.LibraryPoolSize);
            var upCap = (config.UpcomingPoolSize <= 0 ? 12 : config.UpcomingPoolSize) + 1;

            var healthStart = _health.Snapshot();
            var protectedKeys = GetProtectedKeys();

            var (libChanged, libPending, budgetLeft) = await SyncFolderAsync(
                _libraries.LibraryDir, _libraryPool, libCap, budget, keepAll, protectedKeys, cancellationToken).ConfigureAwait(false);
            var (upChanged, upPending, _) = await SyncFolderAsync(
                _libraries.UpcomingDir, upcomingPlus, upCap, budgetLeft, false, protectedKeys, cancellationToken).ConfigureAwait(false);

            _health.CompleteCycle(healthStart, _libraryPool.Count, _upcomingPool.Count);

            if (libChanged || upChanged)
            {
                _libraryManager.QueueLibraryScan();
            }

            _logger.LogInformation(
                "Trailer Preroll rotation (epoch {Epoch}, keepAll={KeepAll}): pool {Lib} library / {Up} upcoming; changed={Changed}, morePending={Pending}",
                epoch,
                keepAll,
                _libraryPool.Count,
                _upcomingPool.Count,
                libChanged || upChanged,
                libPending || upPending);

            return libPending || upPending;
        }

        /// <summary>
        /// Downloads any missing desired files into <paramref name="dir"/>, then removes the oldest
        /// files ONLY if the folder holds more than <paramref name="cap"/> trailers. Existing
        /// trailers are always kept until real replacements have been downloaded (so the folder
        /// never empties). Returns whether anything changed on disk.
        /// </summary>
        private async Task<(bool Changed, bool Pending, int RemainingBudget)> SyncFolderAsync(
            string dir,
            IReadOnlyList<PrerollItem> items,
            int cap,
            int budget,
            bool keepAll,
            HashSet<string> protectedKeys,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(dir);
            var changed = false;
            var pending = false;

            var desired = new HashSet<string>(items.Select(i => i.FileName), StringComparer.OrdinalIgnoreCase);

            // Download missing items up to the budget, in random order (for variety when only a few
            // download per cycle). If the budget runs out before all are cached, report pending.
            var missing = items
                .Where(i => !File.Exists(Path.Combine(dir, i.FileName)))
                .ToList();
            Shuffle(missing);

            foreach (var item in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget <= 0)
                {
                    pending = true;
                    break;
                }

                budget--;
                var target = Path.Combine(dir, item.FileName);
                if (await _downloader.DownloadAsync(item.YoutubeKey, target, cancellationToken).ConfigureAwait(false))
                {
                    changed = true;
                }
            }

            // Prune only when NOT keeping everything, and only when over capacity. Delete stale
            // (non-desired) oldest first so freshly chosen trailers survive.
            if (!keepAll)
            {
                var files = new DirectoryInfo(dir).GetFiles("*.mp4")
                    .Where(f => !f.Name.StartsWith("dl_", StringComparison.Ordinal))
                    .ToList();

                var overflow = files.Count - Math.Max(1, cap);
                if (overflow > 0)
                {
                    // Never delete a trailer that is in someone's Watch Later playlist.
                    var removalOrder = files
                        .Where(f =>
                        {
                            var k = PrerollItem.KeyFromFileName(f.Name);
                            return string.IsNullOrEmpty(k) || !protectedKeys.Contains(k);
                        })
                        .OrderBy(f => desired.Contains(f.Name) ? 1 : 0)
                        .ThenBy(f => f.LastWriteTimeUtc)
                        .Take(overflow);

                    foreach (var f in removalOrder)
                    {
                        try
                        {
                            f.Delete();
                            changed = true;
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            }

            return (changed, pending, budget);
        }

        private List<PrerollItem> BuildLibraryPool(PluginConfiguration config, string epoch)
        {
            if (!config.LibrarySourceYoutube && !config.LibrarySourceTmdb)
            {
                return new List<PrerollItem>();
            }

            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false
            });

            var byKey = new Dictionary<string, PrerollItem>(StringComparer.Ordinal);
            foreach (var movie in movies)
            {
                var trailers = movie.RemoteTrailers;
                if (trailers is null || trailers.Count == 0)
                {
                    continue;
                }

                foreach (var trailer in trailers)
                {
                    var key = YoutubeId.TryExtract(trailer.Url);
                    if (key is null || byKey.ContainsKey(key))
                    {
                        continue;
                    }

                    byKey[key] = new PrerollItem(key, movie.Name, PrerollCategory.Library) { Year = movie.ProductionYear };
                    break;
                }
            }

            var pool = byKey.Values.ToList();

            // "Keep all" mode: cache a trailer for every film (no cap), downloaded gradually over time.
            if (config.KeepAllLibraryTrailers)
            {
                return pool;
            }

            // Otherwise a bounded pool: deterministic per-epoch shuffle so the set is stable within an
            // epoch and rotates across epochs.
            var rng = new Random(StableHash(epoch));
            for (var i = pool.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            var poolSize = config.LibraryPoolSize <= 0 ? 60 : config.LibraryPoolSize;
            return pool.Count > poolSize ? pool.GetRange(0, poolSize) : pool;
        }

        private async Task<List<PrerollItem>> BuildUpcomingPoolAsync(PluginConfiguration config, CancellationToken cancellationToken)
        {
            var result = new List<PrerollItem>();
            if (config.UpcomingTrailerCount <= 0 || !config.UpcomingSourceTmdb || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                return result;
            }

            var existingTmdbIds = GetLibraryTmdbIds();
            var (from, to) = GetWindow(config.UpcomingWindow);
            var cap = config.UpcomingPoolSize <= 0 ? 12 : config.UpcomingPoolSize;

            try
            {
                var client = new TMDbClient(config.TmdbApiKey);
                var byKey = new Dictionary<string, PrerollItem>(StringComparer.Ordinal);

                for (var page = 1; page <= 6 && byKey.Count < cap; page++)
                {
                    var list = await client.GetMovieUpcomingListAsync(null, page, null, cancellationToken).ConfigureAwait(false);
                    if (list?.Results is null || list.Results.Count == 0)
                    {
                        break;
                    }

                    foreach (var movie in list.Results)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!movie.ReleaseDate.HasValue)
                        {
                            continue;
                        }

                        var release = movie.ReleaseDate.Value.Date;
                        if (release < from || (to.HasValue && release > to.Value) || existingTmdbIds.Contains(movie.Id))
                        {
                            continue;
                        }

                        var key = await GetTmdbTrailerKeyAsync(client, movie.Id, cancellationToken).ConfigureAwait(false);
                        if (key is null || byKey.ContainsKey(key))
                        {
                            continue;
                        }

                        byKey[key] = new PrerollItem(key, movie.Title, PrerollCategory.Upcoming) { Year = release.Year };
                    }
                }

                result = byKey.Values.Take(cap).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build upcoming trailer pool from TMDB");
            }

            return result;
        }

        private PrerollItem? BuildBumper(PluginConfiguration config)
        {
            if (!config.EnableIntroVideo)
            {
                return null;
            }

            var key = YoutubeId.TryExtract(config.IntroVideoUrl);
            if (key is null)
            {
                _logger.LogWarning("Intro bumper enabled but URL is not a valid YouTube link: {Url}", config.IntroVideoUrl);
                return null;
            }

            return new PrerollItem(key, "Now for Upcoming Movies", PrerollCategory.Bumper);
        }

        private HashSet<int> GetLibraryTmdbIds()
        {
            var ids = new HashSet<int>();
            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false
            });

            foreach (var movie in movies)
            {
                if (movie.ProviderIds is not null
                    && movie.ProviderIds.TryGetValue(TmdbProviderKey, out var raw)
                    && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        private static async Task<string?> GetTmdbTrailerKeyAsync(TMDbClient client, int movieId, CancellationToken cancellationToken)
        {
            var videos = await client.GetMovieVideosAsync(movieId, cancellationToken).ConfigureAwait(false);
            if (videos?.Results is null)
            {
                return null;
            }

            var youtube = videos.Results
                .Where(v => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(v.Key))
                .ToList();

            // Prefer a full Trailer; fall back to a Teaser (most not-yet-released films only have teasers).
            var best = youtube
                    .Where(v => string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v.Official)
                    .FirstOrDefault()
                ?? youtube
                    .Where(v => string.Equals(v.Type, "Teaser", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v.Official)
                    .FirstOrDefault();

            return best?.Key;
        }

        private static (DateTime From, DateTime? To) GetWindow(UpcomingWindow window)
        {
            var today = DateTime.UtcNow.Date;
            return window switch
            {
                UpcomingWindow.TwoWeeks => (today, today.AddDays(14)),
                UpcomingWindow.OneMonth => (today, today.AddMonths(1)),
                UpcomingWindow.TwoMonths => (today, today.AddMonths(2)),
                UpcomingWindow.ThreeMonths => (today, today.AddMonths(3)),
                UpcomingWindow.NoLimit => (today, (DateTime?)null),
                _ => (today, today.AddMonths(1))
            };
        }

        private static string CurrentEpoch(CacheRotationInterval interval)
        {
            var n = DateTime.UtcNow;
            return interval switch
            {
                CacheRotationInterval.SixHours => n.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + (n.Hour / 6),
                CacheRotationInterval.Daily => n.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                CacheRotationInterval.Weekly => ISOWeek.GetYear(n).ToString(CultureInfo.InvariantCulture) + "-W" + ISOWeek.GetWeekOfYear(n).ToString(CultureInfo.InvariantCulture),
                CacheRotationInterval.Monthly => n.ToString("yyyyMM", CultureInfo.InvariantCulture),
                CacheRotationInterval.Never => "static",
                _ => n.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            };
        }

        private static void Shuffle<T>(IList<T> list)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                var h = 17;
                foreach (var c in s)
                {
                    h = (h * 31) + c;
                }

                return h;
            }
        }
    }
}
