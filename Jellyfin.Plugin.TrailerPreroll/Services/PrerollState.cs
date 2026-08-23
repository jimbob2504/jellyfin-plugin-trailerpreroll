using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.TrailerPreroll.Services
{
    /// <summary>
    /// Tracks lightweight, in-memory per-user session state used to decide whether a TV episode
    /// is the "first of a session" (so trailers do not interrupt binge autoplay).
    /// </summary>
    public class PrerollState
    {
        private readonly ConcurrentDictionary<Guid, DateTime> _lastEpisodeIntroUtc = new();

        /// <summary>
        /// Decides whether trailers should play before an episode for this user, using a sliding
        /// inactivity window. Continuous autoplay keeps sliding the window and is treated as one
        /// session; a gap longer than <paramref name="gapMinutes"/> starts a new session.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="gapMinutes">The inactivity gap, in minutes, that starts a new session.</param>
        /// <returns><c>true</c> to play trailers before this episode.</returns>
        public bool ShouldServeEpisode(Guid userId, int gapMinutes)
        {
            var now = DateTime.UtcNow;
            var gap = TimeSpan.FromMinutes(gapMinutes <= 0 ? 180 : gapMinutes);

            var serve = true;
            _lastEpisodeIntroUtc.AddOrUpdate(
                userId,
                _ =>
                {
                    serve = true;
                    return now;
                },
                (_, previous) =>
                {
                    serve = now - previous > gap;
                    return now;
                });

            return serve;
        }
    }
}
