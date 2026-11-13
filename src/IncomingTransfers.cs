using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepairAlert;

internal sealed class IncomingTransferMeta
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sender { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal static class IncomingTransferStore
{
    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RepairAlert", "IncomingParts");
    private static readonly object Gate = new();
    private static readonly Dictionary<string, FileStream> _openParts = new();

    public static string? DirectoryOverride { get; set; }
    public static long? FreeSpaceOverride { get; set; }

    private static string DirectoryPath
    {
        get
        {
            var path = DirectoryOverride ?? DefaultDirectory;
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static string MetaPath(string id) => Path.Combine(DirectoryPath, id + ".meta.json");
    private static string PartPath(string id) => Path.Combine(DirectoryPath, id + ".part");

    public static void EnsureFreeSpace(long neededBytes)
    {
        var free = FreeSpaceOverride;
        if (free is null)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(DirectoryPath));
                if (root is not null) free = new DriveInfo(root).AvailableFreeSpace;
            }
            catch { free = long.MaxValue; }
        }
        if (free < neededBytes + 32L * 1024 * 1024)
            throw new IOException("Недостаточно свободного места на диске для приёма файла.");
    }

    public static IncomingTransferMeta? LoadMeta(string id)
    {
        try
        {
            var text = File.ReadAllText(MetaPath(id));
            return JsonSerializer.Deserialize<IncomingTransferMeta>(text);
        }
        catch { return null; }
    }

    public static IncomingTransferMeta Begin(string id, string sessionId, string title, string sender,
        string fileName, long size, string sha256)
    {
        lock (Gate)
        {
            var existing = LoadMeta(id);
            if (existing is not null) return existing;
            var meta = new IncomingTransferMeta
            {
                SessionId = sessionId,
                Title = title,
                Sender = sender,
                FileName = FileTransferService.SanitizeFileName(fileName),
                Size = size,
                Sha256 = sha256
            };
            AtomicFileStore.WriteAllText(MetaPath(id), JsonSerializer.Serialize(meta));
            return meta;
        }
    }

    public static long GetOffset(string id)
    {
        try
        {
            var info = new FileInfo(PartPath(id));
            return info.Exists ? info.Length : 0;
        }
        catch { return 0; }
    }

    public static void Write(string id, long offset, byte[] data)
    {
        lock (Gate)
        {
            if (!_openParts.TryGetValue(id, out var stream))
            {
                stream = new FileStream(PartPath(id), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                _openParts[id] = stream;
            }
            if (stream.Length != offset) stream.Position = offset;
            stream.Write(data, 0, data.Length);
            if (stream.Length % (FileTransferService.ChunkBytes * 4L) < FileTransferService.ChunkBytes)
                stream.Flush(true);
        }
    }

    public static string? Finalize(string id)
    {
        lock (Gate)
        {
            if (_openParts.Remove(id, out var stream))
            {
                try { stream.Flush(true); stream.Dispose(); } catch { }
            }
        }
        var meta = LoadMeta(id);
        var partPath = PartPath(id);
        if (meta is null || !File.Exists(partPath)) return null;
        try
        {
            var info = new FileInfo(partPath);
            if (info.Length != meta.Size) return null;
            var hash = ComputeFileHash(partPath);
            if (!string.Equals(hash, meta.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            Directory.CreateDirectory(FileTransferService.ReceivedDirectory);
            var target = AllocateUniquePath(FileTransferService.ReceivedDirectory, meta.FileName);
            File.Move(partPath, target);
            Cleanup(id);
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось завершить приём файла", ex);
            return null;
        }
    }

    public static void Cancel(string id)
    {
        lock (Gate)
        {
            if (_openParts.Remove(id, out var stream))
            {
                try { stream.Dispose(); } catch { }
            }
        }
        Cleanup(id);
    }

    private static void Cleanup(string id)
    {
        try { File.Delete(PartPath(id)); } catch { }
        AtomicFileStore.DeleteWithBackup(MetaPath(id));
    }

    public static string AllocateUniquePath(string directory, string name)
    {
        var basePath = Path.Combine(directory, name);
        for (var attempt = 0; attempt < 10000; attempt++)
        {
            var path = attempt == 0 ? basePath : Path.Combine(directory,
                $"{Path.GetFileNameWithoutExtension(name)} ({attempt}){Path.GetExtension(name)}");
            if (!File.Exists(path)) return path;
        }
        throw new IOException("Не удалось выбрать имя для полученного файла.");
    }

    public static string ComputeFileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<(string Hash, long Size)> HashAndCopyAsync(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sha = SHA256.Create();
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0) break;
            sha.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer, 0, read);
            total += read;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        await output.FlushAsync();
        return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), total);
    }

    public static void CleanupOrphans(TimeSpan maxAge)
    {
        try
        {
            var now = DateTime.Now;
            foreach (var file in Directory.EnumerateFiles(DirectoryPath))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < now - maxAge) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }
}
