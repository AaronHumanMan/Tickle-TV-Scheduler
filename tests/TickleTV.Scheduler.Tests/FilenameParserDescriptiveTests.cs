using TickleTV.Scheduler.Core.Catalog;

namespace TickleTV.Scheduler.Tests;

/// <summary>Descriptive commercial names as found in a real "90s/Uncategorized" folder.</summary>
public class FilenameParserDescriptiveTests
{
    private const string Root = @"G:\Commercials\90s\Uncategorized\";

    [Fact]
    public void Numbered_commercial_rip_is_a_commercial_not_an_episode()
    {
        ParsedName p = FilenameParser.Parse(Root + "90s Commercials - S02.E61.mp4");
        Assert.Equal(MediaKind.Commercial, p.Kind);
        Assert.Null(p.Show);
        Assert.Contains("1990s", p.Tags);
    }

    [Fact]
    public void Brand_before_the_dash_becomes_a_brand_tag()
    {
        ParsedName p = FilenameParser.Parse(Root + "Ross Perot - United We Stand America, 1-800-652-3838.mkv");
        Assert.Equal(MediaKind.Commercial, p.Kind);
        Assert.Equal(FactSource.Folder, p.KindSource);
        Assert.Contains("brand:ross-perot", p.Tags);
        Assert.Null(p.Year); // no year in the name; the folder only gives the decade
        Assert.Contains("1990s", p.Tags);
    }

    [Theory]
    [InlineData("Watch advert - brand not shown.mkv")]
    [InlineData("Night highway footage - advertiser not shown.mkv")]
    [InlineData("Unidentified sports coupe - man in white suit with guitar, ALEGRE plate, no end card.mkv")]
    [InlineData("Zack - animated song Believe in Yourself, Take Time to Prepare, no advertiser shown.mkv")]
    [InlineData("drunk driving PSA - the numbers 5 35 67 93 and Sally Wood's driver licence, no sponsor card.mkv")]
    public void Unbranded_markers_tag_the_clip_and_never_produce_a_brand(string name)
    {
        ParsedName p = FilenameParser.Parse(Root + name);
        Assert.Equal(MediaKind.Commercial, p.Kind);
        Assert.Contains("unbranded", p.Tags);
        Assert.DoesNotContain(p.Tags, t => t.StartsWith("brand:"));
    }

    [Fact]
    public void Psa_is_a_commercial_with_a_psa_tag()
    {
        ParsedName p = FilenameParser.Parse(Root + "drunk driving PSA - the numbers 5 35 67 93 and Sally Wood's driver licence, no sponsor card.mkv");
        Assert.Contains("psa", p.Tags);
        Assert.Null(p.Year); // "5 35 67 93" are not years
    }

    [Theory]
    [InlineData("Nickelodeon show end credits, Benny Hester Productions and MTV Networks, likely Roundhouse - not an advert.mkv")]
    [InlineData("Dragon Warrior opening titles - not an advert.mkv")]
    [InlineData("Roundhouse end credits, Benny Hester Productions and MTV Networks - not an advert (2).mkv")]
    [InlineData("Female presenter to camera on a glass-block set after the NBC Coming Attractions card - not an advert.mkv")]
    public void Not_an_advert_is_kind_other_so_no_pool_picks_it(string name)
    {
        ParsedName p = FilenameParser.Parse(Root + name);
        Assert.Equal(MediaKind.Other, p.Kind);
        Assert.Contains("not-an-advert", p.Tags);
    }

    [Fact]
    public void Several_years_in_a_name_set_no_year()
    {
        ParsedName p = FilenameParser.Parse(Root + "News retrospective titles - 1985 Hurricane Gloria, 1987 Flood, 1990 Fire, no station shown.mkv");
        Assert.Null(p.Year);
    }

    [Fact]
    public void Phone_numbers_and_prices_are_not_years()
    {
        ParsedName p = FilenameParser.Parse(Root + "Leslie Nielsen's Bad Golf Made Easier home video - $19.98, 1-800-862-1000, Omaha NE.mkv");
        Assert.Null(p.Year);
        Assert.Contains("brand:leslie-nielsen-s-bad-golf-made-easier-home-video", p.Tags);
    }

    [Fact]
    public void Decade_folder_gives_a_decade_tag_for_any_decade()
    {
        Assert.Contains("1980s", FilenameParser.Parse(@"G:\Commercials\80s\x - y.mkv").Tags);
        Assert.Contains("2000s", FilenameParser.Parse(@"G:\Commercials\00s\x - y.mkv").Tags);
        Assert.Contains("1990s", FilenameParser.Parse(@"G:\Commercials\1990s\x - y.mkv").Tags);
    }
}
