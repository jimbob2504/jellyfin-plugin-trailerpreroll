using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrailerPreroll.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerPreroll.ScheduledTasks
{
    /// <summary>
    /// A Jellyfin scheduled task (Dashboard &gt; Scheduled Tasks) that caches/rotates the trailer pool.
    /// Unlike the throttled background tick, a scheduled run loops the rotation until the pool is fully
    /// filled, so it is the right place to do the heavy downloading at a quiet time of day.
    /// </summary>
    public class RefreshTrailersTask : IScheduledTask
    {
        // A scheduled run keeps filling until there is nothing left to fetch. Cap the passes so a
        // persistent download failure can never spin forever.
        private const int MaxFillPasses = 60;

        private readonly TrailerLibraryService _libraries;
        private readonly TrailerCatalogService _catalog;
        private readonly ILogger<RefreshTrailersTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RefreshTrailersTask"/> class.
        /// </summary>
        /// <param name="libraries">Library/folder service.</param>
        /// <param name="catalog">Rotation service.</param>
        /// <param name="logger">Logger.</param>
        public RefreshTrailersTask(TrailerLibraryService libraries, TrailerCatalogService catalog, ILogger<RefreshTrailersTask> logger)
        {
            _libraries = libraries;
            _catalog = catalog;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Cache trailer prerolls";

        /// <inheritdoc />
        public string Key => "TrailerPrerollRefresh";

        /// <inheritdoc />
        public string Description => "Downloads and rotates the cached trailer pre-shows (library and upcoming) so they are ready to play before movies and TV.";

        /// <inheritdoc />
        public string Category => "Trailer Preroll";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Default: once a day at 03:30. Admins can change/add triggers in the dashboard.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks
            };
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Trailer Preroll scheduled cache task started.");

            await _libraries.EnsureLibrariesAsync().ConfigureAwait(false);
            progress.Report(5);

            // Fill the pool completely: RotateIfNeededAsync fetches a bounded batch per call and reports
            // whether more downloads are still pending, so loop until it is satisfied (or we are cancelled).
            for (var pass = 0; pass < MaxFillPasses; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var morePending = await _catalog.RotateIfNeededAsync(force: pass == 0, cancellationToken).ConfigureAwait(false);

                // Ramp progress toward 80% across the fill passes without ever reaching it early.
                progress.Report(Math.Min(80d, 5d + (pass + 1) * (75d / MaxFillPasses)));

                if (!morePending)
                {
                    break;
                }
            }

            progress.Report(80);
            await _catalog.RollReplaceAsync(maxPerRun: 3, cancellationToken).ConfigureAwait(false);

            progress.Report(88);
            _catalog.RemoveDuplicateTrailers(cancellationToken);

            progress.Report(92);
            await _catalog.CleanupItemNamesAsync(cancellationToken).ConfigureAwait(false);

            progress.Report(96);
            await _catalog.EnsureUpcomingPostersAsync(cancellationToken).ConfigureAwait(false);

            progress.Report(100);
            _logger.LogInformation("Trailer Preroll scheduled cache task finished.");
        }
    }
}
