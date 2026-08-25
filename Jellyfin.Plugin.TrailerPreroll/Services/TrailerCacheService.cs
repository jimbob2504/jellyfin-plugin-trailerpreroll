using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
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
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TrailerCacheService> _logger;

        private int _ytDlpMissingLogged;
        private int _pathsLogged;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrailerCacheService"/> class.
        /// </summary>
        /// <param name="appPaths">Application paths.</param>
        /// <param name="mediaEncoder">Media encoder (provides the ffmpeg path).</param>
        /// <param name="health">Download health tracker.</param>
        /// <param name="httpClientFactory">HTTP client factory (for downloading yt-dlp/deno).</param>
        /// <param name="logger">Logger.</param>
        public TrailerCacheService(IApplicationPaths appPaths, IMediaEncoder mediaEncoder, PrerollHealth health, IHttpClientFactory httpClientFactory, ILogger<TrailerCacheService> logger)
        {
            _appPaths = appPaths;
            _mediaEncoder = mediaEncoder;
            _health = health;
            _httpClientFactory = httpClientFactory;
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

        /// <summary>
        /// Downloads (or updates) the correct yt-dlp and deno builds for this OS/architecture into the
        /// server data folder, from their official GitHub "latest" releases. Overwrites existing copies
        /// (so it doubles as an updater). ffmpeg is not fetched - Jellyfin provides it.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Per-tool success flags and a short status message.</returns>
        public async Task<(bool YtDlp, bool Deno, string Message)> InstallToolsAsync(CancellationToken cancellationToken)
        {
            var dataDir = _appPaths.DataPath;
            Directory.CreateDirectory(dataDir);

            var isWindows = OperatingSystem.IsWindows();
            var isMac = OperatingSystem.IsMacOS();
            var arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;

            var messages = new List<string>();
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            if (!http.DefaultRequestHeaders.UserAgent.Any())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-TrailerPreroll");
            }

            // ----- yt-dlp: a single self-contained binary -----
            var ytAsset = isWindows ? "yt-dlp.exe" : isMac ? "yt-dlp_macos" : (arm64 ? "yt-dlp_linux_aarch64" : "yt-dlp_linux");
            var ytUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/" + ytAsset;
            var ytTarget = Path.Combine(dataDir, isWindows ? "yt-dlp.exe" : "yt-dlp");
            bool ytOk;
            try
            {
                await DownloadToFileAsync(http, ytUrl, ytTarget, cancellationToken).ConfigureAwait(false);
                MakeExecutable(ytTarget);
                ytOk = File.Exists(ytTarget);
                messages.Add(ytOk ? "yt-dlp OK" : "yt-dlp: no file produced");
            }
            catch (Exception ex)
            {
                ytOk = false;
                messages.Add("yt-dlp failed: " + ex.Message);
                _logger.LogWarning(ex, "Trailer Preroll yt-dlp install failed ({Url})", ytUrl);
            }

            // ----- deno: shipped as a zip; extract the binary -----
            var denoAsset = isWindows
                ? (arm64 ? "deno-aarch64-pc-windows-msvc.zip" : "deno-x86_64-pc-windows-msvc.zip")
                : isMac
                    ? (arm64 ? "deno-aarch64-apple-darwin.zip" : "deno-x86_64-apple-darwin.zip")
                    : (arm64 ? "deno-aarch64-unknown-linux-gnu.zip" : "deno-x86_64-unknown-linux-gnu.zip");
            var denoUrl = "https://github.com/denoland/deno/releases/latest/download/" + denoAsset;
            var denoTarget = Path.Combine(dataDir, isWindows ? "deno.exe" : "deno");
            bool denoOk;
            try
            {
                var tmpZip = Path.Combine(dataDir, "deno_download.zip");
                await DownloadToFileAsync(http, denoUrl, tmpZip, cancellationToken).ConfigureAwait(false);
                ExtractDeno(tmpZip, denoTarget, isWindows);
                try
                {
                    File.Delete(tmpZip);
                }
                catch (IOException)
                {
                }

                MakeExecutable(denoTarget);
                denoOk = File.Exists(denoTarget);
                messages.Add(denoOk ? "deno OK" : "deno: binary not found in archive");
            }
            catch (Exception ex)
            {
                denoOk = false;
                messages.Add("deno failed: " + ex.Message);
                _logger.LogWarning(ex, "Trailer Preroll deno install failed ({Url})", denoUrl);
            }

            _logger.LogInformation("Trailer Preroll tool install complete: yt-dlp={Yt}, deno={Deno}", ytOk, denoOk);
            return (ytOk, denoOk, string.Join("; ", messages));
        }

        /// <summary>
        /// Downloads an image (e.g. a TMDB poster) to a local file. Returns whether the file exists after.
        /// </summary>
        /// <param name="url">The image URL.</param>
        /// <param name="targetPath">The destination file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><c>true</c> on success.</returns>
        public async Task<bool> DownloadImageAsync(string url, string targetPath, CancellationToken cancellationToken)
        {
            try
            {
                var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(60);
                await DownloadToFileAsync(http, url, targetPath, cancellationToken).ConfigureAwait(false);
                return File.Exists(targetPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Trailer Preroll image download failed for {Url}", url);
                return false;
            }
        }

        private static async Task DownloadToFileAsync(HttpClient http, string url, string target, CancellationToken cancellationToken)
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var tmp = target + ".dl";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, target, true);
        }

        private static void ExtractDeno(string zipPath, string denoTarget, bool isWindows)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var wanted = isWindows ? "deno.exe" : "deno";
            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), wanted, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidOperationException("deno binary not found in the downloaded archive");
            }

            var tmp = denoTarget + ".dl";
            entry.ExtractToFile(tmp, true);
            File.Move(tmp, denoTarget, true);
        }

        private static void MakeExecutable(string path)
        {
            if (OperatingSystem.IsWindows() || !File.Exists(path))
            {
                return;
            }

            try
            {
                var mode = File.GetUnixFileMode(path);
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch (Exception)
            {
                // Non-fatal: the file is downloaded; only the +x bit failed.
            }
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
