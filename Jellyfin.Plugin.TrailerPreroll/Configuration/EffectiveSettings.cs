namespace Jellyfin.Plugin.TrailerPreroll.Configuration
{
    /// <summary>
    /// The resolved per-user trailer selection settings actually used when building intros:
    /// either a user's active override, or the global settings when no override applies.
    /// </summary>
    public class EffectiveSettings
    {
        /// <summary>Gets or sets a value indicating whether trailers play before movies.</summary>
        public bool EnableForMovies { get; set; }

        /// <summary>Gets or sets a value indicating whether trailers play before TV shows.</summary>
        public bool EnableForTvShows { get; set; }

        /// <summary>Gets or sets the episode preroll mode.</summary>
        public EpisodeMode EpisodeMode { get; set; }

        /// <summary>Gets or sets how many library trailers to include (0-10).</summary>
        public int LibraryTrailerCount { get; set; }

        /// <summary>Gets or sets the library eligibility filter.</summary>
        public LibraryFilter LibraryFilter { get; set; }

        /// <summary>Gets or sets how many upcoming trailers to include (0-10).</summary>
        public int UpcomingTrailerCount { get; set; }
    }
}
