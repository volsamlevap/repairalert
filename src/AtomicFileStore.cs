using System.Text;

namespace RepairAlert;

internal static class AtomicFileStore
{
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var backup = path + ".bak";
        var bytes = new UTF8Encoding(false).GetBytes(content);
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        try
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, backup, true);
                    return;
                }
                catch (PlatformNotSupportedException) { }
                catch (FileNotFoundException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { File.Copy(temp, backup, true); } catch { }
            File.Move(temp, path, true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    public static string? ReadAllText(string path)
    {
        try
        {
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        catch { }
        return null;
    }

    public static void DeleteWithBackup(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + ".bak"); } catch { }
    }
}
