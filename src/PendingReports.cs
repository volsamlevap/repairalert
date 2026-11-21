using System.Text.Json;

namespace RepairAlert;

internal sealed class PendingReport
{
    public string JobId { get; set; } = "";
    public string JobText { get; set; } = "";
    public string ManagerUser { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public List<ChatEntry> History { get; set; } = new();
    public string Comment { get; set; } = "";
    public string Client { get; set; } = "";
    public string Device { get; set; } = "";
    public string Serial { get; set; } = "";
    public bool Urgent { get; set; }
    public string Password { get; set; } = "";
    public bool NotifyMasterCompleted { get; set; }
}

internal static class PendingReportStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert", "Sessions");

    private static string FilePath(string profile) => Path.Combine(DirectoryPath, profile + "-reports.json");

    public static List<PendingReport> Load(string profile = "manager")
    {
        var text = AtomicFileStore.ReadAllText(FilePath(profile));
        var reports = TryDeserialize(text) ?? TryDeserialize(AtomicFileStore.ReadAllText(FilePath(profile) + ".bak"));
        return reports ?? new List<PendingReport>();
    }

    private static List<PendingReport>? TryDeserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<List<PendingReport>>(text); }
        catch { return null; }
    }

    public static void Save(IEnumerable<PendingReport> reports, string profile = "manager")
    {
        try { AtomicFileStore.WriteAllText(FilePath(profile), JsonSerializer.Serialize(reports.ToList())); }
        catch { }
    }

    public static void Delete(string profile)
    {
        AtomicFileStore.DeleteWithBackup(FilePath(profile));
    }
}

internal static class ReportFlushService
{
    public static List<PendingReport> Flush(IEnumerable<PendingReport> pending,
        Action<PendingReport, string> onSaved)
    {
        var remaining = new List<PendingReport>();
        foreach (var report in pending)
        {
            try
            {
                var path = ReportService.SaveReport(report.JobText, report.StartedAt, report.CompletedAt,
                    report.History, report.ManagerUser, report.MachineName, report.JobId, report.Comment,
                    new JobDetails(report.Client, report.Device, report.Serial, report.Urgent, report.Password));
                onSaved(report, path);
            }
            catch (Exception ex)
            {
                AppLog.Error("Отчёт временно не сохранён, будет повтор автоматически: " + report.JobId, ex);
                remaining.Add(report);
            }
        }
        return remaining;
    }
}
