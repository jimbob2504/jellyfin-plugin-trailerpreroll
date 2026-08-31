using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TrailerPreroll.Configuration
{
    /// <summary>
    /// The set of languages the plugin can filter trailers by, plus a normaliser that maps the many
    /// forms a language can appear in (ISO 639-1 two-letter, ISO 639-2/T and 639-2/B three-letter,
    /// TMDB quirks, and English names) onto a single canonical ISO 639-1 code for comparison.
    /// </summary>
    public static class TrailerLanguages
    {
        /// <summary>
        /// The languages offered in the settings UI, as (ISO 639-1 code, display name). Kept in sync
        /// with the checkbox list on the configuration page.
        /// </summary>
        public static readonly IReadOnlyList<(string Code, string Name)> Offered = new[]
        {
            ("en", "English"),
            ("fr", "French"),
            ("es", "Spanish"),
            ("de", "German"),
            ("it", "Italian"),
            ("pt", "Portuguese"),
            ("nl", "Dutch"),
            ("sv", "Swedish"),
            ("da", "Danish"),
            ("no", "Norwegian"),
            ("fi", "Finnish"),
            ("pl", "Polish"),
            ("ru", "Russian"),
            ("tr", "Turkish"),
            ("ja", "Japanese"),
            ("ko", "Korean"),
            ("zh", "Chinese"),
            ("hi", "Hindi"),
            ("ar", "Arabic"),
            ("th", "Thai")
        };

        // Every accepted spelling/alias -> canonical ISO 639-1 code. Covers 639-2/T, 639-2/B, TMDB's
        // "cn" for Chinese, and lowercase English names.
        private static readonly Dictionary<string, string> Aliases = BuildAliases();

        /// <summary>
        /// Normalises a raw language string (from an audio track's Language, or a TMDB original
        /// language) to a canonical ISO 639-1 code, or <c>null</c> if it is empty/unknown/undefined.
        /// </summary>
        /// <param name="raw">The raw language value.</param>
        /// <returns>The ISO 639-1 code, or <c>null</c>.</returns>
        public static string? ToIso1(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var key = raw.Trim().ToLowerInvariant();
            if (key is "und" or "unknown" or "mul" or "zxx" or "")
            {
                return null;
            }

            return Aliases.TryGetValue(key, out var code) ? code : null;
        }

        private static Dictionary<string, string> BuildAliases()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            void Add(string iso1, params string[] forms)
            {
                map[iso1] = iso1;
                foreach (var f in forms)
                {
                    map[f] = iso1;
                }
            }

            Add("en", "eng", "english");
            Add("fr", "fra", "fre", "french");
            Add("es", "spa", "spanish", "castilian");
            Add("de", "deu", "ger", "german");
            Add("it", "ita", "italian");
            Add("pt", "por", "portuguese");
            Add("nl", "nld", "dut", "dutch", "flemish");
            Add("sv", "swe", "swedish");
            Add("da", "dan", "danish");
            Add("no", "nor", "norwegian", "nob", "nno");
            Add("fi", "fin", "finnish");
            Add("pl", "pol", "polish");
            Add("ru", "rus", "russian");
            Add("tr", "tur", "turkish");
            Add("ja", "jpn", "japanese");
            Add("ko", "kor", "korean");
            Add("zh", "zho", "chi", "chinese", "mandarin", "cmn", "cn", "yue", "cantonese");
            Add("hi", "hin", "hindi");
            Add("ar", "ara", "arabic");
            Add("th", "tha", "thai");

            return map;
        }
    }
}
