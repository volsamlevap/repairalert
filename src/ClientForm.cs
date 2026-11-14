using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RepairAlert;

internal sealed class ClientForm : ChatHostForm
{
    private const int Port = 47891;
    private readonly string _managerName = Environment.UserName;
    private readonly string _managerAccount = Environment.UserDomainName + "\\" + Environment.UserName;
    private readonly string _machineName = Environment.MachineName;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, ManagerJobState> _jobs = new();
    private readonly List<ManagerArchiveEntry> _archive;
    private readonly HashSet<string> _pendingArchivedJobs = new(StringComparer.Ordinal);
    // Заявки других менеджеров (синхронизирует мастер) и очередь удалений без связи
    private readonly Dictionary<string, RemoteJobInfo> _remoteJobs = new();
    private readonly HashSet<string> _pendingRemovedJobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _syncSeenIds = new(StringComparer.Ordinal);
    private bool _remoteSyncActive;
    private string _selectedRemoteId = "";

    private readonly Border _statusDot;
    private readonly TextBlock _statusText;
    private readonly TextBlock _queueText;
    private readonly Border _offlineBanner;
    private readonly TextBlock _offlineBannerText;
    private readonly Button _offlineRetryButton;
    private readonly DispatcherTimer _offlineShowTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _offlineHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _offlineGreen;
    private readonly StackPanel _jobsList;
    private readonly TextBlock _jobsEmpty;
    private readonly Ui.SegmentedControl _jobsTabs;
    private readonly Grid _detailHost;
    private readonly Grid _detailCard;
    private readonly Border _cardTop;
    private readonly TextBlock _detailTitle;
    private readonly StackPanel _detailBadgeRow;
    private readonly TextBlock _detailTimers;
    private readonly TextBlock _detailDescription;
    private readonly TextBlock _detailFields;
    private readonly TextBlock _detailComment;
    private readonly StackPanel _detailActions;
    private readonly Border _archiveUndoBar;
    private readonly TextBlock _archiveUndoText;
    private readonly Ui.SegmentedControl _chatTabs;
    private readonly ChatPanel _chatPanel;
    private readonly ScrollViewer _filesView;
    private readonly StackPanel _filesList;
    private readonly Grid _overlayHost;
    private readonly TextBox _ipInput;
    private readonly Button _connectButton;
    private readonly TextBox _searchInput;
    private readonly Dictionary<string, DateTime> _reminderSentAt = new();

    private readonly DispatcherTimer _reconnectTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _networkChangeTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _jobElapsedTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _reportRetryTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _archiveUndoTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private List<PendingReport> _pendingReports = new();
    private bool _flushingReports;
    private PeerConnection? _connection;
    private string _selectedJobId = "";
    private int _jobsTabIndex;
    private string _archiveUndoJobId = "";
    private int _archiveUndoRemaining;
    private bool _closing;
    private bool _connecting;
    private bool _authorized;
    private bool _versionMismatch;
    private DateTime _versionMismatchAt;
    private DateTime _versionWarnedAt;
    private string _clientNonce = "";
    private byte[]? _pairingSecret;
    private string? _pendingCode;
    private PairingForm? _pairingDialog;

    private sealed class ManagerJobState
    {
        public required string JobId { get; init; }
        public required string Text { get; set; }
        public bool IsAccepted { get; set; }
        public DateTime ReceivedAt { get; set; }
        public DateTime StartedAt { get; set; }
        public bool CompletionSaved { get; set; }
        public bool ReportPending { get; set; }
        public DateTime CompletedAt { get; set; }
        public string Comment { get; set; } = "";
        public string ReportPath { get; set; } = "";
        public bool ArchivePending { get; set; }
        public string Client { get; set; } = "";
        public string Device { get; set; } = "";
        public string Serial { get; set; } = "";
        public bool Urgent { get; set; }
        public JobDetails Details => new(Client, Device, Serial, Urgent);
    }

    public ClientForm() : base("manager")
    {
        _settings = SettingsStore.Load();
        _archive = ManagerArchiveStore.Load();
        // Чужие заявки видны и без связи — показываем копию, сохранённую при последнем подключении
        foreach (var remoteJob in RemoteJobsStore.Load())
            if (remoteJob.JobId.Length > 0) _remoteJobs[remoteJob.JobId] = remoteJob;
        RestoreJobs();
        Ui.Prepare(this, "СЕРВИС ЛЕГКО — менеджер", 1180, 760, 1000, 640);

        var root = new Grid { Background = Ui.Background };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        root.Children.Add(BuildHeader());

        _offlineBannerText = new TextBlock
        {
            Foreground = Ui.Ink,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        _offlineRetryButton = Ui.ActionButton("Повторить сейчас", "WhiteButton");
        _offlineRetryButton.Height = 28;
        _offlineRetryButton.MinHeight = 28;
        _offlineRetryButton.FontSize = 11;
        _offlineRetryButton.Padding = new Thickness(12, 0, 12, 0);
        _offlineRetryButton.Margin = new Thickness(12, 0, 0, 0);
        _offlineRetryButton.VerticalAlignment = VerticalAlignment.Center;
        _offlineRetryButton.Click += async (_, _) => await ManualConnectAsync();
        var bannerGrid = new Grid { Margin = new Thickness(16, 8, 16, 8) };
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bannerGrid.Children.Add(_offlineBannerText);
        Grid.SetColumn(_offlineRetryButton, 1);
        bannerGrid.Children.Add(_offlineRetryButton);
        _offlineBanner = new Border
        {
            Background = Ui.AccentSoft,
            CornerRadius = new CornerRadius(0),
            Child = bannerGrid,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_offlineBanner, 1);
        root.Children.Add(_offlineBanner);
        _offlineShowTimer.Tick += (_, _) =>
        {
            _offlineShowTimer.Stop();
            var offline = _connection is null || !_authorized;
            if (offline && !_versionMismatch && _ipInput is not null && _ipInput.Text.Trim().Length > 0)
                ShowOfflineBanner();
        };
        _offlineHideTimer.Tick += (_, _) =>
        {
            _offlineHideTimer.Stop();
            HideOfflineBanner();
        };

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 280 });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(main, 2);
        root.Children.Add(main);

        var listPanel = new Grid { Margin = new Thickness(14, 12, 6, 12) };
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var newJob = Ui.ActionButton("＋ Новая заявка", "PrimaryButton");
        newJob.Margin = new Thickness(0, 0, 0, 10);
        newJob.Click += (_, _) => ShowNewJobSheet();
        listPanel.Children.Add(newJob);
        var searchWrap = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        _searchInput = new TextBox
        {
            MinHeight = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0, 10, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(_searchInput, "Поиск по заявкам");
        var searchHint = new TextBlock
        {
            Text = "Поиск по заявкам…",
            Foreground = Ui.Tertiary,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsHitTestVisible = false
        };
        _searchInput.TextChanged += (_, _) =>
        {
            searchHint.Visibility = _searchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshJobsView(false);
        };
        searchWrap.Children.Add(_searchInput);
        searchWrap.Children.Add(searchHint);
        Grid.SetRow(searchWrap, 1);
        listPanel.Children.Add(searchWrap);
        _jobsTabs = new Ui.SegmentedControl(new[] { "Активные", "Готово", "Архив" });
        _jobsTabs.Margin = new Thickness(0, 0, 0, 10);
        _jobsTabs.SelectionChanged += index =>
        {
            _jobsTabIndex = index;
            _selectedRemoteId = "";
            var first = VisibleJobs().FirstOrDefault();
            _selectedJobId = first?.JobId ?? "";
            SyncSelectedJob();
            RefreshJobsView();
        };
        Grid.SetRow(_jobsTabs, 2);
        listPanel.Children.Add(_jobsTabs);
        var listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _jobsList = new StackPanel();
        listScroll.Content = _jobsList;
        Grid.SetRow(listScroll, 3);
        listPanel.Children.Add(listScroll);
        _jobsEmpty = new TextBlock
        {
            Foreground = Ui.Tertiary,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(10, 24, 10, 10)
        };
        main.Children.Add(listPanel);

        var separator = new Border
        {
            Width = 1,
            Background = Ui.Separator,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Grid.SetColumn(separator, 1);
        main.Children.Add(separator);

        _detailHost = new Grid { Margin = new Thickness(6, 12, 14, 0) };
        Grid.SetColumn(_detailHost, 1);
        main.Children.Add(_detailHost);

        _detailCard = new Grid();
        _detailCard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detailCard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _cardTop = new Border
        {
            Background = Ui.Surface,
            BorderBrush = Ui.Separator,
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(14, 14, 0, 0),
            Padding = new Thickness(16, 12, 16, 10)
        };
        var topStack = new StackPanel();
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _detailTitle = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleRow.Children.Add(_detailTitle);
        _detailBadgeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_detailBadgeRow, 1);
        titleRow.Children.Add(_detailBadgeRow);
        topStack.Children.Add(titleRow);
        _detailTimers = new TextBlock
        {
            Foreground = Ui.Muted,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        topStack.Children.Add(_detailTimers);
        _detailDescription = new TextBlock
        {
            Foreground = Ui.Ink,
            FontSize = 13,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        topStack.Children.Add(_detailDescription);
        _detailFields = new TextBlock
        {
            Foreground = Ui.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        topStack.Children.Add(_detailFields);
        _detailComment = new TextBlock
        {
            Foreground = Ui.Muted,
            FontSize = 12,
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        topStack.Children.Add(_detailComment);
        _detailActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        topStack.Children.Add(_detailActions);
        _archiveUndoText = new TextBlock
        {
            Foreground = Ui.Ink,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var undoButton = Ui.ActionButton("Отменить", "WhiteButton");
        undoButton.Height = 28;
        undoButton.MinHeight = 28;
        undoButton.FontSize = 11;
        undoButton.Padding = new Thickness(12, 0, 12, 0);
        undoButton.Margin = new Thickness(12, 0, 0, 0);
        undoButton.Click += (_, _) => CancelArchiveUndo();
        var undoGrid = new Grid { Margin = new Thickness(10, 6, 10, 6) };
        undoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        undoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        undoGrid.Children.Add(_archiveUndoText);
        Grid.SetColumn(undoButton, 1);
        undoGrid.Children.Add(undoButton);
        _archiveUndoBar = new Border
        {
            Background = Ui.FillSubtle,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 8, 0, 0),
            Child = undoGrid,
            Visibility = Visibility.Collapsed
        };
        topStack.Children.Add(_archiveUndoBar);
        _cardTop.Child = topStack;
        _detailCard.Children.Add(_cardTop);

        var chatArea = new Grid();
        chatArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chatArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var tabsWrap = new Border
        {
            // Фон окна, а не карточки: серой плашки за переключателем вкладок нет,
            // скруглённый переключатель «плавает» на общем фоне, как поле ввода снизу
            Background = Ui.Background,
            BorderBrush = Ui.Separator,
            BorderThickness = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(12, 8, 12, 0)
        };
        _chatTabs = new Ui.SegmentedControl(new[] { "Чат заявки", "Общий чат", "Файлы" });
        _chatTabs.SelectionChanged += OnChatTabChanged;
        tabsWrap.Child = _chatTabs;
        chatArea.Children.Add(tabsWrap);
        var chatHost = new Border
        {
            Background = Ui.Surface,
            BorderBrush = Ui.Separator,
            BorderThickness = new Thickness(1, 0, 1, 1),
            CornerRadius = new CornerRadius(0)
        };
        var chatContentHost = new Grid();
        _chatPanel = new ChatPanel();
        chatContentHost.Children.Add(_chatPanel);
        _filesList = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        _filesView = new ScrollViewer
        {
            // Фон как у чата (Ui.Background), а не белая карточка:
            // вкладка «Файлы» не должна отличаться заливкой от чатов
            Background = Ui.Background,
            Content = _filesList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed
        };
        chatContentHost.Children.Add(_filesView);
        chatHost.Child = chatContentHost;
        Grid.SetRow(chatHost, 1);
        chatArea.Children.Add(chatHost);
        Grid.SetRow(chatArea, 1);
        _detailCard.Children.Add(chatArea);

        var statusBar = new Border
        {
            Height = 28,
            // Фон окна, а не карточки: серая плашка за полем чата убрана,
            // остаётся только маленький текст статуса
            Background = Ui.Background,
            Padding = new Thickness(14, 0, 14, 0)
        };
        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusDot = Ui.StatusDot(Ui.Danger);
        statusGrid.Children.Add(_statusDot);
        _statusText = new TextBlock
        {
            Text = "Не подключено",
            Foreground = Ui.Muted,
            FontSize = 11,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_statusText, 1);
        statusGrid.Children.Add(_statusText);
        _queueText = new TextBlock
        {
            Text = "",
            Foreground = Ui.Muted,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_queueText, 2);
        statusGrid.Children.Add(_queueText);
        statusBar.Child = statusGrid;
        Grid.SetRow(statusBar, 3);
        root.Children.Add(statusBar);

        _overlayHost = new Grid { Visibility = Visibility.Collapsed };
        Grid.SetRowSpan(_overlayHost, 4);
        root.Children.Add(_overlayHost);

        _ipInput = new TextBox { Text = _settings.MasterAddress };
        System.Windows.Automation.AutomationProperties.SetName(_ipInput, "IP-адрес компьютера мастера");
        _connectButton = Ui.ActionButton("Подключить", "PrimaryButton");
        _connectButton.Click += async (_, _) => await ManualConnectAsync();

        RegisterChatPanel(_chatPanel);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                ShowNewJobSheet();
            }
            else if (e.Key == Key.Escape && _overlayHost.Children.Count > 0)
            {
                e.Handled = true;
                CloseSheet();
            }
        };

        RefreshJobsView();
        _pendingReports = PendingReportStore.Load();
        foreach (var report in _pendingReports)
            if (_jobs.TryGetValue(report.JobId, out var pendingJob) && !pendingJob.CompletionSaved)
                pendingJob.ReportPending = true;
        _reportRetryTimer.Tick += (_, _) => TryFlushPendingReports();
        _reconnectTimer.Tick += async (_, _) =>
        {
            // Блокировка «версии отличаются» снимается сама через 10 минут:
            // если на втором компьютере уже обновили программу, связь восстановится без перезапуска
            if (_versionMismatch && DateTime.Now - _versionMismatchAt > TimeSpan.FromMinutes(10))
                _versionMismatch = false;
            if (!_closing && !_connecting && !_versionMismatch && _connection is null && _ipInput.Text.Trim().Length > 0)
                await ConnectAsync();
        };
        _networkChangeTimer.Tick += async (_, _) =>
        {
            _networkChangeTimer.Stop();
            if (_closing || _versionMismatch) return;
            _connection?.Disconnect();
            if (_connection is null && !_connecting && _ipInput.Text.Trim().Length > 0)
                await ConnectAsync();
        };
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _jobElapsedTimer.Tick += (_, _) => RefreshJobsView(false);
        _archiveUndoTimer.Tick += (_, _) => ArchiveUndoTick();
        Shown += async (_, _) =>
        {
            _reconnectTimer.Start();
            _jobElapsedTimer.Start();
            _reportRetryTimer.Start();
            TryFlushPendingReports();
            if (_ipInput.Text.Trim().Length > 0) await ConnectAsync();
        };
    }

    private FrameworkElement BuildHeader()
    {
        var header = new Border
        {
            Height = 52,
            Background = Ui.Surface,
            BorderBrush = Ui.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 0, 16, 0)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Ui.LogoMark(30));
        var titles = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "СЕРВИС ЛЕГКО",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        titles.Children.Add(new TextBlock
        {
            Text = "Менеджер",
            Foreground = Ui.Muted,
            FontSize = 11
        });
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        var mini = Ui.ActionButton("◱ Мини", "WhiteButton");
        mini.Height = 32;
        mini.MinHeight = 32;
        mini.FontSize = 12;
        mini.Padding = new Thickness(12, 0, 12, 0);
        mini.Margin = new Thickness(0, 0, 8, 0);
        mini.VerticalAlignment = VerticalAlignment.Center;
        mini.ToolTip = "Свернуть в мини-панель над треем — быстрая заявка";
        mini.Click += (_, _) => EnterMiniMode();
        Grid.SetColumn(mini, 3);
        grid.Children.Add(mini);
        var settings = Ui.ActionButton("Настройки", "WhiteButton", Ui.IconSettings);
        settings.Height = 32;
        settings.MinHeight = 32;
        settings.FontSize = 12;
        settings.Padding = new Thickness(12, 0, 12, 0);
        settings.VerticalAlignment = VerticalAlignment.Center;
        settings.Click += (_, _) => ShowSettingsSheet();
        Grid.SetColumn(settings, 4);
        grid.Children.Add(settings);
        header.Child = grid;
        return header;
    }

    private void ShowSheet(FrameworkElement content, double width)
    {
        CloseSheet();
        var dimmer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0))
        };
        dimmer.MouseLeftButtonDown += (_, _) => CloseSheet();
        var card = new Border
        {
            Width = width,
            MaxHeight = ActualHeight - 60 > 300 ? ActualHeight - 60 : 640,
            Background = Ui.Surface,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(22),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 32,
                Opacity = 0.22,
                ShadowDepth = 6,
                Color = Colors.Black
            }
        };
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        card.Child = scroll;
        var host = new Grid();
        host.Children.Add(dimmer);
        host.Children.Add(card);
        _overlayHost.Children.Add(host);
        _overlayHost.Visibility = Visibility.Visible;
    }

    private void CloseSheet()
    {
        _overlayHost.Children.Clear();
        _overlayHost.Visibility = Visibility.Collapsed;
    }

    private void ShowNewJobSheet()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Новая заявка",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(Ui.Note("Опишите проблему — мастер увидит заявку сразу, как только будет связь. Можно работать сразу с несколькими ремонтами."));
        var input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 120,
            MaxLength = 500,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 12)
        };
        System.Windows.Automation.AutomationProperties.SetName(input, "Название и описание нового ремонта");
        panel.Children.Add(input);

        var detailsGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var clientInput = new TextBox { MinHeight = 34, Margin = new Thickness(0, 0, 5, 0), MaxLength = 80 };
        System.Windows.Automation.AutomationProperties.SetName(clientInput, "Клиент");
        var deviceInput = new TextBox { MinHeight = 34, Margin = new Thickness(5, 0, 0, 0), MaxLength = 80 };
        System.Windows.Automation.AutomationProperties.SetName(deviceInput, "Устройство");
        var serialInput = new TextBox { MinHeight = 34, Margin = new Thickness(0, 0, 5, 0), MaxLength = 80 };
        System.Windows.Automation.AutomationProperties.SetName(serialInput, "Серийный номер или IMEI");
        var urgentBox = new CheckBox
        {
            Content = "Срочная заявка",
            FontSize = 13,
            Foreground = Ui.Danger,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        };
        StackPanel Labeled(string label, Control field)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11, Foreground = Ui.Muted, Margin = new Thickness(2, 0, 0, 2)
            });
            stack.Children.Add(field);
            return stack;
        }
        var clientStack = Labeled("Клиент (необязательно)", clientInput);
        detailsGrid.Children.Add(clientStack);
        var deviceStack = Labeled("Устройство (необязательно)", deviceInput);
        Grid.SetColumn(deviceStack, 1);
        detailsGrid.Children.Add(deviceStack);
        var serialStack = Labeled("Серийный № / IMEI (необязательно)", serialInput);
        Grid.SetRow(serialStack, 1);
        detailsGrid.Children.Add(serialStack);
        Grid.SetRow(urgentBox, 1);
        Grid.SetColumn(urgentBox, 1);
        detailsGrid.Children.Add(urgentBox);
        panel.Children.Add(detailsGrid);

        var attachments = new List<string>();
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        void RebuildChips()
        {
            chips.Children.Clear();
            foreach (var path in attachments)
            {
                var chip = new Border
                {
                    Background = Ui.FillSubtle,
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(9, 4, 9, 4),
                    Margin = new Thickness(0, 0, 6, 6)
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var info = new FileInfo(path);
                row.Children.Add(new TextBlock
                {
                    Text = info.Name + "  ·  " + (info.Length >= 1024L * 1024 * 1024
                        ? $"{info.Length / 1024.0 / 1024.0 / 1024.0:0.0} ГБ"
                        : info.Length >= 1024 * 1024
                            ? $"{info.Length / 1024.0 / 1024.0:0.0} МБ"
                            : $"{Math.Max(1, info.Length / 1024)} КБ"),
                    FontSize = 12,
                    Foreground = Ui.Ink,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 260
                });
                var remove = new TextBlock
                {
                    Text = "✕",
                    FontSize = 12,
                    Foreground = Ui.Muted,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var captured = path;
                remove.MouseLeftButtonDown += (_, _) =>
                {
                    attachments.Remove(captured);
                    RebuildChips();
                };
                row.Children.Add(remove);
                chip.Child = row;
                chips.Children.Add(chip);
            }
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Фото / файлы (необязательно):",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        var addFile = Ui.ActionButton("＋ Добавить файл", "WhiteButton");
        addFile.HorizontalAlignment = HorizontalAlignment.Left;
        addFile.MinWidth = 160;
        addFile.Height = 32;
        addFile.MinHeight = 32;
        addFile.FontSize = 12;
        addFile.Margin = new Thickness(0, 0, 0, 6);
        addFile.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Файлы к заявке",
                Filter = "Все файлы|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) != true) return;
            foreach (var path in dialog.FileNames)
            {
                if (!File.Exists(path)) continue;
                var info = new FileInfo(path);
                if (info.Length > FileTransferService.MaxFileBytes)
                {
                    MessageBox.Show(this, "Файл «" + info.Name + "» больше 5 ГБ и не будет прикреплён.",
                        "Файл слишком большой", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }
                if (attachments.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                if (attachments.Count >= 10)
                {
                    MessageBox.Show(this, "К одной заявке можно прикрепить не больше 10 файлов.",
                        "Вложения", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                }
                attachments.Add(path);
            }
            RebuildChips();
        };
        // «С телефона» — QR-код: фото/видео/файлы с телефона попадают в этот же список вложений (2.40)
        var fromPhone = Ui.ActionButton("📱 С телефона", "WhiteButton");
        fromPhone.MinWidth = 140;
        fromPhone.Height = 32;
        fromPhone.MinHeight = 32;
        fromPhone.FontSize = 12;
        fromPhone.Margin = new Thickness(8, 0, 0, 6);
        fromPhone.ToolTip = "Показать QR-код: отсканируйте телефоном и выберите фото, видео или файлы";
        fromPhone.Click += (_, _) =>
        {
            PhoneUploadWindow.ShowFor(this, "Новая заявка (вложения)", (partPath, name) =>
            {
                if (attachments.Count >= 10)
                    return Task.FromResult<string?>("К одной заявке можно прикрепить не больше 10 файлов.");
                string saved;
                try { saved = FileTransferService.SaveCompletedPart(partPath, name); }
                catch (Exception ex)
                {
                    AppLog.Error("Не удалось сохранить файл с телефона", ex);
                    return Task.FromResult<string?>("Не удалось сохранить файл на компьютере: " + ex.Message);
                }
                attachments.Add(saved);
                RebuildChips();
                return Task.FromResult<string?>(null);
            });
        };
        var fileButtons = new StackPanel { Orientation = Orientation.Horizontal };
        addFile.Margin = new Thickness(0, 0, 0, 6);
        fileButtons.Children.Add(addFile);
        fileButtons.Children.Add(fromPhone);
        panel.Children.Add(fileButtons);
        panel.Children.Add(chips);

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancel = Ui.ActionButton("Отмена", "WhiteButton");
        cancel.Margin = new Thickness(0, 0, 5, 0);
        cancel.Click += (_, _) => CloseSheet();
        buttons.Children.Add(cancel);
        var send = Ui.ActionButton("Отправить заявку", "PrimaryButton");
        send.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(send, 1);
        buttons.Children.Add(send);
        panel.Children.Add(buttons);
        send.Click += async (_, _) =>
        {
            var details = new JobDetails(clientInput.Text.Trim(), deviceInput.Text.Trim(),
                serialInput.Text.Trim(), urgentBox.IsChecked == true);
            if (await SendJobAsync(input.Text.Trim(), details) is not string jobId) return;
            CloseSheet();
            // Все вложения новой заявки — одно сообщение в чате заявки
            var files = attachments.ToList();
            var batch = files.Count > 1 ? ChatPanel.NewBatchId() : "";
            foreach (var path in files)
                await QueueFileMessageAsync(jobId, path, batch);
        };
        ShowSheet(panel, 480);
        input.Focus();
    }

    // ---- Мини-панель (быстрая заявка из трея) ----

    private TextBox? _miniInput;
    private TextBlock? _miniConfirm;
    private CheckBox? _miniUrgent;
    private readonly DispatcherTimer _miniConfirmTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    protected override MiniForm? CreateMiniPanel()
    {
        _miniConfirmTimer.Tick += (_, _) =>
        {
            _miniConfirmTimer.Stop();
            if (_miniConfirm is not null) _miniConfirm.Visibility = Visibility.Collapsed;
        };
        return new MiniForm("Менеджер", MiniStatus, BuildMiniBody,
            ShowMainFromMini, () => _miniInput,
            ("Чат с мастером", () => { ShowMainFromMini(); OpenGeneralChat(); }));
    }

    private (string, Brush) MiniStatus()
    {
        var connected = _connection is not null && _authorized;
        return connected
            ? ("На связи с мастером", Ui.Green)
            : ("Нет связи — заявка уйдёт при подключении", Ui.Warning);
    }

    private void BuildMiniBody(StackPanel body, StackPanel jobs)
    {
        body.Children.Add(new TextBlock
        {
            Text = "Быстрая заявка",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 2, 0, 6)
        });
        var inputWrap = new Grid();
        _miniInput = new TextBox
        {
            MinHeight = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0, 10, 0),
            MaxLength = 200
        };
        System.Windows.Automation.AutomationProperties.SetName(_miniInput, "Текст быстрой заявки");
        var hint = new TextBlock
        {
            Text = "Что сломалось? Например: не включается ноутбук",
            Foreground = Ui.Tertiary,
            FontSize = 13,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        inputWrap.Children.Add(_miniInput);
        inputWrap.Children.Add(hint);
        body.Children.Add(inputWrap);

        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _miniUrgent = new CheckBox
        {
            Content = "Срочно",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Danger,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_miniUrgent);
        var send = Ui.ActionButton("Отправить", "PrimaryButton");
        send.Height = 36;
        send.MinHeight = 36;
        send.MinWidth = 108;
        send.FontSize = 13;
        Grid.SetColumn(send, 2);
        row.Children.Add(send);
        body.Children.Add(row);

        _miniConfirm = new TextBlock
        {
            Text = "✓ Заявка отправлена — мастер увидит её сразу",
            Foreground = Ui.Green,
            FontSize = 12,
            Margin = new Thickness(2, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        body.Children.Add(_miniConfirm);

        var input = _miniInput;
        var urgent = _miniUrgent;
        void Sync()
        {
            hint.Visibility = input.Text.Length == 0 && !input.IsFocused
                ? Visibility.Visible : Visibility.Collapsed;
            send.IsEnabled = input.Text.Trim().Length > 0;
        }
        input.TextChanged += (_, _) => Sync();
        input.GotFocus += (_, _) => Sync();
        input.LostFocus += (_, _) => Sync();
        Sync();
        async Task SendQuickAsync()
        {
            var text = input.Text.Trim();
            if (text.Length == 0) return;
            send.IsEnabled = false;
            var jobId = await SendJobAsync(text, new JobDetails(Urgent: urgent.IsChecked == true));
            if (jobId is null)
            {
                Sync();
                return;
            }
            MiniPanel?.RefreshBody();
            if (_miniConfirm is not null)
            {
                _miniConfirm.Visibility = Visibility.Visible;
                _miniConfirmTimer.Stop();
                _miniConfirmTimer.Start();
            }
        }
        send.Click += async (_, _) => await SendQuickAsync();
        // PreviewKeyDown — как и в чате: единый надёжный способ перехвата Enter
        input.PreviewKeyDown += (_, e) =>
        {
            // Enter — отправить быструю заявку; единое правило — EnterSend
            if (EnterSend.ShouldSend(e.Key == Key.Return || e.Key == Key.Enter,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            {
                e.Handled = true;
                _ = SendQuickAsync();
            }
        };

        var activeJobs = _jobs.Values
            .Where(job => !job.CompletionSaved)
            .OrderBy(job => job.IsAccepted ? 0 : 1)
            .ThenByDescending(job => job.IsAccepted ? job.StartedAt : job.ReceivedAt)
            .ToList();
        if (activeJobs.Count == 0)
        {
            body.Children.Add(MiniForm.EmptyLabel("Активных заявок нет — отправьте первую из поля выше."));
            return;
        }
        body.Children.Add(MiniForm.SectionLabel("Активные заявки"));
        foreach (var job in activeJobs)
        {
            var (_, color) = JobBadge(job);
            var subtitle = JobTimeLine(job) + (job.Urgent ? " · СРОЧНО" : "");
            var jobId = job.JobId;
            jobs.Children.Add(MiniForm.JobRow(color, ShortText(job.Text), subtitle, () =>
            {
                ShowMainFromMini();
                SelectJob(jobId);
            }));
        }
    }

    private void ShowCompleteSheet(ManagerJobState job)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Завершить заявку?",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(Ui.Note("Будет сохранён TXT-отчёт в общей папке. Можно добавить короткий итог — он попадёт в отчёт."));
        panel.Children.Add(new TextBlock
        {
            Text = "Что сделано (необязательно):",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 2)
        });
        var comment = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxLength = 300,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 12)
        };
        panel.Children.Add(comment);
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancel = Ui.ActionButton("Отмена", "WhiteButton");
        cancel.Margin = new Thickness(0, 0, 5, 0);
        cancel.Click += (_, _) => CloseSheet();
        buttons.Children.Add(cancel);
        var complete = Ui.ActionButton("Завершить", "SuccessButton");
        complete.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(complete, 1);
        buttons.Children.Add(complete);
        panel.Children.Add(buttons);
        complete.Click += (_, _) =>
        {
            CloseSheet();
            CompleteJob(job, comment.Text.Trim());
        };
        ShowSheet(panel, 460);
        comment.Focus();
    }

    private void ShowSettingsSheet()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Настройки",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(Ui.SectionTitle("Подключение"));
        panel.Children.Add(Ui.Note("IP мастера: Tailscale 100.x.x.x или LAN 192.168.x.x / 10.x.x.x"));
        DetachElement(_ipInput);
        DetachElement(_connectButton);
        panel.Children.Add(_ipInput);
        panel.Children.Add(_connectButton);
        panel.Children.Add(BuildDiscoveryBlock());
        panel.Children.Add(Ui.Note("Если программа просит код сопряжения (после переустановки, смены версии или сброса устройства у мастера), попросите мастера нажать «Добавить компьютер» и введите 6 цифр с его экрана здесь — отдельное окно открывать не нужно. Код действует 5 минут:"));
        var pairRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var codeInput = new TextBox { Width = 110, MinHeight = 34, MaxLength = 6, Margin = new Thickness(0, 0, 6, 0) };
        System.Windows.Automation.AutomationProperties.SetName(codeInput, "Код сопряжения из 6 цифр с экрана мастера");
        var codeButton = Ui.ActionButton("Подтвердить код", "WhiteButton");
        codeButton.MinWidth = 150;
        codeButton.IsEnabled = false;
        codeInput.TextChanged += (_, _) => codeButton.IsEnabled = codeInput.Text.Trim().Length == 6;
        codeButton.Click += async (_, _) =>
        {
            if (_connection is not null && _authorized)
            {
                MessageBox.Show(this, "Этот компьютер уже сопряжён с мастером — код не нужен.",
                    "Сопряжение", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _pendingCode = codeInput.Text.Trim();
            _pairingSecret = SecurityService.DeriveSecret(_pendingCode, _settings.ClientId);
            // Нет подключения — подключаемся: мастер ответит запросом кода,
            // и введённый здесь код уйдёт автоматически, без отдельного окна
            if (_connection is null) await ConnectAsync();
            else await SendPairAsync();
        };
        pairRow.Children.Add(codeInput);
        pairRow.Children.Add(codeButton);
        panel.Children.Add(pairRow);
        var autoStart = new CheckBox
        {
            Content = "Автозапуск вместе с Windows",
            IsChecked = _settings.AutoStart,
            Foreground = Ui.Muted,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 8)
        };
        autoStart.Checked += (_, _) => { _settings.AutoStart = true; SettingsStore.Save(_settings); };
        autoStart.Unchecked += (_, _) => { _settings.AutoStart = false; SettingsStore.Save(_settings); };
        panel.Children.Add(autoStart);
        var startHidden = new CheckBox
        {
            Content = "При автозапуске открываться только в трее (окно появится при событии)",
            IsChecked = _settings.StartHidden,
            Foreground = Ui.Muted,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        startHidden.Checked += (_, _) => { _settings.StartHidden = true; SettingsStore.Save(_settings); };
        startHidden.Unchecked += (_, _) => { _settings.StartHidden = false; SettingsStore.Save(_settings); };
        panel.Children.Add(startHidden);
        panel.Children.Add(SettingsUi.CloseOnSessionSwitchCheckBox(_settings, new Thickness(0, 0, 0, 8)));
        panel.Children.Add(Ui.SectionTitle("Тихие часы"));
        panel.Children.Add(Ui.Note("В это время звуковые сигналы выключены, визуальные уведомления остаются. Оставьте поля пустыми, чтобы выключить режим."));
        var quietRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var quietFrom = new TextBox { Text = _settings.QuietFrom, Width = 70, MinHeight = 34, Margin = new Thickness(0, 0, 6, 0) };
        System.Windows.Automation.AutomationProperties.SetName(quietFrom, "Начало тихих часов, например 22:00");
        quietRow.Children.Add(quietFrom);
        quietRow.Children.Add(new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var quietTo = new TextBox { Text = _settings.QuietTo, Width = 70, MinHeight = 34 };
        System.Windows.Automation.AutomationProperties.SetName(quietTo, "Конец тихих часов, например 08:00");
        quietRow.Children.Add(quietTo);
        void SaveQuiet()
        {
            _settings.QuietFrom = quietFrom.Text.Trim();
            _settings.QuietTo = quietTo.Text.Trim();
            SettingsStore.Save(_settings);
        }
        quietFrom.LostFocus += (_, _) => SaveQuiet();
        quietTo.LostFocus += (_, _) => SaveQuiet();
        panel.Children.Add(quietRow);
        panel.Children.Add(Ui.SectionTitle("Общая папка"));
        var reports = Ui.ActionButton("Открыть общую папку", "WhiteButton");
        reports.Click += (_, _) => StorageService.OpenRootFolder();
        panel.Children.Add(reports);
        var changeStorage = Ui.ActionButton("Изменить общую папку", "WhiteButton");
        changeStorage.Click += (_, _) => StorageService.ChangeFolder(this, _settings);
        panel.Children.Add(changeStorage);
        panel.Children.Add(CreateSoundSettingsPanel(_settings));
        panel.Children.Add(Ui.SectionTitle("Внешний вид"));
        panel.Children.Add(CreateThemeSelector(_settings));
        panel.Children.Add(Ui.SectionTitle("Диагностика"));
        panel.Children.Add(CreateDiagnosticsButtons());
        panel.Children.Add(Ui.Note($"Текущая версия: {SecurityService.AppVersion}\nПользователь: {_managerAccount}\nКомпьютер: {_machineName}"));
        var switchRole = Ui.ActionButton("Сменить роль", "QuietButton");
        switchRole.Click += (_, _) =>
        {
            CloseSheet();
            SwitchRole();
        };
        panel.Children.Add(switchRole);
        ShowSheet(panel, 440);
    }

    private static void DetachElement(FrameworkElement element)
    {
        if (element.Parent is Panel parent) parent.Children.Remove(element);
    }

    private FrameworkElement BuildDiscoveryBlock()
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (_settings.RecentMasters.Count > 0)
        {
            wrap.Children.Add(Ui.Note("Недавние мастера — нажмите, чтобы подставить адрес:"));
            var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
            foreach (var address in _settings.RecentMasters)
            {
                var chip = Ui.ActionButton(address, "WhiteButton");
                chip.Height = 28;
                chip.MinHeight = 28;
                chip.FontSize = 11;
                chip.Padding = new Thickness(10, 0, 10, 0);
                chip.Margin = new Thickness(0, 0, 6, 6);
                var captured = address;
                chip.Click += (_, _) => { _ipInput.Text = captured; };
                chips.Children.Add(chip);
            }
            wrap.Children.Add(chips);
        }
        var status = Ui.Note("");
        var results = new StackPanel();
        var find = Ui.ActionButton("Найти мастера в сети автоматически", "WhiteButton");
        find.Margin = new Thickness(0, 2, 0, 4);
        find.Click += async (_, _) =>
        {
            find.IsEnabled = false;
            status.Text = "Ищем мастера в локальной сети…";
            results.Children.Clear();
            var found = await DiscoveryClient.DiscoverAsync();
            find.IsEnabled = true;
            if (found.Count == 0)
            {
                status.Text = "Мастер не найден. Убедитесь, что программа запущена на компьютере мастера, или введите адрес вручную.";
                return;
            }
            status.Text = found.Count == 1 ? "Найден мастер — нажмите, чтобы подставить адрес:" : "Найдены мастера:";
            foreach (var master in found)
            {
                var pick = Ui.ActionButton(master.DisplayName, "WhiteButton");
                pick.HorizontalContentAlignment = HorizontalAlignment.Left;
                pick.Margin = new Thickness(0, 2, 0, 0);
                var address = master.Address;
                pick.Click += (_, _) =>
                {
                    _ipInput.Text = address;
                    status.Text = "Выбран адрес " + address + ". Нажмите «Подключить».";
                };
                results.Children.Add(pick);
            }
        };
        wrap.Children.Add(find);
        wrap.Children.Add(status);
        wrap.Children.Add(results);
        return wrap;
    }

    private ManagerJobState? SelectedJob =>
        _selectedJobId.Length > 0 && _jobs.TryGetValue(_selectedJobId, out var job) ? job : null;

    protected override List<ManagerJobRecord> GetPersistedJobs() =>
        _jobs.Values.Select(job => new ManagerJobRecord
        {
            JobId = job.JobId,
            Text = job.Text,
            IsAccepted = job.IsAccepted,
            ReceivedAt = job.ReceivedAt,
            StartedAt = job.StartedAt,
            CompletionSaved = job.CompletionSaved,
            ReportPending = job.ReportPending,
            CompletedAt = job.CompletedAt,
            Comment = job.Comment,
            ReportPath = job.ReportPath,
            Client = job.Client,
            Device = job.Device,
            Serial = job.Serial,
            Urgent = job.Urgent
        }).ToList();

    protected override bool IsKnownActiveJob(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) && job.IsAccepted && !job.CompletionSaved;

    protected override IReadOnlyCollection<string> GetPendingArchived() => _pendingArchivedJobs;

    protected override void RestorePendingArchived(IReadOnlyList<string> jobIds)
    {
        foreach (var jobId in jobIds)
            if (jobId.Length > 0) _pendingArchivedJobs.Add(jobId);
    }

    protected override IReadOnlyCollection<string> GetPendingRemoved() => _pendingRemovedJobs;

    protected override void RestorePendingRemoved(IReadOnlyList<string> jobIds)
    {
        foreach (var jobId in jobIds)
            if (jobId.Length > 0) _pendingRemovedJobs.Add(jobId);
    }

    private void RestoreJobs()
    {
        var snapshot = ChatSessionStore.Load("manager");
        foreach (var record in snapshot.Jobs.Where(job => job.JobId.Length > 0))
        {
            _jobs[record.JobId] = new ManagerJobState
            {
                JobId = record.JobId,
                Text = record.Text,
                IsAccepted = record.IsAccepted,
                ReceivedAt = record.ReceivedAt == default ? DateTime.Now : record.ReceivedAt,
                StartedAt = record.StartedAt,
                CompletionSaved = record.CompletionSaved,
                ReportPending = record.ReportPending,
                CompletedAt = record.CompletedAt,
                Comment = record.Comment,
                ReportPath = record.ReportPath,
                Client = record.Client,
                Device = record.Device,
                Serial = record.Serial,
                Urgent = record.Urgent
            };
        }
        if (_jobs.Count == 0 && CurrentJobId.Length > 0)
        {
            _jobs[CurrentJobId] = new ManagerJobState
            {
                JobId = CurrentJobId,
                Text = CurrentJobText,
                IsAccepted = IsJobAccepted,
                ReceivedAt = JobStartedAt == default ? DateTime.Now : JobStartedAt,
                StartedAt = JobStartedAt,
                CompletionSaved = JobCompletionSaved,
                CompletedAt = JobCompletionSaved ? DateTime.Now : default
            };
        }
        _selectedJobId = _jobs.Values
            .OrderBy(job => job.CompletionSaved || job.ReportPending ? 2 : job.IsAccepted ? 1 : 0)
            .ThenByDescending(job => job.IsAccepted ? job.StartedAt : job.ReceivedAt)
            .FirstOrDefault()?.JobId ?? "";
        SyncSelectedJob();
    }

    private void SyncSelectedJob()
    {
        var job = SelectedJob;
        CurrentJobId = job?.JobId ?? "";
        CurrentJobText = job?.Text ?? "";
        IsJobAccepted = job?.IsAccepted == true && !job.CompletionSaved;
        JobStartedAt = job?.StartedAt ?? default;
        JobCompletionSaved = job?.CompletionSaved == true;
    }

    private List<ManagerJobState> VisibleJobs()
    {
        var all = _jobsTabIndex switch
        {
            0 => _jobs.Values.Where(job => !job.CompletionSaved)
                .OrderBy(job => job.IsAccepted ? 0 : 1)
                .ThenByDescending(job => job.IsAccepted ? job.StartedAt : job.ReceivedAt).ToList(),
            1 => _jobs.Values.Where(job => job.CompletionSaved)
                .OrderByDescending(job => job.CompletedAt).ToList(),
            _ => new List<ManagerJobState>()
        };
        var query = _searchInput.Text.Trim();
        if (query.Length == 0) return all;
        return all.Where(job =>
            job.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            job.JobId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            job.Client.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            job.Device.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            job.Serial.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Заявки других менеджеров для текущей вкладки: активные сортируются как у мастера
    /// (ждущие сверху), завершённые — по дате завершения.</summary>
    private List<RemoteJobInfo> VisibleRemoteJobs()
    {
        var done = _jobsTabIndex == 1;
        IEnumerable<RemoteJobInfo> items = _remoteJobs.Values.Where(job =>
            (JobStatus.Normalize(job.Status) == JobStatus.Done) == done);
        var query = _searchInput.Text.Trim();
        if (query.Length > 0)
            items = items.Where(job =>
                job.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                job.JobId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                job.OwnerName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                job.Client.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                job.Device.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                job.Serial.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (done)
            return items.OrderByDescending(job => job.CompletedAt == default ? job.ReceivedAt : job.CompletedAt).ToList();
        return items
            .OrderBy(job => JobStatus.Normalize(job.Status) switch
            {
                JobStatus.Waiting => 0,
                JobStatus.Accepted => 1,
                _ => 2
            })
            .ThenByDescending(job =>
                JobStatus.Normalize(job.Status) == JobStatus.Accepted && job.StartedAt != default
                    ? job.StartedAt : job.ReceivedAt)
            .ToList();
    }

    private static Brush RemoteStatusColor(string status) => JobStatus.Normalize(status) switch
    {
        JobStatus.Accepted => Ui.Accent,
        JobStatus.AwaitingReport => Ui.Warning,
        JobStatus.Done => Ui.Green,
        _ => Ui.Warning
    };

    private FrameworkElement BuildRemoteJobRow(RemoteJobInfo job)
    {
        var selected = job.JobId == _selectedRemoteId;
        var statusColor = RemoteStatusColor(job.Status);
        var card = new Border
        {
            Background = selected ? Ui.AccentSoft : Brushes.Transparent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = Ui.StatusDot(statusColor, 8);
        grid.Children.Add(dot);
        var texts = new StackPanel
        {
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        texts.Children.Add(new TextBlock
        {
            Text = ShortText(job.Text),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = selected ? Ui.Accent : Ui.Ink,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var statusLine = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var line = job.OwnerName + " · " + job.TimeLine();
        if (job.Urgent)
        {
            statusLine.Inlines.Add(new System.Windows.Documents.Run(line) { Foreground = Ui.Muted });
            statusLine.Inlines.Add(new System.Windows.Documents.Run("  ·  СРОЧНО")
            {
                Foreground = Ui.Danger,
                FontWeight = FontWeights.SemiBold
            });
        }
        else
        {
            statusLine.Text = line;
            statusLine.Foreground = Ui.Muted;
        }
        texts.Children.Add(statusLine);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        card.Child = grid;
        var jobId = job.JobId;
        card.MouseEnter += (_, _) =>
        {
            if (jobId != _selectedRemoteId) card.Background = Ui.FillSubtle;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = jobId == _selectedRemoteId ? Ui.AccentSoft : Brushes.Transparent;
        };
        card.MouseLeftButtonDown += (_, _) => SelectRemoteJob(jobId);
        return card;
    }

    private static TextBlock JobsSectionLabel(string text) => new()
    {
        Text = text,
        Foreground = Ui.Tertiary,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(4, 10, 4, 2),
        TextWrapping = TextWrapping.Wrap
    };

    private void SelectJob(string jobId, bool persist = true)
    {
        if (!_jobs.ContainsKey(jobId)) return;
        _selectedRemoteId = "";
        _selectedJobId = jobId;
        SyncSelectedJob();
        RefreshJobsView(persist);
        if (_chatTabs.SelectedIndex == 0) BindJobChat();
    }

    /// <summary>Выбрать чужую заявку: только просмотр карточки и удаление — чат по ней у её автора.</summary>
    private void SelectRemoteJob(string jobId)
    {
        if (!_remoteJobs.ContainsKey(jobId)) return;
        _selectedRemoteId = jobId;
        _selectedJobId = "";
        SyncSelectedJob();
        RefreshJobsView();
    }

    private void BindJobChat()
    {
        if (_selectedRemoteId.Length > 0 && _remoteJobs.TryGetValue(_selectedRemoteId, out var remoteJob))
        {
            _chatPanel.Unbind();
            _chatPanel.SetEmptyHint("Чужая заявка — чат по ней доступен её автору (" + remoteJob.OwnerName + ") и мастеру.");
            _chatPanel.SetSendEnabled(false);
            return;
        }
        var job = SelectedJob;
        if (job is not null)
        {
            OpenChatForSession(job.JobId, "Чат по заявке: " + ShortText(job.Text));
            // В чат завершённой заявки писать нельзя (мастер её уже закрыл),
            // но история читается и копируется
            _chatPanel.SetSendEnabled(!job.CompletionSaved);
        }
        else
        {
            _chatPanel.Unbind();
            _chatPanel.SetEmptyHint("Заявки нет — создайте её кнопкой «＋ Новая заявка», и чат с мастером появится здесь.");
            _chatPanel.SetSendEnabled(false);
        }
    }

    protected override void OnChatSessionShown(string sessionId)
    {
        if (sessionId == GeneralChatId)
        {
            _chatTabs.Select(1, false);
            ShowChatContent(1);
        }
        else
        {
            if (_jobs.ContainsKey(sessionId) && _selectedJobId != sessionId)
            {
                _selectedJobId = sessionId;
                _selectedRemoteId = "";
                SyncSelectedJob();
                RefreshJobsView(false);
            }
            _chatTabs.Select(0, false);
            ShowChatContent(0);
        }
    }

    private void OnChatTabChanged(int index)
    {
        ShowChatContent(index);
        if (index == 0) BindJobChat();
        else if (index == 1) OpenGeneralChat();
        else BuildFilesView();
    }

    private void ShowChatContent(int index)
    {
        _chatPanel.Visibility = index == 2 ? Visibility.Collapsed : Visibility.Visible;
        _filesView.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildFilesView()
    {
        _filesList.Children.Clear();
        var entries = new List<ChatEntry>();
        var job = SelectedJob;
        if (job is not null) entries.AddRange(GetHistoryForSession(job.JobId));
        entries.AddRange(GetHistoryForSession(GeneralChatId));
        var files = entries.Where(entry => entry.FileName.Length > 0)
            .OrderByDescending(entry => entry.Time).ToList();
        if (files.Count == 0)
        {
            _filesList.Children.Add(new TextBlock
            {
                Text = "Файлов пока нет. Прикрепите файл в чате — он появится здесь.",
                Foreground = Ui.Tertiary,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(20, 40, 20, 20)
            });
            return;
        }
        foreach (var file in files)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = file.FileName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{(file.Own ? "Вы" : file.Sender)} · {file.Time:dd.MM.yyyy HH:mm} · {(file.Delivery == "✓" ? "получен" : "в передаче")}",
                Foreground = Ui.Muted,
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
            row.Children.Add(info);
            var open = Ui.ActionButton("Открыть", "ChipButton");
            open.Height = 28;
            open.MinHeight = 28;
            open.VerticalAlignment = VerticalAlignment.Center;
            var path = file.FilePath;
            open.Click += (_, _) =>
            {
                try
                {
                    if (path.Length > 0 && File.Exists(path))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    else FileTransferService.OpenReceivedDirectory();
                }
                catch { }
            };
            Grid.SetColumn(open, 1);
            row.Children.Add(open);
            var card = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Child = row
            };
            card.MouseEnter += (_, _) => card.Background = Ui.FillSubtle;
            card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
            _filesList.Children.Add(card);
        }
    }

    private void RefreshJobsView(bool persist = true)
    {
        var jobs = VisibleJobs();
        _jobsList.Children.Clear();
        if (_jobsTabIndex == 2)
        {
            BuildArchiveList();
        }
        else
        {
            var remote = VisibleRemoteJobs();
            if (jobs.Count == 0 && remote.Count == 0)
            {
                _jobsEmpty.Text = _jobsTabIndex == 0
                    ? "Активных заявок нет.\nНажмите «＋ Новая заявка», чтобы создать первую."
                    : "Завершённых заявок пока нет.";
                _jobsList.Children.Add(_jobsEmpty);
            }
            else
            {
                if (jobs.Count > 0 && remote.Count > 0)
                    _jobsList.Children.Add(JobsSectionLabel("Мои заявки"));
                foreach (var job in jobs) _jobsList.Children.Add(BuildJobRow(job));
                if (remote.Count > 0)
                {
                    _jobsList.Children.Add(JobsSectionLabel("Заявки других менеджеров" +
                        (_connection is null || !_authorized ? " · нет связи, данные на момент отключения" : "")));
                    foreach (var item in remote) _jobsList.Children.Add(BuildRemoteJobRow(item));
                }
            }
        }
        RefreshDetail();
        UpdateStatusBar();
        if (persist) PersistSession();
    }

    private void BuildArchiveList()
    {
        if (_archive.Count == 0)
        {
            _jobsEmpty.Text = "Архив пуст. Уберите завершённую заявку — она появится здесь.";
            _jobsList.Children.Add(_jobsEmpty);
            return;
        }
        foreach (var entry in _archive.OrderByDescending(item => item.CompletedAt))
        {
            var row = new Grid { Margin = new Thickness(0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = ShortText(entry.Text),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = "Завершена " + entry.CompletedAt.ToString("dd.MM.yyyy HH:mm"),
                Foreground = Ui.Muted,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0)
            });
            row.Children.Add(info);
            var open = Ui.ActionButton("Отчёт", "ChipButton");
            open.Height = 28;
            open.MinHeight = 28;
            open.VerticalAlignment = VerticalAlignment.Center;
            var path = entry.ReportPath;
            open.Click += (_, _) =>
            {
                try
                {
                    if (path.Length > 0 && File.Exists(path))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    else ReportService.OpenReportsDirectory();
                }
                catch { }
            };
            Grid.SetColumn(open, 1);
            row.Children.Add(open);
            var deleteEntry = Ui.ActionButton("Удалить", "ChipButton");
            deleteEntry.Height = 28;
            deleteEntry.MinHeight = 28;
            deleteEntry.VerticalAlignment = VerticalAlignment.Center;
            deleteEntry.Margin = new Thickness(6, 0, 0, 0);
            deleteEntry.ToolTip = "Удалить заявку у всех пользователей — файл отчёта в общей папке останется";
            var archivedEntry = entry;
            deleteEntry.Click += (_, _) => DeleteArchivedEntry(archivedEntry);
            Grid.SetColumn(deleteEntry, 2);
            row.Children.Add(deleteEntry);
            var card = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Child = row
            };
            card.MouseEnter += (_, _) => card.Background = Ui.FillSubtle;
            card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
            _jobsList.Children.Add(card);
        }
    }

    private FrameworkElement BuildJobRow(ManagerJobState job)
    {
        var selected = job.JobId == _selectedJobId;
        var (_, statusColor) = JobBadge(job);
        var card = new Border
        {
            Background = selected ? Ui.AccentSoft : Brushes.Transparent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = Ui.StatusDot(statusColor, 8);
        grid.Children.Add(dot);
        var texts = new StackPanel
        {
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        texts.Children.Add(new TextBlock
        {
            Text = ShortText(job.Text),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = selected ? Ui.Accent : Ui.Ink,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var statusLine = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        if (job.Urgent)
        {
            statusLine.Inlines.Add(new System.Windows.Documents.Run(JobTimeLine(job)) { Foreground = Ui.Muted });
            statusLine.Inlines.Add(new System.Windows.Documents.Run("  ·  СРОЧНО")
            {
                Foreground = Ui.Danger,
                FontWeight = FontWeights.SemiBold
            });
        }
        else
        {
            statusLine.Text = JobTimeLine(job);
            statusLine.Foreground = Ui.Muted;
        }
        texts.Children.Add(statusLine);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        card.Child = grid;
        var jobId = job.JobId;
        card.MouseEnter += (_, _) =>
        {
            if (jobId != _selectedJobId) card.Background = Ui.FillSubtle;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = jobId == _selectedJobId ? Ui.AccentSoft : Brushes.Transparent;
        };
        card.MouseLeftButtonDown += (_, _) => SelectJob(jobId);
        return card;
    }

    private static (string, Brush) JobBadge(ManagerJobState job)
    {
        if (job.ArchivePending) return ("В архиве…", Ui.Muted);
        if (job.CompletionSaved) return ("Завершена", Ui.Green);
        if (job.ReportPending) return ("Отчёт в очереди", Ui.Warning);
        if (job.IsAccepted) return ("В работе", Ui.Accent);
        return ("Ожидает мастера", Ui.Warning);
    }

    private static string JobTimeLine(ManagerJobState job)
    {
        if (job.CompletionSaved)
            return "Завершена " + (job.CompletedAt == default ? DateTime.Now : job.CompletedAt).ToString("dd.MM.yyyy HH:mm");
        if (job.ReportPending) return "Отчёт будет сохранён автоматически";
        if (job.IsAccepted)
        {
            var started = job.StartedAt == default ? DateTime.Now : job.StartedAt;
            return $"В работе {JobTiming.Elapsed(started, DateTime.Now)} · принята {started:dd.MM HH:mm}";
        }
        return "Создана " + job.ReceivedAt.ToString("dd.MM.yyyy HH:mm");
    }

    private void RefreshDetail()
    {
        var job = SelectedJob;
        if (_jobsTabIndex == 2)
        {
            _detailCard.Visibility = Visibility.Collapsed;
            _detailHost.Children.Clear();
            _detailHost.Children.Add(Ui.EmptyState("Архив", "Убранные заявки хранятся в списке слева. Отчёты остаются в общей папке."));
            return;
        }
        // Чужая заявка: только просмотр карточки и удаление — чат по ней у её автора и мастера
        var remote = _selectedRemoteId.Length > 0 &&
            _remoteJobs.TryGetValue(_selectedRemoteId, out var remoteJob) ? remoteJob : null;
        if (remote is not null)
        {
            if (_detailHost.Children.Count == 0 || !ReferenceEquals(_detailHost.Children[0], _detailCard))
            {
                _detailHost.Children.Clear();
                _detailHost.Children.Add(_detailCard);
            }
            _detailCard.Visibility = Visibility.Visible;
            _cardTop.Visibility = Visibility.Visible;
            _archiveUndoBar.Visibility = Visibility.Collapsed;
            var remoteTitle = remote.Text.Split('\n')[0].Trim();
            _detailTitle.Text = remoteTitle;
            _detailBadgeRow.Children.Clear();
            _detailBadgeRow.Children.Add(Ui.Badge(JobStatus.Title(remote.Status), RemoteStatusColor(remote.Status)));
            var ownerBadge = Ui.Badge(remote.OwnerName, Ui.Muted);
            ownerBadge.Margin = new Thickness(6, 0, 0, 0);
            ownerBadge.ToolTip = "Менеджер, создавший заявку";
            _detailBadgeRow.Children.Add(ownerBadge);
            if (remote.Urgent)
            {
                var urgentBadge = Ui.Badge("СРОЧНО", Ui.Danger);
                urgentBadge.Margin = new Thickness(6, 0, 0, 0);
                _detailBadgeRow.Children.Add(urgentBadge);
            }
            _detailTimers.Text = RemoteDetailStatusLine(remote);
            var remoteRest = remote.Text.Length > remoteTitle.Length
                ? remote.Text[remoteTitle.Length..].TrimStart('\r', '\n', ' ')
                : "";
            _detailDescription.Text = remoteRest;
            _detailDescription.Visibility = remoteRest.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var remoteFields = JobFieldsLine(remote.Client, remote.Device, remote.Serial);
            _detailFields.Text = remoteFields;
            _detailFields.Visibility = remoteFields.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _detailComment.Text = remote.Comment.Length > 0 ? "Что сделано: " + remote.Comment : "";
            _detailComment.Visibility = remote.Comment.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _detailActions.Children.Clear();
            // Правка чужой заявки доступна при связи с мастером — изменения увидят все (2.35)
            var editRemote = Ui.ActionButton("Изменить", "WhiteButton");
            editRemote.MinWidth = 120;
            var canEditRemote = _connection is not null && _authorized;
            editRemote.IsEnabled = canEditRemote;
            editRemote.ToolTip = canEditRemote
                ? "Изменить описание и реквизиты заявки — обновится у всех пользователей"
                : "Изменить можно при подключении к мастеру";
            var editRemoteTarget = remote;
            editRemote.Click += (_, _) => ShowJobEditSheet(editRemoteTarget.JobId, editRemoteTarget.Text,
                editRemoteTarget.Client, editRemoteTarget.Device, editRemoteTarget.Serial, editRemoteTarget.Urgent);
            _detailActions.Children.Add(editRemote);
            var delete = Ui.ActionButton("Удалить заявку", "DangerButton");
            delete.MinWidth = 150;
            delete.Margin = new Thickness(8, 0, 0, 0);
            delete.ToolTip = "Удалить заявку у всех пользователей — у вас, у мастера и у её автора. Действие нельзя отменить";
            var deleteTarget = remote;
            delete.Click += (_, _) => DeleteRemoteJob(deleteTarget);
            _detailActions.Children.Add(delete);
            if (JobStatus.Normalize(remote.Status) == JobStatus.Done)
            {
                var reports = Ui.ActionButton("Открыть папку отчётов", "WhiteButton");
                reports.MinWidth = 180;
                reports.Margin = new Thickness(8, 0, 0, 0);
                reports.Click += (_, _) => ReportService.OpenReportsDirectory();
                _detailActions.Children.Add(reports);
            }
            if (_chatTabs.SelectedIndex == 0) BindJobChat();
            return;
        }
        // Карточка с чатом видна всегда: общий чат и файлы доступны и без заявок
        if (_detailHost.Children.Count == 0 || !ReferenceEquals(_detailHost.Children[0], _detailCard))
        {
            _detailHost.Children.Clear();
            _detailHost.Children.Add(_detailCard);
        }
        _detailCard.Visibility = Visibility.Visible;
        if (job is null)
        {
            _cardTop.Visibility = Visibility.Collapsed;
            _archiveUndoBar.Visibility = Visibility.Collapsed;
            if (_chatTabs.SelectedIndex == 0)
            {
                // чата по заявке без заявки нет — показываем общий чат
                _chatTabs.Select(1, false);
                ShowChatContent(1);
                OpenGeneralChat();
            }
            return;
        }
        _cardTop.Visibility = Visibility.Visible;
        // если чат отвязан (заявки не было) — привязываем к выбранной заявке
        if (_chatTabs.SelectedIndex == 0 && _chatPanel.SessionId != job.JobId) BindJobChat();
        var fullText = job.Text;
        var title = fullText.Split('\n')[0].Trim();
        _detailTitle.Text = title;
        _detailBadgeRow.Children.Clear();
        var (badgeText, statusColor) = JobBadge(job);
        _detailBadgeRow.Children.Add(Ui.Badge(badgeText, statusColor));
        if (job.Urgent)
        {
            var urgentBadge = Ui.Badge("СРОЧНО", Ui.Danger);
            urgentBadge.Margin = new Thickness(6, 0, 0, 0);
            _detailBadgeRow.Children.Add(urgentBadge);
        }
        _detailTimers.Text = DetailStatusLine(job);
        var rest = fullText.Length > title.Length
            ? fullText[title.Length..].TrimStart('\r', '\n', ' ')
            : "";
        _detailDescription.Text = rest;
        _detailDescription.Visibility = rest.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var fieldsLine = JobFieldsLine(job.Client, job.Device, job.Serial);
        _detailFields.Text = fieldsLine;
        _detailFields.Visibility = fieldsLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _detailComment.Text = job.Comment.Length > 0 ? "Что сделано: " + job.Comment : "";
        _detailComment.Visibility = job.Comment.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _detailActions.Children.Clear();
        if (job.ArchivePending)
        {
            var undo = Ui.ActionButton("Отменить архивацию", "PrimaryButton");
            undo.MinWidth = 200;
            undo.Click += (_, _) => CancelArchiveUndo();
            _detailActions.Children.Add(undo);
        }
        else if (job.CompletionSaved)
        {
            var archive = Ui.ActionButton("В архив", "WhiteButton");
            archive.MinWidth = 140;
            archive.Click += (_, _) => BeginArchiveUndo(job);
            _detailActions.Children.Add(archive);
            if (job.ReportPath.Length > 0)
            {
                var report = Ui.ActionButton("Открыть отчёт", "WhiteButton");
                report.MinWidth = 140;
                report.Margin = new Thickness(8, 0, 0, 0);
                var path = job.ReportPath;
                report.Click += (_, _) =>
                {
                    try
                    {
                        if (File.Exists(path))
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                        else ReportService.OpenReportsDirectory();
                    }
                    catch { }
                };
                _detailActions.Children.Add(report);
                var htmlPath = Path.ChangeExtension(job.ReportPath, ".html");
                if (File.Exists(htmlPath))
                {
                    var htmlButton = Ui.ActionButton("Открыть HTML", "WhiteButton");
                    htmlButton.MinWidth = 130;
                    htmlButton.Margin = new Thickness(8, 0, 0, 0);
                    htmlButton.ToolTip = "Красивый отчёт для печати — откроется в браузере";
                    htmlButton.Click += (_, _) =>
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(htmlPath) { UseShellExecute = true });
                        }
                        catch { }
                    };
                    _detailActions.Children.Add(htmlButton);
                }
            }
            // Завершённую тоже можно удалить у всех — файл отчёта в общей папке останется
            var deleteDone = Ui.ActionButton("Удалить заявку", "DangerButton");
            deleteDone.MinWidth = 150;
            deleteDone.Margin = new Thickness(8, 0, 0, 0);
            deleteDone.ToolTip = "Удалить заявку у всех пользователей — файл отчёта в общей папке останется";
            deleteDone.Click += (_, _) => DeleteOwnJob(job);
            _detailActions.Children.Add(deleteDone);
        }
        else if (job.ReportPending)
        {
            var note = Ui.ActionButton("Отчёт сохраняется…", "WhiteButton");
            note.MinWidth = 200;
            note.IsEnabled = false;
            _detailActions.Children.Add(note);
            // Если общая папка долго недоступна, заявку можно убрать совсем — без отчёта
            var drop = Ui.ActionButton("Убрать без отчёта", "DangerButton");
            drop.MinWidth = 170;
            drop.Margin = new Thickness(8, 0, 0, 0);
            drop.ToolTip = "Общая папка недоступна: заявка будет удалена, отчёт не сохранится";
            drop.Click += (_, _) => ForceRemoveJob(job);
            _detailActions.Children.Add(drop);
        }
        else
        {
            var complete = Ui.ActionButton("Завершить", "SuccessButton");
            complete.MinWidth = 160;
            complete.Click += (_, _) =>
            {
                if (HasPendingMessages(job.JobId))
                {
                    MessageBox.Show(this, "По этой заявке ещё есть сообщения в очереди. Дождитесь отметки «Доставлено», затем завершите её.",
                        "Переписка ещё доставляется", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                ShowCompleteSheet(job);
            };
            _detailActions.Children.Add(complete);
            if (!job.IsAccepted)
            {
                var remind = Ui.ActionButton("Напомнить мастеру", "WhiteButton");
                remind.MinWidth = 170;
                remind.Margin = new Thickness(8, 0, 0, 0);
                var remindedAt = _reminderSentAt.TryGetValue(job.JobId, out var sentAt)
                    ? sentAt : DateTime.MinValue;
                remind.IsEnabled = _connection is not null && _authorized &&
                    DateTime.Now - remindedAt > TimeSpan.FromMinutes(1);
                remind.ToolTip = remind.IsEnabled
                    ? "Продублировать сигнал мастеру"
                    : _connection is null || !_authorized
                        ? "Появится после подключения к мастеру"
                        : "Напоминание уже отправлено, подождите минуту";
                remind.Click += async (_, _) => await RemindMasterAsync(job);
                _detailActions.Children.Add(remind);
                var cancel = Ui.ActionButton("Отменить заявку", "DangerButton");
                cancel.MinWidth = 150;
                cancel.Margin = new Thickness(8, 0, 0, 0);
                cancel.Click += (_, _) => CancelWaitingJob(job);
                _detailActions.Children.Add(cancel);
            }
            else
            {
                // Принятую в работу заявку тоже можно удалить — сразу у всех пользователей
                var delete = Ui.ActionButton("Удалить заявку", "DangerButton");
                delete.MinWidth = 150;
                delete.Margin = new Thickness(8, 0, 0, 0);
                delete.ToolTip = "Удалить заявку у всех пользователей — у вас, у мастера и у других менеджеров. Действие нельзя отменить";
                delete.Click += (_, _) => DeleteOwnJob(job);
                _detailActions.Children.Add(delete);
            }
        }
        // Правка своей заявки доступна в любом состоянии — при связи с мастером (2.35)
        var editOwn = Ui.ActionButton("Изменить", "WhiteButton");
        editOwn.MinWidth = 120;
        editOwn.Margin = new Thickness(8, 0, 0, 0);
        var canEditOwn = _connection is not null && _authorized;
        editOwn.IsEnabled = canEditOwn;
        editOwn.ToolTip = canEditOwn
            ? "Изменить описание и реквизиты заявки — обновится у всех пользователей"
            : "Изменить можно при подключении к мастеру";
        var editOwnJob = job;
        editOwn.Click += (_, _) => ShowJobEditSheet(editOwnJob.JobId, editOwnJob.Text,
            editOwnJob.Client, editOwnJob.Device, editOwnJob.Serial, editOwnJob.Urgent);
        _detailActions.Children.Add(editOwn);
        _archiveUndoBar.Visibility = _archiveUndoJobId == job.JobId ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static string JobFieldsLine(string client, string device, string serial)
    {
        var parts = new List<string>();
        if (client.Length > 0) parts.Add("Клиент: " + client);
        if (device.Length > 0) parts.Add("Устройство: " + device);
        if (serial.Length > 0) parts.Add("SN/IMEI: " + serial);
        return string.Join("  ·  ", parts);
    }

    private string RemoteDetailStatusLine(RemoteJobInfo job)
    {
        var suffix = _connection is null || !_authorized ? " · нет связи, данные на момент отключения" : "";
        return JobStatus.Normalize(job.Status) switch
        {
            JobStatus.Accepted =>
                $"В работе у мастера {JobTiming.Elapsed(job.StartedAt == default ? job.ReceivedAt : job.StartedAt, DateTime.Now)} · принята {(job.StartedAt == default ? job.ReceivedAt : job.StartedAt):dd.MM.yyyy HH:mm}" + suffix,
            JobStatus.AwaitingReport => "Ремонт завершён — отчёт сохраняется на компьютере её автора" + suffix,
            JobStatus.Done =>
                $"Завершена {(job.CompletedAt == default ? job.ReceivedAt : job.CompletedAt):dd.MM.yyyy HH:mm} · отчёт в общей папке" + suffix,
            _ => $"Создана {(job.ReceivedAt == default ? DateTime.Now : job.ReceivedAt):dd.MM.yyyy HH:mm} · ожидает мастера" + suffix
        };
    }

    private string DetailStatusLine(ManagerJobState job)
    {
        if (job.ArchivePending) return "Заявка будет убрана в архив.";
        if (job.CompletionSaved)
            return $"Завершена {(job.CompletedAt == default ? DateTime.Now : job.CompletedAt):dd.MM.yyyy HH:mm} · отчёт сохранён в общей папке";
        if (job.ReportPending)
            return "Заявка завершена. Отчёт будет сохранён автоматически, как только общая папка станет доступна.";
        if (job.IsAccepted)
        {
            var started = job.StartedAt == default ? DateTime.Now : job.StartedAt;
            return $"Мастер принял заявку в {started:dd.MM.yyyy HH:mm} · в работе {JobTiming.Elapsed(started, DateTime.Now)}";
        }
        return _connection is null || !_authorized
            ? "Заявка сохранена на этом ПК и будет отправлена автоматически после подключения."
            : DateTime.Now - job.ReceivedAt > TimeSpan.FromMinutes(Math.Max(1, _settings.ReminderMinutes))
                ? "Заявка отправлена. Мастер пока не ответил — можно напомнить ему кнопкой ниже."
                : "Заявка отправлена. Ожидаем подтверждение мастера.";
    }

    private void UpdateStatusBar()
    {
        var connected = _connection is not null && _authorized;
        _statusDot.Background = connected ? Ui.Green : _versionMismatch ? Ui.Warning : Ui.Danger;
        var pending = PendingMessageCount;
        var reports = _pendingReports.Count;
        var parts = new List<string>();
        if (pending > 0) parts.Add("в очереди сообщений: " + pending);
        if (reports > 0) parts.Add("отчётов ждёт папку: " + reports);
        _queueText.Text = string.Join(" · ", parts);
        UpdateOfflineBanner(connected);
        var active = _jobs.Values.Count(job => !job.CompletionSaved);
        UpdateTrayStatus((connected ? "на связи" : "нет связи") +
            (active > 0 ? " · активных: " + active : "") +
            (pending > 0 ? " · в очереди: " + pending : ""));
    }

    private void UpdateOfflineBanner(bool connected)
    {
        var canShow = !_versionMismatch && _ipInput.Text.Trim().Length > 0;
        if (connected || !canShow)
        {
            _offlineShowTimer.Stop();
            if (_offlineBanner.Visibility == Visibility.Visible)
            {
                if (connected && !_offlineGreen)
                {
                    // связь восстановлена: зелёная полоса на 3 секунды, затем исчезает
                    _offlineGreen = true;
                    _offlineBanner.Background = Ui.Green;
                    _offlineBannerText.Foreground = Brushes.White;
                    _offlineBannerText.Text = "Мастер на связи";
                    _offlineRetryButton.Visibility = Visibility.Collapsed;
                    _offlineHideTimer.Start();
                }
                else if (!connected)
                {
                    _offlineHideTimer.Stop();
                    HideOfflineBanner();
                }
            }
            return;
        }
        // связи нет: показываем полосу только после длительной паузы, чтобы не мельтешила
        _offlineHideTimer.Stop();
        if (_offlineBanner.Visibility == Visibility.Visible)
        {
            if (_offlineGreen) ShowOfflineBanner();
        }
        else if (!_offlineShowTimer.IsEnabled)
        {
            _offlineShowTimer.Start();
        }
    }

    private void ShowOfflineBanner()
    {
        _offlineGreen = false;
        _offlineBanner.Background = Ui.AccentSoft;
        _offlineBannerText.Foreground = Ui.Ink;
        _offlineBannerText.Text = "Нет связи с мастером. Заявки и сообщения сохраняются на этом ПК и уйдут автоматически.";
        _offlineRetryButton.Visibility = Visibility.Visible;
        _offlineBanner.Visibility = Visibility.Visible;
    }

    private void HideOfflineBanner()
    {
        _offlineBanner.Visibility = Visibility.Collapsed;
        _offlineGreen = false;
    }

    private void SetConnectionStatus(string text, Brush color)
    {
        _statusText.Text = text;
        _statusText.Foreground = color;
        UpdateStatusBar();
    }

    private static string ShortText(string text)
    {
        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length > 64 ? singleLine[..64] + "…" : singleLine;
    }

    /// <summary>Ручная попытка подключения (кнопки «Подключить» и «Повторить сейчас»).</summary>
    private async Task ManualConnectAsync()
    {
        // Ручная попытка снимает блокировку «версии отличаются»: если на втором
        // компьютере уже обновили программу, связь восстановится без перезапуска
        if (_versionMismatch)
        {
            _versionMismatch = false;
            if (!_reconnectTimer.IsEnabled) _reconnectTimer.Start();
        }
        await ConnectAsync();
    }

    /// <summary>Короткое описание причины, по которой не удалось подключиться.</summary>
    private static string DescribeConnectFailure(Exception ex)
    {
        var socket = ex as System.Net.Sockets.SocketException
            ?? ex.InnerException as System.Net.Sockets.SocketException;
        if (socket is not null)
        {
            switch (socket.SocketErrorCode)
            {
                case System.Net.Sockets.SocketError.TimedOut:
                    return "Мастер не отвечает — проверьте брандмауэр на его компьютере";
                case System.Net.Sockets.SocketError.ConnectionRefused:
                    return "Компьютер отвечает, но порт закрыт: программа мастера не запущена или её блокирует антивирус";
                case System.Net.Sockets.SocketError.HostUnreachable:
                case System.Net.Sockets.SocketError.NetworkUnreachable:
                    return "Адрес недоступен — проверьте IP и что Tailscale включён на обоих ПК";
                case System.Net.Sockets.SocketError.HostNotFound:
                case System.Net.Sockets.SocketError.NoData:
                    return "Адрес не найден — проверьте IP мастера";
            }
        }
        // наш собственный 7-секундный таймаут ожидания ответа
        if (ex is OperationCanceledException)
            return "Мастер не отвечает — проверьте брандмауэр на его компьютере";
        return "Мастер не в сети";
    }

    private async Task ConnectAsync()
    {
        if (_connection is not null || _connecting || _versionMismatch) return;
        var host = _ipInput.Text.Trim();
        if (host.Length == 0)
        {
            ShowSettingsSheet();
            return;
        }
        _connecting = true;
        _connectButton.IsEnabled = false;
        SetConnectionStatus("Подключение…", Ui.Muted);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(host, Port, timeout.Token);
            var connection = new PeerConnection(tcp);
            _connection = connection;
            _authorized = false;
            connection.MessageReceived += m => SafeUi(() => OnMessage(connection, m));
            connection.ChunkReceived += chunk => SafeUi(() => HandleIncomingChunk(chunk));
            connection.SignatureViolation += () => AppLog.Error("Мастер отклонил подпись сообщения");
            connection.Disconnected += () => SafeUi(() => OnDisconnected(connection));
            var hello = AppMessage.Hello(_settings.ClientId, _managerAccount, _machineName);
            hello.Nonce = SecurityService.NewNonce();
            _clientNonce = hello.Nonce;
            var secret = GetStoredSecret();
            if (secret is not null)
                hello.Sig = SecurityService.HelloProof(secret, _settings.ClientId, hello.Nonce);
            await connection.SendAsync(hello);
            SetConnectionStatus("Проверка доступа…", Ui.Muted);
        }
        catch (Exception ex)
        {
            AppLog.Error("Подключение к мастеру не удалось: " + host, ex);
            _connection?.Dispose();
            _connection = null;
            _authorized = false;
            NotifyTransportChanged(false);
            _ipInput.IsReadOnly = false;
            SetConnectionStatus(DescribeConnectFailure(ex) + " · повтор через 8 с", Ui.Danger);
            RefreshJobsView();
        }
        finally { _connectButton.IsEnabled = true; _connecting = false; }
    }

    private byte[]? GetStoredSecret()
    {
        if (_settings.PairSecret.Length == 0) return null;
        try { return Convert.FromBase64String(_settings.PairSecret); }
        catch { return null; }
    }

    private void CompleteAuthorization(PeerConnection connection, byte[] secret, string host)
    {
        connection.SetSignKey(secret);
        _authorized = true;
        _ = ResumeJobsAfterConnect(connection);
        _ = FlushPendingArchivedAsync();
        _ = FlushPendingRemovedAsync();
        NotifyTransportChanged(true);
        _settings.MasterAddress = host;
        _settings.RememberMaster(host);
        SettingsStore.Save(_settings);
        AppLog.Info("Подключено к мастеру: " + host);
        SetConnectionStatus(PendingMessageCount == 0
            ? "Мастер на связи"
            : $"Мастер на связи · отправляем сохранённые: {PendingMessageCount}", Ui.Green);
        _ipInput.IsReadOnly = true;
        RefreshJobsView();
    }

    private void EnsurePairingDialog()
    {
        if (_pairingDialog is not null && !_pairingDialog.IsDisposed)
        {
            _pairingDialog.Activate();
            return;
        }
        _pairingDialog = new PairingForm();
        _pairingDialog.CodeSubmitted += code =>
        {
            _pendingCode = code;
            _pairingSecret = SecurityService.DeriveSecret(code, _settings.ClientId);
            if (_connection is null)
            {
                // Соединение уже разорвано — сначала подключаемся; мастер ответит
                // запросом кода, и введённый код уйдёт автоматически (PairRequired)
                _pairingDialog.ShowInfo("Подключаемся к мастеру — код отправится сам…");
                _ = ConnectAsync();
            }
            else
            {
                _pairingDialog.ShowInfo("Проверяем код…");
                _ = SendPairAsync();
            }
        };
        _pairingDialog.Cancelled += () => { _pendingCode = null; _pairingSecret = null; };
        _pairingDialog.FormClosed += (_, _) => _pairingDialog = null;
        _pairingDialog.Show(this);
        SetConnectionStatus("Введите код с экрана мастера", Ui.Accent);
    }

    private async Task SendPairAsync()
    {
        var connection = _connection;
        if (connection is null || _authorized || _pairingSecret is null) return;
        try
        {
            var pair = AppMessage.Hello(_settings.ClientId, _managerAccount, _machineName);
            pair.Type = MessageTypes.Pair;
            pair.Nonce = SecurityService.NewNonce();
            _clientNonce = pair.Nonce;
            pair.Sig = SecurityService.PairProof(_pairingSecret, _settings.ClientId, pair.Nonce);
            await connection.SendAsync(pair);
        }
        catch { connection.Disconnect(); }
    }

    private async Task ResumeJobsAfterConnect(PeerConnection connection)
    {
        foreach (var job in _jobs.Values.OrderBy(job => job.ReceivedAt).ToList())
        {
            if (job.CompletionSaved)
            {
                await connection.SendAsync(AppMessage.ManagerCompleted(job.JobId, job.Text,
                    job.StartedAt == default ? job.CompletedAt : job.StartedAt,
                    job.CompletedAt == default ? DateTime.Now : job.CompletedAt, _managerName, job.Comment));
            }
            else if (job.IsAccepted)
            {
                await connection.SendAsync(AppMessage.SyncRequest(job.JobId, job.Text, job.Details));
            }
            else
            {
                await connection.SendAsync(AppMessage.JobRequest(job.JobId, job.Text, _managerName, job.Details));
            }
        }
    }

    private void OnDisconnected(PeerConnection connection)
    {
        if (!ReferenceEquals(_connection, connection)) return;
        var wasAuthorized = _authorized;
        _connection = null;
        _authorized = false;
        NotifyTransportChanged(false);
        _ipInput.IsReadOnly = false;
        Alerts.Stop();
        if (!_versionMismatch)
        {
            SetConnectionStatus(!wasAuthorized && _pairingDialog is not null
                ? "Введите код с экрана мастера"
                : "Восстанавливаем связь…", !wasAuthorized && _pairingDialog is not null ? Ui.Accent : Ui.Danger);
        }
        RefreshJobsView();
    }

    private async Task<string?> SendJobAsync(string text, JobDetails? details = null)
    {
        if (text.Length == 0)
        {
            MessageBox.Show(this, "Введите название ремонта или описание заявки.");
            return null;
        }
        details ??= new JobDetails();
        var job = new ManagerJobState
        {
            JobId = Guid.NewGuid().ToString("N"),
            Text = text,
            ReceivedAt = DateTime.Now,
            Client = details.Client,
            Device = details.Device,
            Serial = details.Serial,
            Urgent = details.Urgent
        };
        _jobs[job.JobId] = job;
        _jobsTabs.Select(0, false);
        _jobsTabIndex = 0;
        _selectedJobId = job.JobId;
        _selectedRemoteId = "";
        SyncSelectedJob();
        TryUpdateJournal(job, "Ожидает подтверждения", null, null);
        PersistSession();
        if (_connection is not null && _authorized)
        {
            try { await _connection.SendAsync(AppMessage.JobRequest(job.JobId, job.Text, _managerName, job.Details)); }
            catch { _connection?.Disconnect(); }
        }
        RefreshJobsView();
        BindJobChat();
        return job.JobId;
    }

    private async Task RemindMasterAsync(ManagerJobState job)
    {
        if (_connection is null || !_authorized) return;
        _reminderSentAt[job.JobId] = DateTime.Now;
        var message = AppMessage.JobReminderMessage(job.JobId, job.Text, _managerName);
        message.ClientId = _settings.ClientId;
        await SendSafeAsync(message);
        RefreshJobsView(false);
    }

    /// <summary>Удалить свою заявку в любом состоянии — исчезнет из списков у всех пользователей.</summary>
    private void DeleteOwnJob(ManagerJobState job)
    {
        var waiting = !job.IsAccepted && !job.CompletionSaved && !job.ReportPending;
        var warning = job.ReportPending
            ? "Отчёт по этой заявке ещё не сохранён в общей папке — при удалении отчёт создан НЕ будет."
            : job.CompletionSaved
                ? "Отчёт уже сохранён в общей папке и останется там."
                : job.IsAccepted
                    ? "Мастер уже взял заявку в работу — он узнает об удалении сразу или при первом подключении."
                    : "Заявка ещё не принята мастером.";
        if (MessageBox.Show(this,
            warning + "\n\nЗаявка «" + ShortText(job.Text) + "» исчезнет из списков у всех пользователей — у вас, у мастера и у других менеджеров. Восстановить её будет нельзя. Удалить?",
            waiting ? "Отмена заявки" : "Удалить заявку",
            MessageBoxButton.YesNo, waiting ? MessageBoxImage.Question : MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        AppLog.Info("Заявка удалена менеджером: " + job.JobId + " · " + ShortText(job.Text));
        // очищаем очередь отчётов, чтобы отчёт не сохранился позже, когда папка появится
        _pendingReports.RemoveAll(item => item.JobId == job.JobId);
        PendingReportStore.Save(_pendingReports);
        RemoveJob(job.JobId);
        UpdateStatusBar();
        _ = NotifyMasterRemovedAsync(job.JobId);
    }

    private void CancelWaitingJob(ManagerJobState job)
    {
        if (job.IsAccepted || job.CompletionSaved || job.ReportPending) return;
        DeleteOwnJob(job);
    }

    /// <summary>Убрать зависшую заявку, не дожидаясь сохранения отчёта (общая папка недоступна).</summary>
    private void ForceRemoveJob(ManagerJobState job)
    {
        if (!job.ReportPending || job.CompletionSaved) return;
        DeleteOwnJob(job);
    }

    /// <summary>Удалить чужую заявку — мастер разошлёт команду всем, включая её автора.</summary>
    private void DeleteRemoteJob(RemoteJobInfo job)
    {
        var warning = JobStatus.Normalize(job.Status) switch
        {
            JobStatus.Done => "Заявка завершена, отчёт уже сохранён в общей папке её автора и останется там.",
            JobStatus.AwaitingReport => "Отчёт по этой заявке ещё сохраняется у её автора — при удалении он может не записаться.",
            JobStatus.Accepted => "Ремонт в работе у мастера — статус и время работы будут потеряны.",
            _ => "Заявка ещё не принята мастером."
        };
        if (MessageBox.Show(this,
            warning + "\n\nЗаявка «" + ShortText(job.Text) + "» (" + job.OwnerName + ") исчезнет из списков у всех пользователей. Восстановить её будет нельзя. Удалить?",
            "Удалить заявку", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        AppLog.Info("Менеджер удалил чужую заявку: " + job.JobId + " · " + ShortText(job.Text));
        _remoteJobs.Remove(job.JobId);
        RemoteJobsStore.Save(_remoteJobs.Values);
        if (_selectedRemoteId == job.JobId) _selectedRemoteId = "";
        RefreshJobsView(false);
        _ = NotifyMasterRemovedAsync(job.JobId);
    }

    /// <summary>Панель правки заявки (своей или чужой): описание и реквизиты.
    /// Правка уходит мастеру, мастер рассылает обновлённую карточку всем пользователям (2.35).</summary>
    private void ShowJobEditSheet(string jobId, string text, string client, string device, string serial, bool urgent)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Изменить заявку",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(Ui.Note("Изменения увидят все: вы, мастер и другие менеджеры. Статус заявки и уже сохранённые отчёты не меняются."));
        panel.Children.Add(Ui.Note("Описание заявки:"));
        var textBox = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            MaxLength = 2000,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 8)
        };
        panel.Children.Add(textBox);
        panel.Children.Add(Ui.Note("Клиент:"));
        var clientBox = new TextBox { Text = client, MaxLength = 200, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(clientBox);
        panel.Children.Add(Ui.Note("Устройство:"));
        var deviceBox = new TextBox { Text = device, MaxLength = 200, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(deviceBox);
        panel.Children.Add(Ui.Note("Серийный номер / IMEI:"));
        var serialBox = new TextBox { Text = serial, MaxLength = 200, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(serialBox);
        var urgentBox = new CheckBox
        {
            Content = "Срочная заявка",
            IsChecked = urgent,
            Foreground = Ui.Muted,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(urgentBox);
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancel = Ui.ActionButton("Отмена", "WhiteButton");
        cancel.Margin = new Thickness(0, 0, 5, 0);
        cancel.Click += (_, _) => CloseSheet();
        buttons.Children.Add(cancel);
        var save = Ui.ActionButton("Сохранить", "PrimaryButton");
        save.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(save, 1);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        save.Click += async (_, _) =>
        {
            var newText = textBox.Text.Trim();
            if (newText.Length == 0)
            {
                MessageBox.Show(this, "Описание заявки не может быть пустым.", "Изменить заявку",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            CloseSheet();
            await SendJobEditAsync(jobId, newText, clientBox.Text.Trim(),
                deviceBox.Text.Trim(), serialBox.Text.Trim(), urgentBox.IsChecked == true);
        };
        ShowSheet(panel, 460);
        textBox.Focus();
    }

    /// <summary>Отправить правку мастеру — он обновит заявку и разошлёт новую карточку всем.</summary>
    private async Task SendJobEditAsync(string jobId, string text, string client, string device, string serial, bool urgent)
    {
        if (_connection is null || !_authorized)
        {
            MessageBox.Show(this, "Нет связи с мастером — изменения не отправлены. Подключитесь и повторите.",
                "Изменить заявку", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await _connection.SendAsync(AppMessage.JobEdit(jobId, text, client, device, serial, urgent, _managerName));
            AppLog.Info("Отправлена правка заявки: " + jobId + " · " + ShortText(text));
        }
        catch { _connection.Disconnect(); }
    }

    /// <summary>Удалить запись из архива — заявка исчезнет и из общего списка у всех пользователей.</summary>
    private void DeleteArchivedEntry(ManagerArchiveEntry entry)
    {
        if (MessageBox.Show(this,
            "Запись «" + ShortText(entry.Text) + "» исчезнет из вашего архива, а сама заявка — из списков у всех пользователей. Файл отчёта в общей папке останется. Удалить?",
            "Удалить заявку", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        AppLog.Info("Удалена запись архива: " + entry.JobId + " · " + ShortText(entry.Text));
        _archive.Remove(entry);
        ManagerArchiveStore.Save(_archive);
        RefreshJobsView(false);
        _ = NotifyMasterRemovedAsync(entry.JobId);
    }

    /// <summary>Сообщить мастеру, что заявка удалена. Без связи — запоминаем и отправим при подключении.</summary>
    private async Task NotifyMasterRemovedAsync(string jobId)
    {
        if (_connection is not null && _authorized)
        {
            try
            {
                await _connection.SendAsync(AppMessage.JobForceRemoved(jobId, _managerName));
                if (_pendingRemovedJobs.Remove(jobId)) PersistSession();
                return;
            }
            catch { _connection.Disconnect(); }
        }
        if (_pendingRemovedJobs.Add(jobId)) PersistSession();
    }

    /// <summary>Дослать мастеру уведомления об удалении, накопленные без связи.</summary>
    private async Task FlushPendingRemovedAsync()
    {
        if (_connection is null || !_authorized || _pendingRemovedJobs.Count == 0) return;
        foreach (var jobId in _pendingRemovedJobs.OrderBy(id => id, StringComparer.Ordinal).ToList())
        {
            try
            {
                await _connection.SendAsync(AppMessage.JobForceRemoved(jobId, _managerName));
                _pendingRemovedJobs.Remove(jobId);
            }
            catch
            {
                _connection.Disconnect();
                break;
            }
        }
        PersistSession();
    }

    private void RemoveJob(string jobId)
    {
        _jobs.Remove(jobId);
        ResetSession(jobId);
        if (_archiveUndoJobId == jobId) CancelArchiveUndo();
        _selectedJobId = VisibleJobs().FirstOrDefault()?.JobId ?? "";
        _selectedRemoteId = "";
        SyncSelectedJob();
        RefreshJobsView();
    }

    private void OnMessage(PeerConnection connection, AppMessage message)
    {
        if (!ReferenceEquals(_connection, connection)) return;
        if (!_authorized)
        {
            HandleAuthMessage(connection, message);
            return;
        }
        if (message.Type == MessageTypes.JobAccepted && _jobs.TryGetValue(message.JobId, out var acceptedJob) &&
            !acceptedJob.CompletionSaved)
        {
            acceptedJob.IsAccepted = true;
            acceptedJob.StartedAt = message.StartedAt?.ToLocalTime() ?? DateTime.Now;
            SelectJob(acceptedJob.JobId, false);
            WindowsNotifications.Show("Заявка принята", ShortText(acceptedJob.Text), () =>
            {
                SelectJob(acceptedJob.JobId);
                BindJobChat();
            });
            TryUpdateJournal(acceptedJob, "В работе", acceptedJob.StartedAt, null);
            PersistSession();
            NotifyTransportChanged(true);
            RefreshJobsView();
        }
        else if (message.Type == MessageTypes.JobState && _jobs.TryGetValue(message.JobId, out var stateJob) &&
                 !stateJob.CompletionSaved)
        {
            stateJob.IsAccepted = message.Accepted;
            if (stateJob.IsAccepted)
            {
                stateJob.StartedAt = message.StartedAt?.ToLocalTime() ??
                    (stateJob.StartedAt == default ? DateTime.Now : stateJob.StartedAt);
                TryUpdateJournal(stateJob, "В работе", stateJob.StartedAt, null);
            }
            else
            {
                stateJob.StartedAt = default;
                TryUpdateJournal(stateJob, "Ожидает подтверждения", null, null);
            }
            SelectJob(stateJob.JobId, false);
            PersistSession();
            if (stateJob.IsAccepted) NotifyTransportChanged(true);
            RefreshJobsView();
        }
        else if (message.Type == MessageTypes.JobCompleted && _jobs.TryGetValue(message.JobId, out var completedJob))
        {
            CompleteJobFromMaster(message, completedJob);
        }
        else if (message.Type == MessageTypes.JobArchived && _jobs.TryGetValue(message.JobId, out var archivedJob) &&
                 !archivedJob.IsAccepted && !archivedJob.CompletionSaved)
        {
            RemoveJob(archivedJob.JobId);
        }
        else if (message.Type == MessageTypes.JobForceRemoved) HandleJobForceRemoved(message);
        else if (message.Type == MessageTypes.JobInfo) ReceiveRemoteJobInfo(message);
        else if (message.Type == MessageTypes.JobSyncBegin)
        {
            _remoteSyncActive = true;
            _syncSeenIds.Clear();
        }
        else if (message.Type == MessageTypes.JobSyncEnd) EndRemoteSync();
        else if (message.Type == MessageTypes.MessageAck) Acknowledge(message.JobId, message.MessageId);
        else if (message.Type == MessageTypes.Read) HandleReadReceipt(message.JobId, message.Text);
        else if (message.Type == MessageTypes.Chat) ReceiveChat(message);
        else if (message.Type == MessageTypes.FileOffer) ReceiveFileOffer(message);
        else if (message.Type == MessageTypes.FileAccept) HandleFileAccept(message.JobId, message);
        else if (message.Type == MessageTypes.FileEnd) HandleIncomingFileEnd(message.JobId, message.MessageId);
        else if (message.Type == MessageTypes.FileCancel) HandleFileCancel(message.JobId, message);
        else if (message.Type == MessageTypes.ChatEdit) ReceiveMessageEdited(ResolveKnownSession(message.JobId), message);
        else if (message.Type == MessageTypes.ChatPin) ReceivePinned(ResolveKnownSession(message.JobId), message);
    }

    /// <summary>Заявку удалили (мастер или другой менеджер) — убираем её отовсюду:
    /// из активных, из архива и из списка чужих заявок.</summary>
    private void HandleJobForceRemoved(AppMessage message)
    {
        var who = message.Sender.Length > 0 ? message.Sender : "Мастер";
        string? title = null;
        if (_jobs.TryGetValue(message.JobId, out var own))
        {
            title = own.Text;
            _pendingReports.RemoveAll(item => item.JobId == own.JobId);
            PendingReportStore.Save(_pendingReports);
            RemoveJob(own.JobId);
        }
        var archiveEntry = _archive.FirstOrDefault(item => item.JobId == message.JobId);
        if (archiveEntry is not null)
        {
            title ??= archiveEntry.Text;
            _archive.Remove(archiveEntry);
            ManagerArchiveStore.Save(_archive);
        }
        if (_remoteJobs.Remove(message.JobId, out var remote))
        {
            title ??= remote.Text;
            RemoteJobsStore.Save(_remoteJobs.Values);
            if (_selectedRemoteId == message.JobId) _selectedRemoteId = "";
        }
        // Инициатор уже убрал заявку у себя локально — ему повторное уведомление не нужно
        if (title is null) return;
        AppLog.Info($"Заявку удалил {who}: {message.JobId} · {ShortText(title)}");
        WindowsNotifications.Show("Заявка удалена", $"{who}: {ShortText(title)}");
        RefreshJobsView();
    }

    /// <summary>Карточка чужой заявки от мастера (полный снимок при входе или одиночное обновление).</summary>
    private void ReceiveRemoteJobInfo(AppMessage message)
    {
        if (message.JobId.Length == 0) return;
        // Своя заявка: статус и даты ведём локально (они главнее копии мастера), а вот правки
        // описания и реквизитов — от мастера или другого менеджера — применяем из карточки (2.35)
        if (message.ClientId == _settings.ClientId || _jobs.ContainsKey(message.JobId))
        {
            if (_jobs.TryGetValue(message.JobId, out var ownJob)) ApplyOwnJobDescription(message, ownJob);
            return;
        }
        var existed = _remoteJobs.ContainsKey(message.JobId);
        var info = RemoteJobInfo.FromMessage(message);
        _remoteJobs[info.JobId] = info;
        if (_remoteSyncActive) _syncSeenIds.Add(info.JobId);
        RemoteJobsStore.Save(_remoteJobs.Values);
        if (!existed && !_remoteSyncActive && JobStatus.Normalize(info.Status) == JobStatus.Waiting)
            WindowsNotifications.Show("Новая заявка — " + info.OwnerName, ShortText(info.Text));
        RefreshJobsView(false);
    }

    /// <summary>Правка своей заявки, пришедшая с мастера (её изменил мастер или другой менеджер).
    /// Обновляем только описание и реквизиты — статус, даты и отчёт остаются локальными (2.35).</summary>
    private void ApplyOwnJobDescription(AppMessage message, ManagerJobState job)
    {
        var changed = false;
        if (message.Text.Trim().Length > 0 && job.Text != message.Text) { job.Text = message.Text; changed = true; }
        if (job.Client != message.Client) { job.Client = message.Client; changed = true; }
        if (job.Device != message.Device) { job.Device = message.Device; changed = true; }
        if (job.Serial != message.Serial) { job.Serial = message.Serial; changed = true; }
        if (job.Urgent != message.Urgent) { job.Urgent = message.Urgent; changed = true; }
        if (!changed) return;
        PersistSession();
        // Во время полного снимка не тревожим; о собственной правке повторно не сообщаем
        if (!_remoteSyncActive && message.Sender.Length > 0 && message.Sender != _managerName)
            WindowsNotifications.Show("Заявка изменена", message.Sender + ": " + ShortText(job.Text));
        RefreshJobsView(false);
    }

    /// <summary>Снимок списка получен целиком: убираем чужие заявки, которых у мастера уже нет.</summary>
    private void EndRemoteSync()
    {
        if (!_remoteSyncActive) return;
        _remoteSyncActive = false;
        foreach (var jobId in RemoteJobsSync.MissingFromSnapshot(_remoteJobs.Keys.ToList(), _syncSeenIds))
        {
            _remoteJobs.Remove(jobId);
            if (_selectedRemoteId == jobId) _selectedRemoteId = "";
        }
        RemoteJobsStore.Save(_remoteJobs.Values);
        RefreshJobsView(false);
    }

    private void HandleAuthMessage(PeerConnection connection, AppMessage message)
    {
        if (message.Type == MessageTypes.HelloOk)
        {
            var secret = GetStoredSecret();
            if (secret is null || message.Sig != SecurityService.ServerProof(secret, _clientNonce, message.Nonce))
            {
                _settings.PairSecret = "";
                SettingsStore.Save(_settings);
                connection.Disconnect();
                EnsurePairingDialog();
                return;
            }
            CompleteAuthorization(connection, secret, _ipInput.Text.Trim());
        }
        else if (message.Type == MessageTypes.PairRequired)
        {
            // Если код уже введён в настройках — отправляем его сразу, окно не открываем
            if (_pendingCode is not null && _pairingSecret is not null) _ = SendPairAsync();
            else EnsurePairingDialog();
        }
        else if (message.Type == MessageTypes.PairOk)
        {
            if (_pairingSecret is null || _pendingCode is null ||
                message.Sig != SecurityService.PairOkProof(_pairingSecret, _settings.ClientId, message.Nonce))
            {
                connection.Disconnect();
                return;
            }
            _settings.PairSecret = Convert.ToBase64String(_pairingSecret);
            SettingsStore.Save(_settings);
            if (_pairingDialog is not null && !_pairingDialog.IsDisposed) _pairingDialog.Close();
            _pendingCode = null;
            AppLog.Info("Сопряжение с мастером выполнено");
            CompleteAuthorization(connection, _pairingSecret, _ipInput.Text.Trim());
            _pairingSecret = null;
            // если код вводился в «⚙ Настройки» — закрываем панель: связь установлена
            CloseSheet();
        }
        else if (message.Type == MessageTypes.AuthFail)
        {
            if (message.Reason == "version")
            {
                // Блокируем авто-повторы на 10 минут, но таймер не останавливаем навсегда:
                // если на втором ПК обновят программу, связь восстановится сама, без перезапуска.
                // Окно с предупреждением показываем не чаще, чем раз в 30 минут.
                var showBox = !_versionMismatch || DateTime.Now - _versionWarnedAt > TimeSpan.FromMinutes(30);
                _versionMismatch = true;
                _versionMismatchAt = DateTime.Now;
                SetConnectionStatus("Обновите программу — версии отличаются", Ui.Danger);
                if (showBox)
                {
                    _versionWarnedAt = DateTime.Now;
                    MessageBox.Show(this,
                        "Версия «СЕРВИС ЛЕГКО» на компьютере мастера отличается от вашей. Обновите программу на обоих компьютерах до последней версии. Программа сама попробует подключиться снова через 10 минут.",
                        "Несовместимые версии", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else if (message.Reason == "bad_code")
            {
                _pendingCode = null;
                _pairingSecret = null;
                if (_pairingDialog is not null && !_pairingDialog.IsDisposed) _pairingDialog.ShowError("Код неверный");
                else EnsurePairingDialog();
            }
            else if (message.Reason == "code_inactive" || message.Reason == "code_locked")
            {
                // Кода у мастера сейчас нет: кнопку не нажимали, код истёк или режим отключён.
                // Подсказываем, к какому компьютеру шла попытка, — чтобы было видно,
                // если менеджер случайно подключается не к тому мастеру.
                _pendingCode = null;
                _pairingSecret = null;
                var host = _ipInput.Text.Trim();
                var where = host.Length > 0 ? $" (подключались к {host})" : "";
                var text = message.Reason == "code_locked"
                    ? "Мастер отключил добавление после неверных попыток — попросите его нажать «Добавить компьютер» ещё раз и продиктовать новый код"
                    : "На компьютере мастера сейчас нет действующего кода — попросите его нажать «Добавить компьютер» и сразу введите новые 6 цифр" + where;
                if (_pairingDialog is not null && !_pairingDialog.IsDisposed) _pairingDialog.ShowError(text);
                else
                {
                    EnsurePairingDialog();
                    _pairingDialog?.ShowError(text);
                }
            }
            else
            {
                _settings.PairSecret = "";
                SettingsStore.Save(_settings);
                _pendingCode = null;
                _pairingSecret = null;
                EnsurePairingDialog();
            }
        }
    }

    private void CompleteJobFromMaster(AppMessage message, ManagerJobState job)
    {
        if (job.CompletionSaved && !job.ReportPending)
        {
            _ = SendReportSavedAsync(job.JobId);
            SelectJob(job.JobId);
            return;
        }
        var startedAt = message.StartedAt?.ToLocalTime() ??
            (job.StartedAt == default ? DateTime.Now : job.StartedAt);
        var completedAt = message.CompletedAt?.ToLocalTime() ?? DateTime.Now;
        job.StartedAt = startedAt;
        job.CompletedAt = completedAt;
        job.IsAccepted = false;
        job.ReportPending = true;
        job.Comment = message.Comment;
        UpsertPendingReport(job, notifyMasterCompleted: false);
        CloseChatSession(job.JobId);
        SelectJob(job.JobId, false);
        PersistSession();
        TryFlushPendingReports();
        RefreshJobsView();
    }

    private void CompleteJob(ManagerJobState job, string comment)
    {
        if (job.CompletionSaved || job.ReportPending) return;
        job.StartedAt = job.StartedAt == default ? DateTime.Now : job.StartedAt;
        job.CompletedAt = DateTime.Now;
        job.IsAccepted = false;
        job.ReportPending = true;
        job.Comment = comment;
        UpsertPendingReport(job, notifyMasterCompleted: true);
        CloseChatSession(job.JobId);
        SelectJob(job.JobId, false);
        PersistSession();
        TryFlushPendingReports();
        RefreshJobsView();
    }

    private void UpsertPendingReport(ManagerJobState job, bool notifyMasterCompleted)
    {
        _pendingReports.RemoveAll(item => item.JobId == job.JobId);
        _pendingReports.Add(new PendingReport
        {
            JobId = job.JobId,
            JobText = job.Text,
            ManagerUser = _managerAccount,
            MachineName = _machineName,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            History = GetHistoryForSession(job.JobId).ToList(),
            Comment = job.Comment,
            Client = job.Client,
            Device = job.Device,
            Serial = job.Serial,
            Urgent = job.Urgent,
            NotifyMasterCompleted = notifyMasterCompleted
        });
        PendingReportStore.Save(_pendingReports);
    }

    private void TryFlushPendingReports()
    {
        if (_flushingReports || _closing) return;
        _flushingReports = true;
        try
        {
            _pendingReports = ReportFlushService.Flush(_pendingReports.ToList(), (report, path) =>
            {
                if (_jobs.TryGetValue(report.JobId, out var job))
                {
                    job.ReportPending = false;
                    job.CompletionSaved = true;
                    job.IsAccepted = false;
                    job.ReportPath = path;
                }
                if (report.NotifyMasterCompleted)
                {
                    _ = SendSafeAsync(AppMessage.ManagerCompleted(report.JobId, report.JobText,
                        report.StartedAt, report.CompletedAt, _managerName, report.Comment));
                }
                else
                {
                    _ = SendReportSavedAsync(report.JobId);
                }
                Alerts.PlayOnce(AlertKind.Completed);
                WindowsNotifications.Show("Ремонт завершён",
                    "Отчёт сохранён: " + Path.GetFileName(path), ReportService.OpenReportsDirectory);
                AppLog.Info("Отчёт сохранён: " + path);
            });
            PendingReportStore.Save(_pendingReports);
        }
        finally
        {
            _flushingReports = false;
            PersistSession();
            RefreshJobsView(false);
        }
    }

    private async Task SendSafeAsync(AppMessage message)
    {
        try { if (_connection is not null && _authorized) await _connection.SendAsync(message); }
        catch { _connection?.Disconnect(); }
    }

    private void BeginArchiveUndo(ManagerJobState job)
    {
        if (!job.CompletionSaved || job.ArchivePending) return;
        if (_archiveUndoJobId.Length > 0) ExecuteArchive(_archiveUndoJobId);
        job.ArchivePending = true;
        _archiveUndoJobId = job.JobId;
        _archiveUndoRemaining = 8;
        _archiveUndoText.Text = "Заявка будет убрана в архив через " + _archiveUndoRemaining + " с";
        _archiveUndoTimer.Start();
        RefreshJobsView();
    }

    private void CancelArchiveUndo()
    {
        if (_archiveUndoJobId.Length > 0 && _jobs.TryGetValue(_archiveUndoJobId, out var job))
            job.ArchivePending = false;
        _archiveUndoJobId = "";
        _archiveUndoTimer.Stop();
        RefreshJobsView();
    }

    private void ArchiveUndoTick()
    {
        if (_archiveUndoJobId.Length == 0)
        {
            _archiveUndoTimer.Stop();
            return;
        }
        _archiveUndoRemaining--;
        if (_archiveUndoRemaining <= 0)
        {
            var jobId = _archiveUndoJobId;
            _archiveUndoJobId = "";
            _archiveUndoTimer.Stop();
            ExecuteArchive(jobId);
            return;
        }
        _archiveUndoText.Text = "Заявка будет убрана в архив через " + _archiveUndoRemaining + " с";
    }

    private void ExecuteArchive(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.ArchivePending = false;
        _archive.Add(new ManagerArchiveEntry
        {
            JobId = job.JobId,
            Text = job.Text,
            CompletedAt = job.CompletedAt == default ? DateTime.Now : job.CompletedAt,
            Comment = job.Comment,
            ReportPath = job.ReportPath
        });
        ManagerArchiveStore.Save(_archive);
        _ = NotifyMasterArchivedAsync(job.JobId);
        RemoveJob(job.JobId);
    }

    /// <summary>Сообщить мастеру, что заявка убрана. Без связи — запоминаем и отправим при подключении.</summary>
    private async Task NotifyMasterArchivedAsync(string jobId, bool withReport = true)
    {
        if (_connection is not null && _authorized)
        {
            try
            {
                if (withReport) await SendReportSavedAsync(jobId);
                await _connection.SendAsync(AppMessage.JobArchived(jobId));
                if (_pendingArchivedJobs.Remove(jobId)) PersistSession();
                return;
            }
            catch { _connection.Disconnect(); }
        }
        if (_pendingArchivedJobs.Add(jobId)) PersistSession();
    }

    /// <summary>Дослать мастеру уведомления об архивировании, накопленные без связи.</summary>
    private async Task FlushPendingArchivedAsync()
    {
        if (_connection is null || !_authorized || _pendingArchivedJobs.Count == 0) return;
        foreach (var jobId in _pendingArchivedJobs.OrderBy(id => id, StringComparer.Ordinal).ToList())
        {
            try
            {
                await _connection.SendAsync(AppMessage.JobArchived(jobId));
                _pendingArchivedJobs.Remove(jobId);
            }
            catch
            {
                _connection.Disconnect();
                break;
            }
        }
        PersistSession();
    }

    private async Task SendReportSavedAsync(string jobId)
    {
        try { if (_connection is not null && _authorized) await _connection.SendAsync(AppMessage.ReportSaved(jobId)); }
        catch { }
    }

    private void TryUpdateJournal(ManagerJobState job, string status, DateTime? startedAt, DateTime? completedAt)
    {
        try
        {
            ReportService.UpdateJournal(job.Text, startedAt, completedAt, _managerAccount,
                _machineName, job.JobId, status);
        }
        catch { }
    }

    protected override async Task SendSessionMessageAsync(string sessionId, AppMessage message)
    {
        if (_connection is null || !_authorized) throw new InvalidOperationException("Нет подключения к мастеру.");
        message.JobId = sessionId;
        message.Sender = _managerName;
        await _connection.SendAsync(message);
    }

    protected override async Task SendSessionAckAsync(string sessionId, string messageId)
    {
        if (_connection is null || !_authorized) throw new InvalidOperationException("Нет подключения к мастеру.");
        await _connection.SendAsync(AppMessage.Ack(sessionId, messageId));
    }

    protected override void SendReadReceipt(string sessionId, IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0) return;
        _ = SendSafeAsync(AppMessage.ReadReceipt(sessionId, messageIds));
    }

    protected override async Task SendSessionChunkAsync(string sessionId, FileChunk chunk)
    {
        if (_connection is null || !_authorized) throw new InvalidOperationException("Нет подключения к мастеру.");
        await _connection.SendChunkAsync(chunk.MessageId, chunk.Offset, chunk.Data, chunk.Data.Length);
    }

    private void SafeUi(Action action)
    {
        if (_closing || IsDisposed) return;
        try { if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(action); else action(); } catch (InvalidOperationException) { }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => SafeUi(() =>
    {
        _networkChangeTimer.Stop();
        _networkChangeTimer.Start();
    });

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (TryHideToTray(e)) return;
        _closing = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _reconnectTimer.Stop();
        _networkChangeTimer.Stop();
        _jobElapsedTimer.Stop();
        _reportRetryTimer.Stop();
        _archiveUndoTimer.Stop();
        _connection?.Dispose();
        Alerts.Dispose();
        base.OnClosing(e);
    }
}
