using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RepairAlert;

internal abstract class ChatHostForm : BaseWindow
{
    protected const string GeneralChatId = "general";
    protected readonly AlertService Alerts = new();
    protected readonly WindowsNotifications WindowsNotifications;
    private readonly string _persistenceProfile;
    private readonly Dictionary<string, List<ChatEntry>> _chats = new();
    private readonly Dictionary<string, string> _sessionTitles = new();
    private readonly Dictionary<string, string> _pinnedMessages = new();
    private readonly List<OutgoingChatMessage> _outbox = new();
    private readonly DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _flushing;
    private bool _hostClosing;
    private bool _allowRealClose;
    private bool _trayHintShown;
    internal static bool IsApplicationExiting { get; private set; }
    protected string CurrentJobId = "";
    protected string CurrentJobText = "";
    protected bool IsJobAccepted;
    protected DateTime JobStartedAt;
    protected bool JobCompletionSaved;
    private ChatPanel? _chatPanel;
    private string _chatSessionId = "";
    private ChatNotificationForm? _notification;

    protected string OpenChatSessionId => _chatSessionId;

    protected void RegisterChatPanel(ChatPanel panel)
    {
        _chatPanel = panel;
        panel.MessageSend += (sessionId, text) => QueueTextMessage(sessionId, text);
        panel.FileSend += (sessionId, path) => _ = QueueFileMessageAsync(sessionId, path);
        panel.CancelFiles += sessionId => CancelPendingFiles(sessionId);
        panel.MessageDeleteRequested += (sessionId, id) => DeleteQueuedMessage(sessionId, id);
        panel.MessageEditRequested += (sessionId, id, text) => EditMessage(sessionId, id, text);
        panel.PinToggled += (sessionId, id) => TogglePin(sessionId, id);
        panel.Read += () => Alerts.Stop();
        panel.MessagesRead += ids =>
        {
            if (_chatSessionId.Length > 0) SendReadReceipt(_chatSessionId, ids);
        };
        UpdateQueueStatus();
    }

    /// <summary>Для правок и закреплений принимаем любую известную сессию, а не только активную заявку.</summary>
    protected string ResolveKnownSession(string jobId)
    {
        if (jobId == GeneralChatId) return GeneralChatId;
        return _chats.ContainsKey(jobId) ? jobId : "";
    }

    /// <summary>Сохранить свою правку сообщения и сообщить собеседнику.</summary>
    private void EditMessage(string sessionId, string messageId, string newText)
    {
        var history = History(sessionId);
        var index = history.FindIndex(entry => entry.Id == messageId && entry.Own);
        if (index < 0) return;
        history[index] = history[index] with { Text = newText };
        if (_chatSessionId == sessionId) _chatPanel?.UpdateMessageText(messageId, newText);
        RefreshPinnedIfNeeded(sessionId, messageId);
        PersistSession();
        // Правка — лёгкое уведомление: без связи просто остаётся локальной
        try
        {
            _ = SendSessionMessageAsync(sessionId,
                AppMessage.ChatEdited(sessionId, messageId, newText));
        }
        catch { }
    }

    /// <summary>Правка сообщения, пришедшая от собеседника.</summary>
    protected void ReceiveMessageEdited(string sessionId, AppMessage message)
    {
        if (sessionId.Length == 0 || message.MessageId.Length == 0 || message.Text.Length == 0) return;
        var history = History(sessionId);
        var index = history.FindIndex(entry => entry.Id == message.MessageId && !entry.Own && entry.FileName.Length == 0);
        if (index < 0) return;
        history[index] = history[index] with { Text = message.Text };
        if (_chatSessionId == sessionId) _chatPanel?.UpdateMessageText(message.MessageId, message.Text);
        RefreshPinnedIfNeeded(sessionId, message.MessageId);
        PersistSession();
    }

    /// <summary>Закрепить/открепить сообщение: в чате всегда не больше одного закрепа.</summary>
    private void TogglePin(string sessionId, string messageId)
    {
        var pinned = !_pinnedMessages.TryGetValue(sessionId, out var current) || current != messageId;
        ApplyPin(sessionId, pinned ? messageId : "");
        PersistSession();
        // Закреп — лёгкое уведомление: без связи просто остаётся локальным
        try
        {
            _ = SendSessionMessageAsync(sessionId,
                AppMessage.ChatPinned(sessionId, messageId, pinned));
        }
        catch { }
    }

    /// <summary>Закрепление/открепление, пришедшее от собеседника.</summary>
    protected void ReceivePinned(string sessionId, AppMessage message)
    {
        if (sessionId.Length == 0) return;
        ApplyPin(sessionId, message.Reason == "pin" ? message.MessageId : "");
        PersistSession();
    }

    private void ApplyPin(string sessionId, string messageId)
    {
        if (messageId.Length > 0) _pinnedMessages[sessionId] = messageId;
        else _pinnedMessages.Remove(sessionId);
        if (_chatSessionId == sessionId)
            _chatPanel?.SetPinned(messageId, messageId.Length > 0 ? PinnedSnippet(sessionId, messageId) : "");
    }

    /// <summary>Короткий текст закреплённого сообщения для полосы над историей.</summary>
    private string PinnedSnippet(string sessionId, string messageId)
    {
        var entry = GetHistoryForSession(sessionId).FirstOrDefault(item => item.Id == messageId);
        if (entry is null) return "сообщение";
        var source = entry.FileName.Length > 0 ? "Файл: " + entry.FileName : entry.Text;
        var singleLine = source.Replace("\r", " ").Replace("\n", " ").Trim();
        if (singleLine.Length > 80) singleLine = singleLine[..80] + "…";
        return (entry.Own ? "Вы" : entry.Sender) + ": " + singleLine;
    }

    /// <summary>Если отредактировали закреплённое сообщение — обновить текст полосы.</summary>
    private void RefreshPinnedIfNeeded(string sessionId, string messageId)
    {
        if (_pinnedMessages.TryGetValue(sessionId, out var pinnedId) && pinnedId == messageId &&
            _chatSessionId == sessionId)
            _chatPanel?.SetPinned(messageId, PinnedSnippet(sessionId, messageId));
    }

    /// <summary>Отправка квитанции «прочитано»; по умолчанию ничего не делает (переопределяется в формах).</summary>
    protected virtual void SendReadReceipt(string sessionId, IReadOnlyList<string> messageIds) { }

    protected void HandleReadReceipt(string sessionId, string idsText)
    {
        if (sessionId.Length == 0 || idsText.Length == 0) return;
        var ids = idsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var history = History(sessionId);
        var changed = false;
        foreach (var id in ids)
        {
            var index = history.FindIndex(entry => entry.Id == id && entry.Own);
            if (index < 0 || history[index].Delivery != "✓") continue;
            history[index] = history[index] with { Delivery = "✓✓" };
            if (_chatSessionId == sessionId) _chatPanel?.UpdateDelivery(id, "✓✓ Прочитано");
            changed = true;
        }
        if (changed) PersistSession();
    }

    protected virtual void OnChatSessionShown(string sessionId) { }

    protected ChatHostForm(string persistenceProfile = "")
    {
        _persistenceProfile = persistenceProfile;
        WindowsNotifications = new WindowsNotifications(this, ExitApplication, ToggleMiniPanel);
        RestoreSession();
        if (!_chats.ContainsKey(GeneralChatId)) _chats[GeneralChatId] = new List<ChatEntry>();
        if (!_sessionTitles.ContainsKey(GeneralChatId)) _sessionTitles[GeneralChatId] = "Общий чат (без ремонта)";
        _retryTimer.Tick += (_, _) => RetryTimedOutMessage();
        _retryTimer.Start();
    }

    protected abstract Task SendSessionMessageAsync(string sessionId, AppMessage message);
    protected abstract Task SendSessionAckAsync(string sessionId, string messageId);

    protected StackPanel CreateDiagnosticsButtons()
    {
        var panel = new StackPanel();
        var copy = Ui.ActionButton("Скопировать диагностику", "QuietButton");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(AppLog.BuildDiagnostics());
                MessageBox.Show(this, "Диагностика скопирована в буфер обмена.",
                    "СЕРВИС ЛЕГКО", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось скопировать диагностику: " + ex.Message,
                    "СЕРВИС ЛЕГКО", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        panel.Children.Add(copy);
        var openLog = Ui.ActionButton("Открыть журнал", "QuietButton");
        openLog.Click += (_, _) =>
        {
            try
            {
                var directory = Path.GetDirectoryName(AppLog.LogFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    System.Diagnostics.Process.Start("explorer.exe", directory);
                }
            }
            catch { }
        };
        panel.Children.Add(openLog);
        return panel;
    }

    protected void OpenGeneralChat() => OpenChatSession(GeneralChatId);
    protected void OpenChat() { if (IsJobAccepted) OpenChatSession(CurrentJobId); }

    protected StackPanel CreateThemeSelector(AppSettings settings)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(Ui.Note("Тема оформления:"));
        var combo = new ComboBox
        {
            ItemsSource = new[] { "Как в Windows", "Светлая", "Тёмная" },
            SelectedIndex = settings.Theme == "dark" ? 2 : settings.Theme == "light" ? 1 : 0
        };
        combo.SelectionChanged += (_, _) =>
        {
            settings.Theme = combo.SelectedIndex == 2 ? "dark" : combo.SelectedIndex == 1 ? "light" : "system";
            SettingsStore.Save(settings);
        };
        panel.Children.Add(combo);
        panel.Children.Add(Ui.Note("Новая тема применится при следующем запуске программы."));
        return panel;
    }

    protected StackPanel CreateSoundSettingsPanel(AppSettings settings)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(Ui.Note("Звуки уведомлений:"));
        void Add(string label, AlertKind kind, string current, Action<string> assign, Action<string> assignCustom)
        {
            panel.Children.Add(Ui.Note(label));
            var combo = new ComboBox
            {
                ItemsSource = SoundCatalog.OptionsWithCustom,
                DisplayMemberPath = nameof(SoundOption.Name),
                SelectedValuePath = nameof(SoundOption.Id),
                SelectedValue = SoundCatalog.Normalize(current)
            };
            var lastSelected = SoundCatalog.Normalize(current);
            var suppress = false;
            combo.SelectionChanged += (_, _) =>
            {
                if (suppress || combo.SelectedValue is not string soundId) return;
                if (soundId == SoundCatalog.CustomId)
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "Выберите свой звук",
                        Filter = "Звук (WAV, MP3)|*.wav;*.mp3|Все файлы|*.*"
                    };
                    if (dialog.ShowDialog(this) == true)
                    {
                        var stored = SoundCatalog.StoreCustomFile(kind, dialog.FileName);
                        if (stored is not null)
                        {
                            assignCustom(stored);
                            assign(SoundCatalog.CustomId);
                            SettingsStore.Save(settings);
                            lastSelected = SoundCatalog.CustomId;
                            Alerts.PlayOnce(kind);
                            return;
                        }
                    }
                    suppress = true;
                    combo.SelectedValue = lastSelected;
                    suppress = false;
                    return;
                }
                assign(soundId);
                SettingsStore.Save(settings);
                lastSelected = soundId;
                Alerts.PlayOnce(kind);
            };
            panel.Children.Add(combo);
        }
        Add("Новая заявка у мастера", AlertKind.Request, settings.RequestSound,
            value => settings.RequestSound = value, value => settings.RequestSoundCustom = value);
        Add("Завершение ремонта у менеджера", AlertKind.Completed, settings.CompletedSound,
            value => settings.CompletedSound = value, value => settings.CompletedSoundCustom = value);
        Add("Чат ремонта", AlertKind.JobChat, settings.JobChatSound,
            value => settings.JobChatSound = value, value => settings.JobChatSoundCustom = value);
        Add("Общий чат", AlertKind.GeneralChat, settings.GeneralChatSound,
            value => settings.GeneralChatSound = value, value => settings.GeneralChatSoundCustom = value);
        return panel;
    }

    protected void OpenChatForSession(string sessionId, string title)
    {
        _sessionTitles[sessionId] = title;
        OpenChatSession(sessionId);
    }

    protected List<ChatEntry> GetJobHistory() => GetHistoryForSession(CurrentJobId);

    protected List<ChatEntry> GetHistoryForSession(string sessionId) =>
        sessionId.Length > 0 && _chats.TryGetValue(sessionId, out var history)
            ? history : new List<ChatEntry>();

    protected virtual List<ManagerJobRecord> GetPersistedJobs() => new();

    /// <summary>Заявки, убранные в архив без связи с мастером (переопределяется у менеджера).</summary>
    protected virtual IReadOnlyCollection<string> GetPendingArchived() => Array.Empty<string>();

    /// <summary>Восстановить список заархивированных офлайн заявок (переопределяется у менеджера).</summary>
    protected virtual void RestorePendingArchived(IReadOnlyList<string> jobIds) { }

    /// <summary>Заявки, удалённые без связи с мастером (переопределяется у менеджера).</summary>
    protected virtual IReadOnlyCollection<string> GetPendingRemoved() => Array.Empty<string>();

    /// <summary>Восстановить список удалённых офлайн заявок (переопределяется у менеджера).</summary>
    protected virtual void RestorePendingRemoved(IReadOnlyList<string> jobIds) { }

    protected int PendingMessageCount => _outbox.Count;
    protected bool HasPendingMessages(string sessionId) => _outbox.Any(item => item.SessionId == sessionId);

    private List<ChatEntry> History(string sessionId)
    {
        if (!_chats.TryGetValue(sessionId, out var history))
        {
            history = new List<ChatEntry>();
            _chats[sessionId] = history;
        }
        return history;
    }

    private string SessionTitle(string sessionId) => _sessionTitles.TryGetValue(sessionId, out var title)
        ? title : (sessionId == GeneralChatId ? "Общий чат (без ремонта)" : "Чат по заявке: " + CurrentJobText);

    private void OpenChatSession(string sessionId)
    {
        if (sessionId.Length == 0) return;
        Alerts.Stop();
        if (_notification is not null && !_notification.IsDisposed) _notification.Close();
        _chatSessionId = sessionId;
        if (_chatPanel is not null)
        {
            _chatPanel.Bind(sessionId, History(sessionId));
            _chatPanel.SetPendingCount(
                _outbox.Count(item => item.SessionId == sessionId),
                _outbox.Count(item => item.SessionId == sessionId && item.Message.Type == MessageTypes.FileOffer));
            // восстанавливаем полосу закреплённого сообщения, если оно есть
            _chatPanel.SetPinned(
                _pinnedMessages.TryGetValue(sessionId, out var pinnedId) ? pinnedId : "",
                _pinnedMessages.TryGetValue(sessionId, out var pinnedId2) ? PinnedSnippet(sessionId, pinnedId2) : "");
        }
        OnChatSessionShown(sessionId);
    }

    private void QueueTextMessage(string sessionId, string text)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = new ChatEntry("Вы", text, DateTime.Now, true, Id: id, Delivery: "В очереди");
        History(sessionId).Add(entry);
        if (_chatSessionId == sessionId) _chatPanel?.Append(entry);
        _outbox.Add(new OutgoingChatMessage
        {
            SessionId = sessionId,
            Message = AppMessage.Chat(sessionId, "", text, id)
        });
        PersistSession();
        UpdateQueueStatus();
        _ = FlushOutboxAsync();
    }

    protected async Task QueueFileMessageAsync(string sessionId, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > FileTransferService.MaxFileBytes)
                throw new InvalidOperationException("Файл больше 5 ГБ.");
            var id = Guid.NewGuid().ToString("N");
            var cachedPath = Path.Combine(ChatSessionStore.OutboxDirectory(_persistenceProfile.Length > 0 ? _persistenceProfile : "default"),
                id + info.Extension);
            var (hash, size) = await IncomingTransferStore.HashAndCopyAsync(info.FullName, cachedPath);
            var entry = new ChatEntry("Вы", "", DateTime.Now, true, info.Name, info.FullName, size, id, "В очереди");
            History(sessionId).Add(entry);
            if (_chatSessionId == sessionId) _chatPanel?.Append(entry);
            _outbox.Add(new OutgoingChatMessage
            {
                SessionId = sessionId,
                Message = AppMessage.FileOffer(sessionId, "", info.Name, size, hash, id),
                FilePath = cachedPath,
                FileSize = size,
                Sha256 = hash
            });
            PersistSession();
            UpdateQueueStatus();
            await FlushOutboxAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось подготовить файл: " + ex.Message,
                "Файл", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected async Task FlushOutboxAsync()
    {
        if (_flushing || _hostClosing) return;
        _flushing = true;
        try
        {
            var sessionIds = _outbox.Select(item => item.SessionId).Distinct().ToList();
            foreach (var sessionId in sessionIds)
            {
                while (!_hostClosing)
                {
                    var item = _outbox.FirstOrDefault(entry => entry.SessionId == sessionId);
                    if (item is null || item.AwaitingAck) break;
                    try
                    {
                        if (!_outbox.Contains(item)) continue;
                        item.SendingNow = true;
                        await SendSessionMessageAsync(item.SessionId, item.Message);
                        item.AwaitingAck = true;
                        item.LastSentAtUtc = DateTime.UtcNow;
                        break;
                    }
                    catch
                    {
                        item.AwaitingAck = false;
                        break;
                    }
                    finally
                    {
                        item.SendingNow = false;
                    }
                }
            }
        }
        finally
        {
            _flushing = false;
            UpdateQueueStatus();
        }
    }

    protected void HandleFileAccept(string sessionId, AppMessage message)
    {
        var item = _outbox.FirstOrDefault(entry =>
            entry.SessionId == sessionId && entry.Message.MessageId == message.MessageId);
        if (item is null) return;
        item.ResumeOffset = Math.Max(0, Math.Min(message.Offset, item.FileSize));
        if (item.ResumeOffset >= item.FileSize)
        {
            _ = SendFileEndSafeAsync(item);
            return;
        }
        if (item.Streaming) return;
        item.Streaming = true;
        item.StreamGeneration++;
        _ = StreamFileChunksAsync(item, item.StreamGeneration);
    }

    private async Task StreamFileChunksAsync(OutgoingChatMessage item, int generation)
    {
        try
        {
            var guid = Guid.ParseExact(item.Message.MessageId, "N");
            await using var stream = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.Position = item.ResumeOffset;
            var buffer = new byte[FileTransferService.ChunkBytes];
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var lastUiUpdate = 0L;
            var sent = item.ResumeOffset;
            while (sent < item.FileSize)
            {
                if (!_outbox.Contains(item) || item.StreamGeneration != generation) return;
                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) break;
                await SendSessionChunkAsync(item.SessionId, new FileChunk(guid, sent, buffer[..read]));
                sent += read;
                item.ResumeOffset = sent;
                if (watch.ElapsedMilliseconds - lastUiUpdate > 300 && _chatSessionId == item.SessionId && _chatPanel is not null)
                {
                    lastUiUpdate = watch.ElapsedMilliseconds;
                    var speed = sent > 0 && watch.Elapsed.TotalSeconds > 0.5
                        ? " · " + FormatSize((long)(sent / watch.Elapsed.TotalSeconds)) + "/с"
                        : "";
                    _chatPanel.UpdateFileProgress(item.Message.MessageId,
                        $"Отправка · {FormatSize(sent)} из {FormatSize(item.FileSize)} · {sent * 100 / Math.Max(1, item.FileSize)}%{speed}",
                        (int)(sent * 100 / Math.Max(1, item.FileSize)));
                }
            }
            if (!_outbox.Contains(item) || item.StreamGeneration != generation) return;
            await SendSessionMessageAsync(item.SessionId, AppMessage.FileEnd(item.SessionId, item.Message.MessageId));
            item.AwaitingAck = true;
            item.LastSentAtUtc = DateTime.UtcNow;
        }
        catch
        {
            item.Streaming = false;
        }
    }

    private async Task SendFileEndSafeAsync(OutgoingChatMessage item)
    {
        try
        {
            await SendSessionMessageAsync(item.SessionId, AppMessage.FileEnd(item.SessionId, item.Message.MessageId));
            item.AwaitingAck = true;
            item.LastSentAtUtc = DateTime.UtcNow;
        }
        catch { }
    }

    private void CancelPendingFiles(string sessionId)
    {
        var items = _outbox
            .Where(item => item.SessionId == sessionId && item.Message.Type == MessageTypes.FileOffer)
            .ToList();
        if (items.Count == 0) return;
        var ids = items.Select(item => item.Message.MessageId).Where(id => id.Length > 0).ToList();
        foreach (var item in items)
        {
            item.StreamGeneration++;
            item.Streaming = false;
            _outbox.Remove(item);
            if (item.FilePath.Length > 0)
            {
                try { File.Delete(item.FilePath); } catch { }
            }
            _ = SendCancelSafeAsync(sessionId, item.Message.MessageId);
        }
        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        History(sessionId).RemoveAll(entry => idSet.Contains(entry.Id));
        if (_chatSessionId == sessionId && _chatPanel is not null)
            _chatPanel.RemoveMessages(ids);
        PersistSession();
        UpdateQueueStatus();
        _ = FlushOutboxAsync();
    }

    /// <summary>Удалить неотправленное сообщение из очереди (и из истории чата).</summary>
    private void DeleteQueuedMessage(string sessionId, string messageId)
    {
        if (sessionId.Length == 0 || messageId.Length == 0) return;
        var index = _outbox.FindIndex(item => item.SessionId == sessionId && item.Message.MessageId == messageId);
        if (index >= 0)
        {
            var item = _outbox[index];
            item.StreamGeneration++;
            item.Streaming = false;
            _outbox.RemoveAt(index);
            if (item.FilePath.Length > 0)
            {
                try { File.Delete(item.FilePath); } catch { }
            }
            if (item.Message.Type == MessageTypes.FileOffer && item.AwaitingAck)
                _ = SendCancelSafeAsync(sessionId, messageId);
        }
        var history = History(sessionId);
        var removed = history.RemoveAll(entry => entry.Id == messageId && entry.Own);
        if (index < 0 && removed == 0) return;
        if (_chatSessionId == sessionId && _chatPanel is not null)
            _chatPanel.RemoveMessages(new[] { messageId });
        PersistSession();
        UpdateQueueStatus();
        _ = FlushOutboxAsync();
    }

    private async Task SendCancelSafeAsync(string sessionId, string messageId, string reason = "canceled")
    {
        try { await SendSessionMessageAsync(sessionId, AppMessage.FileCancel(sessionId, messageId, reason)); }
        catch { }
    }

    private void FailOutgoingFile(string sessionId, string messageId, string reasonText)
    {
        var index = _outbox.FindIndex(item => item.SessionId == sessionId && item.Message.MessageId == messageId);
        if (index >= 0)
        {
            var item = _outbox[index];
            item.StreamGeneration++;
            item.Streaming = false;
            _outbox.RemoveAt(index);
            if (item.FilePath.Length > 0)
            {
                try { File.Delete(item.FilePath); } catch { }
            }
        }
        var history = History(sessionId);
        var entryIndex = history.FindIndex(entry => entry.Id == messageId);
        if (entryIndex >= 0)
            history[entryIndex] = history[entryIndex] with { Delivery = "Ошибка: " + reasonText };
        if (_chatSessionId == sessionId && _chatPanel is not null)
            _chatPanel.UpdateFileProgress(messageId, "Не удалось отправить: " + reasonText, -1, true, true);
        PersistSession();
        UpdateQueueStatus();
        _ = FlushOutboxAsync();
    }

    protected void HandleIncomingFileCancel(string sessionId, string messageId)
    {
        IncomingTransferStore.Cancel(messageId);
        var history = History(sessionId);
        var index = history.FindIndex(entry => entry.Id == messageId);
        if (index >= 0 && history[index].Delivery != "✓")
        {
            history.RemoveAt(index);
            if (_chatSessionId == sessionId && _chatPanel is not null)
                _chatPanel.RemoveMessages(new[] { messageId });
        }
        PersistSession();
    }

    protected void ReceiveFileOfferForSession(string sessionId, string title, AppMessage message)
    {
        _sessionTitles[sessionId] = title;
        if (IsDuplicate(sessionId, message.MessageId))
        {
            var completed = History(sessionId).FirstOrDefault(entry => entry.Id == message.MessageId);
            var offset = completed is not null && completed.Delivery == "✓"
                ? message.Size
                : IncomingTransferStore.GetOffset(message.MessageId);
            _ = SendFileAcceptSafeAsync(sessionId, message.MessageId, offset);
            return;
        }
        if (message.Size > FileTransferService.MaxFileBytes || message.Size <= 0)
        {
            _ = SendCancelSafeAsync(sessionId, message.MessageId, "size");
            return;
        }
        try
        {
            IncomingTransferStore.EnsureFreeSpace(message.Size);
        }
        catch (IOException)
        {
            AppLog.Error("Приём файла отклонён: недостаточно места на диске");
            _ = SendCancelSafeAsync(sessionId, message.MessageId, "no_space");
            MessageBox.Show(this, "Недостаточно места на диске, файл «" +
                FileTransferService.SanitizeFileName(message.FileName) + "» не принят. Освободите место и попросите отправить снова.",
                "Нет места на диске", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var meta = IncomingTransferStore.Begin(message.MessageId, sessionId, title, message.Sender,
            message.FileName, message.Size, message.Sha256);
        var entry = new ChatEntry(message.Sender, "", message.SentAt.ToLocalTime(), false,
            meta.FileName, "", meta.Size, message.MessageId, "receiving");
        History(sessionId).Add(entry);
        if (_chatSessionId == sessionId) _chatPanel?.Append(entry);
        PersistSession();
        _ = SendFileAcceptSafeAsync(sessionId, message.MessageId, IncomingTransferStore.GetOffset(message.MessageId));
    }

    private async Task SendFileAcceptSafeAsync(string sessionId, string messageId, long offset)
    {
        try { await SendSessionMessageAsync(sessionId, AppMessage.FileAccept(sessionId, messageId, offset)); }
        catch { }
    }

    private readonly Dictionary<string, long> _incomingUiUpdates = new();

    protected void HandleIncomingChunk(FileChunk chunk)
    {
        var id = chunk.MessageId.ToString("N");
        var meta = IncomingTransferStore.LoadMeta(id);
        if (meta is null) return;
        var current = IncomingTransferStore.GetOffset(id);
        if (chunk.Offset != current)
        {
            if (chunk.Offset > current)
                AppLog.Error("Пропуск части файла: ожидалось смещение " + current + ", пришло " + chunk.Offset);
            return;
        }
        try
        {
            IncomingTransferStore.Write(id, chunk.Offset, chunk.Data);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось записать часть файла", ex);
            return;
        }
        var now = Environment.TickCount64;
        if (_incomingUiUpdates.TryGetValue(id, out var last) && now - last < 300) return;
        _incomingUiUpdates[id] = now;
        var received = IncomingTransferStore.GetOffset(id);
        if (_chatSessionId == meta.SessionId && _chatPanel is not null && meta.Size > 0)
            _chatPanel.UpdateFileProgress(id,
                $"Получение · {FormatSize(received)} из {FormatSize(meta.Size)} · {received * 100 / meta.Size}%",
                (int)(received * 100 / meta.Size));
    }

    protected void HandleIncomingFileEnd(string sessionId, string messageId)
    {
        _incomingUiUpdates.Remove(messageId);
        var meta = IncomingTransferStore.LoadMeta(messageId);
        if (meta is null)
        {
            var existing = History(sessionId).FirstOrDefault(entry => entry.Id == messageId);
            if (existing is not null && existing.Delivery == "✓")
                _ = SendAckSafeAsync(sessionId, messageId);
            return;
        }
        var path = IncomingTransferStore.Finalize(messageId);
        if (path is null)
        {
            IncomingTransferStore.Cancel(messageId);
            var badHistory = History(sessionId);
            var badIndex = badHistory.FindIndex(entry => entry.Id == messageId);
            if (badIndex >= 0) badHistory.RemoveAt(badIndex);
            if (_chatSessionId == sessionId && _chatPanel is not null)
                _chatPanel.RemoveMessages(new[] { messageId });
            _ = SendCancelSafeAsync(sessionId, messageId, "hash_mismatch");
            AppLog.Error("Файл получен с ошибкой целостности: " + meta.FileName);
            MessageBox.Show(this, "Файл «" + meta.FileName + "» пришёл повреждённым. Попросите отправить его снова.",
                "Ошибка передачи", MessageBoxButton.OK, MessageBoxImage.Warning);
            PersistSession();
            return;
        }
        var history = History(sessionId);
        var index = history.FindIndex(entry => entry.Id == messageId);
        if (index >= 0)
            history[index] = history[index] with { FilePath = path, Delivery = "✓" };
        if (_chatSessionId == sessionId && _chatPanel is not null)
        {
            _chatPanel.UpdateFileProgress(messageId, "✓ Файл получен", 100, true);
            _chatPanel.ShowThumbnail(messageId, path);
        }
        PersistSession();
        NotifyIncoming(sessionId, "Получен файл", meta.Sender, meta.FileName);
        _ = SendAckSafeAsync(sessionId, messageId);
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} ГБ"
        : bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.0} МБ"
            : $"{Math.Max(1, bytes / 1024)} КБ";

    protected abstract Task SendSessionChunkAsync(string sessionId, FileChunk chunk);


    protected void Acknowledge(string sessionId, string messageId)
    {
        if (messageId.Length == 0) return;
        var index = _outbox.FindIndex(item => item.SessionId == sessionId && item.Message.MessageId == messageId);
        if (index < 0) return;
        _outbox.RemoveAt(index);
        MarkDelivered(sessionId, messageId);
        PersistSession();
        UpdateQueueStatus();
        _ = FlushOutboxAsync();
    }

    protected void NotifyTransportChanged(bool connected)
    {
        foreach (var item in _outbox)
        {
            item.AwaitingAck = false;
            item.SendingNow = false;
            item.Streaming = false;
            item.StreamGeneration++;
            item.RetryCount = 0;
        }
        if (connected) _ = FlushOutboxAsync();
        UpdateQueueStatus();
    }

    private void RetryTimedOutMessage()
    {
        if (_hostClosing) return;
        foreach (var item in _outbox.Where(item => ReliableMessageLogic.NeedsRetry(item, DateTime.UtcNow)))
        {
            item.AwaitingAck = false;
            item.RetryCount++;
        }
        if (_outbox.Any(item => !item.AwaitingAck)) _ = FlushOutboxAsync();
    }

    private void MarkDelivered(string sessionId, string messageId)
    {
        var history = History(sessionId);
        var index = history.FindIndex(entry => entry.Id == messageId);
        if (index < 0) return;
        history[index] = history[index] with { Delivery = "✓" };
        if (_chatSessionId == sessionId) _chatPanel?.UpdateDelivery(messageId, "✓ Доставлено");
    }

    private void UpdateQueueStatus()
    {
        if (_chatPanel is not null)
        {
            _chatPanel.SetPendingCount(
                _outbox.Count(item => item.SessionId == _chatSessionId),
                _outbox.Count(item => item.SessionId == _chatSessionId && item.Message.Type == MessageTypes.FileOffer));
        }
    }

    protected void ReceiveChat(AppMessage message)
    {
        var sessionId = ResolveSession(message.JobId);
        if (sessionId.Length > 0) ReceiveChatForSession(sessionId, SessionTitle(sessionId), message);
    }

    protected void ReceiveChatForSession(string sessionId, string title, AppMessage message)
    {
        _sessionTitles[sessionId] = title;
        if (IsDuplicate(sessionId, message.MessageId))
        {
            AcknowledgeReceived(sessionId, message.MessageId);
            return;
        }
        var entry = new ChatEntry(message.Sender, message.Text, message.SentAt.ToLocalTime(), false,
            Id: message.MessageId);
        History(sessionId).Add(entry);
        if (_chatSessionId == sessionId) _chatPanel?.Append(entry);
        PersistSession();
        NotifyIncoming(sessionId, "Новое сообщение", message.Sender, message.Text);
        AcknowledgeReceived(sessionId, message.MessageId);
    }

    protected void ReceiveFileOffer(AppMessage message)
    {
        var sessionId = ResolveSession(message.JobId);
        if (sessionId.Length > 0) ReceiveFileOfferForSession(sessionId, SessionTitle(sessionId), message);
    }

    protected void HandleFileCancel(string sessionId, AppMessage message)
    {
        if (sessionId.Length == 0) return;
        var isOutgoing = _outbox.Any(item =>
            item.SessionId == sessionId && item.Message.MessageId == message.MessageId);
        if (isOutgoing)
        {
            var reason = message.Reason == "no_space" ? "у получателя нет места на диске" : "передача отклонена получателем";
            FailOutgoingFile(sessionId, message.MessageId, reason);
        }
        else
        {
            HandleIncomingFileCancel(sessionId, message.MessageId);
        }
    }

    private bool IsDuplicate(string sessionId, string messageId) =>
        ReliableMessageLogic.ContainsMessage(History(sessionId), messageId);

    private void AcknowledgeReceived(string sessionId, string messageId)
    {
        if (messageId.Length == 0) return;
        _ = SendAckSafeAsync(sessionId, messageId);
    }

    private async Task SendAckSafeAsync(string sessionId, string messageId)
    {
        try { await SendSessionAckAsync(sessionId, messageId); }
        catch { }
    }

    private string ResolveSession(string sessionId)
    {
        if (sessionId == GeneralChatId) return GeneralChatId;
        return IsKnownActiveJob(sessionId) ? sessionId : "";
    }

    protected virtual bool IsKnownActiveJob(string jobId) =>
        IsJobAccepted && jobId == CurrentJobId;

    private void NotifyIncoming(string sessionId, string title, string sender, string text)
    {
        if (_chatPanel is not null && _chatSessionId == sessionId &&
            IsActive && _chatPanel.IsVisible)
        {
            Alerts.Stop();
            return;
        }
        void Open() => OpenChatSession(sessionId);
        if (_notification is null || _notification.IsDisposed)
        {
            _notification = new ChatNotificationForm(sender, text, Open, () => Alerts.Stop(),
                minutes => Alerts.MuteFor(TimeSpan.FromMinutes(minutes)));
            _notification.FormClosed += (_, _) => _notification = null;
            _notification.ShowAtCorner(this);
        }
        var sound = sessionId == GeneralChatId || sessionId.EndsWith(":" + GeneralChatId, StringComparison.Ordinal)
            ? AlertKind.GeneralChat
            : AlertKind.JobChat;
        Alerts.Start(sound);
        WindowsNotifications.Show(title, sender + ": " + text, Open);
    }

    protected void CloseJobChatWindow() => CloseChatSession(CurrentJobId);

    protected void CloseChatSession(string sessionId)
    {
        if (_chatSessionId == sessionId)
        {
            _chatSessionId = "";
            _chatPanel?.Unbind();
        }
        if (_notification is not null && !_notification.IsDisposed) _notification.Close();
    }

    protected void ResetSession(string sessionId)
    {
        if (sessionId.Length > 0)
        {
            _chats.Remove(sessionId);
            _sessionTitles.Remove(sessionId);
            _outbox.RemoveAll(item => item.SessionId == sessionId);
        }
        if (_chatSessionId == sessionId)
        {
            _chatSessionId = "";
            _chatPanel?.Unbind();
        }
        _notification?.Close();
        _notification = null;
        PersistSession();
    }

    protected void ResetJobChat() => ResetSession(CurrentJobId);

    protected void PersistSession()
    {
        if (_persistenceProfile.Length == 0) return;
        var snapshot = new ChatSessionSnapshot
        {
            CurrentJobId = CurrentJobId,
            CurrentJobText = CurrentJobText,
            IsJobAccepted = IsJobAccepted,
            JobStartedAt = JobStartedAt,
            JobCompletionSaved = JobCompletionSaved,
            Jobs = GetPersistedJobs(),
            Chats = _chats.Select(pair => new ChatSessionBucket
            {
                SessionId = pair.Key,
                Title = SessionTitle(pair.Key),
                Entries = pair.Value.ToList(),
                PinnedId = _pinnedMessages.TryGetValue(pair.Key, out var pinned) ? pinned : ""
            }).ToList(),
            Outbox = _outbox.ToList(),
            PendingArchivedJobs = GetPendingArchived().ToList(),
            PendingRemovedJobs = GetPendingRemoved().ToList()
        };
        ChatSessionStore.Save(_persistenceProfile, snapshot);
    }

    private void RestoreSession()
    {
        if (_persistenceProfile.Length == 0) return;
        var snapshot = ChatSessionStore.Load(_persistenceProfile);
        CurrentJobId = snapshot.CurrentJobId;
        CurrentJobText = snapshot.CurrentJobText;
        IsJobAccepted = snapshot.IsJobAccepted;
        JobStartedAt = snapshot.JobStartedAt;
        JobCompletionSaved = snapshot.JobCompletionSaved;
        foreach (var bucket in snapshot.Chats)
        {
            if (bucket.SessionId.Length == 0) continue;
            _chats[bucket.SessionId] = bucket.Entries;
            _sessionTitles[bucket.SessionId] = bucket.Title;
            if (bucket.PinnedId.Length > 0) _pinnedMessages[bucket.SessionId] = bucket.PinnedId;
        }
        _outbox.AddRange(snapshot.Outbox);
        RestorePendingArchived(snapshot.PendingArchivedJobs);
        RestorePendingRemoved(snapshot.PendingRemovedJobs);
    }

    protected void SwitchRole()
    {
        _allowRealClose = true;
        var roleForm = new RoleForm();
        roleForm.Show();
        Close();
    }

    public void ExitApplication()
    {
        if (PendingMessageCount > 0)
        {
            var answer = MessageBox.Show(this,
                $"В очереди ещё {PendingMessageCount} неотправленных сообщений/файлов. Они сохранятся и уйдут при следующем запуске программы. Выйти сейчас?",
                "СЕРВИС ЛЕГКО", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        _allowRealClose = true;
        IsApplicationExiting = true;
        System.Windows.Application.Current.Shutdown();
    }

    public void HideToTrayOnStartup()
    {
        _trayHintShown = true;
        Hide();
    }

    protected void UpdateTrayStatus(string status) => WindowsNotifications.SetStatus(status);

    private MiniForm? _miniPanel;
    protected MiniForm? MiniPanel => _miniPanel;

    /// <summary>Создать мини-панель (переопределяется в формах ролей). Null — мини-режима нет.</summary>
    protected virtual MiniForm? CreateMiniPanel() => null;

    private void ToggleMiniPanel()
    {
        _miniPanel ??= CreateMiniPanel();
        if (_miniPanel is null)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            return;
        }
        _miniPanel.Toggle();
    }

    /// <summary>Скрыть мини-панель и показать полное окно программы.</summary>
    protected void ShowMainFromMini()
    {
        _miniPanel?.Hide();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Свернуть полное окно и показать мини-панель над треем.</summary>
    protected void EnterMiniMode()
    {
        _miniPanel ??= CreateMiniPanel();
        if (_miniPanel is null) return;
        Hide();
        _miniPanel.ShowPanel();
    }

    internal static void PrepareForWindowsShutdown() => IsApplicationExiting = true;

    protected bool TryHideToTray(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowRealClose || IsApplicationExiting) return false;
        e.Cancel = true;
        Hide();
        Alerts.Stop();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            WindowsNotifications.Show(
                "СЕРВИС ЛЕГКО",
                "Программа продолжает работать в области уведомлений возле часов.");
        }
        return true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (TryHideToTray(e)) return;
        _hostClosing = true;
        _retryTimer.Stop();
        PersistSession();
        Alerts.Stop();
        _miniPanel?.CloseForce();
        WindowsNotifications.Dispose();
        base.OnClosing(e);
    }
}
