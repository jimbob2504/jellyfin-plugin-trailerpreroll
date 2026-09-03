using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerPreroll.Services
{
    /// <summary>
    /// Tracks how many times each cached trailer (by YouTube key) has been played, along with the
    /// trailer's display title so the settings page can name it even after the file has been rotated
    /// off disk. Persisted to disk so the rolling rotation can retire a trailer after enough plays.
    /// </summary>
    public class PrerollPlayTracker
    {
        private readonly ILogger<PrerollPlayTracker> _logger;
        private readonly string _file;
        private readonly ConcurrentDictionary<string, Entry> _counts;
        private readonly object _saveLock = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="PrerollPlayTracker"/> class.
        /// </summary>
        /// <param name="appPaths">Application paths.</param>
        /// <param name="logger">Logger.</param>
        public PrerollPlayTracker(IApplicationPaths appPaths, ILogger<PrerollPlayTracker> logger)
        {
            _logger = logger;
            _file = Path.Combine(appPaths.DataPath, "trailer-preroll", "playcounts.json");
            _counts = Load();
        }

        /// <summary>
        /// Records one play for each key (remembering the title for display), and persists the counts.
        /// </summary>
        /// <param name="plays">The (YouTube key, display title) pairs that were served.</param>
        public void RecordPlays(IEnumerable<KeyValuePair<string, string?>> plays)
        {
            var changed = false;
            foreach (var play in plays)
            {
                var key = play.Key;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                _counts.AddOrUpdate(
                    key,
                    _ => new Entry { Count = 1, Title = play.Value },
                    (_, e) =>
                    {
                        e.Count++;
                        if (!string.IsNullOrWhiteSpace(play.Value))
                        {
                            e.Title = play.Value;
                        }

                        return e;
                    });
                changed = true;
            }

            if (changed)
            {
                Save();
            }
        }

        /// <summary>
        /// Gets the keys that have been played at least <paramref name="threshold"/> times.
        /// </summary>
        /// <param name="threshold">The play threshold.</param>
        /// <returns>The keys due for replacement.</returns>
        public IReadOnlyList<string> KeysAtOrOver(int threshold)
        {
            if (threshold <= 0)
            {
                return Array.Empty<string>();
            }

            return _counts.Where(kv => kv.Value.Count >= threshold).Select(kv => kv.Key).ToList();
        }

        /// <summary>
        /// Gets a snapshot of every tracked key with its play count and last-known title.
        /// </summary>
        /// <returns>A copy of the key-to-(count, title) map.</returns>
        public IReadOnlyDictionary<string, (int Count, string? Title)> GetAll()
        {
            return _counts.ToDictionary(kv => kv.Key, kv => (kv.Value.Count, kv.Value.Title), StringComparer.Ordinal);
        }

        /// <summary>
        /// Forgets a key (e.g. after it has been retired/replaced).
        /// </summary>
        /// <param name="key">The YouTube key.</param>
        public void Forget(string key)
        {
            if (_counts.TryRemove(key, out _))
            {
                Save();
            }
        }

        private ConcurrentDictionary<string, Entry> Load()
        {
            var result = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(_file))
                {
                    return result;
                }

                var json = File.ReadAllText(_file);
                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (raw is null)
                {
                    return result;
                }

                foreach (var kv in raw)
                {
                    // Legacy format was { "key": <count> }; new format is { "key": { count, title } }.
                    if (kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetInt32(out var c))
                    {
                        result[kv.Key] = new Entry { Count = c, Title = null };
                    }
                    else if (kv.Value.ValueKind == JsonValueKind.Object)
                    {
                        result[kv.Key] = new Entry
                        {
                            Count = GetInt(kv.Value, "count", "Count"),
                            Title = GetString(kv.Value, "title", "Title")
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Trailer Preroll could not load play counts; starting fresh.");
            }

            return result;
        }

        private static int GetInt(JsonElement obj, params string[] names)
        {
            foreach (var n in names)
            {
                if (obj.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
                {
                    return v;
                }
            }

            return 0;
        }

        private static string? GetString(JsonElement obj, params string[] names)
        {
            foreach (var n in names)
            {
                if (obj.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    return el.GetString();
                }
            }

            return null;
        }

        private void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                    var snapshot = _counts.ToDictionary(kv => kv.Key, kv => kv.Value);
                    var json = JsonSerializer.Serialize(snapshot);
                    File.WriteAllText(_file, json);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Trailer Preroll could not save play counts.");
                }
            }
        }

        private sealed class Entry
        {
            public int Count { get; set; }

            public string? Title { get; set; }
        }
    }
}
