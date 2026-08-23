using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TrailerPreroll.Model
{
    /// <summary>
    /// The category a preroll entry belongs to.
    /// </summary>
    public enum PrerollCategory
    {
        /// <summary>A trailer for a movie already in the user's library.</summary>
        Library = 0,

        /// <summary>A trailer for an upcoming release not in the library.</summary>
        Upcoming = 1,

        /// <summary>The custom intro bumper clip.</summary>
        Bumper = 2
    }

    /// <summary>
    /// A single entry in the preroll catalog. Maps to a YouTube video cached as a local file.
    /// </summary>
    public class PrerollItem
    {
        private static readonly Regex KeyInName = new(@"\[([A-Za-z0-9_-]{11})\]", RegexOptions.Compiled);

        /// <summary>
        /// Initializes a new instance of the <see cref="PrerollItem"/> class.
        /// </summary>
        /// <param name="youtubeKey">The YouTube video id.</param>
        /// <param name="title">Display title.</param>
        /// <param name="category">The category.</param>
        public PrerollItem(string youtubeKey, string title, PrerollCategory category)
        {
            YoutubeKey = youtubeKey;
            Title = title;
            Category = category;
        }

        /// <summary>Gets the YouTube video id.</summary>
        public string YoutubeKey { get; }

        /// <summary>Gets the display title.</summary>
        public string Title { get; }

        /// <summary>Gets the category.</summary>
        public PrerollCategory Category { get; }

        /// <summary>Gets or sets an optional poster/thumbnail image url.</summary>
        public string? ImageUrl { get; set; }

        /// <summary>Gets or sets the production year, if known.</summary>
        public int? Year { get; set; }

        /// <summary>Gets the cache file name, e.g. "Dune [abcDEF12345].mp4".</summary>
        public string FileName => Sanitize(Title) + " [" + YoutubeKey + "].mp4";

        /// <summary>
        /// Extracts the embedded YouTube key from a cached file name/path (the "[key]" segment).
        /// </summary>
        /// <param name="path">The file path or name.</param>
        /// <returns>The key, or null.</returns>
        public static string? KeyFromFileName(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var m = KeyInName.Match(Path.GetFileName(path));
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string Sanitize(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "Trailer";
            }

            var cleaned = new string(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '[' && c != ']').ToArray()).Trim();
            if (cleaned.Length > 80)
            {
                cleaned = cleaned[..80].Trim();
            }

            return string.IsNullOrEmpty(cleaned) ? "Trailer" : cleaned;
        }
    }
}
