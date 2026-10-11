using System.Xml.Linq;
using TickleTV.Scheduler.Core.Guide;

namespace TickleTV.Scheduler.Tests;

public class XmltvWriterTests
{
    [Fact]
    public void Formats_time_the_way_next_parses_it()
    {
        var t = new DateTimeOffset(2026, 6, 30, 15, 30, 0, TimeSpan.FromHours(-6));
        Assert.Equal("20260630153000 -0600", XmltvWriter.FormatTime(t));
    }

    [Fact]
    public void Writes_programme_elements_with_channel_and_title()
    {
        var start = new DateTimeOffset(2026, 10, 10, 20, 0, 0, TimeSpan.FromHours(-5));
        string xml = XmltvWriter.Write("tv.1",
        [
            new GuideProgramme
            {
                Start = start,
                Stop = start.AddMinutes(30),
                Title = "Mister Rogers' Neighborhood",
                SubTitle = "Day Care & Night Care",
                Category = "Kids",
                EpisodeNumber = "S13E16",
            },
        ]);

        XDocument doc = XDocument.Parse(xml);
        XElement programme = doc.Root!.Element("programme")!;

        Assert.Equal("tv.1", programme.Attribute("channel")!.Value);
        Assert.Equal("20261010200000 -0500", programme.Attribute("start")!.Value);
        Assert.Equal("20261010203000 -0500", programme.Attribute("stop")!.Value);
        Assert.Equal("Mister Rogers' Neighborhood", programme.Element("title")!.Value);
        Assert.Equal("Day Care & Night Care", programme.Element("sub-title")!.Value);
        Assert.Equal("Kids", programme.Element("category")!.Value);
        Assert.Equal("S13E16", programme.Element("episode-num")!.Value);
        Assert.Equal("onscreen", programme.Element("episode-num")!.Attribute("system")!.Value);
        Assert.Null(programme.Element("desc"));
    }
}
