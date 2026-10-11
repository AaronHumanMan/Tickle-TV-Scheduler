using TickleTV.Scheduler.Core.Catalog;

namespace TickleTV.Scheduler.Tests;

/// <summary>Names from a large real library: scene-style show rips, decade/category folders, promo-heavy bumper folders.</summary>
public class FilenameParserLibraryTests
{
    [Theory]
    [InlineData(@"G:\Shows\30 for 30\Season 2\30.For.30.S02E12.Big.Shot.720p.HDTV.x264-YesTV.mkv", "30 for 30", 2, 12, "Big Shot")]
    [InlineData(@"G:\Shows\30 for 30\Season 1\30.for.30.S01E23.Into.the.Wind.720p.FLAC.2.0.AVC.REMUX-FraMeSToR.mkv", "30 for 30", 1, 23, "Into the Wind")]
    [InlineData(@"G:\Shows\30 for 30\Season 4\ESPN.30.for.30.S04E38.No.Scope.The.Story.of.Faze.Clan.720p.ESPN.WEB-DL.AAC2.0.H.264-KIMCHI.mkv", "30 for 30", 4, 38, "No Scope The Story of Faze Clan")]
    [InlineData(@"G:\Shows\Good Eats\Season 10\Good.Eats.S10E03.1080p.WEB-DL.AAC2.0.H.264-BTN.mkv", "Good Eats", 10, 3, null)]
    [InlineData(@"G:\Shows\How It's Made\Season 3\How.Its.Made.S03E05.720p.HDTV.x264.mkv", "How It's Made", 3, 5, null)]
    public void Scene_names_take_the_show_from_the_folder_and_drop_release_tokens(string path, string show, int season, int episode, string? title)
    {
        ParsedName p = FilenameParser.Parse(path);
        Assert.Equal(MediaKind.Episode, p.Kind);
        Assert.Equal(show, p.Show);
        Assert.Equal(season, p.Season);
        Assert.Equal(episode, p.Episode);
        Assert.Equal(title, p.EpisodeTitle);
    }

    [Fact]
    public void Modern_marvels_collection_folder_does_not_hide_the_show_name()
    {
        ParsedName p = FilenameParser.Parse(@"G:\Shows\Modern Marvels Collection {S01~S21, 526 of 691 Episodes}\Modern Marvels ~ Season 05\Modern Marvels S05E22 - The Tool Bench; Power Tools.avi");
        Assert.Equal("Modern Marvels", p.Show);
        Assert.Equal(5, p.Season);
        Assert.Equal(22, p.Episode);
        Assert.Equal("The Tool Bench; Power Tools", p.EpisodeTitle);
    }

    [Fact]
    public void Category_folder_under_a_decade_becomes_a_tag()
    {
        ParsedName p = FilenameParser.Parse(@"G:\Commercials\90s\Cars\Jeep Grand Cherokee Laredo $269 a month - California Jeep and Eagle Dealers.mkv");
        Assert.Equal(MediaKind.Commercial, p.Kind);
        Assert.Contains("category:cars", p.Tags);
        Assert.Contains("1990s", p.Tags);
        Assert.Contains("brand:jeep-grand-cherokee-laredo-269-a-month", p.Tags);

        ParsedName h = FilenameParser.Parse(@"G:\Commercials\80s\Health Beauty Personal Care\Prell - shampoo.mkv");
        Assert.Contains("category:health-beauty-personal-care", h.Tags);
        Assert.Contains("1980s", h.Tags);
    }

    [Fact]
    public void Year_folder_sets_the_year_and_the_decade_follows_the_year_not_the_parent_folder()
    {
        ParsedName p = FilenameParser.Parse(@"G:\Commercials\90s\2003 Commercials\Some Brand - a spot.mkv");
        Assert.Equal(2003, p.Year);
        Assert.Contains("2000s", p.Tags);
        Assert.DoesNotContain("1990s", p.Tags);
    }

    [Fact]
    public void Film_trailers_are_commercials_tagged_trailer()
    {
        ParsedName p = FilenameParser.Parse(@"G:\Commercials\90s\Film Trailers\Apollo 13 - film trailer, Exciting and Moving.mkv");
        Assert.Equal(MediaKind.Commercial, p.Kind);
        Assert.Contains("trailer", p.Tags);
        Assert.Contains("category:film-trailers", p.Tags);
        Assert.Contains("brand:apollo-13", p.Tags); // the film is the brand
    }

    [Theory]
    [InlineData("ABC NYPD Blue all new episode Tuesday 10-9c - KGO-TV 7.mkv", true, false)]
    [InlineData("Goof Troop - Kids Club weekdays 8-30am, WFXP FOX 66 Erie.mkv", true, false)]
    [InlineData("Sidney Sheldon's Rage of Angels - miniseries promo.mkv", true, false)]
    [InlineData("MTV - retro TV set in a 50s living room ID.mkv", false, true)]
    [InlineData("Nickelodeon SNICK bumper - projections on a building, orange couch on the beach.mkv", false, false)]
    [InlineData("NBC - Sunday Night at the Movies bumper (3).mkv", true, false)]
    public void Bumper_folder_files_are_bumpers_with_promo_or_station_id_tags(string name, bool promo, bool stationId)
    {
        ParsedName p = FilenameParser.Parse(@"G:\Commercials\90s\Bumpers and Promos\" + name);
        Assert.Equal(MediaKind.Bumper, p.Kind);
        Assert.Equal(promo, p.Tags.Contains("promo"));
        Assert.Equal(stationId, p.Tags.Contains("station-id"));
    }

    [Fact]
    public void Bumper_with_the_word_bumper_and_no_schedule_words_is_tagged_bumper()
    {
        ParsedName p = FilenameParser.Parse(@"G:\Commercials\90s\Bumpers and Promos\Fox Kids Network bumper - couch, the cat, dog and aliens.mkv");
        Assert.Contains("bumper", p.Tags);
        Assert.DoesNotContain("promo", p.Tags);
        Assert.Contains("network:fox-kids-network-bumper", p.Tags);
    }

    [Fact]
    public void Decade_in_a_product_name_does_not_override_the_folder_decade()
    {
        ParsedName p = FilenameParser.Parse(@"G:\Commercials\90s\Bumpers and Promos\MTV - retro TV set in a 50s living room ID.mkv");
        Assert.Contains("1990s", p.Tags);
        Assert.DoesNotContain("1950s", p.Tags);
    }
}
