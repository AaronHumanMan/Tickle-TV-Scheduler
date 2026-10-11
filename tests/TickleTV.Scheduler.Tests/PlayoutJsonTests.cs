using System.Text.Json;
using TickleTV.Scheduler.Core.Playout;

namespace TickleTV.Scheduler.Tests;

public class PlayoutJsonTests
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 23, 20, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void Serializes_to_next_schema_field_names()
    {
        var playout = new Playout
        {
            Items =
            [
                new PlayoutItem
                {
                    Id = "1",
                    Start = T0,
                    Finish = T0.AddMinutes(10),
                    Source = new LocalSource { Path = "/media/show.mkv", InPointMs = 0, OutPointMs = 600_000 },
                },
            ],
        };

        using JsonDocument doc = JsonDocument.Parse(PlayoutJson.Serialize(playout));
        JsonElement root = doc.RootElement;

        Assert.Equal(Playout.SchemaVersion, root.GetProperty("version").GetString());
        JsonElement item = root.GetProperty("items")[0];
        Assert.Equal("2026-02-23T20:00:00-05:00", item.GetProperty("start").GetString());
        Assert.Equal("2026-02-23T20:10:00-05:00", item.GetProperty("finish").GetString());

        JsonElement source = item.GetProperty("source");
        Assert.Equal("local", source.GetProperty("source_type").GetString());
        Assert.Equal("/media/show.mkv", source.GetProperty("path").GetString());
        Assert.Equal(0, source.GetProperty("in_point_ms").GetInt64());
        Assert.Equal(600_000, source.GetProperty("out_point_ms").GetInt64());

        // Nulls are omitted so Next's strict parser never sees unexpected null fields.
        Assert.False(item.TryGetProperty("tracks", out _));
        Assert.False(item.TryGetProperty("watermark", out _));
    }

    [Fact]
    public void Lavfi_tracks_match_the_schema_example()
    {
        var item = new PlayoutItem
        {
            Id = "2",
            Start = T0,
            Finish = T0.AddSeconds(10),
            Tracks = new PlayoutItemTracks
            {
                Audio = new TrackSelection { Source = new LavfiSource { Params = "anullsrc=channel_layout=stereo:sample_rate=48000:d=10" } },
                Video = new TrackSelection { Source = new LocalSource { Path = "/pictures/brb.png" } },
            },
        };

        string json = PlayoutJson.Serialize(new Playout { Items = [item] });
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement tracks = doc.RootElement.GetProperty("items")[0].GetProperty("tracks");

        Assert.Equal("lavfi", tracks.GetProperty("audio").GetProperty("source").GetProperty("source_type").GetString());
        Assert.Equal("local", tracks.GetProperty("video").GetProperty("source").GetProperty("source_type").GetString());
        Assert.False(tracks.TryGetProperty("subtitle", out _));
    }

    [Fact]
    public void Round_trips_through_deserialize()
    {
        var original = new Playout
        {
            Items =
            [
                new PlayoutItem { Id = "a", Start = T0, Finish = T0.AddMinutes(1), Source = new LocalSource { Path = "x.mkv" } },
                new PlayoutItem { Id = "b", Start = T0.AddMinutes(1), Finish = T0.AddMinutes(2), Source = new HttpSource { Uri = "http://h/f.mkv?t={{TOKEN}}" } },
            ],
        };

        Playout back = PlayoutJson.Deserialize(PlayoutJson.Serialize(original));
        Assert.Equal(original, back with { Items = original.Items });
        Assert.IsType<LocalSource>(back.Items[0].Source);
        Assert.IsType<HttpSource>(back.Items[1].Source);
    }

    [Fact]
    public void File_name_uses_compact_iso8601_with_nine_fraction_digits()
    {
        var start = new DateTimeOffset(2026, 4, 13, 0, 0, 0, 0, TimeSpan.FromHours(-5));
        var finish = new DateTimeOffset(2026, 4, 14, 0, 21, 31, 620, TimeSpan.FromHours(-5));

        Assert.Equal(
            "20260413T000000.000000000-0500_20260414T002131.620000000-0500.json",
            PlayoutJson.FileName(start, finish));
    }
}

public class CompactTimestampTests
{
    [Fact]
    public void File_name_timestamps_round_trip()
    {
        var finish = new DateTimeOffset(2026, 10, 10, 23, 40, 0, TimeSpan.FromHours(-5));
        string name = TickleTV.Scheduler.Core.Playout.PlayoutJson.FileName(finish.AddHours(-1), finish);
        string finishPart = name[(name.IndexOf('_') + 1)..^5];

        Assert.True(TickleTV.Scheduler.Cli.Commands.BuildCommand.TryParseCompact(finishPart, out DateTimeOffset parsed));
        Assert.Equal(finish, parsed);
    }
}
