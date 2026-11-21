using System.Text.Json;

namespace RepairAlert;

internal sealed class MasterJobRecord
{
    public string ClientId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string CurrentJobId { get; set; } = "";
    public string CurrentJobText { get; set; } = "";
    public bool IsJobAccepted { get; set; }
    public bool AwaitingReport { get; set; }
    public bool ReportSaved { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string StatusMessage { get; set; } = "";
    public bool StatusSuccess { get; set; }
    public string Comment { get; set; } = "";
    public string Client { get; set; } = "";
    public string Device { get; set; } = "";
    public string Serial { get; set; } = "";
    public bool Urgent { get; set; }
    public string Password { get; set; } = "";
}

internal sealed class MasterJobsSnapshot
{
    public List<MasterJobRecord> Jobs { get; set; } = new();
}

internal static class MasterJobStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert", "Sessions");

    public static List<MasterJobRecord> Load(string profile = "master")
    {
        var path = FilePath(profile);
        var snapshot = TryDeserialize(AtomicFileStore.ReadAllText(path)) ??
                       TryDeserialize(AtomicFileStore.ReadAllText(path + ".bak"));
        return snapshot?.Jobs.Where(job => job.ClientId.Length > 0).ToList() ?? new List<MasterJobRecord>();
    }

    private static MasterJobsSnapshot? TryDeserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<MasterJobsSnapshot>(text); }
        catch { return null; }
    }

    public static void Save(IEnumerable<MasterJobRecord> jobs, string profile = "master")
    {
        try
        {
            var snapshot = new MasterJobsSnapshot { Jobs = jobs.ToList() };
            AtomicFileStore.WriteAllText(FilePath(profile), JsonSerializer.Serialize(snapshot));
        }
        catch { }
    }

    public static void Delete(string profile)
    {
        AtomicFileStore.DeleteWithBackup(FilePath(profile));
    }

    private static string FilePath(string profile) => Path.Combine(DirectoryPath, profile + "-jobs.json");
}
