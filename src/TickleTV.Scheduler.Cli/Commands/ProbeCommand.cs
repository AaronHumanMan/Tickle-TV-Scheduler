using System.CommandLine;
using TickleTV.Scheduler.Core.Media;

namespace TickleTV.Scheduler.Cli.Commands;

public static class ProbeCommand
{
    public static Command Create()
    {
        var file = new Argument<FileInfo>("file") { Description = "Media file to probe." };
        var ffprobe = new Option<string?>("--ffprobe") { Description = "Path to ffprobe (default: from PATH)." };

        var cmd = new Command("probe", "Show duration, chapters, and streams for a media file.");
        cmd.Arguments.Add(file);
        cmd.Options.Add(ffprobe);

        cmd.SetAction(async (parseResult, ct) =>
        {
            FileInfo f = parseResult.GetRequiredValue(file);
            MediaProbe probe = await new Ffprobe(parseResult.GetValue(ffprobe)).ProbeAsync(f.FullName, ct);

            Console.WriteLine($"File:     {probe.Path}");
            Console.WriteLine($"Duration: {Fmt(probe.DurationMs)} ({probe.DurationMs} ms)");
            Console.WriteLine($"Streams:  {string.Join(", ", probe.Streams.Select(s => $"#{s.Index} {s.CodecType}/{s.CodecName}"))}");
            Console.WriteLine($"Chapters: {probe.Chapters.Count}");
            foreach (Chapter c in probe.Chapters)
                Console.WriteLine($"  {Fmt(c.StartMs)} - {Fmt(c.EndMs)}  {c.Title}");
            return 0;
        });
        return cmd;
    }

    internal static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.fff") : t.ToString(@"m\:ss\.fff");
    }
}
