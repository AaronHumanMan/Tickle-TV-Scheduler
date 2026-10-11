# Tickle TV Scheduler

Broadcast-style scheduling for [ErsatzTV Next](https://github.com/ErsatzTV/next): commercial breaks at chapter markers, bumpers around shows, day-part and holiday scheduling, and padding so every show lands on a clean clock boundary.

Next streams the channels. This tool decides what airs, when, and with which breaks, then writes Next's playout JSON and XMLTV guide files.

Status: Phase 1 in progress. A catalog, a YAML channel config with a daily grid, episode order modes, ad pools with cooldown, and a `build` command that writes rolling windows all work against a real Next. See the planning doc in this repo for the full design.

## Requirements

- .NET 10 SDK
- ffprobe on your PATH (or set `ffmpeg.ffprobe_path` in the channel's `channel.json`)
- A running ErsatzTV Next with at least one channel

## Build and test

```
dotnet build
dotnet test
```

## Spike: air one episode through Next

Probe a file to see its chapters:

```
dotnet run --project src/TickleTV.Scheduler.Cli -- probe "D:\TV\Show\S01E01.mkv"
```

Write a one-episode playout window into a Next channel folder, splitting the episode at its chapters, filling breaks from a folder of ad clips, and padding to the next half hour:

```
dotnet run --project src/TickleTV.Scheduler.Cli -- spike ^
  --file "D:\TV\Show\S01E01.mkv" ^
  --channel-dir "C:\ersatztv\config\channels\1" ^
  --ads "D:\TV\Ads" ^
  --xmltv-dir "C:\ersatztv\config\xmltv" --tvg-id ersatztv.1 ^
  --pad 30 --dry-run
```

Drop `--dry-run` to write the files. Next picks the playout up by file name for the current time, so the window starts at the next whole minute unless `--start` is given.

Options: `--pad 30|60|5|10|none`, `--max-breaks N`, `--min-act SECONDS`, `--pre-roll`, `--no-post-roll`, `--title`, `--schema-version 0.0.5`.

Next rejects playout files newer than the schema it supports. Next 0.2.0 accepts up to 0.0.5 (the default here). The engine embedded in legacy ErsatzTV accepts 0.0.4, so pass `--schema-version 0.0.4` for it.

## Set up

```
dotnet run --project src/TickleTV.Scheduler.Cli -- init
```

This writes `tickletv.yaml` to your data folder (`%LOCALAPPDATA%\TickleTV` on Windows, `~/.local/share/TickleTV` elsewhere), finds Next's `lineup.json` when it can, and names a catalog beside the config. Every command looks for `tickletv.yaml` in the current folder first, then in the data folder, so `--config` and `--db` are only needed to point elsewhere.

## The app (for people who don't want a command line)

```
dotnet run --project src/TickleTV.Scheduler.Web
```

Then open http://localhost:5062 in a browser on the same computer. The app shows what has been scanned, a **Needs attention** list of files the scan couldn't label, and plain-language help. On the Needs attention page, **Watch** opens the file in your default video player and a short form lets you say what it is, which show, the brand and category, and any labels. Answers are kept across rescans. The Needs attention and Library pages show 50 results at a time; pick 100, 150 or 200 from the **Per page** menu and the choice is remembered. The Schedule and Build pages can show every channel as a guide grid (channels as rows, hours as columns; press **edit** on a row to change that channel) or as one list per channel; the choice is saved in `tickletv.yaml` under `ui`.

## Catalog: scan your library

The catalog is a SQLite file. Media can live in any folder layout; files are classified from a Kodi-style `.nfo` sidecar when there is one, else from their names and folder hints, and anything the scan gets wrong is fixed once by hand and stays fixed.

```
dotnet run --project src/TickleTV.Scheduler.Cli -- scan "E:\TBTV Test" --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog shows --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog show "The Simpsons" --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog list --kind Commercial --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog kind 12 Bumper --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog tag 12 kids station-id --db tickletv.sqlite
```

What the scan understands:

- A `.nfo` file next to the video (`Episode.nfo` beside `Episode.mkv`, as Kodi, Jellyfin, Emby and tinyMediaManager write them) supplies show, season, episode, title, air date, year and genre, and beats the file name. `tvshow.nfo` in the show or season folder supplies the show name and genres. A `<movie>` sidecar marks a film. Genres become `genre:` tags. A rescan re-reads a sidecar that changed since the last scan without re-probing the video; `catalog retag` re-reads them all. Manual answers still win.
- `Show S04E04 Title.mkv`, `257 - 1990-10-15 - TNG S04 E04 - Family.mkv`, and `Show 3x07 Title.mkv` are episodes. The air date, when present, drives chronological order.
- `S05E02 Cape Feare.mkv` inside `The Simpsons\Season 1\` takes its show name from the folder above the season folder.
- Words like Commercial, PSA, Promo, Bumper, Ident, Filler, and Fallback in the file or folder name set the kind. A four-digit year becomes `1991` and `1990s` tags.
- Technical suffixes such as `(480p_30fps_H264-128kbit_AAC)` are ignored.

A rescan probes only new or changed files. A file that moved or was renamed keeps its tags and play history, because items are keyed by content, not path. Deleted files are marked missing rather than dropped.

## Build a channel

Copy `examples/tickletv.yaml`, point it at your catalog and Next's `lineup.json`, and describe the daily grid. Each slot airs episodes of its show back to back, each padded to its boundary, until the next slot starts.

```
dotnet run --project src/TickleTV.Scheduler.Cli -- build --config tickletv.yaml --dry-run
dotnet run --project src/TickleTV.Scheduler.Cli -- build --config tickletv.yaml
```

- A slot can list several shows: `shows: [Cheers, Frasier, Wings]`. They take turns, one episode each, in that order, and the turn carries over from day to day. With `hold_grid`, a show whose next episode would not fit before the next slot gives its turn to one that fits. The Schedule page and the build warnings say when a block is too short for all of its shows to play in one day (for example two 45-minute shows in an hour-long block): that is allowed, the shows simply alternate days. The My shows page lists each show's typical episode length, which is what that check uses.
- `turns: random` on a block picks the next show at random instead of going down the list, never the same show twice in a row. The default is `turns: in_order`.
- `order: chronological` follows original air date (season and episode as fallback) and wraps. `order: shuffle` airs every episode once before any repeats. Position is remembered per show and channel across builds.
- `ads.pool` is a query over the catalog: `kind = commercial AND era within 3 AND NOT tag:late-night`. `ads.cooldown_minutes` keeps a clip off air after it plays.
- A rebuild joins the running schedule at the end of the programme on air: what's playing finishes as planned, the new schedule starts there, and earlier items are carried into the new file. It keeps the episodes the old window had already assigned, so rebuilding does not move shows forward; pass `--fresh` to pick again. `--dry-run` prints the plan and changes nothing, including rotation state.
- `bumpers` plays one bumper right before the show starts and one right after it ends (`placement: show_edges`, the default), drawn from a pool and rotating. `placement: every_break` puts a bumper before and after the ads in every break instead. Bumpers carry `station-id`, `break-card`, or `promo` tags; use `before_tag` / `after_tag` to pick which plays where.
- `filler` names a clip that covers whatever ads and bumpers cannot fill exactly, playing from its start and looping as needed. Without one, gaps are black video with silence. Any file with Filler, Fallback, or Slate in its name is a filler.
- An episode with no usable chapter marks gets no mid-show breaks; it runs straight through and is padded at the end to the next 5 minutes (`no_chapter_pad`). Set `fallback_breaks` above 0 to space breaks evenly instead.
- `gap.hold_grid` keeps every slot starting on time. When the next episode can't finish before the following slot, the leftover (up to `max_minutes`) becomes a station break of promos and commercials from `gap.pool`; an episode that fits is padded only up to the slot start.
- `cold_open_break` keeps the cut right after a short first chapter (a teaser or cold open), the way real TV breaks.
- `parse <folder>` shows how files would be classified without probing or writing anything.

## Layout

- `src/TickleTV.Scheduler.Core`: catalog (SQLite, scanner, filename parser), playout and XMLTV models, ffprobe wrapper, break planner, padding math, break filler.
- `src/TickleTV.Scheduler.Cli`: command-line entry point.
- `tests/TickleTV.Scheduler.Tests`: xunit tests.
