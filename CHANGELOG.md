# Changelog

All notable changes to **Trailer Preroll** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions use Jellyfin's four-part scheme (`major.minor.build.revision`).

## [1.5.0.1] - 2026-09-03

### Fixed
- The settings page stylesheet is now applied. Jellyfin discards the document `<head>`, so the styles (which lived there) were being dropped — leaving the tabs, language dropdown, and cached-trailer lists unstyled. The styles now live inside the page body.

### Changed
- Cached trailers load automatically as bordered cards (Library and Upcoming), instead of behind a "Show cached trailers" button; a small "Reload" link refreshes the list.

## [1.5.0.0] - 2026-09-03

### Added
- Cached-trailer manager on the **Status** tab: separate **Library** and **Upcoming** dropdowns listing the trailers currently on disk, each showing its play count and a per-trailer **Replace** button (removes that trailer and downloads a different one).
- Language filter is now a native-style dropdown with a checkbox for each language.

### Changed
- Settings page restyled with rounded pill tabs and icons.
- The cached-trailer list is built from the files actually on disk, so rotated-out trailers no longer clutter it.

### Fixed
- Ghost and duplicate library items left behind when trailers rotate (the same trailer showing several times) are now cleaned up automatically.

## [1.4.0.0] - 2026-09-03

### Added
- **Change trailer** button on the web player: skips the current preroll, removes it, and downloads a different one in its place.
- Settings page reorganised into tabs.

### Changed
- Play-count list shows real trailer titles for rotated-out trailers instead of raw YouTube ids (the title is now stored with each play).

### Removed
- Redundant "Mark cached trailers as Trailer type" button (this now runs automatically on startup) and the unused "Source: TMDB / YouTube" checkboxes.

## [1.3.0.0] - 2026-08-31

### Added
- Scheduled task **"Cache trailer prerolls"** (Dashboard → Scheduled Tasks) so the download/rotation time is fully configurable; defaults to a daily 03:30 run.
- Language filter to skip trailers for foreign-language films (library films matched on their audio track, upcoming films on their TMDB original language).

### Removed
- The always-on hourly background run; caching now happens on startup and via the scheduled task.

## [1.2.0.0] - 2026-08-25

### Added
- One-click **Download / update tools** button that fetches the correct yt-dlp and deno for the server automatically.
- Trailer play-count viewer in the settings page.
- Automatic posters — the real film poster for library trailers, and a TMDB poster for upcoming trailers.

### Changed
- Duplicate trailers for the same title are removed, keeping one.

## [1.1.0.0] - 2026-08-23

### Added
- Tool-setup diagnostics (yt-dlp / deno / ffmpeg) in the status panel.

### Changed
- Broadened compatibility to all Jellyfin 10.11.x releases.

### Fixed
- Duplicate entries are no longer added to the Watch Later playlist.

## [1.0.0.0] - 2026-08-23

### Added
- Initial public release.
- Cinema-style trailer pre-show before movies and TV episodes, played through Jellyfin's Cinema Mode / Intros.
- Two trailer pools: films already in your library (from each film's trailer metadata) and upcoming releases (via TMDB).
- Trailers fetched with yt-dlp, muxed with Jellyfin's ffmpeg, into a small rotating cache served from two auto-created libraries.
- Per-user overrides, watched/unwatched filtering, randomisation, configurable counts, pool sizes, rotation interval, quality cap, and optional YouTube cookies support.
- "Want to watch" button on the web player that adds the film to a personal Watch Later playlist; trailers saved this way are protected from deletion.

[1.5.0.1]: https://github.com/jimbob2504/jellyfin-plugin-trailerpreroll/releases/tag/v1.5.0.1
[1.5.0.0]: https://github.com/jimbob2504/jellyfin-plugin-trailerpreroll/releases/tag/v1.5.0.0
[1.4.0.0]: https://github.com/jimbob2504/jellyfin-plugin-trailerpreroll/releases/tag/v1.4.0.0
[1.3.0.0]: https://github.com/jimbob2504/jellyfin-plugin-trailerpreroll/releases/tag/v1.3.0.0
[1.2.0.0]: https://github.com/jimbob2504/jellyfin-plugin-trailerpreroll/releases/tag/v1.2.0.0
[1.1.0.0]: https://github.com/jimbob2504/jellyfin-plugin-trailerpreroll/releases/tag/v1.1.0.0
[1.0.0.0]: https://github.com/jimbob2504/jellyfin-plugin-trailerpreroll/releases/tag/V1.0.0.0
