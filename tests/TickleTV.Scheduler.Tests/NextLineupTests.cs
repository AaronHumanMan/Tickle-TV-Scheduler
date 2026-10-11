using System.Text.Json.Nodes;
using TickleTV.Scheduler.Core.Config;

namespace TickleTV.Scheduler.Tests;

public class NextLineupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tickletv-next-" + Guid.NewGuid().ToString("N"));
    private readonly string _lineup;

    public NextLineupTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "channels", "1"));
        _lineup = Path.Combine(_dir, "lineup.json");
        File.WriteAllText(_lineup, """
            {
              "version": "https://ersatztv.org/lineup/version/0.0.1",
              "server": { "bind_address": "0.0.0.0", "port": 8409 },
              "output": { "folder": "./hls" },
              "xmltv": { "folder": "./xmltv" },
              "channels": [ { "number": "1", "name": "Tickle TV", "config": "./channels/1/channel.json", "tvg_id": "tickle.1" } ]
            }
            """);
        File.WriteAllText(Path.Combine(_dir, "channels", "1", "channel.json"), """
            {
              "version": "https://ersatztv.org/channel/version/0.1.2",
              "playout": { "folder": "./playout" },
              "ffmpeg": { "ffmpeg_path": "C:\\Next\\ffmpeg.exe", "ffprobe_path": "C:\\Next\\ffprobe.exe", "disabled_filters": [], "reports_folder": "C:\\Next\\reports" },
              "normalization": {
                "audio": { "mode": "transcode", "format": "aac", "bitrate_kbps": 192, "buffer_kbps": 384, "channels": 2, "sample_rate_hz": 48000, "normalize_loudness": true },
                "video": { "mode": "transcode", "format": "h264", "bit_depth": 8, "width": 1920, "height": 1080, "bitrate_kbps": 2000, "buffer_kbps": 4000, "accel": null, "tonemap_algorithm": "linear", "vaapi_device": "/dev/dri/renderD128" },
                "subtitle": { "mode": "burn" }
              }
            }
            """);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Loads_channels_with_a_profile_read_from_channel_json()
    {
        var channels = NextLineup.Load(_lineup);
        var c = Assert.Single(channels);
        Assert.Equal("1", c.Number);
        Assert.Equal("tickle.1", c.TvgId);
        Assert.Equal(1080, c.Profile.Quality);
        Assert.Equal("none", c.Profile.Hardware);
        Assert.True(c.Profile.NormalizeLoudness);
        Assert.EndsWith(Path.Combine("channels", "1", "playout"), c.PlayoutFolder);
    }

    [Fact]
    public void Adds_a_channel_copying_ffmpeg_paths_and_writing_the_profile()
    {
        var added = NextLineup.AddChannel(_lineup, "2", "Kids TV", null, new ChannelProfile { Quality = 720, Codec = "h264", Hardware = "nvidia", PassThrough = true, NormalizeLoudness = false }, group: "Tickle");

        Assert.Equal("tickle.2", added.TvgId);
        Assert.Equal(720, added.Profile.Quality);
        Assert.True(Directory.Exists(Path.Combine(_dir, "channels", "2", "playout")));

        JsonNode channel = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "channels", "2", "channel.json")))!;
        Assert.Equal(@"C:\Next\ffmpeg.exe", channel["ffmpeg"]!["ffmpeg_path"]!.GetValue<string>());
        Assert.Equal("cuda", channel["normalization"]!["video"]!["accel"]!.GetValue<string>());
        Assert.Equal("copy", channel["normalization"]!["video"]!["mode"]!.GetValue<string>());
        Assert.Equal(1280, channel["normalization"]!["video"]!["width"]!.GetValue<int>());
        Assert.False(channel["normalization"]!["audio"]!["normalize_loudness"]!.GetValue<bool>());

        JsonNode lineup = JsonNode.Parse(File.ReadAllText(_lineup))!;
        Assert.Equal(2, lineup["channels"]!.AsArray().Count);
        Assert.Equal("8409", lineup["server"]!["port"]!.ToString()); // untouched
        Assert.Equal("Tickle", lineup["channels"]![1]!["group"]!.GetValue<string>());
        Assert.Equal("3", NextLineup.NextFreeNumber(NextLineup.Load(_lineup)));
    }

    [Fact]
    public void Updates_a_channel_without_losing_fields_it_does_not_own()
    {
        NextLineup.UpdateChannel(_lineup, "1", "Tickle TV HD", new ChannelProfile { Quality = 1080, Codec = "hevc", Hardware = "intel", NormalizeLoudness = true });

        JsonNode channel = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "channels", "1", "channel.json")))!;
        Assert.Equal("hevc", channel["normalization"]!["video"]!["format"]!.GetValue<string>());
        Assert.Equal("qsv", channel["normalization"]!["video"]!["accel"]!.GetValue<string>());
        Assert.Equal("/dev/dri/renderD128", channel["normalization"]!["video"]!["vaapi_device"]!.GetValue<string>());
        Assert.Equal(@"C:\Next\reports", channel["ffmpeg"]!["reports_folder"]!.GetValue<string>());
        Assert.Equal("Tickle TV HD", NextLineup.Load(_lineup)[0].Name);
    }

    [Fact]
    public void Refuses_a_duplicate_channel_number()
    {
        Assert.Throws<InvalidOperationException>(() => NextLineup.AddChannel(_lineup, "1", "Dup", null, new ChannelProfile()));
    }
}
