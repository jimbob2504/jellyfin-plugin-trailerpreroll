using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TrailerPreroll.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.TrailerPreroll.Intros
{
    /// <summary>
    /// Resolves the cached preroll video files as <see cref="Trailer"/> items (instead of generic
    /// home videos) so they present as trailers. Only applies to files under the plugin's own two
    /// trailer folders; everything else is left to the default server resolvers.
    /// <para>
    /// Implements <see cref="IMultiItemResolver"/> as well as <see cref="IItemResolver"/> because the
    /// home-video library is resolved by a folder-level multi-item resolver that would otherwise claim
    /// these files before any per-item resolver runs. At <see cref="ResolverPriority.Plugin"/> priority
    /// our multi-item resolver runs first and wins. Discovered automatically via GetExports.
    /// </para>
    /// </summary>
    public class PrerollTrailerResolver : IItemResolver, IMultiItemResolver
    {
        private readonly TrailerLibraryService _libraries;

        /// <summary>
        /// Initializes a new instance of the <see cref="PrerollTrailerResolver"/> class.
        /// </summary>
        /// <param name="libraries">Library/folder service (provides the two trailer folder paths).</param>
        public PrerollTrailerResolver(TrailerLibraryService libraries)
        {
            _libraries = libraries;
        }

        /// <inheritdoc />
        /// <remarks>Highest priority so it wins over the default home-video/video resolvers for these files.</remarks>
        public ResolverPriority Priority => ResolverPriority.Plugin;

        /// <inheritdoc />
        public MultiItemResolverResult ResolveMultiple(Folder parent, List<FileSystemMetadata> files, CollectionType? collectionType, IDirectoryService directoryService)
        {
            var result = new MultiItemResolverResult();
            foreach (var file in files)
            {
                if (!file.IsDirectory && IsOurTrailerFile(file.FullName))
                {
                    result.Items.Add(CreateTrailer(file.FullName));
                }
                else
                {
                    result.ExtraFiles.Add(file);
                }
            }

            // Only claim the folder if it actually holds our trailers; otherwise return an empty
            // result so the library manager falls through to the normal resolvers.
            return result.Items.Count > 0 ? result : new MultiItemResolverResult();
        }

        /// <inheritdoc />
        public BaseItem? ResolvePath(ItemResolveArgs args)
        {
            if (args.IsDirectory || !IsOurTrailerFile(args.Path))
            {
                return null;
            }

            return CreateTrailer(args.Path);
        }

        private static readonly Regex KeyInName = new(@"\s*\[[A-Za-z0-9_-]{11}\]\s*", RegexOptions.Compiled);

        private static Trailer CreateTrailer(string path) => new()
        {
            Path = path,
            // Strip the "[youtube-id]" so the item reads cleanly from the moment it's resolved; the
            // catalog's cleanup pass later refines this (adds the year) and locks the Name field.
            Name = CleanName(Path.GetFileNameWithoutExtension(path)),
            IsInMixedFolder = true
        };

        private static string CleanName(string raw)
        {
            var s = KeyInName.Replace(raw, " ").Trim();
            return string.IsNullOrEmpty(s) ? "Trailer" : s;
        }

        private bool IsOurTrailerFile(string? path)
        {
            if (string.IsNullOrEmpty(path) || !IsVideoFile(path))
            {
                return false;
            }

            if (Path.GetFileName(path).StartsWith("dl_", StringComparison.Ordinal))
            {
                return false; // in-progress download temp file
            }

            return IsUnder(path, _libraries.LibraryDir) || IsUnder(path, _libraries.UpcomingDir);
        }

        private static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }

            var full = Path.GetFullPath(path);
            var baseDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVideoFile(string path)
        {
            var ext = Path.GetExtension(path);
            return string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".mkv", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".webm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".mov", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".m4v", StringComparison.OrdinalIgnoreCase);
        }
    }
}
