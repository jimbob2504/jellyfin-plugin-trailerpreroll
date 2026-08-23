using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TrailerPreroll.Services
{
    /// <summary>
    /// Extracts YouTube video ids from the trailer URLs stored in library metadata,
    /// without any external dependency.
    /// </summary>
    public static class YoutubeId
    {
        private static readonly Regex UrlRegex = new(
            @"(?:v=|youtu\.be/|/embed/|/shorts/|videoid=|video_id=)([A-Za-z0-9_-]{11})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BareId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

        /// <summary>
        /// Attempts to extract a YouTube video id from a url (or a bare id).
        /// </summary>
        /// <param name="url">The candidate url or id.</param>
        /// <returns>The 11-character video id, or null.</returns>
        public static string? TryExtract(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var match = UrlRegex.Match(url);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            var trimmed = url.Trim();
            return BareId.IsMatch(trimmed) ? trimmed : null;
        }
    }
}
