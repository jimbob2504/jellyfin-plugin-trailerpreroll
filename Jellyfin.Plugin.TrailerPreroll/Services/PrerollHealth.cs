using System;
using System.Threading;

namespace Jellyfin.Plugin.TrailerPreroll.Services
{
    /// <summary>
    /// Tracks download success/failure statistics so the settings page can surface the health of the
    /// trailer pipeline (in particular, cookie/bot-block failures that mean cookies.txt has gone stale).
    /// </summary>
    public class PrerollHealth
    {
        private readonly object _lock = new();
        private long _attempts;
        private long _succeeded;
        private long _failed;
        private long _authFailed;

        /// <summary>Gets the total download attempts since server start.</summary>
        public long Attempts => Interlocked.Read(ref _attempts);

        /// <summary>Gets the total successful downloads since server start.</summary>
        public long Succeeded => Interlocked.Read(ref _succeeded);

        /// <summary>Gets the total failed downloads since server start.</summary>
        public long Failed => Interlocked.Read(ref _failed);

        /// <summary>Gets the total failures attributed to YouTube bot/cookie checks since server start.</summary>
        public long AuthFailed => Interlocked.Read(ref _authFailed);

        /// <summary>Gets the time of the last successful download (UTC).</summary>
        public DateTime? LastSuccessUtc { get; private set; }

        /// <summary>Gets the time of the last failed download (UTC).</summary>
        public DateTime? LastFailureUtc { get; private set; }

        /// <summary>Gets the time of the last bot/cookie-related failure (UTC).</summary>
        public DateTime? LastAuthFailureUtc { get; private set; }

        /// <summary>Gets the trimmed error text from the last failure.</summary>
        public string? LastError { get; private set; }

        /// <summary>Gets the time the last rotation cycle finished (UTC).</summary>
        public DateTime? LastCycleUtc { get; private set; }

        /// <summary>Gets the download attempts made during the last rotation cycle.</summary>
        public int CycleAttempts { get; private set; }

        /// <summary>Gets the successful downloads during the last rotation cycle.</summary>
        public int CycleSucceeded { get; private set; }

        /// <summary>Gets the failed downloads during the last rotation cycle.</summary>
        public int CycleFailed { get; private set; }

        /// <summary>Gets the bot/cookie failures during the last rotation cycle.</summary>
        public int CycleAuthFailed { get; private set; }

        /// <summary>Gets the desired library pool size at the last rotation.</summary>
        public int LibraryPoolSize { get; private set; }

        /// <summary>Gets the desired upcoming pool size at the last rotation.</summary>
        public int UpcomingPoolSize { get; private set; }

        /// <summary>Records a successful download.</summary>
        /// <param name="key">The YouTube key.</param>
        public void RecordSuccess(string key)
        {
            Interlocked.Increment(ref _attempts);
            Interlocked.Increment(ref _succeeded);
            lock (_lock)
            {
                LastSuccessUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Records a failed download.</summary>
        /// <param name="key">The YouTube key.</param>
        /// <param name="authRelated">Whether the failure looks like a YouTube bot/cookie block.</param>
        /// <param name="error">Trimmed error text.</param>
        public void RecordFailure(string key, bool authRelated, string? error)
        {
            Interlocked.Increment(ref _attempts);
            Interlocked.Increment(ref _failed);
            if (authRelated)
            {
                Interlocked.Increment(ref _authFailed);
            }

            lock (_lock)
            {
                LastFailureUtc = DateTime.UtcNow;
                LastError = error;
                if (authRelated)
                {
                    LastAuthFailureUtc = DateTime.UtcNow;
                }
            }
        }

        /// <summary>Takes a snapshot of the running totals, used to compute per-cycle deltas.</summary>
        /// <returns>A tuple of (attempts, succeeded, failed, authFailed).</returns>
        public (long A, long S, long F, long Au) Snapshot() => (Attempts, Succeeded, Failed, AuthFailed);

        /// <summary>
        /// Records the results of a rotation cycle using the delta since <paramref name="start"/>.
        /// </summary>
        /// <param name="start">The snapshot taken before the cycle.</param>
        /// <param name="libraryPool">The library pool size.</param>
        /// <param name="upcomingPool">The upcoming pool size.</param>
        public void CompleteCycle((long A, long S, long F, long Au) start, int libraryPool, int upcomingPool)
        {
            lock (_lock)
            {
                LastCycleUtc = DateTime.UtcNow;
                CycleAttempts = (int)(Attempts - start.A);
                CycleSucceeded = (int)(Succeeded - start.S);
                CycleFailed = (int)(Failed - start.F);
                CycleAuthFailed = (int)(AuthFailed - start.Au);
                LibraryPoolSize = libraryPool;
                UpcomingPoolSize = upcomingPool;
            }
        }
    }
}
