namespace Jellyfin.Plugin.TrailerPreroll.Configuration
{
    /// <summary>
    /// Per-user override of the trailer selection settings. When <see cref="Enabled"/> is
    /// <c>true</c>, the values here replace the global settings for the matching user; otherwise
    /// the user inherits the global configuration.
    /// </summary>
    public class UserTrailerOverride
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UserTrailerOverride"/> class with defaults.
        /// </summary>
        public UserTrailerOverride()
        {
            UserId = string.Empty;
            Enabled = true;
            EnableForMovies = true;
            EnableForTvShows = false;
            EpisodeMode = EpisodeMode.FirstEpisodeOfSession;
            LibraryTrailerCount = 3;
            LibraryFilter = LibraryFilter.WatchedAndUnwatched;
            UpcomingTrailerCount = 2;
        }

        /// <summary>Gets or sets the target user's id (Guid, "N"/"D" string form).</summary>
        public string UserId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this override is active. When <c>false</c>,
        /// the user falls back to the global settings.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>Gets or sets a value indicating whether trailers play before movies for this user.</summary>
        public bool EnableForMovies { get; set; }

        /// <summary>Gets or sets a value indicating whether trailers play before TV shows for this user.</summary>
        public bool EnableForTvShows { get; set; }

        /// <summary>Gets or sets the episode preroll mode for this user.</summary>
        public EpisodeMode EpisodeMode { get; set; }

        /// <summary>Gets or sets how many library trailers this user gets (0-10).</summary>
        public int LibraryTrailerCount { get; set; }

        /// <summary>Gets or sets the library eligibility filter for this user.</summary>
        public LibraryFilter LibraryFilter { get; set; }

        /// <summary>Gets or sets how many upcoming trailers this user gets (0-10).</summary>
        public int UpcomingTrailerCount { get; set; }
    }
}
