using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrailerPreroll.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerPreroll.Services
{
    /// <summary>
    /// Downloads and muxes a single trailer to a target file using yt-dlp + ffmpeg.
    /// </summary>
    public class TrailerCacheService
    {
        private readonly IApplicationPaths _appPaths;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly PrerollHealth _health;
        private readonly ILogger<TrailerCacheService> _logger;

        private int _ytDlpMissingLogged;
        private int _pathsLogged;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrailerCacheService"/> class.
        /// </summary>
        /// <param name="appPaths">Application paths.</param>
        /// <param name="mediaEncoder">Media encoder (provides the ffmpeg path).</param>
        /// <param name="health">Download health tracker.</param>
        /// <param name="logger">Logger.</param>
        public TrailerCacheService(IApplicationPaths appPaths, IMediaEncoder mediaEncoder, PrerollHealth health, ILogger<TrailerCacheService> logger)
        {
            _appPaths = appPaths;
            _mediaEncoder = mediaEncoder;
            _health = health;
            _logger = logger;
        }

        private static PluginConfiguration Config => Plugin.Instance!.Config;

        /// <summary>
        /// Downloads a YouTube trailer to <paramref name="targetFilePath"/> (an .mp4 path), if it is
        /// not already present.
        /// </summary>
        /// <param name="key">The YouTube video id.</param>
        /// <param name="targetFilePath">The destination .mp4 path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><c>true</c> if the file exists afterwards.</returns>
        public async Task<bool> DownloadAsync(string key, string targetFilePath, CancellationToken cancellationToken)
        {
            if (File.Exists(targetFilePath))
            {
                return true;
            }

            var ytDlp = ResolveYtDlpPath();
            if (ytDlp is null)
            {
                if (Interlocked.Exchange(ref _ytDlpMissingLogged, 1) == 0)
                {
                    _logger.LogError(
                        "Trailer Preroll cannot find yt-dlp. Put yt-dlp(.exe) in '{DataPath}' or set its path in the plugin settings.",
                        _appPaths.DataPath);
                }

                return false;
            }

            var dir = Path.GetDirectoryName(targetFilePath)!;
            Directory.CreateDirectory(dir);

            var outputTemplate = Path.Combine(dir, "dl_" + key + ".%(ext)s");
            var producedTemp = Path.Combine(dir, "dl_" + key + ".mp4");
            var ffmpeg = ResolveFfmpeg();

            if (Interlocked.Exchange(ref _pathsLogged, 1) == 0)
            {
                _logger.LogInformation(
                    "Trailer Preroll using yt-dlp='{YtDlp}', ffmpeg-location='{Ffmpeg}'.",
                    ytDlp,
                    ffmpeg ?? "(PATH)");
            }

            var height = Config.MaxTrailerHeight <= 0 ? 720 : Config.MaxTrailerHeight;
            var h = height.ToString(CultureInfo.InvariantCulture);
            var format = $"bv*[height<={h}][vcodec^=avc1]+ba[acodec^=mp4a]/b[height<={h}][ext=mp4]/b[height<={h}]/b";
            var url = "https://www.youtube.com/watch?v=" + key;

            var psi = new ProcessStartInfo
            {
                FileName = ytDlp,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Make sure yt-dlp can find the deno JS runtime (needed for YouTube's signature challenge).
            // deno(.exe) is expected in the server data folder alongside yt-dlp.
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            psi.Environment["PATH"] = _appPaths.DataPath + Path.PathSeparator + existingPath;
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(format);
            psi.ArgumentList.Add("--merge-output-format");
            psi.ArgumentList.Add("mp4");
            psi.ArgumentList.Add("--remux-video");
            psi.ArgumentList.Add("mp4");
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--no-part");
            psi.ArgumentList.Add("--force-overwrites");
            psi.ArgumentList.Add("--sleep-requests");
            psi.ArgumentList.Add("1.5");

            // YouTube bot-check mitigations.
            var playerClient = Config.YtDlpPlayerClient;
            if (!string.IsNullOrWhiteSpace(playerClient))
            {
                psi.ArgumentList.Add("--extractor-args");
                psi.ArgumentList.Add("youtube:player_client=" + playerClient.Trim());
            }

            var cookies = Config.YtDlpCookiesPath;
            if (!string.IsNullOrWhiteSpace(cookies) && File.Exists(cookies))
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(cookies);
            }

            if (!string.IsNullOrEmpty(ffmpeg))
            {
                psi.ArgumentList.Add("--ffmpeg-location");
                psi.ArgumentList.Add(ffmpeg);
            }

            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputTemplate);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(url);

            try
            {
                using var process = new Process { StartInfo = psi };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(180));

                if (!process.Start())
                {
                    return false;
                }

                var stderrTask = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();

                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    _logger.LogWarning("Trailer Preroll download timed out for {Key}", key);
                    CleanupTemp(dir, key);
                    _health.RecordFailure(key, false, "timed out");
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    var err = await stderrTask.ConfigureAwait(false);
                    _logger.LogWarning("Trailer Preroll yt-dlp failed for {Key} (exit {Code}): {Error}", key, process.ExitCode, Trim(err));
                    CleanupTemp(dir, key);
                    _health.RecordFailure(key, IsAuthError(err), Trim(err));
                    return false;
                }

                if (!File.Exists(producedTemp))
                {
                    _logger.LogWarning("Trailer Preroll yt-dlp produced no output for {Key}", key);
                    CleanupTemp(dir, key);
                    _health.RecordFailure(key, false, "produced no output");
                    return false;
                }

                File.Move(producedTemp, targetFilePath, true);
                _logger.LogInformation("Trailer Preroll cached trailer {Key}", key);
                _health.RecordSuccess(key);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trailer Preroll download error for {Key}", key);
                CleanupTemp(dir, key);
                _health.RecordFailure(key, false, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Reports whether the external tools the plugin needs are present, for the settings page's
        /// status panel. yt-dlp/deno are looked for in the data folder (or the configured yt-dlp path);
        /// ffmpeg is resolved from Jellyfin's encoder or the server folder.
        /// </summary>
        /// <returns>Presence flags plus the resolved yt-dlp/ffmpeg paths (null if not found).</returns>
        public (bool YtDlp, bool Deno, bool Ffmpeg, string? YtDlpPath, string? FfmpegPath) GetToolStatus()
        {
            var isWindows = OperatingSystem.IsWindows();

            var configured = Config.YtDlpPath;
            string? ytPath = null;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                ytPath = configured;
            }
            else
            {
                var candidate = Path.Combine(_appPaths.DataPath, isWindows ? "yt-dlp.exe" : "yt-dlp");
                if (File.Exists(candidate))
                {
                    ytPath = candidate;
                }
            }

            var denoPath = Path.Combine(_appPaths.DataPath, isWindows ? "deno.exe" : "deno");
            var deno = File.Exists(denoPath);

            var ffmpeg = ResolveFfmpeg();

            return (ytPath is not null, deno, ffmpeg is not null, ytPath, ffmpeg);
        }

        private string? ResolveFfmpeg()
        {
            var enc = _mediaEncoder.EncoderPath;
            if (!string.IsNullOrEmpty(enc) && Path.IsPathRooted(enc) && File.Exists(enc))
            {
                return enc;
            }

            var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) })
            {
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private string? ResolveYtDlpPath()
        {
            var configured = Config.YtDlpPath;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }

            var isWindows = OperatingSystem.IsWindows();
            var candidate = Path.Combine(_appPaths.DataPath, isWindows ? "yt-dlp.exe" : "yt-dlp");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            return isWindows ? "yt-dlp.exe" : "yt-dlp";
        }

        private void CleanupTemp(string dir, string key)
        {
            try
            {
                foreach (var f in new DirectoryInfo(dir).GetFiles("dl_" + key + ".*"))
                {
                    try
                    {
                        f.Delete();
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Trailer Preroll temp cleanup failed for {Key}", key);
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Heuristically detects a YouTube bot/cookie block in yt-dlp's error output (the actionable
        /// "re-export cookies.txt" signal), as opposed to a per-video failure (private/removed/age).
        /// </summary>
        private static bool IsAuthError(string? err)
        {
            if (string.IsNullOrEmpty(err))
            {
                return false;
            }

            var e = err.ToLowerInvariant();
            return e.Contains("not a bot", StringComparison.Ordinal)
                || e.Contains("sign in to confirm", StringComparison.Ordinal)
                || e.Contains("cookies for the authentication", StringComparison.Ordinal)
                || e.Contains("cookies for the account", StringComparison.Ordinal);
        }

        private static string Trim(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            s = s.Trim();
            return s.Length > 300 ? s[^300..] : s;
        }
    }
}
