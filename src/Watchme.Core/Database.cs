using Microsoft.Data.Sqlite;

namespace Watchme.Core;

/// <summary>The SQLite event store. Not thread-safe: the agent uses it from its single writer loop.</summary>
public sealed class Database : IDisposable
{
    public const int SchemaVersion = 2;

    // v2: scraped events carry an external id, so re-reading an overlapping window of an
    // app's own data never creates duplicates.
    private const string SchemaV2 = """
        ALTER TABLE events ADD COLUMN ext_id TEXT;
        CREATE UNIQUE INDEX ux_events_source_ext_id ON events (source, ext_id) WHERE ext_id IS NOT NULL;
        """;

    // The schema keeps only raw facts. `focus_spans` turns them into intervals:
    // every focus event lasts until the next "state" event (another focus change,
    // idle start, lock, suspend or agent stop). Only spans that start with a
    // focus event count as active time.
    private const string SchemaV1 = """
        CREATE TABLE events (
            id     INTEGER PRIMARY KEY,
            ts     INTEGER NOT NULL,   -- unix ms, UTC
            source TEXT    NOT NULL,
            kind   TEXT    NOT NULL,
            app    TEXT,
            pid    INTEGER,
            exe    TEXT,
            title  TEXT,
            data   TEXT                -- JSON object
        );
        CREATE INDEX ix_events_ts ON events (ts);
        CREATE INDEX ix_events_source_kind_ts ON events (source, kind, ts);

        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE VIEW focus_spans AS
        WITH state AS (
            SELECT id, ts, kind, app, exe, title,
                   LEAD(ts) OVER (ORDER BY ts, id) AS next_ts
            FROM events
            WHERE source IN ('focus', 'idle', 'session', 'agent')
        )
        SELECT id,
               ts AS start_ts,
               COALESCE(next_ts, (SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'last_alive'), ts) AS end_ts,
               app, exe, title
        FROM state
        WHERE kind IN ('foreground', 'title');

        CREATE VIEW events_local AS
        SELECT id,
               strftime('%Y-%m-%d %H:%M:%S', ts / 1000, 'unixepoch', 'localtime') AS time,
               source, kind, app, pid, title, exe, data
        FROM events;
        """;

    private readonly SqliteConnection _connection;

    public SqliteConnection Connection => _connection;

    private Database(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens (and creates or migrates) the database. Pass ":memory:" for an in-memory one.</summary>
    public static Database Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        var db = new Database(connection);
        if (path != ":memory:")
        {
            // WAL lets the CLI read while the agent writes.
            db.Execute("PRAGMA journal_mode = WAL;");
            db.Execute("PRAGMA synchronous = NORMAL;");
        }
        db.Execute("PRAGMA busy_timeout = 5000;");
        db.Migrate();
        return db;
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
        if (version >= SchemaVersion)
            return;

        using var tx = _connection.BeginTransaction();
        if (version < 1)
            Execute(SchemaV1, tx);
        if (version < 2)
            Execute(SchemaV2, tx);
        Execute($"PRAGMA user_version = {SchemaVersion};", tx);
        tx.Commit();
    }

    public void Insert(IReadOnlyCollection<WatchEvent> events)
    {
        if (events.Count == 0)
            return;

        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO events (ts, source, kind, app, pid, exe, title, data, ext_id)
            VALUES ($ts, $source, $kind, $app, $pid, $exe, $title, $data, $ext)
            ON CONFLICT DO NOTHING;
            """;
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pApp = cmd.Parameters.Add("$app", SqliteType.Text);
        var pPid = cmd.Parameters.Add("$pid", SqliteType.Integer);
        var pExe = cmd.Parameters.Add("$exe", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pData = cmd.Parameters.Add("$data", SqliteType.Text);
        var pExt = cmd.Parameters.Add("$ext", SqliteType.Text);
        cmd.Prepare();

        foreach (var e in events)
        {
            pTs.Value = e.Ts;
            pSource.Value = e.Source;
            pKind.Value = e.Kind;
            pApp.Value = (object?)e.App ?? DBNull.Value;
            pPid.Value = (object?)e.Pid ?? DBNull.Value;
            pExe.Value = (object?)e.Exe ?? DBNull.Value;
            pTitle.Value = (object?)e.Title ?? DBNull.Value;
            pData.Value = (object?)e.Data ?? DBNull.Value;
            pExt.Value = (object?)e.ExtId ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// If the previous agent run never wrote a stop event (crash, power loss, killed process),
    /// writes one at the last heartbeat so the final focus span doesn't stretch until now.
    /// Returns true when a stop event was added.
    /// </summary>
    public bool CloseUncleanRun()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT kind FROM events WHERE source = 'agent' ORDER BY ts DESC, id DESC LIMIT 1;";
        var lastKind = cmd.ExecuteScalar() as string;
        if (lastKind != "start")
            return false;

        var lastAliveText = GetMeta("last_alive");
        if (!long.TryParse(lastAliveText, out var lastAlive))
            return false;

        Insert([new WatchEvent(lastAlive, EventSources.Agent, "stop") { Data = """{"reason":"unclean"}""" }]);
        return true;
    }

    /// <summary>
    /// Where a scraper left off: the largest <c>ts</c> (or, with <paramref name="dataField"/>,
    /// the largest numeric field in <c>data</c>) among this source's events of this kind
    /// (or of any kind, when <paramref name="kind"/> is null).
    /// </summary>
    public long? Cursor(string source, string? kind, string? dataField = null)
    {
        using var cmd = _connection.CreateCommand();
        var where = kind is null ? "source = $s" : "source = $s AND kind = $k";
        cmd.CommandText = dataField is null
            ? $"SELECT MAX(ts) FROM events WHERE {where};"
            : $"SELECT MAX(CAST(json_extract(data, $path) AS INTEGER)) FROM events WHERE {where};";
        cmd.Parameters.AddWithValue("$s", source);
        if (kind is not null)
            cmd.Parameters.AddWithValue("$k", kind);
        if (dataField is not null)
            cmd.Parameters.AddWithValue("$path", "$." + dataField);
        return cmd.ExecuteScalar() is long value ? value : null;
    }

    /// <summary>The <c>data</c> JSON of this source's most recent event, for scrapers that need a richer cursor.</summary>
    public string? LatestData(string source, string kind)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM events WHERE source = $s AND kind = $k ORDER BY ts DESC, id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$k", kind);
        return cmd.ExecuteScalar() as string;
    }

    public void Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => _connection.Dispose();
}
