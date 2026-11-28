using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace KeyFlare;

public sealed record IssuedKey(long Id, string Secret);
public sealed record Hit(long Id, long KeyId, string Time, string Protocol, string Endpoint);

public sealed class Store(IConfiguration config, IHostEnvironment environment)
{
    private readonly string path = Path.GetFullPath(config["KeyFlare:Database"] ?? "App_Data/keyflare.db", environment.ContentRootPath);

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
        connection.Open();
        return connection;
    }

    public static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    public void Initialize()
    {
        try
        {
            InitializeSchema();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SqliteException { SqliteErrorCode: 8 or 14 })
        {
            throw new InvalidOperationException(
                $"Cannot initialize SQLite database '{path}'. Check that this path names a file and that the application user can write to its directory and any existing database files. For Docker bind mounts, check the host directory permissions.", ex);
        }
    }

    private void InitializeSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open();
        using var command = Command(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS keys(id INTEGER PRIMARY KEY, label TEXT NOT NULL, secret TEXT NOT NULL UNIQUE, active INTEGER NOT NULL DEFAULT 1,
                created TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')));
            CREATE TABLE IF NOT EXISTS hits(id INTEGER PRIMARY KEY, key_id INTEGER NOT NULL REFERENCES keys(id), time TEXT NOT NULL, protocol TEXT NOT NULL, endpoint TEXT NOT NULL,
                method TEXT NOT NULL, target TEXT NOT NULL, ua TEXT NOT NULL, model TEXT, input TEXT NOT NULL, headers TEXT NOT NULL, body BLOB NOT NULL, peer_ip TEXT, client_ip TEXT);
            CREATE INDEX IF NOT EXISTS hits_key_id ON hits(key_id,id);
            CREATE TABLE IF NOT EXISTS channels(id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, url TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS deliveries(hit_id INTEGER REFERENCES hits(id), channel_id INTEGER REFERENCES channels(id) ON DELETE CASCADE,
                attempts INTEGER NOT NULL DEFAULT 0, next_try INTEGER NOT NULL DEFAULT 0, state TEXT NOT NULL DEFAULT 'pending', PRIMARY KEY(hit_id, channel_id));
            """);
        command.ExecuteNonQuery();
    }

    public List<IssuedKey> Issue(string label, int count, string prefix)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        List<IssuedKey> issued = [];
        for (int i = 0; i < count; i++)
        {
            var secret = prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            using var insert = Command(db, "INSERT INTO keys(label,secret) VALUES($label,$secret) RETURNING id",
                ("$label", label), ("$secret", secret));
            insert.Transaction = tx;
            issued.Add(new((long)insert.ExecuteScalar()!, secret));
        }
        tx.Commit();
        return issued;
    }

    public bool IsActive(string secret)
    {
        if (secret.Length is 0 or > 256) return false;
        using var db = Open();
        using var command = Command(db, "SELECT 1 FROM keys WHERE secret=$secret AND active=1", ("$secret", secret));
        return command.ExecuteScalar() is not null;
    }

    public bool Record(string secret, string protocol, string endpoint, bool generation, RequestCapture request)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var insert = Command(db, """
            INSERT INTO hits(key_id,time,protocol,endpoint,method,target,ua,model,input,headers,body,peer_ip,client_ip)
            SELECT id,$time,$protocol,$endpoint,$method,$target,$ua,$model,$input,$headers,$body,$peer,$client
            FROM keys WHERE secret=$secret AND active=1 RETURNING id
            """, ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$protocol", protocol), ("$endpoint", endpoint), ("$secret", secret),
            ("$method", request.Method), ("$target", request.Target), ("$ua", request.UserAgent), ("$model", (object?)request.Model ?? DBNull.Value),
            ("$input", request.Input), ("$headers", request.Headers), ("$body", request.Body),
            ("$peer", (object?)request.PeerIp ?? DBNull.Value), ("$client", (object?)request.ClientIp ?? DBNull.Value));
        insert.Transaction = tx;
        if (insert.ExecuteScalar() is not long id) return false;
        if (generation)
        {
            using var enqueue = Command(db, "INSERT INTO deliveries(hit_id,channel_id) SELECT $id,id FROM channels", ("$id", id));
            enqueue.Transaction = tx;
            enqueue.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }
}
