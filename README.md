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

## Catalog: scan your library

The catalog is a SQLite file. Media can live in any folder layout; files are classified from their names and folder hints, and anything the scan gets wrong is fixed once by hand and stays fixed.

```
dotnet run --project src/TickleTV.Scheduler.Cli -- scan "E:\TBTV Test" --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog shows --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog show "The Simpsons" --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog list --kind Commercial --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog kind 12 Bumper --db tickletv.sqlite
dotnet run --project src/TickleTV.Scheduler.Cli -- catalog tag 12 kids station-id --db tickletv.sqlite
```

What the scan understands:

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

- `order: chronological` follows original air date (season and episode as fallback) and wraps. `order: shuffle` airs every episode once before any repeats. Position is remembered per show and channel across builds.
- `ads.pool` is a query over the catalog: `kind = commercial AND era within 3 AND NOT tag:late-night`. `ads.cooldown_minutes` keeps a clip off air after it plays.
- A rebuild replaces every playout window from its start time forward and merges the guide, so config edits reach the air on the next build. `--dry-run` prints the plan and changes nothing, including rotation state.
- `bumpers` brackets each break with a clip before the ads and one after, drawn from a pool, rotating so the same bumper isn't seen every break. Tag bumpers `break-out` and `break-in` and set `before_tag` / `after_tag` to split the roles.
- `cold_open_break` keeps the cut right after a short first chapter (a teaser or cold open), the way real TV breaks.
- `parse <folder>` shows how files would be classified without probing or writing anything.

## Layout

- `src/TickleTV.Scheduler.Core`: catalog (SQLite, scanner, filename parser), playout and XMLTV models, ffprobe wrapper, break planner, padding math, break filler.
- `src/TickleTV.Scheduler.Cli`: command-line entry point.
- `tests/TickleTV.Scheduler.Tests`: xunit tests.
