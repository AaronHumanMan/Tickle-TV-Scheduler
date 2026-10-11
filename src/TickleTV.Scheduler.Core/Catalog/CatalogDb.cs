using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TickleTV.Scheduler.Core.Catalog;

/// <summary>
/// The catalog: one SQLite file holding media facts, parsed episode data, tags, plays, and rotation state.
/// Pools and the engine query it; the scanner fills it.
/// </summary>
public sealed class CatalogDb : IDisposable
{
    private const int CurrentSchema = 2;
    private readonly SqliteConnection _conn;
    private SqliteTransaction? _tx;

    public CatalogDb(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode = WAL;");
        Migrate();
    }

    public static CatalogDb InMemory()
    {
        var db = new CatalogDb(":memory:");
        return db;
    }

    public void Dispose() => _conn.Dispose();

    private void Migrate()
    {
        Exec("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");
        long version = Scalar<long?>("SELECT version FROM schema_version LIMIT 1") ?? 0;
        if (version >= CurrentSchema) return;

        using SqliteTransaction tx = _conn.BeginTransaction();
        if (version < 1)
        {
            Exec("""
                CREATE TABLE media_items (
                    id INTEGER PRIMARY KEY,
                    content_hash TEXT NOT NULL UNIQUE,
                    path TEXT NOT NULL UNIQUE,
                    file_size INTEGER NOT NULL,
                    modified_utc TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    kind_source TEXT NOT NULL,
                    title TEXT,
                    year INTEGER,
                    duration_ms INTEGER NOT NULL,
                    video_codec TEXT,
                    audio_codec TEXT,
                    width INTEGER,
                    height INTEGER,
                    probed_utc TEXT NOT NULL,
                    missing_utc TEXT
                );
                CREATE INDEX ix_media_items_kind ON media_items(kind);
                CREATE TABLE chapters (
                    item_id INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
                    ordinal INTEGER NOT NULL,
                    start_ms INTEGER NOT NULL,
                    end_ms INTEGER NOT NULL,
                    title TEXT,
                    PRIMARY KEY (item_id, ordinal)
                );
                CREATE TABLE episodes (
                    item_id INTEGER PRIMARY KEY REFERENCES media_items(id) ON DELETE CASCADE,
                    show TEXT NOT NULL,
                    season INTEGER,
                    episode INTEGER,
                    air_date TEXT,
                    episode_title TEXT,
                    source TEXT NOT NULL
                );
                CREATE INDEX ix_episodes_show ON episodes(show);
                CREATE TABLE tags (
                    item_id INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
                    tag TEXT NOT NULL,
                    source TEXT NOT NULL,
                    PRIMARY KEY (item_id, tag)
                );
                CREATE INDEX ix_tags_tag ON tags(tag);
                CREATE TABLE plays (
                    id INTEGER PRIMARY KEY,
                    item_id INTEGER NOT NULL REFERENCES media_items(id) ON DELETE CASCADE,
                    channel TEXT NOT NULL,
                    aired_at TEXT NOT NULL,
                    context TEXT
                );
                CREATE INDEX ix_plays_item ON plays(item_id, aired_at);
                CREATE TABLE rotation (
                    show TEXT NOT NULL,
                    channel TEXT NOT NULL,
                    mode TEXT NOT NULL,
                    next_position INTEGER,
                    cycle_json TEXT,
                    PRIMARY KEY (show, channel)
                );
                """, tx);
        }
        if (version < 2)
        {
            Exec("""
                CREATE TABLE categories (
                    slug TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    created_utc TEXT NOT NULL
                );
                """, tx);
        }
        Exec("DELETE FROM schema_version; INSERT INTO schema_version(version) VALUES (@v);", tx, ("@v", CurrentSchema));
        tx.Commit();
    }

    // ---- items ----

    public MediaItem? FindByPath(string path) =>
        Query("SELECT * FROM media_items WHERE path = @p", ReadItem, ("@p", path)).FirstOrDefault();

    public MediaItem? FindByHash(string hash) =>
        Query("SELECT * FROM media_items WHERE content_hash = @h", ReadItem, ("@h", hash)).FirstOrDefault();

    public MediaItem? FindById(long id) =>
        Query("SELECT * FROM media_items WHERE id = @id", ReadItem, ("@id", id)).FirstOrDefault();

    public long Insert(MediaItem item)
    {
        Exec("""
            INSERT INTO media_items (content_hash, path, file_size, modified_utc, kind, kind_source, title, year,
                duration_ms, video_codec, audio_codec, width, height, probed_utc, missing_utc)
            VALUES (@hash, @path, @size, @mod, @kind, @ks, @title, @year, @dur, @vc, @ac, @w, @h, @probed, NULL)
            """, null,
            ("@hash", item.ContentHash), ("@path", item.Path), ("@size", item.FileSize), ("@mod", Iso(item.ModifiedUtc)),
            ("@kind", item.Kind.ToString()), ("@ks", item.KindSource), ("@title", item.Title), ("@year", item.Year),
            ("@dur", item.DurationMs), ("@vc", item.VideoCodec), ("@ac", item.AudioCodec), ("@w", item.Width), ("@h", item.Height),
            ("@probed", Iso(item.ProbedUtc)));
        return Scalar<long>("SELECT last_insert_rowid()");
    }

    /// <summary>Re-probe result for an existing item. Kind is kept when a person set it.</summary>
    public void UpdateProbe(long id, MediaItem fresh)
    {
        Exec("""
            UPDATE media_items SET path = @path, file_size = @size, modified_utc = @mod, content_hash = @hash,
                duration_ms = @dur, video_codec = @vc, audio_codec = @ac, width = @w, height = @h, probed_utc = @probed,
                missing_utc = NULL,
                kind = CASE WHEN kind_source = 'manual' THEN kind ELSE @kind END,
                kind_source = CASE WHEN kind_source = 'manual' THEN kind_source ELSE @ks END,
                title = CASE WHEN kind_source = 'manual' THEN title ELSE @title END,
                year = COALESCE(@year, year)
            WHERE id = @id
            """, null,
            ("@id", id), ("@path", fresh.Path), ("@size", fresh.FileSize), ("@mod", Iso(fresh.ModifiedUtc)), ("@hash", fresh.ContentHash),
            ("@dur", fresh.DurationMs), ("@vc", fresh.VideoCodec), ("@ac", fresh.AudioCodec), ("@w", fresh.Width), ("@h", fresh.Height),
            ("@probed", Iso(fresh.ProbedUtc)), ("@kind", fresh.Kind.ToString()), ("@ks", fresh.KindSource), ("@title", fresh.Title), ("@year", fresh.Year));
    }

    /// <summary>Refreshes the parsed facts of an item whose kind was not set by hand.</summary>
    public void UpdateParsedFacts(long id, MediaKind kind, string kindSource, string? title, int? year) =>
        Exec("UPDATE media_items SET kind = @k, kind_source = @ks, title = @t, year = COALESCE(@y, year) WHERE id = @id AND kind_source <> 'manual'", null,
            ("@id", id), ("@k", kind.ToString()), ("@ks", kindSource), ("@t", title), ("@y", year));

    public void UpdatePath(long id, string newPath, long size, DateTimeOffset modifiedUtc) =>
        Exec("UPDATE media_items SET path = @p, file_size = @s, modified_utc = @m, missing_utc = NULL WHERE id = @id", null,
            ("@id", id), ("@p", newPath), ("@s", size), ("@m", Iso(modifiedUtc)));

    public void SetKind(long id, MediaKind kind, string source) =>
        Exec("UPDATE media_items SET kind = @k, kind_source = @s WHERE id = @id", null, ("@id", id), ("@k", kind.ToString()), ("@s", source));

    public void MarkMissing(long id, DateTimeOffset when) =>
        Exec("UPDATE media_items SET missing_utc = @w WHERE id = @id AND missing_utc IS NULL", null, ("@id", id), ("@w", Iso(when)));

    public IReadOnlyList<MediaItem> ListItems(MediaKind? kind = null, bool includeMissing = false)
    {
        string sql = "SELECT * FROM media_items WHERE 1=1";
        if (kind is not null) sql += " AND kind = @kind";
        if (!includeMissing) sql += " AND missing_utc IS NULL";
        sql += " ORDER BY path";
        return Query(sql, ReadItem, ("@kind", kind?.ToString()));
    }

    /// <summary>Items whose paths start with any of the given roots, for missing-file detection.</summary>
    public IReadOnlyList<MediaItem> ListUnderRoots(IEnumerable<string> roots)
    {
        var all = ListItems(includeMissing: true);
        var normalized = roots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToList();
        return all.Where(i => normalized.Any(r => i.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // ---- chapters ----

    public void ReplaceChapters(long itemId, IEnumerable<CatalogChapter> chapters)
    {
        Exec("DELETE FROM chapters WHERE item_id = @id", null, ("@id", itemId));
        foreach (CatalogChapter c in chapters)
            Exec("INSERT INTO chapters (item_id, ordinal, start_ms, end_ms, title) VALUES (@id, @o, @s, @e, @t)", null,
                ("@id", itemId), ("@o", c.Ordinal), ("@s", c.StartMs), ("@e", c.EndMs), ("@t", c.Title));
    }

    public IReadOnlyList<CatalogChapter> GetChapters(long itemId) =>
        Query("SELECT ordinal, start_ms, end_ms, title FROM chapters WHERE item_id = @id ORDER BY ordinal",
            r => new CatalogChapter(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3)),
            ("@id", itemId));

    // ---- episodes ----

    /// <summary>Writes parsed episode data unless a person already set it.</summary>
    public void UpsertEpisode(long itemId, EpisodeInfo info)
    {
        string? existingSource = Scalar<string?>("SELECT source FROM episodes WHERE item_id = @id", ("@id", itemId));
        if (existingSource == FactSource.Manual && info.Source != FactSource.Manual) return;
        Exec("""
            INSERT INTO episodes (item_id, show, season, episode, air_date, episode_title, source)
            VALUES (@id, @show, @s, @e, @d, @t, @src)
            ON CONFLICT(item_id) DO UPDATE SET show = excluded.show, season = excluded.season, episode = excluded.episode,
                air_date = excluded.air_date, episode_title = excluded.episode_title, source = excluded.source
            """, null,
            ("@id", itemId), ("@show", info.Show), ("@s", info.Season), ("@e", info.Episode),
            ("@d", info.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("@t", info.EpisodeTitle), ("@src", info.Source));
    }

    public EpisodeInfo? GetEpisode(long itemId) =>
        Query("SELECT show, season, episode, air_date, episode_title, source FROM episodes WHERE item_id = @id", ReadEpisode, ("@id", itemId)).FirstOrDefault();

    public IReadOnlyList<string> ListShows() =>
        Query("SELECT show FROM episodes GROUP BY show COLLATE NOCASE ORDER BY show COLLATE NOCASE", r => r.GetString(0));

    /// <summary>Episodes of a show in broadcast order: air date first, then season and episode, then path.</summary>
    public IReadOnlyList<CatalogEntry> EpisodesOf(string show)
    {
        var ids = Query("""
            SELECT e.item_id FROM episodes e JOIN media_items m ON m.id = e.item_id
            WHERE e.show = @show COLLATE NOCASE AND m.missing_utc IS NULL
            ORDER BY e.air_date IS NULL, e.air_date, e.season, e.episode, m.path
            """, r => r.GetInt64(0), ("@show", show));
        return ids.Select(id => GetEntry(id)!).ToList();
    }

    // ---- tags ----

    public void AddTag(long itemId, string tag, string source) =>
        Exec("""
            INSERT INTO tags (item_id, tag, source) VALUES (@id, @t, @s)
            ON CONFLICT(item_id, tag) DO UPDATE SET source = CASE WHEN tags.source = 'manual' THEN tags.source ELSE excluded.source END
            """, null, ("@id", itemId), ("@t", NormalizeTag(tag)), ("@s", source));

    public void RemoveTag(long itemId, string tag) =>
        Exec("DELETE FROM tags WHERE item_id = @id AND tag = @t", null, ("@id", itemId), ("@t", NormalizeTag(tag)));

    /// <summary>Drops rule-generated tags before a rescan re-derives them; manual tags stay.</summary>
    public void ClearAutoTags(long itemId) =>
        Exec("DELETE FROM tags WHERE item_id = @id AND source <> 'manual'", null, ("@id", itemId));

    public IReadOnlyList<Tag> GetTags(long itemId) =>
        Query("SELECT tag, source FROM tags WHERE item_id = @id ORDER BY tag", r => new Tag(r.GetString(0), r.GetString(1)), ("@id", itemId));

    public static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant().Replace(' ', '-');

    // ---- review queue ----

    /// <summary>
    /// Items a person should look at: the scan could not tell what they are, or could not label them well.
    /// Each item carries the plain-language reason it is listed.
    /// </summary>
    private const string AttentionReasonSql = """
        CASE
            WHEN m.kind = 'Unknown' THEN 'Unknown'
            WHEN m.kind = 'Episode' AND (e.item_id IS NULL OR e.season IS NULL OR e.episode IS NULL) THEN 'EpisodeDetails'
            WHEN m.kind = 'Commercial' AND NOT EXISTS (SELECT 1 FROM tags t WHERE t.item_id = m.id AND t.tag LIKE 'brand:%')
                 AND NOT EXISTS (SELECT 1 FROM tags t WHERE t.item_id = m.id AND t.tag = 'brand-not-needed') THEN 'NoBrand'
            WHEN m.kind = 'Commercial' AND NOT EXISTS (SELECT 1 FROM tags t WHERE t.item_id = m.id AND t.tag LIKE 'category:%') THEN 'NoCategory'
            WHEN m.kind = 'Bumper' AND NOT EXISTS (SELECT 1 FROM tags t WHERE t.item_id = m.id AND t.tag IN ('promo', 'station-id', 'break-card')) THEN 'BumperUnsorted'
            WHEN m.kind = 'Episode' AND (SELECT COUNT(*) FROM chapters c WHERE c.item_id = m.id) <= 1 THEN 'NoChapters'
        END
        """;

    public IReadOnlyList<(CatalogEntry Entry, AttentionReason Reason)> ListNeedingAttention(AttentionReason? only = null, int limit = 200, int offset = 0)
    {
        var rows = Query($"""
            SELECT id, reason FROM (
                SELECT m.id AS id, m.path AS path, {AttentionReasonSql} AS reason
                FROM media_items m LEFT JOIN episodes e ON e.item_id = m.id
                WHERE m.missing_utc IS NULL
            )
            WHERE reason IS NOT NULL AND ((@only IS NULL AND reason <> 'NoChapters') OR reason = @only)
            ORDER BY CASE reason WHEN 'Unknown' THEN 0 WHEN 'EpisodeDetails' THEN 1 WHEN 'BumperUnsorted' THEN 2 WHEN 'NoCategory' THEN 3 WHEN 'NoBrand' THEN 4 ELSE 5 END, path
            LIMIT @limit OFFSET @offset
            """, r => (r.GetInt64(0), r.GetString(1)), ("@only", only?.ToString()), ("@limit", limit), ("@offset", offset));
        return rows.Select(r => (GetEntry(r.Item1)!, Enum.Parse<AttentionReason>(r.Item2))).ToList();
    }

    public IReadOnlyDictionary<AttentionReason, int> CountNeedingAttention()
    {
        var counts = Enum.GetValues<AttentionReason>().ToDictionary(r => r, _ => 0);
        foreach ((string reason, long n) in Query($"""
            SELECT reason, COUNT(*) FROM (
                SELECT {AttentionReasonSql} AS reason
                FROM media_items m LEFT JOIN episodes e ON e.item_id = m.id
                WHERE m.missing_utc IS NULL
            )
            WHERE reason IS NOT NULL GROUP BY reason
            """, r => (r.GetString(0), r.GetInt64(1))))
            counts[Enum.Parse<AttentionReason>(reason)] = (int)n;
        return counts;
    }

    public IReadOnlyList<string> ListTagValues(string prefix) =>
        Query("SELECT DISTINCT substr(tag, @n) FROM tags WHERE tag LIKE @p ORDER BY 1",
            r => r.GetString(0), ("@n", prefix.Length + 1), ("@p", prefix + "%"));

    public int CountItems() => (int)Scalar<long>("SELECT COUNT(*) FROM media_items WHERE missing_utc IS NULL");

    public IReadOnlyDictionary<MediaKind, int> CountByKind()
    {
        var result = new Dictionary<MediaKind, int>();
        foreach ((string kind, long n) in Query("SELECT kind, COUNT(*) FROM media_items WHERE missing_utc IS NULL GROUP BY kind", r => (r.GetString(0), r.GetInt64(1))))
            result[Enum.Parse<MediaKind>(kind)] = (int)n;
        return result;
    }

    // ---- browse and search ----

    public sealed record SearchFilter
    {
        public string? Text { get; init; }
        public MediaKind? Kind { get; init; }
        public string? Show { get; init; }
        public string? Category { get; init; }
        public string? Tag { get; init; }
        public bool IncludeMissing { get; init; }
    }

    private static (string Where, (string, object?)[] Args) SearchWhere(SearchFilter f)
    {
        var where = new List<string> { "1=1" };
        var args = new List<(string, object?)>();
        if (!f.IncludeMissing) where.Add("m.missing_utc IS NULL");
        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            where.Add("(m.title LIKE @text OR m.path LIKE @text OR e.episode_title LIKE @text)");
            args.Add(("@text", "%" + f.Text.Trim() + "%"));
        }
        if (f.Kind is { } k) { where.Add("m.kind = @kind"); args.Add(("@kind", k.ToString())); }
        if (!string.IsNullOrWhiteSpace(f.Show)) { where.Add("e.show = @show COLLATE NOCASE"); args.Add(("@show", f.Show.Trim())); }
        if (!string.IsNullOrWhiteSpace(f.Category))
        {
            where.Add("EXISTS (SELECT 1 FROM tags t WHERE t.item_id = m.id AND t.tag = @cat)");
            args.Add(("@cat", "category:" + NormalizeTag(f.Category)));
        }
        if (!string.IsNullOrWhiteSpace(f.Tag))
        {
            where.Add("EXISTS (SELECT 1 FROM tags t WHERE t.item_id = m.id AND t.tag = @tag)");
            args.Add(("@tag", NormalizeTag(f.Tag)));
        }
        return (string.Join(" AND ", where), args.ToArray());
    }

    public int CountSearch(SearchFilter f)
    {
        (string where, var args) = SearchWhere(f);
        return (int)Scalar<long>($"SELECT COUNT(*) FROM media_items m LEFT JOIN episodes e ON e.item_id = m.id WHERE {where}", args);
    }

    public IReadOnlyList<CatalogEntry> Search(SearchFilter f, int limit = 50, int offset = 0)
    {
        (string where, var args) = SearchWhere(f);
        var ids = Query($"""
            SELECT m.id FROM media_items m LEFT JOIN episodes e ON e.item_id = m.id
            WHERE {where}
            ORDER BY e.show COLLATE NOCASE, e.season, e.episode, m.title COLLATE NOCASE, m.path
            LIMIT @limit OFFSET @offset
            """, r => r.GetInt64(0), [.. args, ("@limit", limit), ("@offset", offset)]);
        return ids.Select(id => GetEntry(id)!).ToList();
    }

    public IReadOnlyList<string> ListAllTags() =>
        Query("SELECT DISTINCT tag FROM tags WHERE tag NOT LIKE 'category:%' AND tag NOT LIKE 'brand:%' AND tag NOT LIKE 'show:%' AND tag NOT LIKE 'network:%' ORDER BY tag", r => r.GetString(0));

    // ---- categories ----

    /// <summary>A commercial category a person can pick from. Slugs match the category:... tags.</summary>
    public sealed record Category(string Slug, string Name, int Items);

    public void AddCategory(string name)
    {
        string slug = NormalizeTag(name);
        if (slug.Length == 0) return;
        Exec("INSERT INTO categories (slug, name, created_utc) VALUES (@s, @n, @c) ON CONFLICT(slug) DO NOTHING", null,
            ("@s", slug), ("@n", name.Trim()), ("@c", Iso(DateTimeOffset.UtcNow)));
    }

    public void RemoveCategory(string slug) =>
        Exec("DELETE FROM categories WHERE slug = @s", null, ("@s", NormalizeTag(slug)));

    /// <summary>Every category: the ones people added plus the ones the scan derived from folders, with item counts.</summary>
    public IReadOnlyList<Category> ListCategories() =>
        Query("""
            SELECT slug, name, (SELECT COUNT(*) FROM tags t WHERE t.tag = 'category:' || slug) AS items FROM (
                SELECT slug, name FROM categories
                UNION
                SELECT substr(tag, 10) AS slug, substr(tag, 10) AS name FROM tags
                WHERE tag LIKE 'category:%' AND substr(tag, 10) NOT IN (SELECT slug FROM categories)
            )
            ORDER BY name COLLATE NOCASE
            """, r => new Category(r.GetString(0), PrettyName(r.GetString(1)), r.GetInt32(2)));

    /// <summary>A derived category keeps only its slug, so show it as words: "health-beauty-personal-care" becomes "Health Beauty Personal Care".</summary>
    private static string PrettyName(string nameOrSlug)
    {
        if (nameOrSlug.Contains(' ') || nameOrSlug.Any(char.IsUpper)) return nameOrSlug;
        return string.Join(' ', nameOrSlug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    // ---- entries ----

    public CatalogEntry? GetEntry(long id)
    {
        MediaItem? item = FindById(id);
        if (item is null) return null;
        return new CatalogEntry
        {
            Item = item,
            Episode = GetEpisode(id),
            Chapters = GetChapters(id),
            Tags = GetTags(id),
        };
    }

    // ---- plays ----

    public void RecordPlay(long itemId, string channel, DateTimeOffset airedAt, string? context) =>
        Exec("INSERT INTO plays (item_id, channel, aired_at, context) VALUES (@id, @c, @a, @x)", null,
            ("@id", itemId), ("@c", channel), ("@a", Iso(airedAt)), ("@x", context));

    /// <summary>Slot (episode) plays for a channel from <paramref name="since"/> on, in air order. Used to keep a rebuild's episode picks stable.</summary>
    public IReadOnlyList<(long ItemId, DateTimeOffset AiredAt, string Context)> ListSlotPlays(string channel, DateTimeOffset since) =>
        Query("SELECT item_id, aired_at, context FROM plays WHERE channel = @c AND aired_at >= @s AND context LIKE 'slot %' ORDER BY aired_at",
            r => (r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2)),
            ("@c", channel), ("@s", Iso(since)));

    /// <summary>Removes a channel's plays from <paramref name="since"/> on: a rebuild replaces that window's history.</summary>
    public void DeletePlays(string channel, DateTimeOffset since) =>
        Exec("DELETE FROM plays WHERE channel = @c AND aired_at >= @s", null, ("@c", channel), ("@s", Iso(since)));

    public DateTimeOffset? LastPlayed(long itemId, string? channel = null)
    {
        string sql = "SELECT MAX(aired_at) FROM plays WHERE item_id = @id" + (channel is null ? "" : " AND channel = @c");
        string? s = Scalar<string?>(sql, ("@id", itemId), ("@c", channel));
        return s is null ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
    }

    // ---- rotation ----

    public (string Mode, int? NextPosition, string? CycleJson)? GetRotation(string show, string channel) =>
        Query("SELECT mode, next_position, cycle_json FROM rotation WHERE show = @s COLLATE NOCASE AND channel = @c",
            r => (r.GetString(0), r.IsDBNull(1) ? (int?)null : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2)),
            ("@s", show), ("@c", channel)).Cast<(string, int?, string?)?>().FirstOrDefault();

    public void SetRotation(string show, string channel, string mode, int? nextPosition, string? cycleJson) =>
        Exec("""
            INSERT INTO rotation (show, channel, mode, next_position, cycle_json) VALUES (@s, @c, @m, @n, @j)
            ON CONFLICT(show, channel) DO UPDATE SET mode = excluded.mode, next_position = excluded.next_position, cycle_json = excluded.cycle_json
            """, null, ("@s", show), ("@c", channel), ("@m", mode), ("@n", nextPosition), ("@j", cycleJson));

    // ---- plumbing ----

    /// <summary>Starts a unit of work. A build runs inside one so a dry run can roll back rotation and plays.</summary>
    public void BeginWork()
    {
        if (_tx is not null) throw new InvalidOperationException("Work already in progress.");
        _tx = _conn.BeginTransaction();
    }

    public void CommitWork()
    {
        _tx?.Commit();
        _tx?.Dispose();
        _tx = null;
    }

    public void RollbackWork()
    {
        _tx?.Rollback();
        _tx?.Dispose();
        _tx = null;
    }

    private static MediaItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        ContentHash = r.GetString(r.GetOrdinal("content_hash")),
        Path = r.GetString(r.GetOrdinal("path")),
        FileSize = r.GetInt64(r.GetOrdinal("file_size")),
        ModifiedUtc = ParseIso(r.GetString(r.GetOrdinal("modified_utc"))),
        Kind = Enum.Parse<MediaKind>(r.GetString(r.GetOrdinal("kind"))),
        KindSource = r.GetString(r.GetOrdinal("kind_source")),
        Title = Str(r, "title"),
        Year = r.IsDBNull(r.GetOrdinal("year")) ? null : r.GetInt32(r.GetOrdinal("year")),
        DurationMs = r.GetInt64(r.GetOrdinal("duration_ms")),
        VideoCodec = Str(r, "video_codec"),
        AudioCodec = Str(r, "audio_codec"),
        Width = r.IsDBNull(r.GetOrdinal("width")) ? null : r.GetInt32(r.GetOrdinal("width")),
        Height = r.IsDBNull(r.GetOrdinal("height")) ? null : r.GetInt32(r.GetOrdinal("height")),
        ProbedUtc = ParseIso(r.GetString(r.GetOrdinal("probed_utc"))),
        MissingUtc = Str(r, "missing_utc") is { } m ? ParseIso(m) : null,
    };

    private static EpisodeInfo ReadEpisode(SqliteDataReader r) => new()
    {
        Show = r.GetString(0),
        Season = r.IsDBNull(1) ? null : r.GetInt32(1),
        Episode = r.IsDBNull(2) ? null : r.GetInt32(2),
        AirDate = r.IsDBNull(3) ? null : DateOnly.ParseExact(r.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        EpisodeTitle = r.IsDBNull(4) ? null : r.GetString(4),
        Source = r.GetString(5),
    };

    private static string? Str(SqliteDataReader r, string col)
    {
        int i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

    private void Exec(string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.Transaction = tx ?? _tx;
        cmd.CommandText = sql;
        foreach ((string name, object? value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string, object?)[] args)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        foreach ((string name, object? value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        object? result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return default!;
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        foreach ((string name, object? value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using SqliteDataReader reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }
}
