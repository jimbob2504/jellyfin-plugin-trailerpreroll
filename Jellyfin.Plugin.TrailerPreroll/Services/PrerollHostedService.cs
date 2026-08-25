using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerPreroll.Services
{
    /// <summary>
    /// Ensures the two trailer libraries exist and rotates the cached trailer set on a schedule,
    /// on startup, and whenever the configuration changes.
    /// </summary>
    public class PrerollHostedService : IHostedService, IDisposable
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

        // When a download budget was hit (more trailers still to fetch), tick again soon so the pool
        // fills gradually rather than waiting a full hour.
        private static readonly TimeSpan FillInterval = TimeSpan.FromMinutes(2);

        private readonly TrailerLibraryService _libraries;
        private readonly TrailerCatalogService _catalog;
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<PrerollHostedService> _logger;

        private Timer? _timer;
        private int _running;
        private int _legacyConversionDone;

        /// <summary>
        /// Initializes a new instance of the <see cref="PrerollHostedService"/> class.
        /// </summary>
        /// <param name="libraries">Library/folder service.</param>
        /// <param name="catalog">Rotation service.</param>
        /// <param name="appPaths">Application paths (for the web client root).</param>
        /// <param name="logger">Logger.</param>
        public PrerollHostedService(TrailerLibraryService libraries, TrailerCatalogService catalog, IApplicationPaths appPaths, ILogger<PrerollHostedService> logger)
        {
            _libraries = libraries;
            _catalog = catalog;
            _appPaths = appPaths;
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            EnsureClientScriptInjected();

            // One-shot timer that re-arms itself after each tick (short interval while still filling).
            _timer = new Timer(_ => _ = TickAsync(false), null, StartupDelay, Timeout.InfiniteTimeSpan);
            if (Plugin.Instance is not null)
            {
                Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Adds the plugin's client-script tag to the web client's index.html (idempotent). This is
        /// how the "Want to watch" button reaches the web player. Re-runs each startup so it survives
        /// web-client updates that replace index.html.
        /// </summary>
        private void EnsureClientScriptInjected()
        {
            try
            {
                var webPath = _appPaths.WebPath;
                if (string.IsNullOrEmpty(webPath))
                {
                    return;
                }

                var indexPath = Path.Combine(webPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    _logger.LogWarning("Trailer Preroll could not find web index.html at {Path}; the Want-to-watch button will not load.", indexPath);
                    return;
                }

                var html = File.ReadAllText(indexPath);
                const string marker = "src=\"/TrailerPreroll/ClientScript\"";
                if (html.Contains(marker, StringComparison.Ordinal))
                {
                    return; // already injected
                }

                var closeBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (closeBody < 0)
                {
                    _logger.LogWarning("Trailer Preroll could not inject client script (no </body> in index.html).");
                    return;
                }

                const string tag = "<script plugin=\"Trailer Preroll\" version=\"1.0.0.0\" src=\"/TrailerPreroll/ClientScript\" defer></script>";
                html = html.Insert(closeBody, tag);
                File.WriteAllText(indexPath, html);
                _logger.LogInformation("Trailer Preroll injected the Want-to-watch client script into {Path}.", indexPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trailer Preroll could not inject the client script into index.html.");
            }
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (Plugin.Instance is not null)
            {
                Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
            }

            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
        {
            _ = TickAsync(true);
        }

        private async Task TickAsync(bool force)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                return;
            }

            var morePending = false;
            try
            {
                await _libraries.EnsureLibrariesAsync().ConfigureAwait(false);

                // One-time per session: re-type any pre-existing plain-Video trailer items as Trailer.
                // Guarded so it runs at most once per restart (prevents churn if resolution fails).
                if (Interlocked.Exchange(ref _legacyConversionDone, 1) == 0)
                {
                    _catalog.ConvertLegacyVideos(CancellationToken.None);
                }

                morePending = await _catalog.RotateIfNeededAsync(force, CancellationToken.None).ConfigureAwait(false);
                await _catalog.RollReplaceAsync(maxPerRun: 3, CancellationToken.None).ConfigureAwait(false);
                _catalog.RemoveDuplicateTrailers(CancellationToken.None);
                await _catalog.CleanupItemNamesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trailer Preroll refresh failed");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                // Re-arm: soon if still filling the pool, otherwise the normal hourly check.
                try
                {
                    _timer?.Change(morePending ? FillInterval : CheckInterval, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Plugin.Instance is not null)
            {
                Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
            }

            _timer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
