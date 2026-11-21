using System.Text.Json;

namespace RepairAlert;

internal static class MessageTypes
{
    public const string Hello = "hello";
    public const string HelloOk = "hello_ok";
    public const string PairRequired = "pair_required";
    public const string Pair = "pair";
    public const string PairOk = "pair_ok";
    public const string AuthFail = "auth_fail";
    public const string SyncRequest = "sync_request";
    public const string JobState = "job_state";
    public const string JobRequest = "job_request";
    public const string JobAccepted = "job_accepted";
    public const string JobCompleted = "job_completed";
    public const string ManagerCompleted = "manager_completed";
    public const string JobArchived = "job_archived";
    public const string ReportSaved = "report_saved";
    public const string MessageAck = "message_ack";
    public const string Chat = "chat";
    public const string FileOffer = "file_offer";
    public const string FileAccept = "file_accept";
    public const string FileEnd = "file_end";
    public const string FileCancel = "file_cancel";
    public const string Read = "read";
    public const string JobReminder = "job_reminder";
    // Принудительное удаление зависшей заявки без сохранения отчёта
    // (отправляет та сторона, которая убирает заявку у себя).
    // С версии 2.33 — общее «удалить заявку у всех»: мастер удаляет запись,
    // запоминает её номер и рассылает команду всем менеджерам.
    public const string JobForceRemoved = "job_force_removed";
    // Карточка заявки для общего списка: мастер рассылает менеджерам
    // состояние каждой заявки (создана/в работе/ждёт отчёт/завершена)
    public const string JobInfo = "job_info";
    // Маркеры полного снимка списка заявок при подключении менеджера:
    // между ними мастер шлёт JobInfo по каждой заявке; после End менеджер
    // убирает у себя чужие заявки, которых в снимке не было (они удалены)
    public const string JobSyncBegin = "job_sync_begin";
    public const string JobSyncEnd = "job_sync_end";
    // Правка своего сообщения чата: MessageId — сообщение-цель, Text — новый текст
    public const string ChatEdit = "chat_edit";
    // Правка полей заявки (текст, клиент, устройство, серийный номер, срочность).
    // Шлётся мастеру; мастер обновляет запись и рассылает JobInfo всем.
    public const string JobEdit = "job_edit";
    // Закрепление сообщения чата: MessageId — сообщение-цель, Reason — "pin" или "unpin"
    public const string ChatPin = "chat_pin";
    public const string Ping = "ping";
}

internal sealed record JobDetails(string Client = "", string Device = "", string Serial = "", bool Urgent = false,
    string Password = "")
{
    public bool HasAny => Client.Length > 0 || Device.Length > 0 || Serial.Length > 0 || Urgent
        || Password.Length > 0;
}

internal sealed class AppMessage
{
    public string Type { get; set; } = "";
    public string JobId { get; set; } = "";
    public string Text { get; set; } = "";
    public string Sender { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string MachineName { get; set; } = "";
    public bool Accepted { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public long Offset { get; set; }
    public string Sha256 { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Comment { get; set; } = "";
    /// <summary>Статус заявки в карточке JobInfo: waiting / accepted / awaiting_report / done
    /// (см. JobStatus). Поле добавлено в 2.33; старые версии его просто игнорируют.</summary>
    public string Status { get; set; } = "";
    public string Client { get; set; } = "";
    public string Device { get; set; } = "";
    public string Serial { get; set; } = "";
    public bool Urgent { get; set; }
    public string Nonce { get; set; } = "";
    public string Sig { get; set; } = "";
    public int ProtocolVersion { get; set; } = SecurityService.ProtocolVersion;
    public string AppVersion { get; set; } = SecurityService.AppVersion;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static AppMessage? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<AppMessage>(json); }
        catch (JsonException) { return null; }
    }

    public static AppMessage Hello(string clientId, string userName, string machineName) => new()
    { Type = MessageTypes.Hello, ClientId = clientId, UserName = userName, MachineName = machineName, Sender = userName };

    public static AppMessage JobRequest(string jobId, string text, string managerName = "Менеджер",
        JobDetails? details = null)
    {
        var message = new AppMessage
        { Type = MessageTypes.JobRequest, JobId = jobId, Text = text, Sender = managerName };
        if (details is not null)
        {
            message.Client = details.Client;
            message.Device = details.Device;
            message.Serial = details.Serial;
            message.Urgent = details.Urgent;
            // Пароль едет в Reason: в job-сообщениях оно не используется,
            // а новое поле сломало бы HMAC-подписи у старых версий
            // (та же схема, что у пачки файлов в Comment).
            message.Reason = details.Password;
        }
        return message;
    }

    public static AppMessage SyncRequest(string jobId, string text, JobDetails? details = null)
    {
        var message = new AppMessage
        { Type = MessageTypes.SyncRequest, JobId = jobId, Text = text, Sender = "Менеджер" };
        if (details is not null)
        {
            message.Client = details.Client;
            message.Device = details.Device;
            message.Serial = details.Serial;
            message.Urgent = details.Urgent;
            message.Reason = details.Password; // см. комментарий в JobRequest
        }
        return message;
    }

    public static AppMessage JobState(string jobId, string text, bool accepted, DateTime startedAt) => new()
    { Type = MessageTypes.JobState, JobId = jobId, Text = text, Accepted = accepted, StartedAt = startedAt, Sender = "Мастер" };

    public static AppMessage JobAccepted(string jobId, DateTime startedAt) => new()
    { Type = MessageTypes.JobAccepted, JobId = jobId, Sender = "Мастер", StartedAt = startedAt };

    public static AppMessage JobCompleted(string jobId, string text, DateTime startedAt, DateTime completedAt,
        string comment = "") => new()
    {
        Type = MessageTypes.JobCompleted, JobId = jobId, Text = text, Sender = "Мастер",
        StartedAt = startedAt, CompletedAt = completedAt, Comment = comment
    };

    public static AppMessage ManagerCompleted(string jobId, string text, DateTime startedAt, DateTime completedAt,
        string managerName = "Менеджер", string comment = "") => new()
    {
        Type = MessageTypes.ManagerCompleted, JobId = jobId, Text = text, Sender = managerName,
        StartedAt = startedAt, CompletedAt = completedAt, Comment = comment,
        MessageId = Guid.NewGuid().ToString("N")
    };

    public static AppMessage JobArchived(string jobId) => new()
    { Type = MessageTypes.JobArchived, JobId = jobId, Sender = "Менеджер" };

    public static AppMessage ReportSaved(string jobId) => new()
    { Type = MessageTypes.ReportSaved, JobId = jobId, Sender = "Менеджер" };

    public static AppMessage JobForceRemoved(string jobId, string sender) => new()
    { Type = MessageTypes.JobForceRemoved, JobId = jobId, Sender = sender };

    /// <summary>Карточка заявки для общего списка у менеджеров (и снимок, и обновления).
    /// SentAt несёт дату создания заявки (UTC), чтобы у всех сортировка была одинаковой.</summary>
    public static AppMessage JobInfo(string jobId, string text, string ownerClientId, string ownerName,
        string ownerMachine, string status, DateTime receivedAt, DateTime startedAt, DateTime completedAt,
        string comment, string client, string device, string serial, bool urgent,
        string password = "") => new()
    {
        Type = MessageTypes.JobInfo,
        JobId = jobId,
        Text = text,
        ClientId = ownerClientId,
        UserName = ownerName,
        MachineName = ownerMachine,
        Status = status,
        SentAt = receivedAt == default ? DateTime.UtcNow : receivedAt.ToUniversalTime(),
        StartedAt = startedAt == default ? null : startedAt.ToUniversalTime(),
        CompletedAt = completedAt == default ? null : completedAt.ToUniversalTime(),
        Comment = comment,
        Client = client,
        Device = device,
        Serial = serial,
        Urgent = urgent,
        Reason = password // пароль едет в Reason, см. JobRequest
    };

    /// <summary>Правка полей заявки менеджером (или мастером — для единообразия).
    /// Меняются только описательные поля; статус и даты остаются как были.</summary>
    public static AppMessage JobEdit(string jobId, string text, string client, string device,
        string serial, bool urgent, string sender, string password = "") => new()
    {
        Type = MessageTypes.JobEdit,
        JobId = jobId,
        Text = text,
        Client = client,
        Device = device,
        Serial = serial,
        Urgent = urgent,
        Sender = sender,
        Reason = password // пароль едет в Reason, см. JobRequest
    };

    public static AppMessage ChatEdited(string jobId, string targetMessageId, string newText) => new()
    { Type = MessageTypes.ChatEdit, JobId = jobId, MessageId = targetMessageId, Text = newText };

    public static AppMessage ChatPinned(string jobId, string targetMessageId, bool pinned) => new()
    { Type = MessageTypes.ChatPin, JobId = jobId, MessageId = targetMessageId, Reason = pinned ? "pin" : "unpin" };

    public static AppMessage Chat(string jobId, string sender, string text, string messageId = "") => new()
    { Type = MessageTypes.Chat, JobId = jobId, Text = text, Sender = sender, MessageId = messageId };

    public static AppMessage FileOffer(string chatId, string sender, string fileName, long size, string sha256,
        string messageId) => new()
    {
        Type = MessageTypes.FileOffer, JobId = chatId, FileName = fileName, Size = size,
        Sha256 = sha256, Sender = sender, MessageId = messageId
    };

    public static AppMessage FileAccept(string chatId, string messageId, long offset) => new()
    { Type = MessageTypes.FileAccept, JobId = chatId, MessageId = messageId, Offset = offset, Sender = "Получатель" };

    public static AppMessage FileEnd(string chatId, string messageId) => new()
    { Type = MessageTypes.FileEnd, JobId = chatId, MessageId = messageId, Sender = "Отправитель" };

    public static AppMessage FileCancel(string chatId, string messageId, string reason) => new()
    { Type = MessageTypes.FileCancel, JobId = chatId, MessageId = messageId, Reason = reason, Sender = "Отправитель" };

    public static AppMessage Ack(string jobId, string messageId) => new()
    { Type = MessageTypes.MessageAck, JobId = jobId, MessageId = messageId, Sender = "Получатель" };

    public static AppMessage ReadReceipt(string jobId, IEnumerable<string> messageIds) => new()
    { Type = MessageTypes.Read, JobId = jobId, Text = string.Join(",", messageIds), Sender = "Получатель" };

    public static AppMessage JobReminderMessage(string jobId, string text, string managerName = "Менеджер") => new()
    {
        Type = MessageTypes.JobReminder, JobId = jobId, Text = text, Sender = managerName,
        MessageId = Guid.NewGuid().ToString("N")
    };
}
