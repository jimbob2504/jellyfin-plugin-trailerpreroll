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
    /// Tracks how many times each cached trailer (by YouTube key) has been played, persisted to disk,
    /// so the rolling rotation can retire a trailer after it has been shown enough times.
    /// </summary>
    public class PrerollPlayTracker
    {
        private readonly ILogger<PrerollPlayTracker> _logger;
        private readonly string _file;
        private readonly ConcurrentDictionary<string, int> _counts;
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
        /// Records one play for each key and returns nothing; counts are persisted.
        /// </summary>
        /// <param name="keys">The YouTube keys that were served.</param>
        public void RecordPlays(IEnumerable<string> keys)
        {
            var changed = false;
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                _counts.AddOrUpdate(key, 1, (_, c) => c + 1);
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

            return _counts.Where(kv => kv.Value >= threshold).Select(kv => kv.Key).ToList();
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

        private ConcurrentDictionary<string, int> Load()
        {
            try
            {
                if (File.Exists(_file))
                {
                    var json = File.ReadAllText(_file);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (dict is not null)
                    {
                        return new ConcurrentDictionary<string, int>(dict, StringComparer.Ordinal);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Trailer Preroll could not load play counts; starting fresh.");
            }

            return new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        }

        private void Save()
        {
            lock (_saveLock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                    var json = JsonSerializer.Serialize(new Dictionary<string, int>(_counts));
                    File.WriteAllText(_file, json);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Trailer Preroll could not save play counts.");
                }
            }
        }
    }
}
