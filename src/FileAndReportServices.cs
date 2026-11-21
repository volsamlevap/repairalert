using System.Text;

namespace RepairAlert;

internal static class FileTransferService
{
    // Лимит одного вложения — 5 ГБ. Тип long: 5 ГБ не помещается в int.
    public const long MaxFileBytes = 5L * 1024 * 1024 * 1024;
    public const int ChunkBytes = 256 * 1024;
    public static string ReceivedDirectory => StoragePaths.ReceivedDirectory;

    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string SanitizeFileName(string? name)
    {
        var raw = name ?? "";
        var lastSeparator = Math.Max(raw.LastIndexOf('\\'), raw.LastIndexOf('/'));
        var onlyName = lastSeparator >= 0 ? raw[(lastSeparator + 1)..] : raw;
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars())
        {
            '\\', '/', ':', '*', '?', '"', '<', '>', '|'
        };
        var cleaned = new string(onlyName
            .Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)
            .ToArray()).Trim().TrimEnd('.').Trim();
        if (cleaned.Length == 0) cleaned = "Файл.bin";
        var baseName = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(baseName.ToUpperInvariant())) cleaned = "_" + cleaned;
        if (cleaned.Length > 100)
        {
            var extension = Path.GetExtension(cleaned);
            var core = Path.GetFileNameWithoutExtension(cleaned);
            var keep = Math.Max(1, 100 - extension.Length);
            cleaned = (core.Length > keep ? core[..keep] : core) + extension;
        }
        return cleaned;
    }

    public static string SaveCompletedPart(string partPath, string fileName)
    {
        var name = SanitizeFileName(fileName);
        Directory.CreateDirectory(ReceivedDirectory);
        var target = IncomingTransferStore.AllocateUniquePath(ReceivedDirectory, name);
        File.Move(partPath, target);
        return target;
    }

    public static void OpenReceivedDirectory()
    {
        Directory.CreateDirectory(ReceivedDirectory);
        System.Diagnostics.Process.Start("explorer.exe", ReceivedDirectory);
    }
}

internal static class ReportService
{
    public static string ReportsDirectory => StoragePaths.ReportsDirectory;
    public static string JournalPath => StoragePaths.JournalPath;

    public static string SaveReport(string jobText, DateTime startedAt, DateTime completedAt,
        IEnumerable<ChatEntry> history, string managerUser = "", string machineName = "", string jobId = "",
        string comment = "", JobDetails? details = null)
    {
        Directory.CreateDirectory(ReportsDirectory);
        var firstLine = jobText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Ремонт";
        var safeName = Sanitize(firstLine.Trim());
        if (safeName.Length > 45) safeName = safeName[..45];
        if (safeName.Length == 0) safeName = "Ремонт";
        var safeUser = Sanitize(managerUser.Split('\\').Last());
        if (safeUser.Length > 25) safeUser = safeUser[..25];
        var filePrefix = safeUser.Length > 0 ? $"{safeName}_{safeUser}" : safeName;
        var path = Path.Combine(ReportsDirectory, $"{filePrefix}_{completedAt:yyyy-MM-dd_HH-mm-ss}.txt");
        var suffix = 1;
        while (File.Exists(path))
            path = Path.Combine(ReportsDirectory, $"{filePrefix}_{completedAt:yyyy-MM-dd_HH-mm-ss}_{suffix++}.txt");

        var entries = history.ToList();
        var repairNumber = RepairNumber(jobId);
        var duration = JobTiming.Elapsed(startedAt, completedAt);
        var report = new StringBuilder();
        report.AppendLine("СЕРВИС ЛЕГКО");
        report.AppendLine("ОТЧЁТ О ЗАВЕРШЁННОМ РЕМОНТЕ");
        report.AppendLine(new string('=', 32));
        if (repairNumber.Length > 0) report.AppendLine("Номер ремонта: " + repairNumber);
        report.AppendLine("Статус: Завершён");
        if (details is not null && details.Urgent) report.AppendLine("Срочность: СРОЧНАЯ ЗАЯВКА");
        report.AppendLine("Название ремонта: " + jobText);
        if (details is not null && details.Client.Length > 0) report.AppendLine("Клиент: " + details.Client);
        if (details is not null && details.Device.Length > 0) report.AppendLine("Устройство: " + details.Device);
        if (details is not null && details.Serial.Length > 0) report.AppendLine("Серийный номер / IMEI: " + details.Serial);
        if (details is not null && details.Password.Length > 0) report.AppendLine("Пароль устройства: " + details.Password);
        if (managerUser.Length > 0) report.AppendLine("Пользователь Windows (менеджер): " + managerUser);
        if (machineName.Length > 0) report.AppendLine("Компьютер менеджера: " + machineName);
        report.AppendLine("Дата и время начала: " + startedAt.ToString("dd.MM.yyyy HH:mm:ss"));
        report.AppendLine("Дата и время завершения: " + completedAt.ToString("dd.MM.yyyy HH:mm:ss"));
        report.AppendLine("Длительность ремонта: " + duration);
        if (comment.Trim().Length > 0) report.AppendLine("Что сделано: " + comment.Trim());
        report.AppendLine();
        report.AppendLine("СОДЕРЖАНИЕ ЧАТА");
        report.AppendLine(new string('-', 32));
        var any = false;
        foreach (var entry in entries)
        {
            any = true;
            var sender = entry.Own
                ? (managerUser.Length > 0 ? "Менеджер (" + managerUser + ")" : "Менеджер")
                : entry.Sender;
            report.AppendLine($"[{entry.Time:dd.MM.yyyy HH:mm:ss}] {sender}:");
            if (entry.FileName.Length > 0) report.AppendLine($"[Прикреплён файл: {entry.FileName}]");
            else report.AppendLine(entry.Text);
            report.AppendLine();
        }
        if (!any) report.AppendLine("Переписка по этой заявке отсутствовала.");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
        var verification = File.ReadAllText(path);
        if (verification.Length < 40 || !verification.Contains("ОТЧЁТ О ЗАВЕРШЁННОМ РЕМОНТЕ", StringComparison.Ordinal))
            throw new IOException("Отчёт записался не полностью. Сохранение будет повторено автоматически.");
        SaveHtmlReport(Path.ChangeExtension(path, ".html"), jobText, startedAt, completedAt, entries,
            managerUser, machineName, repairNumber, comment, details, duration);
        UpdateJournal(jobText, startedAt, completedAt, managerUser, machineName, jobId, "Завершён", Path.GetFileName(path));
        return path;
    }

    private static void SaveHtmlReport(string path, string jobText, DateTime startedAt, DateTime completedAt,
        List<ChatEntry> entries, string managerUser, string machineName, string repairNumber,
        string comment, JobDetails? details, string duration)
    {
        static string H(string value) => System.Net.WebUtility.HtmlEncode(value);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
        html.Append("<title>Отчёт о ремонте — СЕРВИС ЛЕГКО</title><style>");
        html.Append("body{font-family:'Segoe UI',Arial,sans-serif;max-width:760px;margin:32px auto;padding:0 20px;color:#1d1d1f;}");
        html.Append("h1{font-size:22px;margin:0 0 2px}.sub{color:#6e6e73;font-size:13px;margin:0 0 20px}");
        html.Append("table{border-collapse:collapse;width:100%;margin:0 0 20px}");
        html.Append("td{padding:7px 10px;border-bottom:1px solid #e5e5ea;font-size:14px;vertical-align:top}");
        html.Append("td.k{color:#6e6e73;width:220px}.urg{color:#ff3b30;font-weight:600}");
        html.Append(".msg{margin:0 0 14px}.meta{color:#6e6e73;font-size:12px;margin:0 0 3px}");
        html.Append(".text{font-size:14px;white-space:pre-wrap;margin:0}.file{color:#0071e3;font-size:13px}");
        html.Append("h2{font-size:16px;margin:26px 0 12px;border-bottom:1px solid #e5e5ea;padding-bottom:6px}");
        html.Append("@media print{body{margin:8px auto}}");
        html.Append("</style></head><body>");
        html.Append("<h1>СЕРВИС ЛЕГКО — отчёт о завершённом ремонте</h1>");
        html.Append("<p class=\"sub\">Сформировано ").Append(H(DateTime.Now.ToString("dd.MM.yyyy HH:mm"))).Append("</p>");
        html.Append("<table>");
        void Row(string key, string value, bool urgent = false)
        {
            if (value.Length == 0) return;
            html.Append("<tr><td class=\"k\">").Append(H(key)).Append("</td><td")
                .Append(urgent ? " class=\"urg\">" : ">").Append(H(value)).Append("</td></tr>");
        }
        Row("Номер ремонта", repairNumber);
        Row("Статус", "Завершён");
        if (details is not null && details.Urgent) Row("Срочность", "СРОЧНАЯ ЗАЯВКА", true);
        Row("Название ремонта", jobText);
        if (details is not null)
        {
            Row("Клиент", details.Client);
            Row("Устройство", details.Device);
            Row("Серийный номер / IMEI", details.Serial);
            Row("Пароль устройства", details.Password);
        }
        Row("Пользователь Windows (менеджер)", managerUser);
        Row("Компьютер менеджера", machineName);
        Row("Дата и время начала", startedAt.ToString("dd.MM.yyyy HH:mm:ss"));
        Row("Дата и время завершения", completedAt.ToString("dd.MM.yyyy HH:mm:ss"));
        Row("Длительность ремонта", duration);
        Row("Что сделано", comment.Trim());
        html.Append("</table>");
        html.Append("<h2>Содержание чата</h2>");
        if (entries.Count == 0) html.Append("<p class=\"meta\">Переписка по этой заявке отсутствовала.</p>");
        foreach (var entry in entries)
        {
            var sender = entry.Own
                ? (managerUser.Length > 0 ? "Менеджер (" + managerUser + ")" : "Менеджер")
                : entry.Sender;
            html.Append("<div class=\"msg\"><p class=\"meta\">[").Append(H(entry.Time.ToString("dd.MM.yyyy HH:mm:ss")))
                .Append("] ").Append(H(sender)).Append("</p>");
            if (entry.FileName.Length > 0)
                html.Append("<p class=\"file\">Прикреплён файл: ").Append(H(entry.FileName)).Append("</p>");
            else html.Append("<p class=\"text\">").Append(H(entry.Text)).Append("</p>");
            html.Append("</div>");
        }
        html.Append("</body></html>");
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(true));
        var verification = File.ReadAllText(path);
        if (!verification.Contains("ОТЧЁТ", StringComparison.OrdinalIgnoreCase) &&
            !verification.Contains("отчёт", StringComparison.OrdinalIgnoreCase))
            throw new IOException("HTML-отчёт записался не полностью. Сохранение будет повторено автоматически.");
        if (!verification.Contains("</html>", StringComparison.Ordinal))
            throw new IOException("HTML-отчёт записался не полностью. Сохранение будет повторено автоматически.");
    }

    public static void OpenReportsDirectory()
    {
        Directory.CreateDirectory(ReportsDirectory);
        System.Diagnostics.Process.Start("explorer.exe", ReportsDirectory);
    }

    private static readonly object JournalProcessLock = new();
    private const string JournalMutexName = @"Global\SERVIS_LEGKO_RepairJournal";

    public static void UpdateJournal(string jobText, DateTime? startedAt, DateTime? completedAt,
        string managerUser, string machineName, string jobId, string status, string reportFile = "")
    {
        var repairNumber = RepairNumber(jobId);
        if (repairNumber.Length == 0) return;
        lock (JournalProcessLock)
        {
            Mutex? mutex = null;
            var acquired = false;
            try
            {
                try { mutex = new Mutex(false, JournalMutexName); } catch { }
                if (mutex is not null)
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
                    catch (AbandonedMutexException) { acquired = true; }
                }
                WriteJournalRow(jobText, startedAt, completedAt, managerUser, machineName,
                    repairNumber, status, reportFile);
            }
            finally
            {
                if (acquired && mutex is not null)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex?.Dispose();
            }
        }
    }

    private static void WriteJournalRow(string jobText, DateTime? startedAt, DateTime? completedAt,
        string managerUser, string machineName, string repairNumber, string status, string reportFile)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            try
            {
                using var stream = new FileStream(JournalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                var lines = new List<string>();
                if (stream.Length > 0)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true);
                    while (!reader.EndOfStream)
                    {
                        var line = reader.ReadLine();
                        if (line is not null) lines.Add(line);
                    }
                }
                const string header = "Номер;Название ремонта;Пользователь Windows;Компьютер;Начало;Завершение;Статус;Файл отчёта";
                if (lines.Count == 0) lines.Add(header);
                var row = string.Join(";", new[]
                {
                    Csv(repairNumber), Csv(jobText), Csv(managerUser), Csv(machineName),
                    Csv(startedAt?.ToString("dd.MM.yyyy HH:mm:ss") ?? ""),
                    Csv(completedAt?.ToString("dd.MM.yyyy HH:mm:ss") ?? ""), Csv(status),
                    Csv(reportFile.Length > 0 ? "Отчёты\\" + reportFile : "")
                });
                var prefix = Csv(repairNumber) + ";";
                var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.Ordinal));
                if (index >= 0) lines[index] = row; else lines.Add(row);
                stream.Position = 0;
                stream.SetLength(0);
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 1024, true) { AutoFlush = true })
                    foreach (var line in lines) writer.WriteLine(line);
                return;
            }
            catch (IOException) when (attempt < 24) { Thread.Sleep(120); }
        }
    }

    private static string RepairNumber(string jobId) => string.IsNullOrWhiteSpace(jobId)
        ? "" : "R-" + jobId[..Math.Min(8, jobId.Length)].ToUpperInvariant();

    private static string Csv(string value)
    {
        var singleLine = value.Replace("\r\n", " / ").Replace('\n', ' ').Replace('\r', ' ');
        return '"' + singleLine.Replace("\"", "\"\"") + '"';
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }
}
