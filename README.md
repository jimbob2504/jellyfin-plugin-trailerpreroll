# Trailer Preroll (Jellyfin plugin)

Plays a cinema-style pre-show of trailers before your movies and TV episodes — trailers for
films **already in your library** plus **upcoming releases** — the way the ads and trailers roll
before a film at the cinema.

Trailers are downloaded on a small, rotating schedule with [yt-dlp](https://github.com/yt-dlp/yt-dlp),
muxed with Jellyfin's bundled ffmpeg, and served from two auto-created libraries. They play through
Jellyfin's built-in **Cinema Mode / Intros** feature.

> Built and tested against **Jellyfin 10.11.11** (.NET 9) on Windows.

## Features

- Trailers before **movies** and/or **TV episodes** (with a "first episode of a session only" mode
  so it doesn't interrupt a binge).
- Two trailer pools: **your library** (from each film's own trailer metadata) and **upcoming
  releases** (via TMDB).
- **Per-user overrides** — give specific users different settings (e.g. one user gets any trailer,
  another only unwatched-movie trailers).
- **Watched / unwatched** filtering, randomisation, configurable counts and pool sizes.
- A small **bounded, rotating cache** (or "keep a trailer for every film" mode), with gradual
  per-cycle downloading to stay gentle on YouTube.
- **Rolling rotation** — replace a trailer after it has played N times.
- A **"Want to watch" button** on the web player: while a trailer plays, one click adds the film to
  your personal *Watch Later* playlist. Trailers saved this way are protected from deletion.
- A **status panel** in the settings page showing download health (and warning when your YouTube
  cookies have gone stale).

## Requirements

This plugin drives external tools. On the Jellyfin server machine you need:

| Tool | Where | Notes |
|------|-------|-------|
| **yt-dlp** | `<data>/yt-dlp(.exe)` or on `PATH`, or set a path in settings | Downloads the trailers. Keep it updated (`yt-dlp -U`). |
| **deno** | `<data>/deno(.exe)` | Needed for YouTube's signature challenge (yt-dlp uses it as a JS runtime). |
| **cookies.txt** | path set in settings | Exported from a browser logged into YouTube. Greatly improves reliability against "Sign in to confirm you're not a bot". |
| **ffmpeg** | provided by Jellyfin | Used to mux video+audio. The plugin auto-detects Jellyfin's ffmpeg. |
| **TMDB API key** | set in settings | Only needed for the *upcoming releases* pool. |

`<data>` is Jellyfin's data folder (the one containing `jellyfin.db`).

### About cookies

`cookies.txt` is the fragile part. Export it with a "Get cookies.txt LOCALLY"-style browser
extension **from a fully logged-in YouTube session**. Cookies rotate/expire when you keep using
YouTube in the same browser — exporting from a logged-in **incognito** window and then closing it
(without logging out) tends to produce cookies that last for weeks. The status panel warns you when
downloads start failing bot checks so you know it's time to re-export.

## Installation

### Option A — plugin repository (recommended)

1. In Jellyfin: **Dashboard → Plugins → Repositories → +**
2. Add this repository manifest URL:
   ```
   https://raw.githubusercontent.com/jimbob2504/jellyfin-plugin-trailerpreroll/main/manifest.json
   ```
3. Go to **Catalog**, find **Trailer Preroll**, install it, and restart Jellyfin.

### Option B — manual

1. Download `trailer-preroll_<version>.zip` from the [Releases](https://github.com/jimbob2504/jellyfin-plugin-trailerpreroll/releases) page.
2. Extract it into a folder named `Trailer Preroll` under your Jellyfin `plugins` directory
   (so you have `plugins/Trailer Preroll/Jellyfin.Plugin.TrailerPreroll.dll` and `meta.json`).
3. Restart Jellyfin.

Then place `yt-dlp`, `deno` and `cookies.txt` as described above, open the plugin settings, add your
TMDB key and cookies path, and click **Download trailers now**.

## Notes & caveats

- The plugin auto-creates two **Home videos** libraries: *Trailer Preroll (Library)* and
  *Trailer Preroll (Upcoming)*.
- Cinema-mode intros must be supported/enabled by your client.
- The **"Want to watch" button** works in the **web client only** (it injects a small script into the
  web client's `index.html`, re-applied automatically on each server start). It does not appear on
  clients that don't use the Jellyfin web UI (e.g. the MPV desktop app).
- Trailers are real YouTube videos; some are region-locked or age-restricted and will be skipped.

## Building from source

Requires the .NET 9 SDK.

```bash
cd Jellyfin.Plugin.TrailerPreroll
dotnet build -c Release
```

The plugin DLL is written to `bin/Release/net9.0/Jellyfin.Plugin.TrailerPreroll.dll`. To package a
release, zip that DLL together with a `meta.json` (see the repo root for a template).

## License

MIT — see [LICENSE](LICENSE).

This project bundles nothing from YouTube; it orchestrates yt-dlp/ffmpeg at runtime. You are
responsible for your own use of those tools and of downloaded content.
