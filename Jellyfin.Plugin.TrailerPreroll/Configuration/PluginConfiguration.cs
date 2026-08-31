using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TrailerPreroll.Configuration
{
    /// <summary>
    /// Which library items are eligible to source trailers from.
    /// </summary>
    public enum LibraryFilter
    {
        /// <summary>Pull from both watched and unwatched items.</summary>
        WatchedAndUnwatched = 0,

        /// <summary>Pull only from unwatched items.</summary>
        UnwatchedOnly = 1
    }

    /// <summary>
    /// When to show trailers before TV episodes.
    /// </summary>
    public enum EpisodeMode
    {
        /// <summary>Only before the first episode watched in a session (avoids interrupting binge autoplay).</summary>
        FirstEpisodeOfSession = 0,

        /// <summary>Before every episode.</summary>
        EveryEpisode = 1
    }

    /// <summary>
    /// How often the cached set of trailers is rotated (re-picked).
    /// </summary>
    public enum CacheRotationInterval
    {
        /// <summary>Rotate every 6 hours.</summary>
        SixHours = 0,

        /// <summary>Rotate once a day.</summary>
        Daily = 1,

        /// <summary>Rotate once a week.</summary>
        Weekly = 2,

        /// <summary>Rotate once a month.</summary>
        Monthly = 3,

        /// <summary>Never rotate automatically.</summary>
        Never = 4
    }

    /// <summary>
    /// How far ahead to look for upcoming releases.
    /// </summary>
    public enum UpcomingWindow
    {
        /// <summary>Two weeks.</summary>
        TwoWeeks = 0,

        /// <summary>One month.</summary>
        OneMonth = 1,

        /// <summary>Two months.</summary>
        TwoMonths = 2,

        /// <summary>Three months.</summary>
        ThreeMonths = 3,

        /// <summary>No limit.</summary>
        NoLimit = 4
    }

    /// <summary>
    /// Plugin configuration for Trailer Preroll.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration"/> class with defaults.
        /// </summary>
        public PluginConfiguration()
        {
            EnableForMovies = true;
            EnableForTvShows = false;
            EpisodeMode = EpisodeMode.FirstEpisodeOfSession;

            LibraryTrailerCount = 3;
            LibraryFilter = LibraryFilter.WatchedAndUnwatched;
            LibrarySourceTmdb = true;
            LibrarySourceYoutube = true;

            UpcomingTrailerCount = 2;
            UpcomingWindow = UpcomingWindow.OneMonth;
            UpcomingSourceTmdb = true;
            UpcomingSourceYoutube = true;

            TmdbApiKey = string.Empty;

            EnableIntroVideo = false;
            IntroVideoUrl = string.Empty;

            AllowSkip = true;
            Randomize = true;

            SessionGapMinutes = 180;
            LibraryPoolSize = 60;
            UpcomingPoolSize = 12;
            KeepAllLibraryTrailers = false;
            DownloadsPerCycle = 10;

            CacheCapMb = 2048;
            MaxTrailerHeight = 720;
            YtDlpPath = string.Empty;
            RotationInterval = CacheRotationInterval.Daily;
            YtDlpPlayerClient = string.Empty;
            YtDlpCookiesPath = string.Empty;
            RotateAfterPlays = 5;

            AllowedTrailerLanguages = new List<string>();

            UserOverrides = new List<UserTrailerOverride>();
        }

        // ----- Media type settings -----

        /// <summary>Gets or sets a value indicating whether trailers play before movies.</summary>
        public bool EnableForMovies { get; set; }

        /// <summary>Gets or sets a value indicating whether trailers play before TV shows.</summary>
        public bool EnableForTvShows { get; set; }

        /// <summary>Gets or sets the episode preroll mode.</summary>
        public EpisodeMode EpisodeMode { get; set; }

        // ----- Library trailers -----

        /// <summary>Gets or sets how many trailers to pull from the user's own library (0-10).</summary>
        public int LibraryTrailerCount { get; set; }

        /// <summary>Gets or sets the library eligibility filter.</summary>
        public LibraryFilter LibraryFilter { get; set; }

        /// <summary>Gets or sets a value indicating whether TMDB is an allowed source for library trailers.</summary>
        public bool LibrarySourceTmdb { get; set; }

        /// <summary>Gets or sets a value indicating whether YouTube is an allowed source for library trailers.</summary>
        public bool LibrarySourceYoutube { get; set; }

        // ----- Upcoming trailers -----

        /// <summary>Gets or sets how many upcoming trailers to include (0-10).</summary>
        public int UpcomingTrailerCount { get; set; }

        /// <summary>Gets or sets how far ahead to look for upcoming releases.</summary>
        public UpcomingWindow UpcomingWindow { get; set; }

        /// <summary>Gets or sets a value indicating whether TMDB is an allowed source for upcoming trailers.</summary>
        public bool UpcomingSourceTmdb { get; set; }

        /// <summary>Gets or sets a value indicating whether YouTube is an allowed source for upcoming trailers.</summary>
        public bool UpcomingSourceYoutube { get; set; }

        // ----- TMDB -----

        /// <summary>Gets or sets the optional TMDB API key. If blank, TMDB-sourced trailers are skipped.</summary>
        public string TmdbApiKey { get; set; }

        // ----- Intro bumper -----

        /// <summary>Gets or sets a value indicating whether to play a custom intro clip before the upcoming block.</summary>
        public bool EnableIntroVideo { get; set; }

        /// <summary>Gets or sets the YouTube URL of the intro bumper video.</summary>
        public string IntroVideoUrl { get; set; }

        // ----- Playback -----

        /// <summary>Gets or sets a value indicating whether trailers can be skipped during playback.</summary>
        public bool AllowSkip { get; set; }

        /// <summary>Gets or sets a value indicating whether trailer selection is randomized on each play.</summary>
        public bool Randomize { get; set; }

        // ----- Advanced -----

        /// <summary>
        /// Gets or sets the number of minutes of inactivity that ends a "session" for the
        /// first-episode-only TV mode.
        /// </summary>
        public int SessionGapMinutes { get; set; }

        /// <summary>
        /// Gets or sets how many library trailers are kept in the rotating pool/cache.
        /// </summary>
        public int LibraryPoolSize { get; set; }

        /// <summary>
        /// Gets or sets how many upcoming trailers are kept in the pool/cache.
        /// </summary>
        public int UpcomingPoolSize { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to keep a trailer for every library film
        /// (downloaded gradually over time and never deleted), instead of a bounded rotating pool.
        /// This can use a lot of disk on large libraries.
        /// </summary>
        public bool KeepAllLibraryTrailers { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of trailers to download per background cycle, so the pool
        /// fills gradually instead of all at once. 0 = no limit (download everything each cycle).
        /// </summary>
        public int DownloadsPerCycle { get; set; }

        /// <summary>
        /// Gets or sets the maximum size (in MB) of the local trailer cache. Oldest trailers are
        /// evicted once the cache exceeds this. Trailers are streamed from this small rotating cache,
        /// not bulk-downloaded.
        /// </summary>
        public int CacheCapMb { get; set; }

        /// <summary>
        /// Gets or sets the maximum trailer height to cache (e.g. 720). Lower = smaller files.
        /// </summary>
        public int MaxTrailerHeight { get; set; }

        /// <summary>
        /// Gets or sets an optional explicit path to the yt-dlp executable. If blank, the plugin
        /// looks for yt-dlp(.exe) in the server data folder, then on the system PATH.
        /// </summary>
        public string YtDlpPath { get; set; }

        /// <summary>
        /// Gets or sets how often the cached set of trailers is rotated (re-picked).
        /// </summary>
        public CacheRotationInterval RotationInterval { get; set; }

        /// <summary>
        /// Gets or sets how many times a single trailer is played before it is retired and replaced
        /// with a fresh one (rolling rotation, one at a time). 0 disables play-based rotation.
        /// </summary>
        public int RotateAfterPlays { get; set; }

        /// <summary>
        /// Gets or sets the yt-dlp YouTube player client(s) used for extraction (e.g. "android").
        /// Helps get past YouTube's bot checks. Blank uses yt-dlp's default.
        /// </summary>
        public string YtDlpPlayerClient { get; set; }

        /// <summary>
        /// Gets or sets an optional path to a YouTube cookies.txt file (exported from a logged-in
        /// browser). Greatly improves download reliability against YouTube bot detection.
        /// </summary>
        public string YtDlpCookiesPath { get; set; }

        // ----- Languages -----

        /// <summary>
        /// Gets or sets the ISO 639-1 language codes (e.g. "en", "fr") whose films are eligible for
        /// trailers. A library film is matched on its audio track language; an upcoming film on its
        /// TMDB original language. An empty list means no language filtering (every language is
        /// allowed). Films whose language can't be determined are always included.
        /// </summary>
        public List<string> AllowedTrailerLanguages { get; set; }

        // ----- Per-user overrides -----

        /// <summary>
        /// Gets or sets the per-user selection overrides. Users not listed (or whose override is
        /// disabled) inherit the global settings above.
        /// </summary>
        public List<UserTrailerOverride> UserOverrides { get; set; }

        /// <summary>
        /// Resolves the effective trailer selection settings for a user, applying that user's
        /// active override if one exists, otherwise falling back to the global settings.
        /// </summary>
        /// <param name="userId">The user's id.</param>
        /// <returns>The resolved settings.</returns>
        public EffectiveSettings ResolveForUser(Guid userId)
        {
            var over = FindOverride(userId);
            if (over is not null && over.Enabled)
            {
                return new EffectiveSettings
                {
                    EnableForMovies = over.EnableForMovies,
                    EnableForTvShows = over.EnableForTvShows,
                    EpisodeMode = over.EpisodeMode,
                    LibraryTrailerCount = over.LibraryTrailerCount,
                    LibraryFilter = over.LibraryFilter,
                    UpcomingTrailerCount = over.UpcomingTrailerCount
                };
            }

            return new EffectiveSettings
            {
                EnableForMovies = EnableForMovies,
                EnableForTvShows = EnableForTvShows,
                EpisodeMode = EpisodeMode,
                LibraryTrailerCount = LibraryTrailerCount,
                LibraryFilter = LibraryFilter,
                UpcomingTrailerCount = UpcomingTrailerCount
            };
        }

        private UserTrailerOverride? FindOverride(Guid userId)
        {
            var overrides = UserOverrides;
            if (overrides is null || overrides.Count == 0)
            {
                return null;
            }

            foreach (var over in overrides)
            {
                if (over is not null
                    && Guid.TryParse(over.UserId, out var id)
                    && id.Equals(userId))
                {
                    return over;
                }
            }

            return null;
        }
    }
}
