using System.Security.Cryptography;
using System.Text.Json;
using DownloadManager.Core;
namespace DownloadManager.Infrastructure.Persistence;

public sealed class PreferencesStore(string filename, IQueuePayloadProtector protector)
{
    private const int MaxFileBytes = 65536;
    private sealed record Envelope
    {
        public required string Kind { get; init; }
        public int Version { get; init; }
        public required DesktopPreferences Value { get; init; }
    }
    public DesktopPreferences Load()
    {
        if (!File.Exists(filename)) return new();
        if (new FileInfo(filename).Length > MaxFileBytes) throw new InvalidDataException("Preferences exceed their size limit.");
        var clear = protector.Unprotect(File.ReadAllBytes(filename));
        try
        {
            var document = JsonSerializer.Deserialize<Envelope>(clear) ?? throw new InvalidDataException("Invalid preferences.");
            if (document.Kind != "DesktopPreferences" || document.Version != 1) throw new InvalidDataException("Unsupported preferences schema.");
            document.Value.Validate();
            return document.Value with { Category = DownloadCategory.Normalize(document.Value.Category) };
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public void Save(DesktopPreferences value)
    {
        value.Validate();
        var clear = JsonSerializer.SerializeToUtf8Bytes(new Envelope { Kind = "DesktopPreferences", Version = 1, Value = value with { Category = DownloadCategory.Normalize(value.Category) } });
        byte[] encrypted;
        try { encrypted = protector.Protect(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        if (encrypted.Length > MaxFileBytes) throw new InvalidDataException("Preferences exceed their size limit.");
        var target = Path.GetFullPath(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(encrypted); stream.Flush(flushToDisk: true); }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
