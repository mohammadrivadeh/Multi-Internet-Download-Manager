using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Core;
using Microsoft.Data.Sqlite;

namespace DownloadManager.Infrastructure.Persistence;

public interface IQueuePayloadProtector
{
    byte[] Protect(byte[] cleartext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Windows user-profile protection. Never falls back to plaintext storage.</summary>
public sealed class WindowsQueueProtector : IQueuePayloadProtector
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("DownloadManager.Queue.v1");
    public byte[] Protect(byte[] cleartext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected queue storage requires Windows.");
        return ProtectedData.Protect(cleartext, Purpose, DataProtectionScope.CurrentUser);
    }
    public byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected queue storage requires Windows.");
        return ProtectedData.Unprotect(ciphertext, Purpose, DataProtectionScope.CurrentUser);
    }
}

public interface IQueueStore
{
    IReadOnlyList<QueueJob> Load();
    void Save(QueueJob job);
    void Remove(Guid id);
}

/// <summary>Encrypts the complete payload, including signed URLs, destinations and network selections.</summary>
public sealed class QueueStore : IQueueStore, IDisposable
{
    private readonly string filename;
    private readonly IQueuePayloadProtector protector;
    private readonly FileStream lease;
    private bool disposed;
    public QueueStore(string filename, IQueuePayloadProtector protector)
    {
        this.filename = Path.GetFullPath(filename);
        this.protector = protector;
        Directory.CreateDirectory(Path.GetDirectoryName(this.filename)!);
        lease = new FileStream(this.filename + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var connection = Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema is not (0 or 1)) throw new InvalidDataException("Unsupported queue schema.");
            using var command = connection.CreateCommand();
            if (schema == 0)
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new InvalidDataException("Unrecognized queue database.");
            }
            command.CommandText = "CREATE TABLE IF NOT EXISTS jobs (id TEXT PRIMARY KEY, payload BLOB NOT NULL); PRAGMA user_version=1";
            command.ExecuteNonQuery();
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lease.Dispose();
    }

    public IReadOnlyList<QueueJob> Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,payload FROM jobs LIMIT 1001";
        using var rows = command.ExecuteReader();
        var jobs = new List<QueueJob>();
        while (rows.Read())
        {
            if (jobs.Count == 1000) throw new InvalidDataException("Queue capacity exceeded.");
            var encrypted = (byte[])rows.GetValue(1);
            if (encrypted.Length > 1024 * 1024) throw new InvalidDataException("Queue payload exceeds its limit.");
            var clear = protector.Unprotect(encrypted);
            try
            {
                var job = JsonSerializer.Deserialize<QueueJob>(clear) ?? throw new InvalidDataException("Invalid queue payload.");
                if (job.Id.ToString("N") != rows.GetString(0)) throw new InvalidDataException("Queue identity mismatch.");
                job.Validate();
                jobs.Add(job with { Paths = Array.AsReadOnly(job.Paths.ToArray()) });
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        return jobs;
    }

    public void Save(QueueJob job)
    {
        job.Validate();
        var clear = JsonSerializer.SerializeToUtf8Bytes(job);
        byte[] payload;
        try { payload = protector.Protect(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        if (payload.Length > 1024 * 1024) throw new InvalidDataException("Queue payload exceeds its limit.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO jobs VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$id", job.Id.ToString("N"));
        command.Parameters.AddWithValue("$payload", payload);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Remove(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = filename, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL; PRAGMA secure_delete=ON";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
