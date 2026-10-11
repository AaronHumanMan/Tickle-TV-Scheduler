using TickleTV.Scheduler.Core.Catalog;

namespace TickleTV.Scheduler.Tests;

public class PoolQueryTests
{
    private static CatalogEntry Entry(MediaKind kind, int? year, long durationMs = 30_000, string? show = null, params string[] tags) => new()
    {
        Item = new MediaItem
        {
            Id = 1, ContentHash = "h", Path = "/x.mp4", FileSize = 1, ModifiedUtc = DateTimeOffset.UnixEpoch,
            Kind = kind, KindSource = FactSource.Filename, Year = year, DurationMs = durationMs, ProbedUtc = DateTimeOffset.UnixEpoch,
        },
        Episode = show is null ? null : new EpisodeInfo { Show = show, Source = FactSource.Filename },
        Chapters = [],
        Tags = tags.Select(t => new Tag(t, FactSource.Manual)).ToList(),
    };

    [Fact]
    public void Kind_and_year_range()
    {
        var q = PoolQuery.Parse("kind = commercial AND year between 1988 and 1994");
        Assert.True(q.Matches(Entry(MediaKind.Commercial, 1991)));
        Assert.False(q.Matches(Entry(MediaKind.Commercial, 2004)));
        Assert.False(q.Matches(Entry(MediaKind.Bumper, 1991)));
    }

    [Fact]
    public void Not_excludes_a_tag()
    {
        var q = PoolQuery.Parse("kind = commercial AND NOT tag:late-night");
        Assert.True(q.Matches(Entry(MediaKind.Commercial, 1991)));
        Assert.False(q.Matches(Entry(MediaKind.Commercial, 1991, 30_000, null, "late-night")));
    }

    [Fact]
    public void Era_within_uses_the_airing_episode_year()
    {
        var q = PoolQuery.Parse("kind = commercial AND era within 3");
        var ctx = new QueryContext(1991);
        Assert.True(q.Matches(Entry(MediaKind.Commercial, 1993), ctx));
        Assert.False(q.Matches(Entry(MediaKind.Commercial, 1998), ctx));
        Assert.False(q.Matches(Entry(MediaKind.Commercial, 1991))); // no context: cannot match
    }

    [Fact]
    public void Duration_and_show_terms()
    {
        Assert.True(PoolQuery.Parse("duration <= 60s").Matches(Entry(MediaKind.Commercial, 1991, 45_000)));
        Assert.False(PoolQuery.Parse("duration <= 30s").Matches(Entry(MediaKind.Commercial, 1991, 45_000)));
        Assert.True(PoolQuery.Parse("show = tng").Matches(Entry(MediaKind.Episode, 1990, 1, "TNG")));
    }

    [Fact]
    public void Bad_term_is_rejected_with_a_message()
    {
        var ex = Assert.Throws<FormatException>(() => PoolQuery.Parse("colour = blue"));
        Assert.Contains("colour = blue", ex.Message);
    }

    [Fact]
    public void Or_joins_groups_and_binds_looser_than_and()
    {
        var q = PoolQuery.Parse("kind = commercial AND NOT tag:trailer OR kind = bumper AND tag:promo");
        Assert.True(q.Matches(Entry(MediaKind.Commercial, 1991)));
        Assert.False(q.Matches(Entry(MediaKind.Commercial, 1991, 30_000, null, "trailer")));
        Assert.True(q.Matches(Entry(MediaKind.Bumper, 1991, 30_000, null, "promo")));
        Assert.False(q.Matches(Entry(MediaKind.Bumper, 1991)));
    }
}
