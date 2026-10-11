using TickleTV.Scheduler.Core.Catalog;

namespace TickleTV.Scheduler.Tests;

public class FilenameParserTests
{
    [Fact]
    public void Tng_style_name_with_index_date_show_and_title()
    {
        ParsedName p = FilenameParser.Parse(@"E:\TBTV Test\Shows\TNG\Season 1\257 - 1990-10-15 - TNG S04 E04 - Family.mkv");

        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal("TNG", p.Show);
        Assert.Equal(FactSource.Filename, p.ShowSource);
        Assert.Equal(4, p.Season);
        Assert.Equal(4, p.Episode);
        Assert.Equal(new DateOnly(1990, 10, 15), p.AirDate);
        Assert.Equal(1990, p.Year);
        Assert.Equal("Family", p.EpisodeTitle);
    }

    [Fact]
    public void Simpsons_style_name_takes_show_from_the_folder_above_season()
    {
        ParsedName p = FilenameParser.Parse(@"E:\TBTV Test\Shows\The Simpsons\Season 1\S05E02 Cape Feare.mkv");

        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal("The Simpsons", p.Show);
        Assert.Equal(FactSource.Folder, p.ShowSource);
        Assert.Equal(5, p.Season);
        Assert.Equal(2, p.Episode);
        Assert.Null(p.AirDate);
        Assert.Equal("Cape Feare", p.EpisodeTitle);
    }

    [Fact]
    public void Commercial_with_technical_suffix_and_year()
    {
        ParsedName p = FilenameParser.Parse(@"E:\TBTV Test\Commercials\Season 1\Taco Bell MC Hammer Commercial 1991 (480p_30fps_H264-128kbit_AAC).mp4");

        Assert.Equal(MediaKind.Commercial, p.Kind);
        Assert.Equal(1991, p.Year);
        Assert.Equal("Taco Bell MC Hammer Commercial 1991", p.Title);
        Assert.Null(p.Show);
    }

    [Fact]
    public void Psa_and_promo_are_commercials()
    {
        Assert.Equal(MediaKind.Commercial, FilenameParser.Parse(@"C:\x\Global Releaf Christopher Reeve PSA 1990.mp4").Kind);
        Assert.Equal(MediaKind.Commercial, FilenameParser.Parse(@"C:\x\The Dolly Show promo 1988.mp4").Kind);
    }

    [Fact]
    public void Fallback_word_means_filler()
    {
        ParsedName p = FilenameParser.Parse(@"E:\TBTV Test\Fallback\Fallback vaporwave.mp4");
        Assert.Equal(MediaKind.Filler, p.Kind);
    }

    [Fact]
    public void Folder_name_classifies_when_the_file_name_does_not()
    {
        ParsedName p = FilenameParser.Parse(@"D:\Media\Bumpers\network-id-blue.mp4");
        Assert.Equal(MediaKind.Bumper, p.Kind);
        Assert.Equal(FactSource.Folder, p.KindSource);
    }

    [Fact]
    public void Season_x_episode_form_is_recognised()
    {
        ParsedName p = FilenameParser.Parse(@"D:\TV\Cheers\Cheers 3x07 Coach Buries a Grudge.mkv");
        Assert.Equal("Cheers", p.Show);
        Assert.Equal(3, p.Season);
        Assert.Equal(7, p.Episode);
        Assert.Equal("Coach Buries a Grudge", p.EpisodeTitle);
    }

    [Fact]
    public void Unrecognised_file_is_unknown_not_guessed()
    {
        ParsedName p = FilenameParser.Parse(@"D:\stuff\clip.mp4");
        Assert.Equal(MediaKind.Unknown, p.Kind);
        Assert.Equal(FactSource.Default, p.KindSource);
        Assert.Equal("clip", p.Title);
    }

    [Fact]
    public void Year_inside_a_date_is_not_double_counted_and_numbers_in_titles_are_left_alone()
    {
        ParsedName p = FilenameParser.Parse(@"D:\x\Show S01E01 - 2001 A Space Odyssey tribute 1999-05-01.mkv");
        Assert.Equal(new DateOnly(1999, 5, 1), p.AirDate);
        Assert.Equal(1999, p.Year);
    }
}
