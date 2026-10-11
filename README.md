# Tickle TV Scheduler

Broadcast-style scheduling for [ErsatzTV Next](https://github.com/ErsatzTV/next): commercial breaks at chapter markers, bumpers around shows, day-part and holiday scheduling, and padding so every show lands on a clean clock boundary.

Next streams the channels. This tool decides what airs, when, and with which breaks, then writes Next's playout JSON and XMLTV guide files.

Status: Phase 0 spike. See the planning doc in this repo for the full design.

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

Options: `--pad 30|60|5|10|none`, `--max-breaks N`, `--min-act SECONDS`, `--pre-roll`, `--no-post-roll`, `--title`.

## Layout

- `src/TickleTV.Scheduler.Core`: playout and XMLTV models, ffprobe wrapper, break planner, padding math.
- `src/TickleTV.Scheduler.Cli`: command-line entry point.
- `tests/TickleTV.Scheduler.Tests`: xunit tests.
