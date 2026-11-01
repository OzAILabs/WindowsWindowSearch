using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WindowSearcher.Core;

public enum DocKind
{
    Window = 0,
    Tab = 1,
}

/// <summary>
/// One indexed window or browser tab. A null <see cref="Body"/> or <see cref="Thumb"/> keeps the
/// stored value; a null <see cref="ViewedAt"/> means the user did not look at it (background read),
/// so its recency is left unchanged.
/// </summary>
public sealed record DocRecord(
    string Key,
    DocKind Kind,
    long Hwnd,
    int Pid,
    string Process,
    string Title,
    string? Url,
    string? Body,
    string? Thumb,
    DateTimeOffset? ViewedAt);

/// <summary>Lightweight row (no body) used to restore previews and recency for live tabs.</summary>
public sealed record DocSummary(
    string Key,
    DocKind Kind,
    long Hwnd,
    int Pid,
    string Title,
    string? Url,
    string? Thumb,
    DateTimeOffset LastSeen);

public sealed record DocHit(
    string Key,
    DocKind Kind,
    long Hwnd,
    int Pid,
    string Process,
    string Title,
    string? Url,
    string? Thumb,
    DateTimeOffset LastSeen,
    string Snippet,
    double Bm25);

/// <param name="ContentChanged">Title, URL or body changed, so the text was re-indexed.</param>
/// <param name="PreviousThumb">Thumbnail file that was replaced and can be deleted.</param>
/// <param name="Summary">The row as stored after the update.</param>
public sealed record UpsertResult(bool ContentChanged, string? PreviousThumb, DocSummary Summary);

/// <summary>
/// SQLite FTS5 index of window and tab text. One instance per thread; SQLite runs in WAL
/// mode so the UI thread can search while the background worker writes.
/// </summary>
public sealed class SearchIndex : IDisposable
{
    public const char HighlightStart = '\u0001';
    public const char HighlightEnd = '\u0002';

    private readonly SqliteConnection _db;

    /// <param name="dbPath">The database file.</param>
    /// <param name="key">Encrypts the database. Null leaves it unencrypted.</param>
    public SearchIndex(string dbPath, string? key = null)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            Password = key ?? string.Empty,
        }.ToString());
        try
        {
            _db.Open();
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=2000;");
            EnsureSchema();
        }
        catch
        {
            _db.Dispose(); // don't keep the file locked: callers may delete it
            throw;
        }
    }

    /// <summary>True for a database file written without encryption (releases before 1.0.14).</summary>
    public static bool IsUnencrypted(string dbPath)
    {
        if (!File.Exists(dbPath)) return false;
        ReadOnlySpan<byte> plainHeader = "SQLite format 3"u8;
        using var file = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[plainHeader.Length];
        return file.Read(header) == header.Length && header.SequenceEqual(plainHeader);
    }

    /// <summary>True if the file doesn't exist yet or this key opens it.</summary>
    public static bool CanOpen(string dbPath, string key)
    {
        if (!File.Exists(dbPath)) return true;
        try
        {
            using var index = new SearchIndex(dbPath, key);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS docs(
              id        INTEGER PRIMARY KEY,
              key       TEXT NOT NULL UNIQUE,
              kind      INTEGER NOT NULL,
              hwnd      INTEGER NOT NULL,
              pid       INTEGER NOT NULL,
              process   TEXT NOT NULL,
              title     TEXT NOT NULL,
              url       TEXT,
              body      TEXT,
              hash      TEXT NOT NULL,
              thumb     TEXT,
              last_seen INTEGER NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts USING fts5(
              title, url, body,
              content='docs', content_rowid='id',
              tokenize='porter unicode61 remove_diacritics 2',
              prefix='2 3');
            CREATE TRIGGER IF NOT EXISTS docs_ai AFTER INSERT ON docs BEGIN
              INSERT INTO docs_fts(rowid, title, url, body) VALUES (new.id, new.title, new.url, new.body);
            END;
            CREATE TRIGGER IF NOT EXISTS docs_ad AFTER DELETE ON docs BEGIN
              INSERT INTO docs_fts(docs_fts, rowid, title, url, body) VALUES ('delete', old.id, old.title, old.url, old.body);
            END;
            CREATE TRIGGER IF NOT EXISTS docs_au AFTER UPDATE OF title, url, body ON docs BEGIN
              INSERT INTO docs_fts(docs_fts, rowid, title, url, body) VALUES ('delete', old.id, old.title, old.url, old.body);
              INSERT INTO docs_fts(rowid, title, url, body) VALUES (new.id, new.title, new.url, new.body);
            END;
            """);
    }

    /// <summary>
    /// Inserts or updates a document. Text is only re-indexed when title, URL or body changed.
    /// </summary>
    public UpsertResult Upsert(DocRecord doc)
    {
        long? id = null;
        string? oldHash = null, oldThumb = null, oldBody = null;
        long oldSeen = 0;
        using (var q = Cmd("SELECT id, hash, thumb, body, last_seen FROM docs WHERE key = $key", ("$key", doc.Key)))
        using (var r = q.ExecuteReader())
        {
            if (r.Read())
            {
                id = r.GetInt64(0);
                oldHash = r.GetString(1);
                oldThumb = r.IsDBNull(2) ? null : r.GetString(2);
                oldBody = r.IsDBNull(3) ? null : r.GetString(3);
                oldSeen = r.GetInt64(4);
            }
        }

        var body = doc.Body ?? oldBody;
        var thumb = doc.Thumb ?? oldThumb;
        var seen = doc.ViewedAt?.ToUnixTimeMilliseconds() ?? oldSeen;
        var hash = Hash(doc.Title, doc.Url, body);
        var summary = new DocSummary(doc.Key, doc.Kind, doc.Hwnd, doc.Pid, doc.Title, doc.Url, thumb,
            DateTimeOffset.FromUnixTimeMilliseconds(seen));

        if (id is null)
        {
            using var ins = Cmd("""
                INSERT INTO docs(key, kind, hwnd, pid, process, title, url, body, hash, thumb, last_seen)
                VALUES ($key, $kind, $hwnd, $pid, $process, $title, $url, $body, $hash, $thumb, $seen)
                """,
                ("$key", doc.Key), ("$kind", (int)doc.Kind), ("$hwnd", doc.Hwnd), ("$pid", doc.Pid),
                ("$process", doc.Process), ("$title", doc.Title), ("$url", doc.Url), ("$body", body),
                ("$hash", hash), ("$thumb", thumb), ("$seen", seen));
            ins.ExecuteNonQuery();
            return new UpsertResult(true, null, summary);
        }

        var changed = oldHash != hash;
        // Only touch the FTS-indexed columns when they changed; the update trigger fires on them.
        var sql = changed
            ? """
              UPDATE docs SET kind=$kind, hwnd=$hwnd, pid=$pid, process=$process, title=$title, url=$url,
                body=$body, hash=$hash, thumb=$thumb, last_seen=$seen WHERE id=$id
              """
            : """
              UPDATE docs SET kind=$kind, hwnd=$hwnd, pid=$pid, process=$process, thumb=$thumb, last_seen=$seen
              WHERE id=$id
              """;
        using var up = Cmd(sql,
            ("$kind", (int)doc.Kind), ("$hwnd", doc.Hwnd), ("$pid", doc.Pid), ("$process", doc.Process),
            ("$title", doc.Title), ("$url", doc.Url), ("$body", body), ("$hash", hash),
            ("$thumb", thumb), ("$seen", seen), ("$id", id.Value));
        up.ExecuteNonQuery();
        var replacedThumb = oldThumb is not null && oldThumb != thumb ? oldThumb : null;
        return new UpsertResult(changed, replacedThumb, summary);
    }

    /// <summary>Full-text search. <paramref name="ftsQuery"/> must come from <see cref="FtsQuery.Build"/>.</summary>
    public List<DocHit> Search(string ftsQuery, int limit = 200)
    {
        var hits = new List<DocHit>();
        using var q = Cmd("""
            SELECT d.key, d.kind, d.hwnd, d.pid, d.process, d.title, d.url, d.thumb, d.last_seen,
                   snippet(docs_fts, -1, char(1), char(2), '…', 18),
                   bm25(docs_fts, 8.0, 3.0, 1.0) AS rank
            FROM docs_fts JOIN docs d ON d.id = docs_fts.rowid
            WHERE docs_fts MATCH $q
            ORDER BY rank
            LIMIT $limit
            """, ("$q", ftsQuery), ("$limit", limit));
        try
        {
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                hits.Add(new DocHit(
                    Key: r.GetString(0),
                    Kind: (DocKind)r.GetInt32(1),
                    Hwnd: r.GetInt64(2),
                    Pid: r.GetInt32(3),
                    Process: r.GetString(4),
                    Title: r.GetString(5),
                    Url: r.IsDBNull(6) ? null : r.GetString(6),
                    Thumb: r.IsDBNull(7) ? null : r.GetString(7),
                    LastSeen: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)),
                    Snippet: r.IsDBNull(9) ? string.Empty : r.GetString(9),
                    Bm25: r.GetDouble(10)));
            }
        }
        catch (SqliteException)
        {
            // Malformed query text; treat as no content hits.
        }
        return hits;
    }

    public List<DocSummary> GetSummaries()
    {
        var list = new List<DocSummary>();
        using var q = Cmd("SELECT key, kind, hwnd, pid, title, url, thumb, last_seen FROM docs");
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DocSummary(
                r.GetString(0), (DocKind)r.GetInt32(1), r.GetInt64(2), r.GetInt32(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7))));
        }
        return list;
    }

    public int Count()
    {
        using var q = Cmd("SELECT COUNT(*) FROM docs");
        return Convert.ToInt32(q.ExecuteScalar());
    }

    /// <summary>Deletes documents by key and returns their thumbnail paths so callers can delete the files.</summary>
    public List<string> Delete(IEnumerable<string> keys)
    {
        var thumbs = new List<string>();
        using var tx = _db.BeginTransaction();
        foreach (var key in keys)
        {
            using (var q = Cmd("SELECT thumb FROM docs WHERE key = $key", ("$key", key)))
            {
                q.Transaction = tx;
                if (q.ExecuteScalar() is string t) thumbs.Add(t);
            }
            using var del = Cmd("DELETE FROM docs WHERE key = $key", ("$key", key));
            del.Transaction = tx;
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return thumbs;
    }

    public void Clear() => Exec("DELETE FROM docs; INSERT INTO docs_fts(docs_fts) VALUES('rebuild');");

    public void Dispose() => _db.Dispose();

    private static string Hash(string title, string? url, string? body)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes($"{title}\u0000{url}\u0000{body}"));
        return Convert.ToHexString(bytes);
    }

    private void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return c;
    }
}
