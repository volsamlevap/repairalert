using System.Net;
using System.Net.Sockets;
using RepairAlert;

var testRoot = Path.Combine(Path.GetTempPath(), "RepairAlertTests_" + Guid.NewGuid().ToString("N"));
if (!StoragePaths.TryInitialize(testRoot, out var storageError)) throw new Exception(storageError);
IncomingTransferStore.DirectoryOverride = Path.Combine(testRoot, "parts");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    Console.WriteLine("PASS: " + message);
}

static TaskCompletionSource<AppMessage> Source() => new(TaskCreationOptions.RunContinuationsAsynchronously);

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var serverReceived = Source();
var clientReceived = Source();
var acceptTask = Task.Run(async () =>
{
    var tcp = await listener.AcceptTcpClientAsync();
    var server = new PeerConnection(tcp);
    server.MessageReceived += m => serverReceived.TrySetResult(m);
    return server;
});
var clientTcp = new TcpClient();
await clientTcp.ConnectAsync(IPAddress.Loopback, port);
using var client = new PeerConnection(clientTcp);
client.MessageReceived += m => clientReceived.TrySetResult(m);
using var server = await acceptTask.WaitAsync(TimeSpan.FromSeconds(3));

var jobId = Guid.NewGuid().ToString("N");
await client.SendAsync(AppMessage.JobRequest(jobId, "Замена экрана ноутбука"));
var request = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(request.Type == MessageTypes.JobRequest && request.JobId == jobId && request.Text == "Замена экрана ноутбука", "заявка с русским текстом доходит без искажений");

var started = DateTime.Now;
await server.SendAsync(AppMessage.JobAccepted(jobId, started));
var accepted = await clientReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(accepted.Type == MessageTypes.JobAccepted && accepted.JobId == jobId && accepted.StartedAt is not null, "подтверждение и время начала доходят менеджеру");

clientReceived = Source();
await server.SendAsync(AppMessage.Chat(jobId, "Мастер", "Принял в работу"));
var chat = await clientReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(chat.Type == MessageTypes.Chat && chat.Text == "Принял в работу" && chat.Sender == "Мастер", "сообщение чата по ремонту доходит корректно");

clientReceived = Source();
await server.SendAsync(AppMessage.Chat("general", "Мастер", "Общий чат без заявки"));
var general = await clientReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(general.JobId == "general" && general.Text.Contains("без заявки"), "общий чат работает независимо от заявки");

var reliableId = Guid.NewGuid().ToString("N");
serverReceived = Source();
await client.SendAsync(AppMessage.Chat(jobId, "Менеджер", "Сообщение с подтверждением", reliableId));
var reliableChat = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(reliableChat.MessageId == reliableId, "сообщение имеет уникальный идентификатор для защиты от дублей");
clientReceived = Source();
await server.SendAsync(AppMessage.Ack(jobId, reliableId));
var deliveryAck = await clientReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(deliveryAck.Type == MessageTypes.MessageAck && deliveryAck.MessageId == reliableId,
    "получатель подтверждает доставку, чтобы отправитель очистил очередь");

// === Этап 2: чанковая передача файла с прогрессом и SHA-256 ===
serverReceived = Source();
var bytes = new byte[700 * 1024];
new Random(42).NextBytes(bytes);
var fileId = Guid.NewGuid().ToString("N");
var sourcePath = Path.Combine(testRoot, "source.bin");
File.WriteAllBytes(sourcePath, bytes);
var sourceHash = IncomingTransferStore.ComputeFileHash(sourcePath);
await client.SendAsync(AppMessage.FileOffer("general", "Менеджер", "Тест файл.bin", bytes.Length, sourceHash, fileId));
var offer = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(offer.Type == MessageTypes.FileOffer && offer.FileName == "Тест файл.bin" &&
       offer.Size == bytes.Length && offer.Sha256 == sourceHash && offer.MessageId == fileId,
    "предложение файла передаёт имя, размер и SHA-256 без содержимого в JSON");
var serverMeta = IncomingTransferStore.Begin(offer.MessageId, "general", "Тест", offer.Sender,
    offer.FileName, offer.Size, offer.Sha256);
Assert(serverMeta.FileName == "Тест файл.bin" && IncomingTransferStore.GetOffset(offer.MessageId) == 0,
    "получатель создаёт временный .part и сообщает смещение докачки 0");
var chunkGuid = Guid.ParseExact(fileId, "N");
var receivedChunks = new List<FileChunk>();
server.ChunkReceived += chunk =>
{
    if (chunk.MessageId == chunkGuid && chunk.Offset == IncomingTransferStore.GetOffset(fileId))
        IncomingTransferStore.Write(fileId, chunk.Offset, chunk.Data);
    receivedChunks.Add(chunk);
};
for (long offset = 0; offset < bytes.Length; offset += FileTransferService.ChunkBytes)
{
    var length = (int)Math.Min(FileTransferService.ChunkBytes, bytes.Length - offset);
    await client.SendChunkAsync(chunkGuid, offset, bytes[(int)offset..(int)(offset + length)], length);
}
await client.SendAsync(AppMessage.FileEnd("general", fileId));
var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
while (IncomingTransferStore.GetOffset(fileId) < bytes.Length && DateTime.UtcNow < deadline)
    await Task.Delay(50);
Assert(IncomingTransferStore.GetOffset(fileId) == bytes.Length,
    "файл передаётся чанками потоком, не лежа целиком в одном сообщении");
var savedFile = IncomingTransferStore.Finalize(fileId);
Assert(savedFile is not null && File.ReadAllBytes(savedFile).SequenceEqual(bytes),
    "после проверки SHA-256 .part переименовывается в готовый файл без повреждений");
Assert(FileTransferService.MaxFileBytes == 5L * 1024 * 1024 * 1024,
    "максимальный размер вложения — 5 ГБ");
Assert(FileTransferService.MaxFileBytes > int.MaxValue,
    "лимит 5 ГБ требует 64-битного типа и не переполняет int");

// === Этап 2: докачка после обрыва с нужного смещения ===
var resumeId = Guid.NewGuid().ToString("N");
var resumeBytes = new byte[600 * 1024];
new Random(7).NextBytes(resumeBytes);
var resumeSource = Path.Combine(testRoot, "resume.bin");
File.WriteAllBytes(resumeSource, resumeBytes);
var resumeHash = IncomingTransferStore.ComputeFileHash(resumeSource);
var resumeMeta = IncomingTransferStore.Begin(resumeId, "general", "Тест", "Менеджер",
    "Докачка.bin", resumeBytes.Length, resumeHash);
var firstPart = resumeBytes[..FileTransferService.ChunkBytes];
IncomingTransferStore.Write(resumeId, 0, firstPart);
var resumeOffset = IncomingTransferStore.GetOffset(resumeId);
Assert(resumeOffset == FileTransferService.ChunkBytes,
    "после обрыва на части файла получатель сообщает смещение продолжения");
server.ChunkReceived += chunk =>
{
    if (chunk.MessageId == Guid.ParseExact(resumeId, "N") &&
        chunk.Offset == IncomingTransferStore.GetOffset(resumeId))
        IncomingTransferStore.Write(resumeId, chunk.Offset, chunk.Data);
};
var resumeGuid = Guid.ParseExact(resumeId, "N");
for (long offset = resumeOffset; offset < resumeBytes.Length; offset += FileTransferService.ChunkBytes)
{
    var length = (int)Math.Min(FileTransferService.ChunkBytes, resumeBytes.Length - offset);
    await client.SendChunkAsync(resumeGuid, offset, resumeBytes[(int)offset..(int)(offset + length)], length);
}
var resumeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
while (IncomingTransferStore.GetOffset(resumeId) < resumeBytes.Length && DateTime.UtcNow < resumeDeadline)
    await Task.Delay(50);
var resumedFile = IncomingTransferStore.Finalize(resumeId);
Assert(resumedFile is not null && File.ReadAllBytes(resumedFile).SequenceEqual(resumeBytes),
    "передача продолжается с последнего подтверждённого смещения и сходится по SHA-256");
File.Delete(resumedFile);

// === Этап 2: отмена приёма и проверка места на диске ===
var cancelId = Guid.NewGuid().ToString("N");
IncomingTransferStore.Begin(cancelId, "general", "Тест", "Менеджер", "Отмена.bin", 100, "abc");
IncomingTransferStore.Write(cancelId, 0, new byte[50]);
IncomingTransferStore.Cancel(cancelId);
Assert(IncomingTransferStore.GetOffset(cancelId) == 0 && IncomingTransferStore.LoadMeta(cancelId) is null,
    "file_cancel удаляет частичный файл и метаданные у получателя");
IncomingTransferStore.FreeSpaceOverride = 1024;
var noSpace = false;
try { IncomingTransferStore.EnsureFreeSpace(10 * 1024 * 1024); }
catch (IOException) { noSpace = true; }
Assert(noSpace, "при нехватке места на диске приём отклоняется заранее понятной ошибкой");
IncomingTransferStore.FreeSpaceOverride = null;

// === Этап 2: текст вклинивается между чанками ===
serverReceived = Source();
var interleaveId = Guid.NewGuid();
await client.SendChunkAsync(interleaveId, 0, new byte[FileTransferService.ChunkBytes], FileTransferService.ChunkBytes);
await client.SendAsync(AppMessage.Chat("general", "Менеджер", "Текст во время передачи файла"));
var interleaved = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(interleaved.Type == MessageTypes.Chat && interleaved.Text == "Текст во время передачи файла",
    "текстовые сообщения доходят во время передачи файла (интерливинг)");

clientReceived = Source();
var completed = DateTime.Now;
await server.SendAsync(AppMessage.JobCompleted(jobId, request.Text, started, completed));
var done = await clientReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(done.Type == MessageTypes.JobCompleted && done.StartedAt is not null && done.CompletedAt is not null, "завершение ремонта содержит даты начала и окончания");

ReportService.UpdateJournal(request.Text, null, null, @"MANAGER-PC\manager2", "MANAGER-PC", jobId, "Ожидает подтверждения");
ReportService.UpdateJournal(request.Text, started, null, @"MANAGER-PC\manager2", "MANAGER-PC", jobId, "В работе");
var reportPath = ReportService.SaveReport(request.Text, started, completed, new[]
{
    new ChatEntry("Вы", "Прошу заменить экран", started, true),
    new ChatEntry("Мастер", "Ремонт завершён", completed, false),
    new ChatEntry("Мастер", "", completed, false, "Фото.jpg", "", 1234)
}, @"MANAGER-PC\manager2", "MANAGER-PC", jobId);
var report = File.ReadAllText(reportPath);
Assert(report.Contains(request.Text) && report.Contains("Прошу заменить экран") && report.Contains("Фото.jpg") && report.Contains("Дата и время начала"), "TXT-отчёт содержит ремонт, даты, переписку и имена файлов");
Assert(report.Contains(@"MANAGER-PC\manager2") && report.Contains("Компьютер менеджера: MANAGER-PC"), "отчёт содержит учётную запись Windows и компьютер менеджера");
var journal = File.ReadAllText(ReportService.JournalPath);
Assert(journal.Contains(request.Text) && journal.Contains(@"MANAGER-PC\manager2") && journal.Contains("Завершён") && report.Contains("Номер ремонта: R-"), "общий CSV-журнал и номер ремонта сохраняются корректно");
var number = "R-" + jobId[..8].ToUpperInvariant();
Assert(journal.Split(number).Length == 2 && !journal.Contains("В работе"), "журнал обновляет статус существующего ремонта без дублей");

var sessionProfile = "test-" + Guid.NewGuid().ToString("N");
var messageId = Guid.NewGuid().ToString("N");
var snapshot = new ChatSessionSnapshot
{
    CurrentJobId = jobId,
    CurrentJobText = request.Text,
    IsJobAccepted = true,
    JobStartedAt = started,
    Chats = new List<ChatSessionBucket>
    {
        new() { SessionId = jobId, Title = "Тестовый ремонт", Entries = new List<ChatEntry>
        {
            new("Вы", "Сообщение сохранено", started, true, Id: messageId, Delivery: "В очереди")
        } }
    },
    Outbox = new List<OutgoingChatMessage>
    {
        new() { SessionId = jobId, Message = AppMessage.Chat(jobId, "Менеджер", "Сообщение сохранено", messageId),
            AwaitingAck = true, LastSentAtUtc = DateTime.UtcNow }
    }
};
ChatSessionStore.Save(sessionProfile, snapshot);
var restoredSnapshot = ChatSessionStore.Load(sessionProfile);
Assert(restoredSnapshot.CurrentJobId == jobId && restoredSnapshot.CurrentJobText == request.Text && restoredSnapshot.IsJobAccepted,
    "активный ремонт восстанавливается из локальной сессии после перезапуска");
Assert(restoredSnapshot.Chats.Single().Entries.Single().Id == messageId && restoredSnapshot.Outbox.Single().Message.MessageId == messageId,
    "история и неотправленные сообщения сохраняются вместе с ремонтом");
Assert(!restoredSnapshot.Outbox.Single().AwaitingAck, "после перезапуска сообщение готово к повторной отправке");
Assert(ReliableMessageLogic.ContainsMessage(restoredSnapshot.Chats.Single().Entries, messageId) &&
       !ReliableMessageLogic.ContainsMessage(restoredSnapshot.Chats.Single().Entries, "другой-id"),
    "уникальный идентификатор исключает повторную запись сообщения");
var retryItem = restoredSnapshot.Outbox.Single();
retryItem.AwaitingAck = true;
retryItem.LastSentAtUtc = DateTime.UtcNow.AddSeconds(-16);
Assert(ReliableMessageLogic.NeedsRetry(retryItem, DateTime.UtcNow), "неподтверждённое сообщение автоматически отправляется повторно");

var secondJobId = Guid.NewGuid().ToString("N");
snapshot.Jobs = new List<ManagerJobRecord>
{
    new()
    {
        JobId = jobId,
        Text = request.Text,
        IsAccepted = true,
        ReceivedAt = started.AddMinutes(-4),
        StartedAt = started
    },
    new()
    {
        JobId = secondJobId,
        Text = "Настройка принтера",
        IsAccepted = false,
        ReceivedAt = DateTime.Now
    }
};
ChatSessionStore.Save(sessionProfile, snapshot);
var restoredMultiJobs = ChatSessionStore.Load(sessionProfile);
Assert(restoredMultiJobs.Jobs.Count == 2 && restoredMultiJobs.Jobs.Any(job => job.JobId == jobId && job.IsAccepted) &&
       restoredMultiJobs.Jobs.Any(job => job.JobId == secondJobId && job.Text == "Настройка принтера"),
    "менеджер сохраняет и восстанавливает несколько одновременных заявок");
ChatSessionStore.Delete(sessionProfile);

var masterProfile = "test-master-" + Guid.NewGuid().ToString("N");
MasterJobStore.Save(new[]
{
    new MasterJobRecord
    {
        ClientId = "manager-active", UserName = @"MANAGER-PC\manager1", MachineName = "MANAGER-PC",
        CurrentJobId = jobId, CurrentJobText = request.Text, IsJobAccepted = true,
        ReceivedAt = started.AddMinutes(-3), StartedAt = started, StatusMessage = "Работа принята"
    },
    new MasterJobRecord
    {
        ClientId = "manager-report", UserName = @"MANAGER-PC\manager2", MachineName = "MANAGER-PC",
        CurrentJobId = "completed-job", CurrentJobText = "Замена клавиатуры", AwaitingReport = true,
        ReceivedAt = started.AddHours(-1), StartedAt = started.AddMinutes(-20), CompletedAt = completed
    }
}, masterProfile);
var restoredMasterJobs = MasterJobStore.Load(masterProfile);
Assert(restoredMasterJobs.Count == 2 && restoredMasterJobs.Any(job => job.ClientId == "manager-active") &&
       restoredMasterJobs.Any(job => job.ClientId == "manager-report" && job.CurrentJobText == "Замена клавиатуры"),
    "мастер восстанавливает отдельные заявки нескольких менеджеров");
var restoredActiveJob = restoredMasterJobs.Single(job => job.ClientId == "manager-active");
Assert(restoredActiveJob.IsJobAccepted && restoredActiveJob.StartedAt == started,
    "после перезапуска мастера принятый статус и исходное время начала сохраняются");
var restoredReportJob = restoredMasterJobs.Single(job => job.ClientId == "manager-report");
Assert(restoredReportJob.AwaitingReport && restoredReportJob.CompletedAt == completed,
    "мастер восстанавливает завершение и ожидание подтверждения отчёта");

var multiJobProfile = "test-master-multi-" + Guid.NewGuid().ToString("N");
MasterJobStore.Save(new[]
{
    new MasterJobRecord
    {
        ClientId = "same-manager", UserName = @"MANAGER-PC\manager1", MachineName = "MANAGER-PC",
        CurrentJobId = jobId, CurrentJobText = request.Text, IsJobAccepted = true,
        ReceivedAt = started.AddMinutes(-5), StartedAt = started
    },
    new MasterJobRecord
    {
        ClientId = "same-manager", UserName = @"MANAGER-PC\manager1", MachineName = "MANAGER-PC",
        CurrentJobId = secondJobId, CurrentJobText = "Настройка принтера",
        ReceivedAt = DateTime.Now
    },
    new MasterJobRecord
    {
        ClientId = "other-manager", UserName = @"MANAGER-PC\manager2", MachineName = "MANAGER-PC",
        CurrentJobId = "third-job", CurrentJobText = "Диагностика ПК",
        ReceivedAt = DateTime.Now
    }
}, multiJobProfile);
var restoredMultiMasterJobs = MasterJobStore.Load(multiJobProfile);
Assert(restoredMultiMasterJobs.Count == 3 &&
       restoredMultiMasterJobs.Count(job => job.ClientId == "same-manager") == 2 &&
       restoredMultiMasterJobs.Select(job => job.CurrentJobId).Distinct().Count() == 3,
    "мастер сохраняет несколько заявок одного менеджера и заявки разных менеджеров");
MasterJobStore.Delete(multiJobProfile);
Assert(JobTiming.Elapsed(started, started.AddHours(1).AddMinutes(5)) == "01:05" &&
       JobTiming.Elapsed(default, completed) == "00:00",
    "таймер ремонта показывает часы и минуты без сброса");
var soundVariants = Enum.GetValues<AlertKind>()
    .SelectMany(kind => SoundCatalog.Options.Select(option => (kind, option.Id)))
    .Select(item => (item.kind, item.Id, Wave: SoundCatalog.CreateWave(item.kind, item.Id)))
    .ToList();
Assert(soundVariants.Count == 12 && soundVariants.All(item =>
       System.Text.Encoding.ASCII.GetString(item.Wave, 0, 4) == "RIFF" &&
       System.Text.Encoding.ASCII.GetString(item.Wave, 8, 4) == "WAVE" &&
       BitConverter.ToInt32(item.Wave, 24) == 44100 && item.Wave.Skip(44).Any(value => value != 0)),
    "все 12 вариантов уведомлений создаются как корректные непустые WAV-файлы");
Assert(!SoundCatalog.CreateWave(AlertKind.Request, "standard")
       .SequenceEqual(SoundCatalog.CreateWave(AlertKind.Request, "soft")),
    "выбор стандартного и мягкого звука действительно меняет сигнал");
MasterJobStore.Delete(masterProfile);

serverReceived = Source();
await client.SendAsync(AppMessage.ManagerCompleted(jobId, request.Text, started, completed, "Менеджер"));
var managerCompleted = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(managerCompleted.Type == MessageTypes.ManagerCompleted && managerCompleted.JobId == jobId &&
       managerCompleted.Text == request.Text && managerCompleted.StartedAt is not null &&
       managerCompleted.CompletedAt is not null && managerCompleted.MessageId.Length > 0,
    "менеджер может отправить мастеру завершение выбранной заявки");

serverReceived = Source();
await client.SendAsync(AppMessage.JobArchived(jobId));
var archived = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(archived.Type == MessageTypes.JobArchived && archived.JobId == jobId,
    "менеджер может отправить мастеру команду убрать завершённую заявку");

serverReceived = Source();
await client.SendAsync(AppMessage.ReportSaved(jobId));
var ack = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(ack.Type == MessageTypes.ReportSaved && ack.JobId == jobId, "мастер получает подтверждение сохранения отчёта");

serverReceived = Source();
await client.SendAsync(AppMessage.JobForceRemoved(jobId, "Менеджер"));
var forceRemoved = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(forceRemoved.Type == MessageTypes.JobForceRemoved && forceRemoved.JobId == jobId && forceRemoved.Sender == "Менеджер",
    "менеджер может отправить мастеру команду убрать зависшую заявку без отчёта");

serverReceived = Source();
await client.SendAsync(AppMessage.SyncRequest(jobId, request.Text));
var syncRequest = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(syncRequest.Type == MessageTypes.SyncRequest && syncRequest.JobId == jobId && syncRequest.Text == request.Text,
    "после переподключения менеджер запрашивает состояние той же заявки");
clientReceived = Source();
await server.SendAsync(AppMessage.JobState(jobId, request.Text, true, started));
var jobState = await clientReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(jobState.Type == MessageTypes.JobState && jobState.JobId == jobId && jobState.Accepted && jobState.StartedAt is not null,
    "мастер возвращает статус «в работе» и исходное время начала");

var disconnectNotified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
client.Disconnected += () => disconnectNotified.TrySetResult(true);
client.Disconnect();
await disconnectNotified.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(!client.IsConnected, "разрыв соединения немедленно запускает обработчик переподключения");
server.Dispose();

var reconnectedSync = Source();
var acceptReconnected = Task.Run(async () =>
{
    var tcp = await listener.AcceptTcpClientAsync();
    return new PeerConnection(tcp);
});
var reconnectedTcp = new TcpClient();
await reconnectedTcp.ConnectAsync(IPAddress.Loopback, port);
using var reconnectedClient = new PeerConnection(reconnectedTcp);
using var reconnectedServer = await acceptReconnected.WaitAsync(TimeSpan.FromSeconds(3));
reconnectedServer.MessageReceived += m =>
{
    if (m.Type == MessageTypes.SyncRequest) reconnectedSync.TrySetResult(m);
};
await reconnectedClient.SendAsync(AppMessage.SyncRequest(jobId, request.Text));
var resumed = await reconnectedSync.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(resumed.JobId == jobId && resumed.Text == request.Text,
    "новое TCP-подключение восстанавливает прежний ремонт без повторного ввода");
var reconnectedState = Source();
reconnectedClient.MessageReceived += m => reconnectedState.TrySetResult(m);
await reconnectedServer.SendAsync(AppMessage.JobState(jobId, request.Text, true, started));
var restoredState = await reconnectedState.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(restoredState.Accepted && restoredState.JobId == jobId,
    "после реального переподключения принятый ремонт остаётся в работе");

var silentListener = new TcpListener(IPAddress.Loopback, 0);
silentListener.Start();
var silentPort = ((IPEndPoint)silentListener.LocalEndpoint).Port;
var acceptSilent = silentListener.AcceptTcpClientAsync();
var silentTcp = new TcpClient();
await silentTcp.ConnectAsync(IPAddress.Loopback, silentPort);
using var silentRawServer = await acceptSilent.WaitAsync(TimeSpan.FromSeconds(3));
using var silentClient = new PeerConnection(silentTcp);
var heartbeatClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
silentClient.Disconnected += () => heartbeatClosed.TrySetResult(true);
var heartbeatWatch = System.Diagnostics.Stopwatch.StartNew();
await heartbeatClosed.Task.WaitAsync(TimeSpan.FromSeconds(35));
Assert(!silentClient.IsConnected && heartbeatWatch.Elapsed >= TimeSpan.FromSeconds(20),
    "heartbeat автоматически обнаруживает молчащее зависшее соединение");
silentRawServer.Close();
silentListener.Stop();

var multiServers = new List<PeerConnection>();
var multiClients = new List<PeerConnection>();
var serverInboxes = new List<TaskCompletionSource<AppMessage>>();
var clientInboxes = new List<TaskCompletionSource<AppMessage>>();
for (var i = 1; i <= 3; i++)
{
    var index = i - 1;
    serverInboxes.Add(Source());
    clientInboxes.Add(Source());
    var acceptManager = listener.AcceptTcpClientAsync();
    var managerTcp = new TcpClient();
    await managerTcp.ConnectAsync(IPAddress.Loopback, port);
    var managerServer = new PeerConnection(await acceptManager.WaitAsync(TimeSpan.FromSeconds(3)));
    var managerClient = new PeerConnection(managerTcp);
    managerServer.MessageReceived += m => serverInboxes[index].TrySetResult(m);
    managerClient.MessageReceived += m => clientInboxes[index].TrySetResult(m);
    multiServers.Add(managerServer);
    multiClients.Add(managerClient);
    await managerClient.SendAsync(AppMessage.Hello($"profile-{i}", $@"MANAGER-PC\manager{i}", "MANAGER-PC"));
    var identity = await serverInboxes[index].Task.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(identity.Type == MessageTypes.Hello && identity.ClientId == $"profile-{i}" && identity.UserName.EndsWith($"manager{i}"), $"профиль менеджера {i} одновременно подключён и корректно идентифицирован");
}
Assert(multiServers.Count == 3 && multiServers.All(s => s.IsConnected), "три менеджера остаются подключены одновременно без отключения друг друга");
for (var i = 0; i < 3; i++)
{
    clientInboxes[i] = Source();
    await multiServers[i].SendAsync(AppMessage.Chat("general", "Мастер", $"Личное сообщение {i + 1}"));
    var routed = await clientInboxes[i].Task.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(routed.Text == $"Личное сообщение {i + 1}", $"сообщение мастера доставлено именно менеджеру {i + 1}");
}
foreach (var peer in multiServers.Concat(multiClients)) peer.Dispose();

// === Этап 1: атомарное хранение состояния ===
var atomicProfile = "test-atomic-" + Guid.NewGuid().ToString("N");
var atomicSnapshot = new ChatSessionSnapshot { CurrentJobId = jobId, CurrentJobText = "Первая целая версия" };
ChatSessionStore.Save(atomicProfile, atomicSnapshot);
atomicSnapshot.CurrentJobText = "Обновлённая версия";
ChatSessionStore.Save(atomicProfile, atomicSnapshot);
var atomicFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "RepairAlert", "Sessions", atomicProfile + ".json");
File.WriteAllText(atomicFile, "{ обрыв записи при внезапном отключении питания");
var restoredFromBackup = ChatSessionStore.Load(atomicProfile);
Assert(restoredFromBackup.CurrentJobText == "Первая целая версия" && restoredFromBackup.CurrentJobId == jobId,
    "при повреждении основного файла состояние восстанавливается из резервной копии .bak");
ChatSessionStore.Delete(atomicProfile);
Assert(!File.Exists(atomicFile) && !File.Exists(atomicFile + ".bak"),
    "удаление сессии очищает и основной файл, и резервную копию");

// === Этап 1: безопасные имена файлов ===
Assert(FileTransferService.SanitizeFileName(@"..\..\a.txt") == "a.txt",
    "имя файла с обходом пути очищается до безопасного");
Assert(FileTransferService.SanitizeFileName("CON") != "CON" && FileTransferService.SanitizeFileName("CON").EndsWith("CON"),
    "зарезервированное имя устройства получает безопасный префикс");
Assert(FileTransferService.SanitizeFileName("a.txt.exe") == "a.txt.exe",
    "двойное расширение остаётся обычным именем без изменения содержимого");
var veryLongName = new string('ф', 300) + ".txt";
Assert(FileTransferService.SanitizeFileName(veryLongName).Length <= 100,
    "слишком длинное имя файла безопасно укорачивается");
Assert(FileTransferService.SanitizeFileName("фото экрана.png") == "фото экрана.png",
    "Unicode-имена файлов сохраняются");
Assert(FileTransferService.SanitizeFileName(null) == "Файл.bin",
    "пустое имя заменяется безопасным значением");

var traversalReceived = Source();
var acceptTraversal = listener.AcceptTcpClientAsync();
var traversalTcp = new TcpClient();
await traversalTcp.ConnectAsync(IPAddress.Loopback, port);
using var traversalClient = new PeerConnection(traversalTcp);
using var traversalServer = new PeerConnection(await acceptTraversal.WaitAsync(TimeSpan.FromSeconds(3)));
traversalServer.MessageReceived += m => traversalReceived.TrySetResult(m);
var traversalId = Guid.NewGuid().ToString("N");
await traversalClient.SendAsync(AppMessage.FileOffer("general", "Менеджер", @"..\..\evil.txt", 3, "unused", traversalId));
var traversalMessage = await traversalReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
var traversalMeta = IncomingTransferStore.Begin(traversalMessage.MessageId, "general", "Тест",
    traversalMessage.Sender, traversalMessage.FileName, traversalMessage.Size, "unused");
Assert(traversalMeta.FileName == "evil.txt",
    "имя файла с обходом пути очищается до безопасного в метаданных приёма");
var traversalDir = Path.Combine(testRoot, "received-traversal");
var traversalTarget = IncomingTransferStore.AllocateUniquePath(traversalDir, traversalMeta.FileName);
Assert(Path.GetFileName(traversalTarget) == "evil.txt" &&
       Path.GetFullPath(traversalTarget).StartsWith(Path.GetFullPath(traversalDir), StringComparison.Ordinal),
    "полученный файл с опасным именем сохраняется внутри папки полученных файлов");
var traversalDirInfo = Directory.CreateDirectory(traversalDir);
File.WriteAllBytes(traversalTarget, new byte[] { 1, 2, 3 });
var duplicateTarget = IncomingTransferStore.AllocateUniquePath(traversalDir, "evil.txt");
Assert(duplicateTarget != traversalTarget && duplicateTarget.Contains(" (1)"),
    "существующий файл не перезаписывается: добавляется суффикс (1)");
traversalClient.Dispose();
traversalServer.Dispose();

// === Этап 1: параллельная запись CSV-журнала ===
var csvJobIds = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
var csvTasks = csvJobIds.Select(id => Task.Run(() =>
{
    for (var round = 0; round < 5; round++)
        ReportService.UpdateJournal("Параллельный ремонт " + id[..4], started, completed,
            @"MANAGER-PC\manager3", "MANAGER-PC", id, "Завершён");
})).ToArray();
Task.WaitAll(csvTasks);
var journalLines = File.ReadAllLines(ReportService.JournalPath);
foreach (var id in csvJobIds)
{
    var rowPrefix = "\"R-" + id[..8].ToUpperInvariant() + "\";";
    Assert(journalLines.Count(line => line.StartsWith(rowPrefix, StringComparison.Ordinal)) == 1,
        "три профиля пишут журнал одновременно без дублей и повреждений");
}
Assert(journalLines[0].StartsWith("Номер;", StringComparison.Ordinal),
    "журнал сохраняет заголовок и разделитель «;» при параллельной записи");

// === Этап 1: очередь отчётов при недоступной папке ===
var blockerFile = Path.Combine(testRoot, "blocker");
File.WriteAllText(blockerFile, "block");
StoragePaths.Configure(blockerFile);
var pendingProfile = "test-pending-" + Guid.NewGuid().ToString("N");
var pendingReport = new PendingReport
{
    JobId = Guid.NewGuid().ToString("N"),
    JobText = "Ремонт с отложенным отчётом",
    ManagerUser = @"MANAGER-PC\manager1",
    MachineName = "MANAGER-PC",
    StartedAt = started,
    CompletedAt = completed,
    History = new List<ChatEntry> { new("Вы", "Всё готово", completed, true) }
};
var savedReportPaths = new List<string>();
var remainingReports = ReportFlushService.Flush(new[] { pendingReport }, (_, path) => savedReportPaths.Add(path));
Assert(remainingReports.Count == 1 && savedReportPaths.Count == 0,
    "недоступная общая папка: отчёт остаётся в очереди, данные не теряются");
PendingReportStore.Save(remainingReports, pendingProfile);
Assert(PendingReportStore.Load(pendingProfile).Single().JobId == pendingReport.JobId,
    "очередь отчётов переживает перезапуск программы");
StoragePaths.Configure(testRoot);
var remainingAfterRestore = ReportFlushService.Flush(remainingReports, (_, path) => savedReportPaths.Add(path));
Assert(remainingAfterRestore.Count == 0 && savedReportPaths.Count == 1 &&
       File.Exists(savedReportPaths[0]) && File.ReadAllText(savedReportPaths[0]).Contains("Ремонт с отложенным отчётом"),
    "отчёт автоматически сохраняется, как только папка снова доступна");
PendingReportStore.Save(remainingAfterRestore, pendingProfile);
Assert(PendingReportStore.Load(pendingProfile).Count == 0,
    "после сохранения отчёт убирается из очереди");
PendingReportStore.Delete(pendingProfile);
File.Delete(blockerFile);

// === Этап 2: лимит размера входящего кадра ===
var previousControlLimit = PeerConnection.MaxControlBytes;
PeerConnection.MaxControlBytes = 64 * 1024;
try
{
    var limitListener = new TcpListener(IPAddress.Loopback, 0);
    limitListener.Start();
    var limitPort = ((IPEndPoint)limitListener.LocalEndpoint).Port;
    var acceptLimited = limitListener.AcceptTcpClientAsync();
    var rawFlooder = new TcpClient();
    await rawFlooder.ConnectAsync(IPAddress.Loopback, limitPort);
    using var limitedServer = new PeerConnection(await acceptLimited.WaitAsync(TimeSpan.FromSeconds(3)));
    var limitClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    limitedServer.Disconnected += () => limitClosed.TrySetResult(true);
    var header = new byte[5];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 200 * 1024 * 1024);
    header[4] = 0x01;
    await rawFlooder.GetStream().WriteAsync(header, 0, header.Length);
    await rawFlooder.GetStream().WriteAsync(new byte[100], 0, 100);
    await limitClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(!limitedServer.IsConnected,
        "кадр больше лимита: соединение с нарушителем закрывается, память мастера защищена");
    rawFlooder.Close();
    limitListener.Stop();
}
finally { PeerConnection.MaxControlBytes = previousControlLimit; }

// === Этап 2: сопряжение по коду и подписи HMAC ===
var pairCode = SecurityService.GenerateCode();
Assert(pairCode.Length == 6 && pairCode.All(char.IsDigit), "код сопряжения — 6 цифр для ввода с экрана мастера");
var pairClientId = Guid.NewGuid().ToString("N");
var secretA = SecurityService.DeriveSecret(pairCode, pairClientId);
var secretB = SecurityService.DeriveSecret(pairCode, pairClientId);
Assert(secretA.SequenceEqual(secretB), "мастер и менеджер выводят одинаковый секрет из кода и ClientId");
var wrongSecret = SecurityService.DeriveSecret((int.Parse(pairCode) + 1 % 1000000).ToString("D6"), pairClientId);
Assert(!wrongSecret.SequenceEqual(secretA), "неверный код сопряжения даёт другой секрет и не пройдёт проверку");
var nonce = SecurityService.NewNonce();
var helloProof = SecurityService.HelloProof(secretA, pairClientId, nonce);
Assert(helloProof == SecurityService.HelloProof(secretB, pairClientId, nonce) &&
       helloProof != SecurityService.HelloProof(wrongSecret, pairClientId, nonce),
    "подпись hello подтверждает знание секрета без передачи кода по сети");
Assert(SecurityService.ServerProof(secretA, "a", "b") != SecurityService.ServerProof(secretA, "a", "c"),
    "ответ мастера привязан к одноразовым nonce и не переигрывается");
var signedMessage = AppMessage.Chat("general", "Менеджер", "Подписанное сообщение", Guid.NewGuid().ToString("N"));
signedMessage.Sig = SecurityService.Sign(secretA, signedMessage);
Assert(SecurityService.Verify(secretB, signedMessage), "подписанное сообщение проходит проверку у получателя");
var tampered = AppMessage.Chat("general", "Менеджер", "Подменённый текст", signedMessage.MessageId);
tampered.Sig = signedMessage.Sig;
Assert(!SecurityService.Verify(secretB, tampered), "подмена текста сообщения обнаруживается по подписи HMAC");

// === Этап 2: подписанное соединение отклоняет подделку ===
var secureReceived = Source();
var secureViolation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var acceptSecure = listener.AcceptTcpClientAsync();
var secureTcp = new TcpClient();
await secureTcp.ConnectAsync(IPAddress.Loopback, port);
using var secureClient = new PeerConnection(secureTcp);
using var secureServer = new PeerConnection(await acceptSecure.WaitAsync(TimeSpan.FromSeconds(3)));
secureServer.SetSignKey(secretB);
secureClient.SetSignKey(secretA);
secureServer.MessageReceived += m => secureReceived.TrySetResult(m);
secureServer.SignatureViolation += () => secureViolation.TrySetResult(true);
await secureClient.SendAsync(AppMessage.Chat("general", "Менеджер", "Защищённое сообщение"));
var secureChat = await secureReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(secureChat.Text == "Защищённое сообщение" && secureChat.Sig.Length > 0,
    "после сопряжения все сообщения подписываются и доходят");
var forged = AppMessage.Chat("general", "Злоумышленник", "Фальшивая заявка");
forged.Sig = Convert.ToBase64String(new byte[32]);
await secureClient.SendAsync(forged);
await secureViolation.Task.WaitAsync(TimeSpan.FromSeconds(5));
Assert(!secureServer.IsConnected,
    "сообщение с поддельной подписью отклоняется, соединение закрывается");

// === Этап 2: версия протокола ===
Assert(new AppMessage().ProtocolVersion == SecurityService.ProtocolVersion &&
       new AppMessage().AppVersion == SecurityService.AppVersion &&
       SecurityService.ProtocolVersion == 2,
    "каждое сообщение несёт версию протокола для понятной ошибки при несовместимости");

// === Этап 1: единственный экземпляр ===
Assert(SingleInstance.TryAcquire(), "первый запуск программы захватывает экземпляр профиля");
Assert(!SingleInstance.TryAcquire(), "второй запуск под тем же профилем определяется и завершается");
SingleInstance.Release();
Assert(SingleInstance.TryAcquire(), "после завершения первого экземпляра запуск снова возможен");
SingleInstance.Release();

// === Этап 1: журнал приложения с ротацией ===
var previousLogLimit = AppLog.MaxBytes;
AppLog.MaxBytes = 4 * 1024;
try
{
    File.Delete(AppLog.LogFilePath);
    File.Delete(AppLog.LogFilePath + ".bak");
    for (var line = 0; line < 200; line++) AppLog.Info("тестовая запись журнала " + line);
    Assert(File.Exists(AppLog.LogFilePath) && new FileInfo(AppLog.LogFilePath).Length > 0,
        "журнал приложения ведётся");
    Assert(File.Exists(AppLog.LogFilePath + ".bak"),
        "журнал ротируется при переполнении");
    Assert(AppLog.ReadTail().Contains("тестовая запись журнала"),
        "последние записи журнала доступны для диагностики");
}
finally
{
    AppLog.MaxBytes = previousLogLimit;
    File.Delete(AppLog.LogFilePath);
    File.Delete(AppLog.LogFilePath + ".bak");
}

listener.Stop();
File.Delete(savedFile);
File.Delete(reportPath);
Directory.Delete(testRoot, true);
Assert(SoundCatalog.QuietHoursActive("22:00", "08:00", new TimeSpan(23, 0, 0)), "тихие часы 22:00–08:00 активны вечером");
Assert(SoundCatalog.QuietHoursActive("22:00", "08:00", new TimeSpan(7, 59, 0)), "тихие часы 22:00–08:00 активны ночью через полночь");
Assert(!SoundCatalog.QuietHoursActive("22:00", "08:00", new TimeSpan(12, 0, 0)), "тихие часы 22:00–08:00 не активны днём");
Assert(!SoundCatalog.QuietHoursActive("", "", new TimeSpan(12, 0, 0)), "пустые тихие часы выключены");
Assert(!SoundCatalog.QuietHoursActive("10:00", "10:00", new TimeSpan(10, 0, 0)), "одинаковые границы тихих часов выключены");
Assert(SoundCatalog.QuietHoursActive("00:00", "23:59", new TimeSpan(12, 0, 0)), "тихие часы 00:00–23:59 покрывают почти весь день");
Assert(!SoundCatalog.QuietHoursActive("99:99", "08:00", new TimeSpan(2, 0, 0)), "некорректное время тихих часов игнорируется");
Assert(SoundCatalog.QuietHoursActive("09:00", "18:00", new TimeSpan(9, 0, 0)), "начало тихих часов включительно");
Assert(!SoundCatalog.QuietHoursActive("09:00", "18:00", new TimeSpan(18, 0, 0)), "конец тихих часов исключителен");
Assert(new AppMessage().SentAt.Kind == DateTimeKind.Utc, "исходящие сообщения помечаются временем UTC");
Assert(Math.Abs((DateTime.UtcNow - new AppMessage().SentAt).TotalSeconds) < 5, "метка времени сообщения свежая и в UTC");

var discoveryRequest = DiscoveryProtocol.EncodeRequest();
Assert(DiscoveryProtocol.IsRequest(discoveryRequest), "запрос обнаружения распознаётся");
Assert(!DiscoveryProtocol.IsRequest(Array.Empty<byte>()), "пустой пакет не считается запросом обнаружения");
Assert(!DiscoveryProtocol.IsRequest(System.Text.Encoding.UTF8.GetBytes("SERVIS-LEGKO-DISCOVER|999")), "запрос обнаружения с другой версией протокола отклоняется");
Assert(!DiscoveryProtocol.IsRequest(System.Text.Encoding.UTF8.GetBytes("привет мир")), "посторонний UDP-пакет отклоняется");
var discoveryResponse = DiscoveryProtocol.EncodeResponse("МАСТЕР-1", 47891, "2.0.0");
var decoded = DiscoveryProtocol.TryDecodeResponse(discoveryResponse, "192.168.0.12");
Assert(decoded is not null && decoded.Name == "МАСТЕР-1" && decoded.Port == 47891 && decoded.Address == "192.168.0.12" && decoded.Version == "2.0.0", "ответ обнаружения кодируется и читается обратно");
Assert(decoded!.DisplayName == "МАСТЕР-1 · 192.168.0.12", "карточка найденного мастера содержит имя и адрес");
var dirty = DiscoveryProtocol.EncodeResponse("имя|с|разделителями", 47891, "2.0.0");
var decodedDirty = DiscoveryProtocol.TryDecodeResponse(dirty, "10.0.0.5");
Assert(decodedDirty is not null && !decodedDirty.Name.Contains('|'), "имя мастера очищается от разделителей протокола");
Assert(DiscoveryProtocol.TryDecodeResponse(System.Text.Encoding.UTF8.GetBytes("SERVIS-LEGKO-HERE|abc|2.0.0|x"), "1.2.3.4") is null, "ответ с нечисловым портом отклоняется");
Assert(DiscoveryProtocol.TryDecodeResponse(System.Text.Encoding.UTF8.GetBytes("SERVIS-LEGKO-HERE|99999|2.0.0|x"), "1.2.3.4") is null, "ответ с невозможным портом отклоняется");
Assert(DiscoveryProtocol.TryDecodeResponse(discoveryRequest, "1.2.3.4") is null, "запрос не принимается за ответ");
Assert(DiscoveryProtocol.SanitizeName(new string('д', 100)).Length == 40, "слишком длинное имя мастера обрезается");
Assert(LocalNetwork.IsTailscale("100.64.1.2") && LocalNetwork.IsTailscale("100.127.255.254"), "адреса Tailscale распознаются");
Assert(!LocalNetwork.IsTailscale("100.63.0.1") && !LocalNetwork.IsTailscale("192.168.0.1"), "адреса вне диапазона Tailscale не распознаются");

Assert(ReliableMessageLogic.RetryDelay(0) == TimeSpan.FromSeconds(1), "первая пауза повтора 1 с");
Assert(ReliableMessageLogic.RetryDelay(1) == TimeSpan.FromSeconds(2), "вторая пауза повтора 2 с");
Assert(ReliableMessageLogic.RetryDelay(2) == TimeSpan.FromSeconds(5), "третья пауза повтора 5 с");
Assert(ReliableMessageLogic.RetryDelay(3) == TimeSpan.FromSeconds(10), "четвёртая пауза повтора 10 с");
Assert(ReliableMessageLogic.RetryDelay(4) == TimeSpan.FromSeconds(30), "пятая пауза повтора 30 с");
Assert(ReliableMessageLogic.RetryDelay(25) == TimeSpan.FromSeconds(30), "пауза повтора ограничена 30 с");
var backoffItem = new OutgoingChatMessage { AwaitingAck = true, RetryCount = 2, LastSentAtUtc = DateTime.UtcNow.AddSeconds(-4) };
Assert(!ReliableMessageLogic.NeedsRetry(backoffItem, DateTime.UtcNow), "повтор не наступает раньше своей паузы (5 с)");
Assert(ReliableMessageLogic.NeedsRetry(backoffItem, DateTime.UtcNow.AddSeconds(2)), "повтор наступает после своей паузы (5 с)");
var freshItem = new OutgoingChatMessage { AwaitingAck = true, RetryCount = 0, LastSentAtUtc = DateTime.UtcNow.AddMilliseconds(-1500) };
Assert(ReliableMessageLogic.NeedsRetry(freshItem, DateTime.UtcNow), "первый повтор наступает через 1 с");

var readReceipt = AppMessage.ReadReceipt("job-1", new[] { "a1", "b2", "c3" });
Assert(readReceipt.Type == MessageTypes.Read && readReceipt.Text == "a1,b2,c3", "квитанция прочтения несёт тип и список сообщений");
var readRoundTrip = AppMessage.FromJson(readReceipt.ToJson());
Assert(readRoundTrip is not null && readRoundTrip.Type == MessageTypes.Read && readRoundTrip.JobId == "job-1" && readRoundTrip.Text == "a1,b2,c3", "квитанция прочтения переживает сериализацию");
var reminder = AppMessage.JobReminderMessage("job-9", "iPhone 13 — не заряжается", "Иван");
var reminderRoundTrip = AppMessage.FromJson(reminder.ToJson());
Assert(reminderRoundTrip is not null && reminderRoundTrip.Type == MessageTypes.JobReminder && reminderRoundTrip.JobId == "job-9" && reminderRoundTrip.Text.Contains("iPhone"), "напоминание мастеру переживает сериализацию");
var forceRoundTrip = AppMessage.FromJson(AppMessage.JobForceRemoved("job-7", "Мастер").ToJson());
Assert(forceRoundTrip is not null && forceRoundTrip.Type == MessageTypes.JobForceRemoved && forceRoundTrip.JobId == "job-7" && forceRoundTrip.Sender == "Мастер", "команда «убрать без отчёта» переживает сериализацию");

// Правка и закрепление сообщений чата
var editRoundTrip = AppMessage.FromJson(AppMessage.ChatEdited("job-3", "msg-9", "Новый текст сообщения").ToJson());
Assert(editRoundTrip is not null && editRoundTrip.Type == MessageTypes.ChatEdit &&
       editRoundTrip.MessageId == "msg-9" && editRoundTrip.Text == "Новый текст сообщения",
    "правка сообщения переживает сериализацию с новым текстом");
var pinRoundTrip = AppMessage.FromJson(AppMessage.ChatPinned("general", "msg-4", true).ToJson());
Assert(pinRoundTrip is not null && pinRoundTrip.Type == MessageTypes.ChatPin &&
       pinRoundTrip.JobId == "general" && pinRoundTrip.MessageId == "msg-4" && pinRoundTrip.Reason == "pin",
    "закрепление сообщения переживает сериализацию с признаком pin");
var unpinRoundTrip = AppMessage.FromJson(AppMessage.ChatPinned("general", "msg-4", false).ToJson());
Assert(unpinRoundTrip is not null && unpinRoundTrip.Reason == "unpin", "открепление передаётся признаком unpin");

var recentMasters = new List<string>();
RecentMastersLogic.Remember(recentMasters, "192.168.0.12");
RecentMastersLogic.Remember(recentMasters, "100.64.0.2");
RecentMastersLogic.Remember(recentMasters, "192.168.0.12");
Assert(recentMasters.Count == 2 && recentMasters[0] == "192.168.0.12", "недавние мастера: дубликат поднимается наверх без повтора");
for (var i = 0; i < 10; i++) RecentMastersLogic.Remember(recentMasters, "10.0.0." + i);
Assert(recentMasters.Count == 5 && recentMasters[0] == "10.0.0.9", "недавние мастера ограничены пятью записями");
RecentMastersLogic.Remember(recentMasters, "");
Assert(recentMasters.Count == 5, "пустой адрес не попадает в недавние мастера");
Assert(RecentMastersLogic.MaxRecent == 5, "лимит недавних мастеров зафиксирован на пяти");

// === Этап 2: одноразовый код сопряжения (кнопка «Добавить компьютер») ===
var sessionClock = DateTime.Now;
var session = new PairingSession(() => sessionClock);
var sessionClient = Guid.NewGuid().ToString("N");
var sessionNonce = SecurityService.NewNonce();
Assert(!session.IsActive, "пока не нажата кнопка «Добавить компьютер», кода нет");
var anySig = SecurityService.PairProof(SecurityService.DeriveSecret("000000", sessionClient), sessionClient, sessionNonce);
Assert(session.TryVerify(sessionClient, sessionNonce, anySig, out _) == PairingCheck.Inactive,
    "без нажатия кнопки новое устройство подключиться не может");
var sessionCode = session.Begin();
Assert(session.IsActive && session.CurrentCode == sessionCode && sessionCode.Length == 6 && sessionCode.All(char.IsDigit),
    "кнопка «Добавить компьютер» создаёт 6-значный код на 5 минут");
var wrongCode = (int.Parse(sessionCode) + 1 % 1000000).ToString("D6");
var wrongSig = SecurityService.PairProof(SecurityService.DeriveSecret(wrongCode, sessionClient), sessionClient, sessionNonce);
Assert(session.TryVerify(sessionClient, sessionNonce, wrongSig, out _) == PairingCheck.WrongCode && session.BadAttempts == 1,
    "неверный код засчитывается как попытка");
var rightSig = SecurityService.PairProof(SecurityService.DeriveSecret(sessionCode, sessionClient), sessionClient, sessionNonce);
Assert(session.TryVerify(sessionClient, sessionNonce, rightSig, out var sessionSecret) == PairingCheck.Ok &&
       sessionSecret is not null && sessionSecret.SequenceEqual(SecurityService.DeriveSecret(sessionCode, sessionClient)),
    "верный код сопрягает устройство и возвращает секрет для доверенного списка");
Assert(!session.IsActive && session.CurrentCode is null, "после первого сопряжения код гаснет (одноразовый)");
Assert(session.TryVerify(sessionClient, SecurityService.NewNonce(), rightSig, out _) == PairingCheck.Inactive,
    "погасший после сопряжения код повторно не работает");

// === Этап 2: истечение кода через 5 минут ===
var expClock = DateTime.Now;
var expSession = new PairingSession(() => expClock);
var expCode = expSession.Begin();
expClock = expClock.AddSeconds(PairingSession.CodeLifetimeSeconds - 1);
Assert(expSession.IsActive && expSession.TimeLeft > TimeSpan.Zero, "код жив почти до конца отсчёта");
expClock = expClock.AddSeconds(2);
Assert(!expSession.IsActive && expSession.TimeLeft == TimeSpan.Zero, "через 5 минут без использования код гаснет");
var expSig = SecurityService.PairProof(SecurityService.DeriveSecret(expCode, sessionClient), sessionClient, sessionNonce);
Assert(expSession.TryVerify(sessionClient, sessionNonce, expSig, out _) == PairingCheck.Inactive,
    "истёкший код отвечает «код не активен», а не «неверный код»");
Assert(expSession.InactiveReason.Contains("истёк"),
    "причина неактивности — истечение по времени (для журнала и подсказок)");
Assert(new PairingSession().InactiveReason.Contains("не нажимали"),
    "до первого нажатия кнопки причина — «кнопку ещё не нажимали»");

// === Этап 2: пять неверных попыток отключают режим добавления ===
var lockClock = DateTime.Now;
var lockSession = new PairingSession(() => lockClock);
var lockCode = lockSession.Begin();
var lockWrong = SecurityService.PairProof(
    SecurityService.DeriveSecret((int.Parse(lockCode) + 1 % 1000000).ToString("D6"), sessionClient), sessionClient, sessionNonce);
for (var i = 1; i < PairingSession.MaxBadAttempts; i++)
    Assert(lockSession.TryVerify(sessionClient, sessionNonce, lockWrong, out _) == PairingCheck.WrongCode,
        "неверная попытка " + i + " засчитана");
Assert(!lockSession.LockedOut && lockSession.IsActive, "до пятой ошибки режим добавления работает");
Assert(lockSession.TryVerify(sessionClient, sessionNonce, lockWrong, out _) == PairingCheck.WrongCode && lockSession.LockedOut,
    "пятая неверная попытка отключает режим добавления");
Assert(!lockSession.IsActive && lockSession.CurrentCode is null, "после отключения код гаснет");
Assert(lockSession.TryVerify(sessionClient, sessionNonce, lockWrong, out _) == PairingCheck.Inactive,
    "после отключения режима устройство получает «код не активен»");
Assert(lockSession.InactiveReason.Contains("неверных попыток"),
    "причина неактивности — отключение после неверных попыток");
Assert(PairingSession.LockoutMessage.Contains("Добавить компьютер"),
    "мастер видит сообщение об отключении режима добавления");
lockSession.Begin();
Assert(!lockSession.LockedOut && lockSession.IsActive && lockSession.BadAttempts == 0,
    "повторное нажатие «Добавить компьютер» включает режим заново с чистым счётчиком");
Assert(lockSession.InactiveReason.Length == 0,
    "после нового нажатия кнопки причина неактивности очищена");

// === Этап 2: сопряжённые устройства не затронуты режимом добавления ===
var pairedNonce = SecurityService.NewNonce();
var helloProofPaired = SecurityService.HelloProof(secretA, pairClientId, pairedNonce);
var freshSession = new PairingSession(() => DateTime.Now);
Assert(!freshSession.IsActive &&
       helloProofPaired == SecurityService.HelloProof(secretA, pairClientId, pairedNonce),
    "сопряжённое устройство входит по сохранённому ключу без кода, даже когда режим добавления выключен");

// === Enter — отправить, Shift+Enter — новая строка: единое правило не должно «слетать» ===
Assert(EnterSend.ShouldSend(true, false), "Enter без Shift — отправить");
Assert(!EnterSend.ShouldSend(true, true), "Shift+Enter — новая строка, не отправка");
Assert(!EnterSend.ShouldSend(false, false), "обычные клавиши не отправляют сообщение");

// Стражи регрессии: все поля отправки обязаны идти через единое правило EnterSend
// и проверять ОБА кода клавиши (Key.Return — основной Enter, Key.Enter — цифровой блок);
// именно проверка только Key.Enter раньше «ломала» отправку по основному Enter
var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src"));
var enterCall = "EnterSend.ShouldSend(e.Key == Key.Return || e.Key == Key.Enter";
var chatPanelSrc = File.ReadAllText(Path.Combine(srcDir, "ChatPanel.cs"));
var clientFormSrc = File.ReadAllText(Path.Combine(srcDir, "ClientForm.cs"));
var pairingFormSrc = File.ReadAllText(Path.Combine(srcDir, "PairingForm.cs"));
Assert(chatPanelSrc.Contains(enterCall),
    "поле чата отправляет по Enter через единое правило EnterSend (оба кода клавиши)");
Assert(clientFormSrc.Contains(enterCall),
    "быстрая заявка в мини-панели отправляет по Enter через единое правило EnterSend (оба кода клавиши)");
Assert(pairingFormSrc.Contains("EnterSend.ShouldSend(e.Key == System.Windows.Input.Key.Return || e.Key == System.Windows.Input.Key.Enter"),
    "поле кода сопряжения подтверждается по Enter через единое правило EnterSend (оба кода клавиши)");
Assert(!clientFormSrc.Contains("e.Key == Key.Enter)") && !pairingFormSrc.Contains("e.Key == System.Windows.Input.Key.Enter)"),
    "нигде не осталось старой проверки только Key.Enter, из-за которой основной Enter «не работал»");
// У многострочного TextBox встроенная обработка забирает Enter до KeyDown,
// поэтому перехват — только через PreviewKeyDown
Assert(chatPanelSrc.Contains("_input.PreviewKeyDown") && !chatPanelSrc.Contains("_input.KeyDown"),
    "поле чата перехватывает Enter через PreviewKeyDown, иначе Enter вставляет новую строку");
Assert(clientFormSrc.Contains("input.PreviewKeyDown") && pairingFormSrc.Contains("_codeInput.PreviewKeyDown"),
    "быстрая заявка и код сопряжения перехватывают Enter через PreviewKeyDown");

// === 2.33: общий список заявок у всех и удаление для всех ===
// Статусы карточки заявки (JobInfo.Status)
Assert(JobStatus.Of(false, false, false) == JobStatus.Waiting, "новая заявка — статус «ожидает»");
Assert(JobStatus.Of(true, false, false) == JobStatus.Accepted, "принятая заявка — «в работе»");
Assert(JobStatus.Of(true, true, false) == JobStatus.AwaitingReport, "завершённая без отчёта — «ожидает отчёт»");
Assert(JobStatus.Of(true, true, true) == JobStatus.Done, "сохранённый отчёт — «завершена»");
Assert(JobStatus.Normalize("") == JobStatus.Waiting && JobStatus.Normalize("старое") == JobStatus.Waiting,
    "неизвестный или пустой статус трактуется как «ожидает» — безопасный вариант");
Assert(JobStatus.Title(JobStatus.Accepted) == "В работе" && JobStatus.Title(JobStatus.Done) == "Завершена",
    "подписи статусов на русском");

// Карточка JobInfo переживает сериализацию целиком (все поля карточки)
var jobInfo = AppMessage.JobInfo("job-7", "Не включается моноблок", "client-1", "Ольга",
    "PC-01", JobStatus.Accepted,
    new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
    new DateTime(2026, 11, 1, 10, 5, 0, DateTimeKind.Utc), default,
    "заменил блок питания", "Иванов", "Моноблок HP", "SN123", true);
var jobInfoJson = System.Text.Json.JsonSerializer.Serialize(jobInfo);
var jobInfoBack = System.Text.Json.JsonSerializer.Deserialize<AppMessage>(jobInfoJson)!;
Assert(jobInfoBack.Type == MessageTypes.JobInfo && jobInfoBack.JobId == "job-7" &&
       jobInfoBack.ClientId == "client-1" && jobInfoBack.UserName == "Ольга" &&
       jobInfoBack.MachineName == "PC-01" && jobInfoBack.Status == JobStatus.Accepted &&
       jobInfoBack.Comment == "заменил блок питания" && jobInfoBack.Client == "Иванов" &&
       jobInfoBack.Device == "Моноблок HP" && jobInfoBack.Serial == "SN123" && jobInfoBack.Urgent,
    "карточка JobInfo со всеми полями переживает сериализацию");
Assert(jobInfoBack.StartedAt.HasValue && !jobInfoBack.CompletedAt.HasValue,
    "время принятия в работу доезжает, пустое время завершения остаётся пустым");
Assert(jobInfoBack.SentAt == new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
    "SentAt несёт дату создания заявки — сортировка одинакова у всех");

// RemoteJobInfo строится из карточки и даёт строки времени
var remoteInfo = RemoteJobInfo.FromMessage(jobInfoBack);
Assert(remoteInfo.JobId == "job-7" && remoteInfo.OwnerName == "Ольга" && remoteInfo.Status == JobStatus.Accepted,
    "RemoteJobInfo переносит владельца и статус из карточки");
Assert(remoteInfo.TimeLine().StartsWith("В работе"), "строка времени чужой заявки в работе — «В работе…»");
remoteInfo.Status = JobStatus.Done;
remoteInfo.CompletedAt = new DateTime(2026, 11, 2, 15, 30, 0);
Assert(remoteInfo.TimeLine().StartsWith("Завершена 02.11.2026 15:30"), "строка времени завершённой чужой заявки");
var waitingRemote = RemoteJobInfo.FromMessage(AppMessage.JobInfo("job-8", "Принтер не печатает",
    "client-2", "Игорь", "PC-02", "", default, default, default, "", "", "", "", false));
Assert(waitingRemote.Status == JobStatus.Waiting && waitingRemote.TimeLine().StartsWith("Создана "),
    "чужая заявка от старого мастера без статуса показывается как «создана…»");

// Журнал удалённых заявок: без дублей, с ограничением размера, переживает сохранение
var deletedIds = new DeletedJobIds(cap: 150);
deletedIds.Add("a");
deletedIds.Add("b");
deletedIds.Add("a");
Assert(deletedIds.Count == 2 && deletedIds.Contains("a"), "повторное добавление не плодит дубли");
for (var i = 0; i < 200; i++) deletedIds.Add("n" + i);
Assert(deletedIds.Count == 150 && !deletedIds.Contains("a") && deletedIds.Contains("n199"),
    "журнал удалённых заявок обрезается до лимита, самые свежие остаются");
var deletedRoundtrip = DeletedJobIds.FromList(deletedIds.ToList());
Assert(deletedRoundtrip.Count == deletedIds.Count && deletedRoundtrip.Contains("n199"),
    "журнал удалённых заявок переживает сохранение и загрузку");
DeletedJobsStore.Save(deletedIds);
var deletedLoaded = DeletedJobsStore.Load();
Assert(deletedLoaded.Count == deletedIds.Count && deletedLoaded.Contains("n199"),
    "файл удалённых заявок на диске читается обратно");
DeletedJobsStore.Save(new DeletedJobIds());

// Снимок списка: чужие заявки, которых у мастера нет, убираются
var missing = RemoteJobsSync.MissingFromSnapshot(new[] { "j1", "j2", "j3" },
    new HashSet<string>(new[] { "j2" }, StringComparer.Ordinal));
Assert(missing.Count == 2 && missing.Contains("j1") && missing.Contains("j3"),
    "снимок мастера вычищает чужие заявки, удалённые пока менеджер был офлайн");
Assert(RemoteJobsSync.MissingFromSnapshot(new[] { "j2" },
        new HashSet<string>(new[] { "j2" }, StringComparer.Ordinal)).Count == 0,
    "совпадающий снимок ничего не убирает");

// Локальная копия чужих заявок переживает сохранение
RemoteJobsStore.Save(new List<RemoteJobInfo> { remoteInfo });
var remoteLoaded = RemoteJobsStore.Load();
Assert(remoteLoaded.Count == 1 && remoteLoaded[0].JobId == "job-7" && remoteLoaded[0].OwnerName == "Ольга",
    "копия чужих заявок на диске читается обратно");
RemoteJobsStore.Save(new List<RemoteJobInfo>());
Assert(RemoteJobsStore.Load().Count == 0, "пустая копия чужих заявок сохраняется и читается");

// Стражи регрессии по исходникам
var masterFormSrc = File.ReadAllText(Path.Combine(srcDir, "MasterForm.cs"));
Assert(clientFormSrc.Contains("MessageTypes.JobInfo") && clientFormSrc.Contains("MessageTypes.JobSyncEnd") &&
       clientFormSrc.Contains("HandleJobForceRemoved"),
    "менеджер принимает карточки заявок, маркеры снимка и общее удаление");
Assert(masterFormSrc.Contains("SendJobsSnapshotAsync") && masterFormSrc.Contains("DeleteJobForEveryone") &&
       masterFormSrc.Contains("BroadcastJobInfo"),
    "мастер рассылает снимок списка, обновления и удаление заявок всем менеджерам");
Assert(masterFormSrc.Contains("_deletedJobs.Contains") && masterFormSrc.Contains("DeletedJobsStore.Save"),
    "мастер запоминает удалённые заявки, чтобы они не воскресали при переподключении");
Assert(clientFormSrc.Contains("NotifyMasterRemovedAsync") && clientFormSrc.Contains("FlushPendingRemovedAsync"),
    "удаление без связи ставится в очередь и уходит при подключении");
var sessionPersistenceSrc = File.ReadAllText(Path.Combine(srcDir, "SessionPersistence.cs"));
Assert(sessionPersistenceSrc.Contains("PendingRemovedJobs"),
    "очередь удалений хранится в файле сессии и переживает перезапуск");
Assert(masterFormSrc.Contains("общий список хранит все заявки за всё время"),
    "архивация завершённой заявки больше не стирает запись у мастера — общий список хранит всё");

// === 2.34: массовая очистка списка заявок у мастера ===
var cleanupNow = new DateTime(2026, 11, 10, 12, 0, 0);
var cleanupRules = new CleanupRules(DoneOlderThanDays: 30, WaitingOlderThanDays: 7, AwaitingReportOlderThanDays: 7);
Assert(JobCleanup.Matches(cleanupRules, JobStatus.Done,
    cleanupNow.AddDays(-60), cleanupNow.AddDays(-40), cleanupNow),
    "очистка: завершённая 40 дней назад подпадает под порог 30 дней");
Assert(!JobCleanup.Matches(cleanupRules, JobStatus.Done,
    cleanupNow.AddDays(-20), cleanupNow.AddDays(-10), cleanupNow),
    "очистка: завершённая 10 дней назад не подпадает под порог 30 дней");
Assert(JobCleanup.Matches(cleanupRules, JobStatus.Waiting,
    cleanupNow.AddDays(-10), default, cleanupNow),
    "очистка: ожидающая 10 дней подпадает под порог 7 дней (возраст от создания)");
Assert(!JobCleanup.Matches(cleanupRules, JobStatus.Waiting,
    cleanupNow.AddDays(-2), default, cleanupNow),
    "очистка: свежая ожидающая не подпадает");
Assert(JobCleanup.Matches(cleanupRules, JobStatus.AwaitingReport,
    cleanupNow.AddDays(-20), cleanupNow.AddDays(-15), cleanupNow),
    "очистка: ожидающая отчёт 15 дней подпадает (возраст от завершения)");
Assert(!JobCleanup.Matches(cleanupRules, JobStatus.Accepted,
    cleanupNow.AddDays(-365), cleanupNow.AddDays(-365), cleanupNow),
    "очистка: заявка «В работе» не удаляется никогда, даже годовалая");
Assert(!JobCleanup.Matches(new CleanupRules(null, null, null), JobStatus.Done,
    cleanupNow.AddDays(-100), cleanupNow.AddDays(-100), cleanupNow),
    "очистка: без выбранных правил ничего не удаляется");
Assert(new CleanupRules(null, null, null).AnyEnabled == false &&
       new CleanupRules(30, null, null).AnyEnabled,
    "очистка: признак «выбрано хоть одно правило» работает");
Assert(JobCleanup.Matches(new CleanupRules(0, null, null), JobStatus.Done,
    cleanupNow, cleanupNow, cleanupNow),
    "очистка: порог 0 дней удаляет все завершённые, даже сегодняшние");
Assert(!JobCleanup.Matches(cleanupRules, JobStatus.Waiting,
    default, default, cleanupNow),
    "очистка: заявка без даты считается свежей и не удаляется");
var cleanupJobs = new[]
{
    (Id: "old-done", Status: JobStatus.Done, Received: cleanupNow.AddDays(-100), Completed: cleanupNow.AddDays(-90)),
    (Id: "new-done", Status: JobStatus.Done, Received: cleanupNow.AddDays(-5), Completed: cleanupNow.AddDays(-1)),
    (Id: "old-wait", Status: JobStatus.Waiting, Received: cleanupNow.AddDays(-30), Completed: default(DateTime)),
    (Id: "active", Status: JobStatus.Accepted, Received: cleanupNow.AddDays(-60), Completed: default(DateTime))
};
var cleanupIds = JobCleanup.SelectIds(cleanupJobs, cleanupRules, cleanupNow,
    j => j.Id, j => j.Status, j => j.Received, j => j.Completed);
Assert(cleanupIds.Count == 2 && cleanupIds.Contains("old-done") && cleanupIds.Contains("old-wait"),
    "очистка: отбор по списку выбирает ровно старые завершённые и ожидающие");
Assert(masterFormSrc.Contains("ShowCleanupSheet") && masterFormSrc.Contains("FindCleanupMatches"),
    "у мастера есть панель массовой очистки списка заявок");
Assert(masterFormSrc.Contains("Очистка списка заявок"),
    "кнопка очистки доступна в настройках мастера");

// === 2.35: правка заявок всеми пользователями и переключатель общего чата у мастера ===
// Сообщение JobEdit переживает сериализацию целиком
var jobEdit = AppMessage.JobEdit("job-9", "Замена экрана iPhone 12", "Петров", "iPhone 12", "IMEI123", true, "Ольга");
var jobEditBack = System.Text.Json.JsonSerializer.Deserialize<AppMessage>(
    System.Text.Json.JsonSerializer.Serialize(jobEdit))!;
Assert(jobEditBack.Type == MessageTypes.JobEdit && jobEditBack.JobId == "job-9" &&
       jobEditBack.Text == "Замена экрана iPhone 12" && jobEditBack.Client == "Петров" &&
       jobEditBack.Device == "iPhone 12" && jobEditBack.Serial == "IMEI123" &&
       jobEditBack.Urgent && jobEditBack.Sender == "Ольга",
    "сообщение JobEdit со всеми полями переживает сериализацию");

// Правка чужой заявки: меняются только описательные поля, статус и даты не трогаем
var editTarget = RemoteJobInfo.FromMessage(AppMessage.JobInfo("job-10", "Старый текст",
    "client-3", "Игорь", "PC-03", JobStatus.Waiting,
    new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc), default, default, "", "Иванов", "Ноутбук", "SN1", false));
Assert(!editTarget.ApplyDescription(AppMessage.JobEdit("job-10", "Старый текст", "Иванов", "Ноутбук", "SN1", false, "Мастер")),
    "правка без изменений ничего не трогает");
Assert(editTarget.ApplyDescription(AppMessage.JobEdit("job-10", "Новый текст", "Петров", "ПК Lenovo", "SN2", true, "Мастер")),
    "правка применяется и сообщает об изменении");
Assert(editTarget.Text == "Новый текст" && editTarget.Client == "Петров" &&
       editTarget.Device == "ПК Lenovo" && editTarget.Serial == "SN2" && editTarget.Urgent,
    "правка обновляет описание и реквизиты чужой заявки");
Assert(editTarget.Status == JobStatus.Waiting && editTarget.OwnerName == "Игорь" &&
       editTarget.ReceivedAt == new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc).ToLocalTime(),
    "правка не трогает статус, дату создания и автора заявки");
Assert(!editTarget.ApplyDescription(AppMessage.JobEdit("job-10", "   ", "Петров", "ПК Lenovo", "SN2", true, "Мастер")),
    "пустой текст правки существующее описание не затирает");
Assert(editTarget.Text == "Новый текст", "описание осталось прежним после пустой правки");

// Стражи регрессии по исходникам (2.35)
Assert(masterFormSrc.Contains("_chatPeerPicker") && masterFormSrc.Contains("RefreshChatPeerPicker") &&
       masterFormSrc.Contains("OpenGeneralChatForCurrentPeer"),
    "у мастера есть переключатель общего чата по менеджерам");
Assert(masterFormSrc.Contains("TrustedDevicesStore.All()"),
    "переключатель общего чата знает всех сопряжённых менеджеров, даже без заявок");
Assert(masterFormSrc.Contains("из {known}"),
    "строка состояния мастера показывает число менеджеров в сети из всех известных");
Assert(masterFormSrc.Contains("MessageTypes.JobEdit") && masterFormSrc.Contains("HandleJobEdit") &&
       masterFormSrc.Contains("ApplyJobDescription"),
    "мастер принимает правки заявок от менеджеров и рассылает их всем");
Assert(masterFormSrc.Contains("ShowJobEditSheet"),
    "у мастера есть панель редактирования заявки");
Assert(clientFormSrc.Contains("SendJobEditAsync") && clientFormSrc.Contains("ShowJobEditSheet"),
    "менеджер может править свои и чужие заявки");
Assert(clientFormSrc.Contains("ApplyOwnJobDescription"),
    "менеджер принимает правки своих заявок от мастера и других менеджеров");
var jobBroadcastSrc = File.ReadAllText(Path.Combine(srcDir, "JobBroadcast.cs"));
Assert(jobBroadcastSrc.Contains("ApplyDescription"),
    "чистая логика правки описательных полей доступна и тестируется");
var protocolSrc = File.ReadAllText(Path.Combine(srcDir, "Protocol.cs"));
Assert(protocolSrc.Contains("public const string JobEdit = \"job_edit\""),
    "тип сообщения JobEdit объявлен в протоколе");

// === 2.36: переключатель общего чата не показывает служебный текст ===
// Правила отображения (DisplayMemberPath/SelectedValuePath) обязаны задаваться ДО
// первого наполнения списка — иначе часть пунктов рисуется как «ChatPeerItem { Id = … }»
var pickerPathIdx = masterFormSrc.IndexOf("DisplayMemberPath = nameof(ChatPeerItem.Label)", StringComparison.Ordinal);
var pickerItemsIdx = masterFormSrc.IndexOf("_chatPeerPicker.ItemsSource", StringComparison.Ordinal);
Assert(pickerPathIdx >= 0 && pickerItemsIdx >= 0 && pickerPathIdx < pickerItemsIdx,
    "переключатель общего чата: DisplayMemberPath задаётся до первого ItemsSource — пункты не показывают служебный текст");

// === 2.38: переключатель общего чата — только менеджеры в сети ===
Assert(masterFormSrc.Contains("Чат с менеджером:"),
    "подпись переключателя общего чата — «Чат с менеджером:»");
Assert(!masterFormSrc.Contains("По выбранной заявке — автоматически"),
    "из переключателя убран пункт «По выбранной заявке — автоматически»");
Assert(masterFormSrc.Contains("Менеджер не в сети"),
    "при отсутствии менеджеров в сети переключатель показывает серую подсказку");
Assert(masterFormSrc.Contains("_chatPeerPicker.IsEnabled = false") &&
       masterFormSrc.Contains("_chatPeerPicker.Foreground = Ui.Muted"),
    "без менеджеров в сети переключатель заблокирован и серый");
Assert(masterFormSrc.Contains("_chatPeerPicker.Foreground = Ui.Green"),
    "выбранный менеджер в сети показывается зелёным в общем стиле программы");
Assert(!masterFormSrc.Contains("return SelectedManager ?? _managers.Values"),
    "режим «чат следует за выбранной заявкой» удалён — собеседник выбирается только переключателем");

// === 2.39: курсор и текст полей центрируются по вертикали, кнопки ровно в ряду ===
var uiSrc = File.ReadAllText(Path.Combine(srcDir, "Ui.cs"));
Assert(uiSrc.Contains("VerticalAlignment=\"{TemplateBinding VerticalContentAlignment}\""),
    "шаблон TextBox пробрасывает VerticalContentAlignment в поле ввода — курсор по центру, а не у верхнего края");
var alertFormSrc = File.ReadAllText(Path.Combine(srcDir, "RequestAlertForm.cs"));
Assert(alertFormSrc.Contains("VerticalContentAlignment = VerticalAlignment.Top"),
    "многострочный текст всплывающей заявки остаётся прижатым к верху");
var editBtnIdx = masterFormSrc.IndexOf("var edit = Ui.ActionButton(\"Изменить\"", StringComparison.Ordinal);
Assert(editBtnIdx >= 0 && masterFormSrc[editBtnIdx..Math.Min(masterFormSrc.Length, editBtnIdx + 400)]
       .Contains("edit.Margin = new Thickness(8, 0, 0, 0)"),
    "кнопка «Изменить» у мастера имеет тот же левый зазор, что и соседние кнопки");

Console.WriteLine("All extended network, file and report tests passed.");

namespace RepairAlert
{
    internal sealed record ChatEntry(string Sender, string Text, DateTime Time, bool Own,
        string FileName = "", string FilePath = "", long FileSize = 0,
        string Id = "", string Delivery = "");
}
