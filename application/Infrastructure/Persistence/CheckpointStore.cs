using Microsoft.Data.Sqlite;
using DownloadManager.Core;

namespace DownloadManager.Infrastructure.Persistence;

internal sealed class CheckpointStore : IDisposable
{
    private readonly SqliteConnection connection;
    public CheckpointStore(string filename, string sourceHash, long length, string etag)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = filename, Pooling = false }.ToString());
        try
        {
            connection.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema is not (0 or 1)) throw new InvalidDataException("Unsupported checkpoint schema.");
            if (schema == 0)
            {
                using var tables = connection.CreateCommand();
                tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt32(tables.ExecuteScalar()) != 0)
                    throw new InvalidDataException("Unrecognized checkpoint database. Preserve it and choose a new destination.");
            }
            Execute("CREATE TABLE IF NOT EXISTS identity (id INTEGER PRIMARY KEY CHECK(id=1), source TEXT NOT NULL, length INTEGER NOT NULL, etag TEXT NOT NULL); CREATE TABLE IF NOT EXISTS ranges (start INTEGER PRIMARY KEY, finish INTEGER NOT NULL, sha256 TEXT NOT NULL); PRAGMA user_version=1;");
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT source,length,etag FROM identity WHERE id=1";
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != sourceHash || reader.GetInt64(1) != length || reader.GetString(2) != etag)
                    throw new InvalidDataException("The saved download belongs to a different resource. Preserve it and select a new destination.");
            }
            else
            {
                reader.Close();
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO identity VALUES(1,$source,$length,$etag)";
                insert.Parameters.AddWithValue("$source", sourceHash);
                insert.Parameters.AddWithValue("$length", length);
                insert.Parameters.AddWithValue("$etag", etag);
                insert.ExecuteNonQuery();
            }
        }
        catch { connection.Dispose(); throw; }
    }

    public List<(ByteRange Range, string Hash)> Load()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT start,finish,sha256 FROM ranges ORDER BY start";
        using var reader = command.ExecuteReader();
        var rows = new List<(ByteRange, string)>();
        while (reader.Read()) rows.Add((new(reader.GetInt64(0), reader.GetInt64(1)), reader.GetString(2)));
        return rows;
    }

    public void Save(ByteRange range, string hash)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO ranges VALUES($start,$finish,$hash)";
        command.Parameters.AddWithValue("$start", range.Start);
        command.Parameters.AddWithValue("$finish", range.EndExclusive);
        command.Parameters.AddWithValue("$hash", hash);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Remove(long start)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ranges WHERE start=$start";
        command.Parameters.AddWithValue("$start", start);
        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    public void Dispose() => connection.Dispose();
}
