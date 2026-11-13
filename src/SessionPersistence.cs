using System.Text.Json;

namespace RepairAlert;

internal sealed class ChatSessionSnapshot
{
    public string CurrentJobId { get; set; } = "";
    public string CurrentJobText { get; set; } = "";
    public bool IsJobAccepted { get; set; }
    public DateTime JobStartedAt { get; set; }
    public bool JobCompletionSaved { get; set; }
    public List<ManagerJobRecord> Jobs { get; set; } = new();
    public List<ChatSessionBucket> Chats { get; set; } = new();
    public List<OutgoingChatMessage> Outbox { get; set; } = new();
    /// <summary>Заявки, убранные в архив локально, но о которых мастер ещё не знает (не было связи).</summary>
    public List<string> PendingArchivedJobs { get; set; } = new();
    /// <summary>Заявки, удалённые локально, но о которых мастер ещё не знает (не было связи).</summary>
    public List<string> PendingRemovedJobs { get; set; } = new();
}

internal sealed class ManagerJobRecord
{
    public string JobId { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsAccepted { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public bool CompletionSaved { get; set; }
    public bool ReportPending { get; set; }
    public DateTime CompletedAt { get; set; }
    public string Comment { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public string Client { get; set; } = "";
    public string Device { get; set; } = "";
    public string Serial { get; set; } = "";
    public bool Urgent { get; set; }
}

internal sealed class ManagerArchiveEntry
{
    public string JobId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CompletedAt { get; set; }
    public string Comment { get; set; } = "";
    public string ReportPath { get; set; } = "";
}

internal static class ManagerArchiveStore
{
    private static readonly string FilePathValue = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert", "Sessions",
        "manager-archive.json");

    public static List<ManagerArchiveEntry> Load()
    {
        var text = AtomicFileStore.ReadAllText(FilePathValue) ??
                   AtomicFileStore.ReadAllText(FilePathValue + ".bak");
        if (string.IsNullOrWhiteSpace(text)) return new List<ManagerArchiveEntry>();
        try { return JsonSerializer.Deserialize<List<ManagerArchiveEntry>>(text) ?? new List<ManagerArchiveEntry>(); }
        catch { return new List<ManagerArchiveEntry>(); }
    }

    public static void Save(IEnumerable<ManagerArchiveEntry> entries)
    {
        try { AtomicFileStore.WriteAllText(FilePathValue, JsonSerializer.Serialize(entries.ToList())); }
        catch { }
    }
}

internal static class RecentMastersLogic
{
    public const int MaxRecent = 5;

    public static void Remember(List<string> recent, string address)
    {
        if (address.Length == 0) return;
        recent.RemoveAll(item => string.Equals(item, address, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, address);
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
    }
}

internal sealed class ChatSessionBucket
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<ChatEntry> Entries { get; set; } = new();
    /// <summary>Идентификатор закреплённого сообщения (пусто — закрепа нет).</summary>
    public string PinnedId { get; set; } = "";
}

internal sealed class OutgoingChatMessage
{
    public string SessionId { get; set; } = "";
    public AppMessage Message { get; set; } = new();
    public string FilePath { get; set; } = "";
    public bool AwaitingAck { get; set; }
    public bool SendingNow { get; set; }
    public DateTime LastSentAtUtc { get; set; }
    public long ResumeOffset { get; set; }
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = "";
    public int RetryCount { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Streaming { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int StreamGeneration { get; set; }
}

internal static class ChatSessionStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert", "Sessions");

    public static string OutboxDirectory(string profile)
    {
        var path = Path.Combine(DirectoryPath, profile + "-files");
        Directory.CreateDirectory(path);
        return path;
    }

    public static ChatSessionSnapshot Load(string profile)
    {
        var path = FilePath(profile);
        var snapshot = TryDeserialize(AtomicFileStore.ReadAllText(path)) ??
                       TryDeserialize(AtomicFileStore.ReadAllText(path + ".bak")) ??
                       new ChatSessionSnapshot();
        foreach (var item in snapshot.Outbox)
        {
            item.AwaitingAck = false;
            item.SendingNow = false;
            item.Streaming = false;
        }
        return snapshot;
    }

    private static ChatSessionSnapshot? TryDeserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<ChatSessionSnapshot>(text); }
        catch { return null; }
    }

    public static void Save(string profile, ChatSessionSnapshot snapshot)
    {
        try
        {
            AtomicFileStore.WriteAllText(FilePath(profile), JsonSerializer.Serialize(snapshot));
        }
        catch { }
    }

    private static string FilePath(string profile) => Path.Combine(DirectoryPath, profile + ".json");

    public static void Delete(string profile)
    {
        AtomicFileStore.DeleteWithBackup(FilePath(profile));
        try
        {
            var files = Path.Combine(DirectoryPath, profile + "-files");
            if (Directory.Exists(files)) Directory.Delete(files, true);
        }
        catch { }
    }
}
