using System.Globalization;
using System.Text;
using System.Xml;

namespace TickleTV.Scheduler.Core.Guide;

/// <summary>One guide entry. Next copies each programme element verbatim into its /xmltv.xml.</summary>
public sealed record GuideProgramme
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset Stop { get; init; }
    public required string Title { get; init; }
    public string? SubTitle { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    /// <summary>On-screen episode number such as <c>S03E07</c>.</summary>
    public string? EpisodeNumber { get; init; }
}

/// <summary>
/// Writes the per-channel XMLTV file that ErsatzTV Next reads from its lineup's
/// <c>xmltv.folder</c>. The file is named <c>{tvg_id}.xml</c>. Next reads only the
/// programme elements and ignores the surrounding document.
/// </summary>
public static class XmltvWriter
{
    private const string TimeFormat = "yyyyMMddHHmmss";

    public static string FileName(string tvgId) => $"{tvgId}.xml";

    public static string Write(string tvgId, IEnumerable<GuideProgramme> programmes)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false,
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("tv");
            writer.WriteAttributeString("generator-info-name", "Tickle TV Scheduler");

            foreach (GuideProgramme p in programmes)
            {
                writer.WriteStartElement("programme");
                writer.WriteAttributeString("start", FormatTime(p.Start));
                writer.WriteAttributeString("stop", FormatTime(p.Stop));
                writer.WriteAttributeString("channel", tvgId);

                writer.WriteElementString("title", p.Title);
                if (!string.IsNullOrEmpty(p.SubTitle)) writer.WriteElementString("sub-title", p.SubTitle);
                if (!string.IsNullOrEmpty(p.Description)) writer.WriteElementString("desc", p.Description);
                if (!string.IsNullOrEmpty(p.Category)) writer.WriteElementString("category", p.Category);
                if (!string.IsNullOrEmpty(p.EpisodeNumber))
                {
                    writer.WriteStartElement("episode-num");
                    writer.WriteAttributeString("system", "onscreen");
                    writer.WriteString(p.EpisodeNumber);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>XMLTV time: <c>20260630153000 -0600</c>.</summary>
    public static string FormatTime(DateTimeOffset t)
    {
        string offset = t.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "");
        return $"{t.ToString(TimeFormat, CultureInfo.InvariantCulture)} {offset}";
    }
}
