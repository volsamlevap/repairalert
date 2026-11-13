using System.Text.Json;

namespace RepairAlert;

/// <summary>Статусы заявки для общего списка (сообщение JobInfo, поле Status).</summary>
internal static class JobStatus
{
    public const string Waiting = "waiting";               // создана, мастер ещё не принял
    public const string Accepted = "accepted";             // в работе у мастера
    public const string AwaitingReport = "awaiting_report"; // завершена, отчёт ещё не сохранён
    public const string Done = "done";                     // завершена, отчёт сохранён

    public static string Of(bool isAccepted, bool awaitingReport, bool reportSaved)
    {
        if (reportSaved) return Done;
        if (awaitingReport) return AwaitingReport;
        return isAccepted ? Accepted : Waiting;
    }

    /// <summary>Неизвестный/пустой статус (старая версия мастера) считаем «ожидает» — безопаснее всего.</summary>
    public static string Normalize(string status) => status switch
    {
        Accepted => Accepted,
        AwaitingReport => AwaitingReport,
        Done => Done,
        _ => Waiting
    };

    /// <summary>Подпись статуса для списка и карточки заявки.</summary>
    public static string Title(string status) => Normalize(status) switch
    {
        Accepted => "В работе",
        AwaitingReport => "Ожидает отчёт",
        Done => "Завершена",
        _ => "Ожидает мастера"
    };
}

/// <summary>Чужая заявка в списке менеджера: только просмотр и удаление (синхронизируется мастером).</summary>
internal sealed class RemoteJobInfo
{
    public string JobId { get; set; } = "";
    public string Text { get; set; } = "";
    public string OwnerClientId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string OwnerMachine { get; set; } = "";
    public string Status { get; set; } = JobStatus.Waiting;
    public DateTime ReceivedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string Comment { get; set; } = "";
    public string Client { get; set; } = "";
    public string Device { get; set; } = "";
    public string Serial { get; set; } = "";
    public bool Urgent { get; set; }

    public static RemoteJobInfo FromMessage(AppMessage message) => new()
    {
        JobId = message.JobId,
        Text = message.Text,
        OwnerClientId = message.ClientId,
        OwnerName = message.UserName,
        OwnerMachine = message.MachineName,
        Status = JobStatus.Normalize(message.Status),
        ReceivedAt = message.SentAt == default ? DateTime.Now : message.SentAt.ToLocalTime(),
        StartedAt = message.StartedAt?.ToLocalTime() ?? default,
        CompletedAt = message.CompletedAt?.ToLocalTime() ?? default,
        Comment = message.Comment,
        Client = message.Client,
        Device = message.Device,
        Serial = message.Serial,
        Urgent = message.Urgent
    };

    /// <summary>Применить правку полей из JobInfo (2.35): только описание и реквизиты,
    /// статус и даты не трогаем. Пустой текст правки существующий не затирает.
    /// Возвращает true, если хоть одно поле изменилось.</summary>
    public bool ApplyDescription(AppMessage message)
    {
        var changed = false;
        var text = message.Text.Trim();
        if (text.Length > 0 && Text != message.Text) { Text = message.Text; changed = true; }
        if (Client != message.Client) { Client = message.Client; changed = true; }
        if (Device != message.Device) { Device = message.Device; changed = true; }
        if (Serial != message.Serial) { Serial = message.Serial; changed = true; }
        if (Urgent != message.Urgent) { Urgent = message.Urgent; changed = true; }
        return changed;
    }

    /// <summary>Строка времени для списка — в том же стиле, что и у своих заявок.</summary>
    public string TimeLine()
    {
        if (JobStatus.Normalize(Status) == JobStatus.Done)
            return "Завершена " + (CompletedAt == default ? DateTime.Now : CompletedAt).ToString("dd.MM.yyyy HH:mm");
        if (JobStatus.Normalize(Status) == JobStatus.AwaitingReport)
            return "Отчёт сохраняется";
        if (JobStatus.Normalize(Status) == JobStatus.Accepted)
            return $"В работе {JobTiming.Elapsed(StartedAt == default ? DateTime.Now : StartedAt, DateTime.Now)}";
        return "Создана " + (ReceivedAt == default ? DateTime.Now : ReceivedAt).ToString("dd.MM.yyyy HH:mm");
    }
}

/// <summary>Чистая логика набора удалённых заявок: без дублей, с ограничением размера.
/// Нужна мастеру, чтобы «забытые» заявки не воскресали при переподключении менеджеров.</summary>
internal sealed class DeletedJobIds
{
    private readonly int _cap;
    private readonly List<string> _ordered = new();

    public DeletedJobIds(int cap = 5000) => _cap = Math.Max(100, cap);

    public int Count => _ordered.Count;

    public bool Contains(string jobId) => _ordered.Contains(jobId, StringComparer.Ordinal);

    public void Add(string jobId)
    {
        if (jobId.Length == 0) return;
        _ordered.RemoveAll(item => string.Equals(item, jobId, StringComparison.Ordinal));
        _ordered.Add(jobId);
        if (_ordered.Count > _cap) _ordered.RemoveRange(0, _ordered.Count - _cap);
    }

    public List<string> ToList() => new(_ordered);

    public static DeletedJobIds FromList(IEnumerable<string>? ids, int cap = 5000)
    {
        var set = new DeletedJobIds(cap);
        if (ids is null) return set;
        foreach (var id in ids) set.Add(id);
        return set;
    }
}

/// <summary>Журнал номеров удалённых заявок на компьютере мастера (Sessions/master-deleted-jobs.json).</summary>
internal static class DeletedJobsStore
{
    private static readonly string FilePathValue = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert", "Sessions",
        "master-deleted-jobs.json");

    public static DeletedJobIds Load()
    {
        var text = AtomicFileStore.ReadAllText(FilePathValue) ??
                   AtomicFileStore.ReadAllText(FilePathValue + ".bak");
        if (string.IsNullOrWhiteSpace(text)) return new DeletedJobIds();
        try { return DeletedJobIds.FromList(JsonSerializer.Deserialize<List<string>>(text)); }
        catch { return new DeletedJobIds(); }
    }

    public static void Save(DeletedJobIds ids)
    {
        try { AtomicFileStore.WriteAllText(FilePathValue, JsonSerializer.Serialize(ids.ToList())); }
        catch { }
    }
}

/// <summary>Локальная копия чужих заявок у менеджера — чтобы список был виден и без связи
/// (Sessions/manager-remote-jobs.json). При подключении мастер присылает свежий снимок.</summary>
internal static class RemoteJobsStore
{
    private static readonly string FilePathValue = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert", "Sessions",
        "manager-remote-jobs.json");

    public static List<RemoteJobInfo> Load()
    {
        var text = AtomicFileStore.ReadAllText(FilePathValue) ??
                   AtomicFileStore.ReadAllText(FilePathValue + ".bak");
        if (string.IsNullOrWhiteSpace(text)) return new List<RemoteJobInfo>();
        try
        {
            return JsonSerializer.Deserialize<List<RemoteJobInfo>>(text)?
                .Where(job => job.JobId.Length > 0).ToList() ?? new List<RemoteJobInfo>();
        }
        catch { return new List<RemoteJobInfo>(); }
    }

    public static void Save(IEnumerable<RemoteJobInfo> jobs)
    {
        try { AtomicFileStore.WriteAllText(FilePathValue, JsonSerializer.Serialize(jobs.ToList())); }
        catch { }
    }
}

/// <summary>Чистая логика полного снимка: какие известные чужие заявки надо убрать,
/// если их не было в свежем снимке мастера.</summary>
internal static class RemoteJobsSync
{
    public static List<string> MissingFromSnapshot(IEnumerable<string> knownIds, IReadOnlyCollection<string> snapshotIds) =>
        knownIds.Where(id => !snapshotIds.Contains(id, StringComparer.Ordinal)).ToList();
}

/// <summary>Правила массовой очистки списка заявок у мастера (2.34):
/// порог в днях для каждой группы; null — группу не трогаем, 0 — удаляем все в группе.</summary>
internal sealed record CleanupRules(int? DoneOlderThanDays, int? WaitingOlderThanDays, int? AwaitingReportOlderThanDays)
{
    public bool AnyEnabled => DoneOlderThanDays.HasValue || WaitingOlderThanDays.HasValue || AwaitingReportOlderThanDays.HasValue;
}

/// <summary>Чистая логика массовой очистки: какие заявки попадают под выбранные правила.
/// Возраст считается от завершения (для завершённых и ждущих отчёт) или от создания.</summary>
internal static class JobCleanup
{
    public static bool Matches(CleanupRules rules, string status, DateTime receivedAt, DateTime completedAt, DateTime now)
    {
        var normalized = JobStatus.Normalize(status);
        int? threshold = normalized switch
        {
            JobStatus.Done => rules.DoneOlderThanDays,
            JobStatus.AwaitingReport => rules.AwaitingReportOlderThanDays,
            JobStatus.Waiting => rules.WaitingOlderThanDays,
            _ => null // «В работе» массово не удаляем никогда — это живой ремонт
        };
        if (!threshold.HasValue) return false;
        var basis = normalized is JobStatus.Done or JobStatus.AwaitingReport
            ? (completedAt == default ? receivedAt : completedAt)
            : receivedAt;
        if (basis == default) basis = now; // без даты считаем свежей — не удаляем
        return (now - basis).TotalDays >= threshold.Value;
    }

    public static List<string> SelectIds<T>(IEnumerable<T> jobs, CleanupRules rules, DateTime now,
        Func<T, string> id, Func<T, string> status, Func<T, DateTime> receivedAt, Func<T, DateTime> completedAt) =>
        jobs.Where(job => Matches(rules, status(job), receivedAt(job), completedAt(job), now))
            .Select(id).ToList();
}
