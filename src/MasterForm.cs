using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RepairAlert;

internal sealed class MasterForm : ChatHostForm
{
    private const int Port = 47891;
    private readonly AppSettings _settings;
    private readonly AlertService _requestAlerts = new();
    private readonly DispatcherTimer _networkRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _jobElapsedTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, ManagerState> _managers = new();
    private readonly Dictionary<PeerConnection, string> _connectionManagers = new();
    private readonly HashSet<PeerConnection> _allConnections = new();
    private readonly List<string> _pendingAlerts = new();
    private readonly Dictionary<string, DateTime> _postponed = new();
    private readonly DispatcherTimer _postponeTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    // Номера удалённых заявок: удалённое не воскресает при переподключении менеджеров
    private readonly DeletedJobIds _deletedJobs = DeletedJobsStore.Load();

    private readonly Border _statusDot;
    private readonly TextBlock _statusText;
    private readonly TextBlock _queueText;
    private readonly StackPanel _jobsList;
    private readonly TextBlock _jobsEmpty;
    private readonly TextBox _searchInput;
    private readonly Ui.SegmentedControl _jobsTabs;
    private readonly Grid _detailHost;
    private readonly Grid _detailCard;
    private readonly Border _cardTop;
    private readonly TextBlock _detailTitle;
    private readonly StackPanel _detailBadgeRow;
    private readonly TextBlock _detailManager;
    private readonly TextBlock _detailTimers;
    private readonly TextBlock _detailDescription;
    private readonly TextBlock _detailFields;
    private readonly TextBlock _detailComment;
    private readonly StackPanel _detailActions;
    private readonly Ui.SegmentedControl _chatTabs;
    // Переключатель общего чата по менеджерам (2.35, логика 2.38): у каждого менеджера — свой общий чат
    // с мастером; переключатель показывает только менеджеров в сети, без сети — поле неактивно
    private readonly ComboBox _chatPeerPicker = new();
    private readonly Border _chatPeerRow = new() { Visibility = Visibility.Collapsed };
    private bool _chatPeerSync;
    private string _chatPeerId = ""; // пусто — не выбран (нет менеджеров в сети)
    private string _chatPeerSignature = "";
    private readonly ChatPanel _chatPanel;
    private readonly ScrollViewer _filesView;
    private readonly StackPanel _filesList;
    private readonly Grid _overlayHost;
    private readonly ComboBox _ipList = new();
    private readonly TextBlock _ipHint;
    private readonly StackPanel _devicesList;

    // Одноразовый код сопряжения: появляется только по кнопке «Добавить компьютер»
    private readonly PairingSession _pairing = new();
    private readonly DispatcherTimer _pairingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? _pairCodeText;
    private TextBlock? _pairHintText;

    private string _activeManagerId = "";
    private string _activeJobId = "";
    private int _jobsTabIndex;
    private TcpListener? _listener;
    private DiscoveryResponder? _discovery;
    private RequestAlertForm? _requestAlert;
    private bool _closing;

    private sealed class ManagerState
    {
        public required string ClientId { get; init; }
        public string UserName { get; set; } = "";
        public string MachineName { get; set; } = "";
        public PeerConnection? Connection { get; set; }
        public Dictionary<string, MasterJobRecord> Jobs { get; } = new();
        public Queue<DateTime> JobRequestTimes { get; } = new();
        public string GeneralSessionKey => ClientId + ":general";
        public string JobSessionKey(string jobId) => ClientId + ":" + jobId;
    }

    /// <summary>Пункт переключателя общего чата: менеджер и подпись с пометкой о связи (2.35).</summary>
    private sealed record ChatPeerItem(string Id, string Label)
    {
        // И выпадающий список, и поле выбранного пункта показывают только Label,
        // без служебного текста вида «ChatPeerItem { Id = … }»
        public override string ToString() => Label;
    }

    public MasterForm() : base("master")
    {
        _settings = SettingsStore.Load();
        StoragePaths.Configure(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RepairAlert", "MasterData"));
        Ui.Prepare(this, "СЕРВИС ЛЕГКО — мастер", 1180, 760, 1000, 640);

        var root = new Grid { Background = Ui.Background };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new Border
        {
            Height = 52,
            Background = Ui.Surface,
            BorderBrush = Ui.Separator,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 0, 16, 0)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(Ui.LogoMark(30));
        var titles = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "СЕРВИС ЛЕГКО", FontSize = 14, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "Мастер", Foreground = Ui.Muted, FontSize = 11 });
        Grid.SetColumn(titles, 1);
        headerGrid.Children.Add(titles);
        var miniButton = Ui.ActionButton("◱ Мини", "WhiteButton");
        miniButton.Height = 32;
        miniButton.MinHeight = 32;
        miniButton.FontSize = 12;
        miniButton.Padding = new Thickness(12, 0, 12, 0);
        miniButton.Margin = new Thickness(0, 0, 8, 0);
        miniButton.VerticalAlignment = VerticalAlignment.Center;
        miniButton.ToolTip = "Свернуть в мини-панель над треем — заявки и быстрый приём";
        miniButton.Click += (_, _) => EnterMiniMode();
        Grid.SetColumn(miniButton, 3);
        headerGrid.Children.Add(miniButton);
        var settingsButton = Ui.ActionButton("Настройки", "WhiteButton", Ui.IconSettings);
        settingsButton.Height = 32;
        settingsButton.MinHeight = 32;
        settingsButton.FontSize = 12;
        settingsButton.Padding = new Thickness(12, 0, 12, 0);
        settingsButton.VerticalAlignment = VerticalAlignment.Center;
        settingsButton.Click += (_, _) => ShowSettingsSheet();
        Grid.SetColumn(settingsButton, 4);
        headerGrid.Children.Add(settingsButton);
        header.Child = headerGrid;
        root.Children.Add(header);

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 280 });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(main, 1);
        root.Children.Add(main);

        var listPanel = new Grid { Margin = new Thickness(14, 12, 6, 12) };
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var searchWrap = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        _searchInput = new TextBox
        {
            MinHeight = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0, 10, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(_searchInput, "Поиск по заявкам и менеджерам");
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
            RefreshJobsView();
        };
        searchWrap.Children.Add(_searchInput);
        searchWrap.Children.Add(searchHint);
        listPanel.Children.Add(searchWrap);
        _jobsTabs = new Ui.SegmentedControl(new[] { "Активные", "Завершённые" });
        _jobsTabs.Margin = new Thickness(0, 0, 0, 10);
        _jobsTabs.SelectionChanged += index =>
        {
            _jobsTabIndex = index;
            var first = VisibleJobs().FirstOrDefault();
            if (first.Manager is not null) _activeManagerId = first.Manager.ClientId;
            _activeJobId = first.Job?.CurrentJobId ?? "";
            RefreshJobsView();
        };
        Grid.SetRow(_jobsTabs, 1);
        listPanel.Children.Add(_jobsTabs);
        var listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _jobsList = new StackPanel();
        listScroll.Content = _jobsList;
        Grid.SetRow(listScroll, 2);
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
        _detailManager = new TextBlock
        {
            Foreground = Ui.Muted,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        topStack.Children.Add(_detailManager);
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
        _cardTop.Child = topStack;
        _detailCard.Children.Add(_cardTop);

        var chatArea = new Grid();
        chatArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
        // Переключатель собеседника общего чата — виден только на вкладке «Общий чат» (2.35)
        _chatPeerRow.Background = Ui.Background;
        _chatPeerRow.BorderBrush = Ui.Separator;
        _chatPeerRow.BorderThickness = new Thickness(1, 0, 1, 0);
        _chatPeerRow.Padding = new Thickness(12, 6, 12, 8);
        var peerStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        peerStack.Children.Add(new TextBlock
        {
            Text = "Чат с менеджером:",
            Foreground = Ui.Muted,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _chatPeerPicker.MinWidth = 250;
        _chatPeerPicker.FontSize = 12;
        // Правила отображения задаём ДО первого наполнения списка: иначе часть пунктов
        // успевает отрисоваться служебным текстом вида «ChatPeerItem { Id = … }»
        _chatPeerPicker.DisplayMemberPath = nameof(ChatPeerItem.Label);
        _chatPeerPicker.SelectedValuePath = nameof(ChatPeerItem.Id);
        _chatPeerPicker.SelectionChanged += OnChatPeerPicked;
        peerStack.Children.Add(_chatPeerPicker);
        _chatPeerRow.Child = peerStack;
        Grid.SetRow(_chatPeerRow, 1);
        chatArea.Children.Add(_chatPeerRow);
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
        Grid.SetRow(chatHost, 2);
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
        _statusDot = Ui.StatusDot(Ui.Muted);
        statusGrid.Children.Add(_statusDot);
        _statusText = new TextBlock
        {
            Text = "Запуск сервера…",
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
        Grid.SetRow(statusBar, 2);
        root.Children.Add(statusBar);

        _overlayHost = new Grid { Visibility = Visibility.Collapsed };
        Grid.SetRowSpan(_overlayHost, 3);
        root.Children.Add(_overlayHost);

        _ipHint = Ui.Note("Поиск сетевых адресов…");
        _pairingTimer.Tick += (_, _) => UpdatePairingView();
        _pairingTimer.Start();
        _devicesList = new StackPanel();

        RegisterChatPanel(_chatPanel);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _overlayHost.Children.Count > 0)
            {
                e.Handled = true;
                CloseSheet();
            }
        };

        RestoreManagerJobs();
        RefreshManagerList();
        _jobElapsedTimer.Tick += (_, _) => RefreshJobsView(false);
        _jobElapsedTimer.Start();
        _postponeTimer.Tick += (_, _) => ShowNextRequestAlert();
        _postponeTimer.Start();
        Loaded += (_, _) => StartServer();
        _networkRefreshTimer.Tick += (_, _) => { _networkRefreshTimer.Stop(); RefreshAddresses(); };
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    private ManagerState? SelectedManager =>
        _activeManagerId.Length > 0 && _managers.TryGetValue(_activeManagerId, out var state) ? state : null;

    private MasterJobRecord? SelectedJob
    {
        get
        {
            var state = SelectedManager;
            return state is not null && _activeJobId.Length > 0 &&
                   state.Jobs.TryGetValue(_activeJobId, out var job) ? job : null;
        }
    }

    private List<(ManagerState Manager, MasterJobRecord Job)> VisibleJobs()
    {
        var all = _managers.Values.SelectMany(state =>
            state.Jobs.Values.Select(job => (Manager: state, Job: job)));
        var filtered = _jobsTabIndex == 0
            ? all.Where(item => !item.Job.ReportSaved)
            : all.Where(item => item.Job.ReportSaved);
        var query = _searchInput.Text.Trim();
        if (query.Length > 0)
            filtered = filtered.Where(item =>
                item.Job.CurrentJobText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Job.CurrentJobId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Job.Client.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Job.Device.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Job.Serial.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Manager.UserName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Manager.MachineName.Contains(query, StringComparison.OrdinalIgnoreCase));
        return filtered
            .OrderBy(item => item.Job.ReportSaved ? 3 : item.Job.AwaitingReport ? 2 : item.Job.IsJobAccepted ? 1 : 0)
            .ThenByDescending(item => item.Job.IsJobAccepted ? item.Job.StartedAt : item.Job.ReceivedAt)
            .ThenBy(item => item.Manager.UserName)
            .ToList();
    }

    private void ShowSheet(FrameworkElement content, double width)
    {
        CloseSheet();
        var dimmer = new Border { Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)) };
        dimmer.MouseLeftButtonDown += (_, _) => CloseSheet();
        var card = new Border
        {
            Width = width,
            MaxHeight = 640,
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
        card.Child = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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

    private void ShowCompleteSheet(ManagerState state, MasterJobRecord job)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Завершить ремонт?",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(Ui.Note($"Отчёт будет создан на компьютере {state.UserName}. Можно добавить короткий итог — он попадёт в отчёт."));
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
        complete.Click += async (_, _) =>
        {
            CloseSheet();
            await CompleteJobAsync(state, job, comment.Text.Trim());
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
        _pairCodeText = new TextBlock
        {
            Foreground = Ui.Accent,
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 2)
        };
        panel.Children.Add(_pairCodeText);
        _pairHintText = Ui.Note("");
        panel.Children.Add(_pairHintText);
        var addComputer = Ui.ActionButton("Добавить компьютер", "WhiteButton");
        addComputer.Click += (_, _) => { _pairing.Begin(); UpdatePairingView(); };
        panel.Children.Add(addComputer);
        UpdatePairingView();
        panel.Children.Add(Ui.SectionTitle("Сопряжённые устройства"));
        RefreshDevicesList();
        DetachElement(_devicesList);
        panel.Children.Add(_devicesList);
        panel.Children.Add(Ui.SectionTitle("Адрес этого компьютера"));
        panel.Children.Add(Ui.Note("Менеджеры подключаются к этому компьютеру. Укажите им адрес из списка ниже."));
        DetachElement(_ipList);
        DetachElement(_ipHint);
        panel.Children.Add(_ipList);
        panel.Children.Add(_ipHint);
        var addressButtons = new Grid();
        addressButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addressButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var copy = Ui.ActionButton("Копировать IP", "WhiteButton");
        copy.Margin = new Thickness(0, 0, 5, 0);
        copy.Click += (_, _) => { if (_ipList.Text.Length > 0) Clipboard.SetText(_ipList.Text); };
        addressButtons.Children.Add(copy);
        var refresh = Ui.ActionButton("Обновить адреса", "WhiteButton");
        refresh.Margin = new Thickness(5, 0, 0, 0);
        refresh.Click += (_, _) => RefreshAddresses();
        Grid.SetColumn(refresh, 1);
        addressButtons.Children.Add(refresh);
        panel.Children.Add(addressButtons);
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
        panel.Children.Add(Ui.Note("Полученные файлы хранятся локально на этом компьютере."));
        panel.Children.Add(CreateSoundSettingsPanel(_settings));
        panel.Children.Add(Ui.SectionTitle("Внешний вид"));
        panel.Children.Add(CreateThemeSelector(_settings));
        panel.Children.Add(Ui.SectionTitle("Очистка списка заявок"));
        panel.Children.Add(Ui.Note("Массовое удаление старых и «фантомных» заявок — сразу у всех пользователей. Заявки в работе очистка не трогает."));
        var cleanup = Ui.ActionButton("Очистка списка заявок…", "WhiteButton");
        cleanup.Click += (_, _) => ShowCleanupSheet();
        panel.Children.Add(cleanup);
        panel.Children.Add(Ui.SectionTitle("Диагностика"));
        panel.Children.Add(CreateDiagnosticsButtons());
        var switchRole = Ui.ActionButton("Сменить роль", "QuietButton");
        switchRole.Click += (_, _) =>
        {
            CloseSheet();
            SwitchRole();
        };
        panel.Children.Add(switchRole);
        ShowSheet(panel, 460);
    }

    private static void DetachElement(FrameworkElement element)
    {
        if (element.Parent is Panel parent) parent.Children.Remove(element);
    }

    /// <summary>Панель массовой очистки: пороги по возрасту для каждой группы,
    /// живой предпросмотр количества и подтверждение. «В работе» не затрагивается никогда.</summary>
    private void ShowCleanupSheet()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Очистка списка заявок",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(Ui.Note("Выбранные заявки будут удалены сразу у всех пользователей — у вас и у всех менеджеров (у отключённых — при первом подключении). Файлы отчётов в общих папках остаются. Заявки «В работе» очистка никогда не трогает."));

        // null — группу не трогаем, 0 — удаляем все в группе, иначе — старше N дней
        var doneOptions = new (string Label, int? Days)[]
            { ("Не трогать", null), ("Старше 30 дней", 30), ("Старше 90 дней", 90), ("Старше 180 дней", 180), ("Все завершённые", 0) };
        var waitingOptions = new (string Label, int? Days)[]
            { ("Не трогать", null), ("Старше 7 дней", 7), ("Старше 30 дней", 30), ("Все ожидающие", 0) };
        var awaitingOptions = new (string Label, int? Days)[]
            { ("Не трогать", null), ("Старше 7 дней", 7), ("Старше 30 дней", 30), ("Все ожидающие отчёт", 0) };

        ComboBox MakeCombo((string Label, int? Days)[] options)
        {
            var combo = new ComboBox
            {
                ItemsSource = options.Select(o => o.Label).ToList(),
                SelectedIndex = 0,
                Margin = new Thickness(0, 2, 0, 8)
            };
            return combo;
        }

        panel.Children.Add(Ui.Note("Завершённые (отчёт сохранён):"));
        var doneCombo = MakeCombo(doneOptions);
        panel.Children.Add(doneCombo);
        panel.Children.Add(Ui.Note("Ожидающие принятия (мастер не принял):"));
        var waitingCombo = MakeCombo(waitingOptions);
        panel.Children.Add(waitingCombo);
        panel.Children.Add(Ui.Note("Ожидающие отчёт (общая папка недоступна):"));
        var awaitingCombo = MakeCombo(awaitingOptions);
        panel.Children.Add(awaitingCombo);

        var preview = new TextBlock
        {
            Foreground = Ui.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10)
        };
        panel.Children.Add(preview);

        CleanupRules CurrentRules() => new(
            doneOptions[doneCombo.SelectedIndex].Days,
            waitingOptions[waitingCombo.SelectedIndex].Days,
            awaitingOptions[awaitingCombo.SelectedIndex].Days);

        var deleteButton = Ui.ActionButton("Удалить", "DangerButton");
        deleteButton.MinWidth = 180;
        deleteButton.IsEnabled = false;

        void UpdatePreview()
        {
            var rules = CurrentRules();
            if (!rules.AnyEnabled)
            {
                preview.Text = "Выберите, что удалять, — выше.";
                deleteButton.IsEnabled = false;
                deleteButton.Content = "Удалить";
                return;
            }
            var count = FindCleanupMatches(rules).Count;
            preview.Text = count == 0
                ? "Под выбранные правила сейчас ничего не подпадает."
                : "Под выбранные правила подпадают: " + count + " " + JobCleanupPlural(count) + ".";
            deleteButton.IsEnabled = count > 0;
            deleteButton.Content = "Удалить " + count + " " + JobCleanupPlural(count);
        }
        doneCombo.SelectionChanged += (_, _) => UpdatePreview();
        waitingCombo.SelectionChanged += (_, _) => UpdatePreview();
        awaitingCombo.SelectionChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        deleteButton.Click += (_, _) =>
        {
            var rules = CurrentRules();
            var matches = FindCleanupMatches(rules);
            if (matches.Count == 0) return;
            if (MessageBox.Show(this,
                "Будет удалено заявок: " + matches.Count + ". Они исчезнут из списков у всех пользователей — у вас и у всех менеджеров. Восстановить их будет нельзя. Продолжить?",
                "Очистка списка заявок", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (var (state, job) in matches)
                DeleteJobForEveryone(state, job.CurrentJobId, "Мастер");
            AppLog.Info("Очистка списка заявок: удалено " + matches.Count);
            WindowsNotifications.Show("Список заявок очищен", "Удалено заявок: " + matches.Count);
            CloseSheet();
        };

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancel = Ui.ActionButton("Отмена", "WhiteButton");
        cancel.Margin = new Thickness(0, 0, 5, 0);
        cancel.Click += (_, _) => CloseSheet();
        buttons.Children.Add(cancel);
        deleteButton.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(deleteButton, 1);
        buttons.Children.Add(deleteButton);
        panel.Children.Add(buttons);
        ShowSheet(panel, 480);
    }

    private static string JobCleanupPlural(int count)
    {
        var mod100 = count % 100;
        if (mod100 is >= 11 and <= 14) return "заявок";
        return count % 10 == 1 ? "заявка" : count % 10 is >= 2 and <= 4 ? "заявки" : "заявок";
    }

    /// <summary>Заявки, попадающие под правила очистки (возраст — от завершения или от создания).</summary>
    private List<(ManagerState State, MasterJobRecord Job)> FindCleanupMatches(CleanupRules rules)
    {
        var now = DateTime.Now;
        return _managers.Values
            .SelectMany(state => state.Jobs.Values.Select(job => (state, job)))
            .Where(pair => JobCleanup.Matches(rules,
                JobStatus.Of(pair.job.IsJobAccepted, pair.job.AwaitingReport, pair.job.ReportSaved),
                pair.job.ReceivedAt, pair.job.CompletedAt, now))
            .ToList();
    }

    private void RestoreManagerJobs()
    {
        foreach (var job in MasterJobStore.Load().Where(job => job.CurrentJobId.Length > 0))
        {
            if (!_managers.TryGetValue(job.ClientId, out var state))
            {
                state = new ManagerState { ClientId = job.ClientId };
                _managers.Add(job.ClientId, state);
            }
            state.UserName = job.UserName;
            state.MachineName = job.MachineName;
            state.Jobs[job.CurrentJobId] = job;
        }
        // Сопряжённые устройства тоже попадают в список менеджеров: общий чат с менеджером
        // можно открыть, даже если у него ещё ни разу не было заявок (2.35)
        foreach (var device in TrustedDevicesStore.All())
        {
            if (_managers.TryGetValue(device.ClientId, out var known))
            {
                if (known.UserName.Length == 0) known.UserName = device.UserName;
                if (known.MachineName.Length == 0) known.MachineName = device.MachineName;
            }
            else
            {
                _managers.Add(device.ClientId, new ManagerState
                {
                    ClientId = device.ClientId,
                    UserName = device.UserName,
                    MachineName = device.MachineName
                });
            }
        }
    }

    private void PersistManagerJobs()
    {
        var records = _managers.Values.SelectMany(state => state.Jobs.Values.Select(job =>
        {
            job.ClientId = state.ClientId;
            job.UserName = state.UserName;
            job.MachineName = state.MachineName;
            return job;
        })).ToList();
        MasterJobStore.Save(records);
    }

    private void StartServer()
    {
        try
        {
            RefreshAddresses();
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            AppLog.Info("Сервер запущен на порту " + Port);
            _discovery = DiscoveryResponder.TryStart(
                Environment.MachineName, Port, SecurityService.AppVersion);
            UpdateStatusBar();
            _ = AcceptLoop();
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось запустить сервер", ex);
            var socket = ex as System.Net.Sockets.SocketException
                ?? ex.InnerException as System.Net.Sockets.SocketException;
            _statusText.Text = socket?.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse
                ? "Порт 47891 уже занят другой программой — закройте вторую копию «СЕРВИС ЛЕГКО» или старую «РемонтСвязь» в трее и перезапустите программу"
                : "Не удалось запустить сервер: " + ex.Message;
            _statusDot.Background = Ui.Danger;
        }
    }

    private void RefreshAddresses()
    {
        var selected = _ipList.Text;
        var addresses = GetLocalAddresses();
        _ipList.Items.Clear();
        foreach (var ip in addresses) _ipList.Items.Add(ip);
        _ipList.Text = addresses.Contains(selected) ? selected : addresses.FirstOrDefault() ?? "IP не найден";
        _ipHint.Text = addresses.Any(IsTailscale)
            ? "Tailscale обнаружен. Для работы через разные сети используйте адрес 100.x."
            : "Для интернета запустите Tailscale. Список обновляется автоматически при смене сети.";
    }

    private static List<string> GetLocalAddresses()
    {
        var result = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var address in ni.GetIPProperties().UnicastAddresses)
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                    result.Add(address.Address.ToString());
        }
        return result.Distinct().OrderByDescending(IsTailscale).ThenBy(ip => ip).ToList();
    }

    private static bool IsTailscale(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4 && parts[0] == "100" && int.TryParse(parts[1], out var second) && second >= 64 && second <= 127;
    }

    private async Task AcceptLoop()
    {
        if (_listener is null) return;
        try
        {
            while (!_closing)
            {
                var tcp = await _listener.AcceptTcpClientAsync();
                var connection = new PeerConnection(tcp);
                _allConnections.Add(connection);
                connection.MessageReceived += m => SafeUi(() => OnMessage(connection, m));
                connection.ChunkReceived += chunk => SafeUi(() => HandleIncomingChunk(chunk));
                connection.SignatureViolation += () => AppLog.Error("Отклонено сообщение с неверной подписью");
                connection.Disconnected += () => SafeUi(() => OnDisconnected(connection));
                _ = AuthTimeoutAsync(connection);
            }
        }
        catch (Exception) when (_closing) { }
        catch (Exception ex) { SafeUi(() => _statusText.Text = "Сервер остановлен: " + ex.Message); }
    }

    private async Task AuthTimeoutAsync(PeerConnection connection)
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        if (!_closing && !_connectionManagers.ContainsKey(connection) && connection.IsConnected)
        {
            AppLog.Error("Соединение закрыто: не прошло проверку доступа за отведённое время");
            connection.Disconnect();
        }
    }

    private void OnDisconnected(PeerConnection connection)
    {
        _allConnections.Remove(connection);
        if (_connectionManagers.TryGetValue(connection, out var clientId))
        {
            _connectionManagers.Remove(connection);
            if (_managers.TryGetValue(clientId, out var state) && ReferenceEquals(state.Connection, connection))
                state.Connection = null;
            RefreshJobsView();
        }
        UpdateStatusBar();
    }

    private void OnMessage(PeerConnection connection, AppMessage message)
    {
        if (!_connectionManagers.ContainsKey(connection))
        {
            HandleAuthMessage(connection, message);
            return;
        }
        var clientId = _connectionManagers[connection];
        if (!_managers.TryGetValue(clientId, out var state)) return;

        if (message.Type == MessageTypes.Hello)
        {
            _ = SendHelloOkAsync(state, connection, message.Nonce);
            return;
        }
        if (message.Type == MessageTypes.JobRequest)
        {
            if (!AllowJobRequest(state))
            {
                AppLog.Error($"Слишком частые заявки от {state.UserName} — лишние отклонены");
                return;
            }
            ReceiveJob(state, message);
        }
        else if (message.Type == MessageTypes.SyncRequest) SyncManagerJob(state, message);
        else if (message.Type == MessageTypes.ManagerCompleted) CompleteJobFromManager(state, message);
        else if (message.Type == MessageTypes.JobArchived) ArchiveCompletedJob(state, message);
        else if (message.Type == MessageTypes.MessageAck) Acknowledge(state.ClientId + ":" + message.JobId, message.MessageId);
        else if (message.Type == MessageTypes.Read) HandleReadReceipt(state.ClientId + ":" + message.JobId, message.Text);
        else if (message.Type == MessageTypes.JobReminder) HandleJobReminder(state, message);
        else if (message.Type == MessageTypes.Chat) ReceiveManagerChat(state, message);
        else if (message.Type == MessageTypes.FileOffer) ReceiveManagerFileOffer(state, message);
        else if (message.Type == MessageTypes.FileAccept)
            HandleFileAccept(state.ClientId + ":" + message.JobId, message);
        else if (message.Type == MessageTypes.FileEnd)
            HandleIncomingFileEnd(state.ClientId + ":" + message.JobId, message.MessageId);
        else if (message.Type == MessageTypes.FileCancel)
            HandleFileCancel(state.ClientId + ":" + message.JobId, message);
        else if (message.Type == MessageTypes.ReportSaved) MarkReportSaved(state, message.JobId);
        else if (message.Type == MessageTypes.JobForceRemoved) ForceRemoveByManager(state, message.JobId);
        else if (message.Type == MessageTypes.JobEdit) HandleJobEdit(state, message);
        else if (message.Type == MessageTypes.ChatEdit) ReceiveManagerChatEdit(state, message);
        else if (message.Type == MessageTypes.ChatPin) ReceiveManagerChatPin(state, message);
    }

    /// <summary>Правка сообщения чата, пришедшая от менеджера.</summary>
    private void ReceiveManagerChatEdit(ManagerState state, AppMessage message)
    {
        var sessionId = message.JobId == GeneralChatId ? state.GeneralSessionKey : state.JobSessionKey(message.JobId);
        ReceiveMessageEdited(sessionId, message);
    }

    /// <summary>Закрепление сообщения чата, пришедшее от менеджера.</summary>
    private void ReceiveManagerChatPin(ManagerState state, AppMessage message)
    {
        var sessionId = message.JobId == GeneralChatId ? state.GeneralSessionKey : state.JobSessionKey(message.JobId);
        ReceivePinned(sessionId, message);
    }

    private static bool AllowJobRequest(ManagerState state)
    {
        var now = DateTime.UtcNow;
        while (state.JobRequestTimes.Count > 0 && now - state.JobRequestTimes.Peek() > TimeSpan.FromMinutes(1))
            state.JobRequestTimes.Dequeue();
        if (state.JobRequestTimes.Count >= 10) return false;
        state.JobRequestTimes.Enqueue(now);
        return true;
    }

    private void HandleAuthMessage(PeerConnection connection, AppMessage message)
    {
        if (message.Type != MessageTypes.Hello && message.Type != MessageTypes.Pair)
        {
            connection.Disconnect();
            return;
        }
        if (message.ProtocolVersion != SecurityService.ProtocolVersion)
        {
            AppLog.Error($"Подключение с несовместимой версией протокола {message.ProtocolVersion} отклонено");
            _ = FailAndCloseAsync(connection, "version");
            return;
        }
        if (message.ClientId.Length == 0)
        {
            connection.Disconnect();
            return;
        }
        var secret = TrustedDevicesStore.GetSecret(message.ClientId);
        if (secret is null)
        {
            if (message.Type == MessageTypes.Hello)
            {
                _ = SendSafeDirectAsync(connection, new AppMessage { Type = MessageTypes.PairRequired });
                return;
            }
            HandlePairAttempt(connection, message);
            return;
        }
        if (message.Type == MessageTypes.Pair)
        {
            HandlePairAttempt(connection, message);
            return;
        }
        if (message.Sig.Length == 0 || message.Sig != SecurityService.HelloProof(secret, message.ClientId, message.Nonce))
        {
            AppLog.Error("Отклонён вход устройства с неверной подписью: " + message.ClientId);
            _ = FailAndCloseAsync(connection, "unknown_device");
            return;
        }
        var state = AuthorizeManager(connection, message, secret);
        _ = SendHelloOkAsync(state, connection, message.Nonce);
    }

    private void HandlePairAttempt(PeerConnection connection, AppMessage message)
    {
        // Подпись Pair строится из кода, поэтому проверка подписи — это проверка кода,
        // но сам код по сети не передаётся и в журнал не пишется
        var result = _pairing.TryVerify(message.ClientId, message.Nonce, message.Sig, out var secret);
        if (result == PairingCheck.Ok && secret is not null)
        {
            TrustedDevicesStore.Add(message.ClientId, secret, message.UserName, message.MachineName);
            RefreshDevicesList();
            AuthorizeManager(connection, message, secret);
            _ = SendSafeDirectAsync(connection, new AppMessage
            {
                Type = MessageTypes.PairOk,
                ClientId = message.ClientId,
                Nonce = message.Nonce,
                Sig = SecurityService.PairOkProof(secret, message.ClientId, message.Nonce)
            });
            UpdatePairingView();
            return;
        }
        var when = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
        var who = message.ClientId.Replace('\r', ' ').Replace('\n', ' ');
        if (result == PairingCheck.WrongCode)
        {
            AppLog.Error($"Неудачная попытка сопряжения (неверный код). IP: {connection.RemoteAddress}, ClientId: {who}, время: {when}");
            // Соединение держим, пока режим добавления не отключился — менеджер может повторить ввод
            _ = FailAndCloseAsync(connection, "bad_code", keepOpen: !_pairing.LockedOut);
        }
        else
        {
            AppLog.Error($"Неудачная попытка сопряжения (код не активен: {_pairing.InactiveReason}). IP: {connection.RemoteAddress}, ClientId: {who}, время: {when}");
            // Блокировку показываем отдельно, чтобы менеджер видел точную причину
            _ = FailAndCloseAsync(connection, _pairing.LockedOut ? "code_locked" : "code_inactive");
        }
        UpdatePairingView();
    }

    private ManagerState AuthorizeManager(PeerConnection connection, AppMessage hello, byte[] secret)
    {
        if (!_managers.TryGetValue(hello.ClientId, out var state))
        {
            state = new ManagerState { ClientId = hello.ClientId };
            _managers.Add(hello.ClientId, state);
        }
        var previous = state.Connection;
        state.UserName = string.IsNullOrWhiteSpace(hello.UserName) ? "Менеджер" : hello.UserName;
        state.MachineName = string.IsNullOrWhiteSpace(hello.MachineName) ? "ПК" : hello.MachineName;
        state.Connection = connection;
        connection.SetSignKey(secret);
        _connectionManagers[connection] = hello.ClientId;
        AppLog.Info($"Менеджер подключён: {state.UserName} · {state.MachineName}");
        if (previous is not null && !ReferenceEquals(previous, connection))
        {
            _connectionManagers.Remove(previous);
            previous.Dispose();
        }
        var firstJob = state.Jobs.Values
            .OrderBy(job => job.ReportSaved ? 3 : job.AwaitingReport ? 2 : job.IsJobAccepted ? 1 : 0)
            .ThenByDescending(job => job.IsJobAccepted ? job.StartedAt : job.ReceivedAt)
            .FirstOrDefault();
        RefreshManagerList(hello.ClientId, firstJob?.CurrentJobId);
        UpdateStatusBar();
        NotifyTransportChanged(true);
        return state;
    }

    private async Task SendHelloOkAsync(ManagerState state, PeerConnection connection, string clientNonce)
    {
        if (!ReferenceEquals(state.Connection, connection)) return;
        var secret = TrustedDevicesStore.GetSecret(state.ClientId);
        if (secret is null) return;
        var serverNonce = SecurityService.NewNonce();
        await SendSafeDirectAsync(connection, new AppMessage
        {
            Type = MessageTypes.HelloOk,
            ClientId = state.ClientId,
            Nonce = serverNonce,
            Sig = SecurityService.ServerProof(secret, clientNonce, serverNonce)
        });
        // После входа менеджер получает полный список заявок всех пользователей:
        // между маркерами Begin/End — по одной карточке JobInfo на заявку
        _ = SendJobsSnapshotAsync(state, connection);
    }

    /// <summary>Полный снимок всех заявок (всех менеджеров, за всё время) для подключившегося менеджера.</summary>
    private async Task SendJobsSnapshotAsync(ManagerState target, PeerConnection connection)
    {
        try
        {
            await connection.SendAsync(new AppMessage { Type = MessageTypes.JobSyncBegin });
            var jobs = _managers.Values
                .SelectMany(item => item.Jobs.Values)
                .Where(job => !_deletedJobs.Contains(job.CurrentJobId))
                .OrderBy(job => job.ReceivedAt)
                .ToList();
            foreach (var job in jobs)
            {
                if (!ReferenceEquals(target.Connection, connection)) return;
                await connection.SendAsync(BuildJobInfo(job));
            }
            await connection.SendAsync(new AppMessage { Type = MessageTypes.JobSyncEnd });
        }
        catch { }
    }

    /// <summary>Карточка заявки для общего списка у менеджеров.</summary>
    private static AppMessage BuildJobInfo(MasterJobRecord job) => AppMessage.JobInfo(
        job.CurrentJobId, job.CurrentJobText, job.ClientId, job.UserName, job.MachineName,
        JobStatus.Of(job.IsJobAccepted, job.AwaitingReport, job.ReportSaved),
        job.ReceivedAt, job.StartedAt, job.CompletedAt, job.Comment,
        job.Client, job.Device, job.Serial, job.Urgent);

    /// <summary>Рассказать всем подключённым менеджерам о создании/смене состояния заявки.
    /// editorName — кто изменил заявку (для уведомления у автора); пусто — обычное обновление.</summary>
    private void BroadcastJobInfo(MasterJobRecord job, string editorName = "")
    {
        var message = BuildJobInfo(job);
        message.Sender = editorName;
        foreach (var manager in _managers.Values)
            if (manager.Connection is not null)
                _ = SendSafeDirectAsync(manager.Connection, message);
    }

    /// <summary>Рассказать всем подключённым менеджерам, что заявка удалена.</summary>
    private void BroadcastForceRemoved(string jobId, string senderName)
    {
        foreach (var manager in _managers.Values)
            if (manager.Connection is not null)
                _ = SendSafeDirectAsync(manager.Connection, AppMessage.JobForceRemoved(jobId, senderName));
    }

    /// <summary>Удалить заявку у всех: запомнить номер, убрать из списка, разослать команду.</summary>
    private void DeleteJobForEveryone(ManagerState state, string jobId, string deletedBy)
    {
        _deletedJobs.Add(jobId);
        DeletedJobsStore.Save(_deletedJobs);
        RemoveJobEverywhere(state, jobId);
        BroadcastForceRemoved(jobId, deletedBy);
    }

    private static async Task SendSafeDirectAsync(PeerConnection connection, AppMessage message)
    {
        try { await connection.SendAsync(message); }
        catch { }
    }

    private async Task FailAndCloseAsync(PeerConnection connection, string reason, bool keepOpen = false)
    {
        try { await connection.SendAsync(new AppMessage { Type = MessageTypes.AuthFail, Reason = reason }); }
        catch { }
        if (!keepOpen) connection.Disconnect();
    }

    private void ReceiveJob(ManagerState state, AppMessage message)
    {
        if (_deletedJobs.Contains(message.JobId))
        {
            // Заявку уже удалили — гасим устаревшую копию у менеджера, не создавая запись заново
            if (state.Connection is not null)
                _ = SendSafeDirectAsync(state.Connection, AppMessage.JobForceRemoved(message.JobId, "Мастер"));
            return;
        }
        if (state.Jobs.TryGetValue(message.JobId, out var existing))
        {
            if (existing.AwaitingReport || existing.ReportSaved) _ = SendCompletedAgainAsync(state, existing);
            else if (existing.IsJobAccepted) _ = SendAcceptedAsync(state, existing.CurrentJobId, existing.StartedAt);
            else AddPendingAlert(existing.CurrentJobId);
            return;
        }
        var job = new MasterJobRecord
        {
            ClientId = state.ClientId,
            UserName = state.UserName,
            MachineName = state.MachineName,
            CurrentJobId = message.JobId,
            CurrentJobText = message.Text,
            Client = message.Client,
            Device = message.Device,
            Serial = message.Serial,
            Urgent = message.Urgent,
            ReceivedAt = DateTime.Now,
            StatusMessage = "Новая заявка — подтвердите получение."
        };
        state.Jobs[job.CurrentJobId] = job;
        _jobsTabs.Select(0, false);
        _jobsTabIndex = 0;
        AddPendingAlert(job.CurrentJobId);
        RefreshManagerList(state.ClientId, job.CurrentJobId);
        WindowsNotifications.Show("Новая заявка: " + state.UserName, job.CurrentJobText,
            () => { SelectJob(state.ClientId, job.CurrentJobId); ShowRequestAlertFor(state.ClientId, job.CurrentJobId); });
        ShowNextRequestAlert();
        BroadcastJobInfo(job);
    }

    private void SyncManagerJob(ManagerState state, AppMessage message)
    {
        if (_deletedJobs.Contains(message.JobId))
        {
            // Заявку уже удалили — гасим устаревшую копию у менеджера
            if (state.Connection is not null)
                _ = SendSafeDirectAsync(state.Connection, AppMessage.JobForceRemoved(message.JobId, "Мастер"));
            return;
        }
        if (state.Jobs.TryGetValue(message.JobId, out var job))
        {
            if (job.AwaitingReport || job.ReportSaved)
            {
                _ = SendCompletedAgainAsync(state, job);
                return;
            }
            _ = SendJobStateAsync(state, job.CurrentJobId, job.CurrentJobText, job.IsJobAccepted, job.StartedAt);
            return;
        }

        job = new MasterJobRecord
        {
            ClientId = state.ClientId,
            UserName = state.UserName,
            MachineName = state.MachineName,
            CurrentJobId = message.JobId,
            CurrentJobText = message.Text,
            Client = message.Client,
            Device = message.Device,
            Serial = message.Serial,
            Urgent = message.Urgent,
            ReceivedAt = DateTime.Now,
            StatusMessage = "Восстановленная заявка — подтвердите получение."
        };
        state.Jobs[job.CurrentJobId] = job;
        AddPendingAlert(job.CurrentJobId);
        RefreshManagerList(state.ClientId, job.CurrentJobId);
        ShowNextRequestAlert();
        _ = SendJobStateAsync(state, job.CurrentJobId, job.CurrentJobText, false, default);
        BroadcastJobInfo(job);
    }

    private async Task SendJobStateAsync(ManagerState state, string jobId, string text, bool accepted, DateTime startedAt)
    {
        try { if (state.Connection is not null) await state.Connection.SendAsync(AppMessage.JobState(jobId, text, accepted, startedAt)); }
        catch { }
    }

    private async Task SendCompletedAgainAsync(ManagerState state, MasterJobRecord job)
    {
        try
        {
            if (state.Connection is not null)
                await state.Connection.SendAsync(AppMessage.JobCompleted(job.CurrentJobId, job.CurrentJobText,
                    job.StartedAt, job.CompletedAt == default ? DateTime.Now : job.CompletedAt, job.Comment));
        }
        catch { }
    }

    private void ReceiveManagerChat(ManagerState state, AppMessage message)
    {
        message.Sender = state.UserName;
        if (message.JobId == GeneralChatId)
        {
            ReceiveChatForSession(state.GeneralSessionKey, "Общий чат — " + state.UserName, message);
        }
        else if (state.Jobs.TryGetValue(message.JobId, out var job) && job.IsJobAccepted && !job.AwaitingReport && !job.ReportSaved)
        {
            ReceiveChatForSession(state.JobSessionKey(job.CurrentJobId),
                $"Чат по заявке — {state.UserName}: {job.CurrentJobText}", message);
        }
    }

    private void ReceiveManagerFileOffer(ManagerState state, AppMessage message)
    {
        message.Sender = state.UserName;
        if (message.JobId == GeneralChatId)
        {
            ReceiveFileOfferForSession(state.GeneralSessionKey, "Общий чат — " + state.UserName, message);
        }
        else if (state.Jobs.TryGetValue(message.JobId, out var job) && job.IsJobAccepted && !job.AwaitingReport && !job.ReportSaved)
        {
            ReceiveFileOfferForSession(state.JobSessionKey(job.CurrentJobId),
                $"Чат по заявке — {state.UserName}: {job.CurrentJobText}", message);
        }
    }

    private void CompleteJobFromManager(ManagerState state, AppMessage message)
    {
        if (_deletedJobs.Contains(message.JobId))
        {
            // Заявку уже удалили — гасим устаревшую копию у менеджера
            if (state.Connection is not null)
                _ = SendSafeDirectAsync(state.Connection, AppMessage.JobForceRemoved(message.JobId, "Мастер"));
            return;
        }
        if (!state.Jobs.TryGetValue(message.JobId, out var job))
        {
            job = new MasterJobRecord
            {
                ClientId = state.ClientId,
                UserName = state.UserName,
                MachineName = state.MachineName,
                CurrentJobId = message.JobId,
                CurrentJobText = message.Text,
                ReceivedAt = message.StartedAt?.ToLocalTime() ?? DateTime.Now
            };
            state.Jobs[job.CurrentJobId] = job;
        }
        job.StartedAt = message.StartedAt?.ToLocalTime() ?? job.StartedAt;
        job.CompletedAt = message.CompletedAt?.ToLocalTime() ?? DateTime.Now;
        job.IsJobAccepted = false;
        job.AwaitingReport = false;
        job.ReportSaved = true;
        job.Comment = message.Comment;
        job.StatusMessage = "Заявку завершил менеджер. Отчёт сохранён у " + state.UserName + ".";
        job.StatusSuccess = true;
        _pendingAlerts.Remove(job.CurrentJobId);
        _postponed.Remove(job.CurrentJobId);
        CloseChatSession(state.JobSessionKey(job.CurrentJobId));
        RefreshManagerList(state.ClientId, job.CurrentJobId);
        WindowsNotifications.Show("Заявка завершена менеджером",
            $"{state.UserName}: {ShortText(job.CurrentJobText)}");
        BroadcastJobInfo(job);
    }

    /// <summary>Менеджер убрал завершённую заявку в свой локальный архив.
    /// На сервере запись остаётся: общий список хранит все заявки за всё время.
    /// Если же так менеджер отменил ещё не принятую заявку — это удаление для всех.</summary>
    private void ArchiveCompletedJob(ManagerState state, AppMessage message)
    {
        if (!state.Jobs.TryGetValue(message.JobId, out var job)) return;
        if (!job.ReportSaved && !job.AwaitingReport && !job.IsJobAccepted)
        {
            AppLog.Info($"Менеджер {state.UserName} отменил ожидавшую заявку: {job.CurrentJobId} · {ShortText(job.CurrentJobText)}");
            DeleteJobForEveryone(state, job.CurrentJobId, state.UserName);
            return;
        }
        _pendingAlerts.Remove(message.JobId);
        _postponed.Remove(message.JobId);
        AppLog.Info($"Менеджер {state.UserName} убрал заявку в свой архив: {job.CurrentJobId} · {ShortText(job.CurrentJobText)}");
        RefreshManagerList(state.ClientId, job.CurrentJobId);
    }

    private void MarkReportSaved(ManagerState state, string jobId)
    {
        if (!state.Jobs.TryGetValue(jobId, out var job)) return;
        job.AwaitingReport = false;
        job.ReportSaved = true;
        job.IsJobAccepted = false;
        job.StatusMessage = "Ремонт завершён. Отчёт сохранён у " + state.UserName + ".";
        job.StatusSuccess = true;
        _pendingAlerts.Remove(jobId);
        RefreshManagerList(state.ClientId, jobId);
        WindowsNotifications.Show("Отчёт сохранён", $"{state.UserName}: отчёт по ремонту создан.");
        BroadcastJobInfo(job);
    }

    /// <summary>Менеджер удалил заявку — своих или чужих: удаляем у всех и запоминаем номер,
    /// чтобы заявка не воскресла при переподключении любого менеджера.</summary>
    private void ForceRemoveByManager(ManagerState state, string jobId)
    {
        if (!TryFindJob(jobId, out var ownerState, out var job))
        {
            // Заявки уже нет — просто запоминаем номер, чтобы она не вернулась с переподключением
            _deletedJobs.Add(jobId);
            DeletedJobsStore.Save(_deletedJobs);
            return;
        }
        AppLog.Info($"Заявку удалил {state.UserName}: {jobId} · {ShortText(job.CurrentJobText)}");
        DeleteJobForEveryone(ownerState, jobId, state.UserName);
        WindowsNotifications.Show("Заявка удалена",
            $"{state.UserName} удалил(а): {ShortText(job.CurrentJobText)}");
    }

    /// <summary>Правка полей заявки от менеджера (2.35): править может любой менеджер —
    /// мастер обновляет свою запись и рассылает новую карточку всем пользователям.</summary>
    private void HandleJobEdit(ManagerState editor, AppMessage message)
    {
        if (message.JobId.Length == 0) return;
        if (_deletedJobs.Contains(message.JobId))
        {
            // Заявку уже удалили — гасим устаревшую копию у редактора
            if (editor.Connection is not null)
                _ = SendSafeDirectAsync(editor.Connection, AppMessage.JobForceRemoved(message.JobId, "Мастер"));
            return;
        }
        if (!TryFindJob(message.JobId, out _, out var job)) return;
        if (!ApplyJobDescription(job, message)) return;
        var editorName = editor.UserName.Length > 0 ? editor.UserName : "Менеджер";
        AppLog.Info($"Заявку изменил {editorName}: {job.CurrentJobId} · {ShortText(job.CurrentJobText)}");
        WindowsNotifications.Show("Заявка изменена", $"{editorName}: {ShortText(job.CurrentJobText)}");
        RefreshJobsView();
        BroadcastJobInfo(job, editorName);
    }

    /// <summary>Применить правку к записи мастера: только описание и реквизиты,
    /// статус и даты не трогаем. Пустой текст существующий не затирает.</summary>
    private static bool ApplyJobDescription(MasterJobRecord job, AppMessage message)
    {
        var changed = false;
        if (message.Text.Trim().Length > 0 && job.CurrentJobText != message.Text) { job.CurrentJobText = message.Text; changed = true; }
        if (job.Client != message.Client) { job.Client = message.Client; changed = true; }
        if (job.Device != message.Device) { job.Device = message.Device; changed = true; }
        if (job.Serial != message.Serial) { job.Serial = message.Serial; changed = true; }
        if (job.Urgent != message.Urgent) { job.Urgent = message.Urgent; changed = true; }
        return changed;
    }

    /// <summary>Панель правки заявки мастером: описание и реквизиты. Изменения рассылаются всем (2.35).</summary>
    private void ShowJobEditSheet(ManagerState state, MasterJobRecord job)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Изменить заявку",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(Ui.Note("Изменения увидят все: вы и все менеджеры (у отключённых — при первом подключении). Статус заявки и уже сохранённые отчёты не меняются."));
        panel.Children.Add(Ui.Note("Описание заявки:"));
        var text = new TextBox
        {
            Text = job.CurrentJobText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            MaxLength = 2000,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 8)
        };
        panel.Children.Add(text);
        panel.Children.Add(Ui.Note("Клиент:"));
        var client = new TextBox { Text = job.Client, MaxLength = 200, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(client);
        panel.Children.Add(Ui.Note("Устройство:"));
        var device = new TextBox { Text = job.Device, MaxLength = 200, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(device);
        panel.Children.Add(Ui.Note("Серийный номер / IMEI:"));
        var serial = new TextBox { Text = job.Serial, MaxLength = 200, Margin = new Thickness(0, 2, 0, 8) };
        panel.Children.Add(serial);
        var urgent = new CheckBox
        {
            Content = "Срочная заявка",
            IsChecked = job.Urgent,
            Foreground = Ui.Muted,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(urgent);
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
        save.Click += (_, _) =>
        {
            var newText = text.Text.Trim();
            if (newText.Length == 0)
            {
                MessageBox.Show(this, "Описание заявки не может быть пустым.", "Изменить заявку",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var edit = AppMessage.JobEdit(job.CurrentJobId, newText, client.Text.Trim(),
                device.Text.Trim(), serial.Text.Trim(), urgent.IsChecked == true, "Мастер");
            CloseSheet();
            if (!ApplyJobDescription(job, edit)) return;
            AppLog.Info($"Заявку изменил мастер: {job.CurrentJobId} · {ShortText(job.CurrentJobText)}");
            RefreshJobsView();
            BroadcastJobInfo(job, "Мастер");
        };
        ShowSheet(panel, 460);
        text.Focus();
    }

    /// <summary>Мастер удаляет заявку любого состояния — у всех пользователей сразу.</summary>
    private void DeleteJobByMaster(ManagerState state, MasterJobRecord job)
    {
        var warning = job.ReportSaved
            ? "Заявка завершена, отчёт уже сохранён в общей папке менеджера и останется там."
            : job.AwaitingReport
                ? "Отчёт по этой заявке ещё не сохранён в общей папке — при удалении отчёт создан НЕ будет."
                : job.IsJobAccepted
                    ? "Ремонт в работе! Статус и время работы будут потеряны."
                    : "Заявка ещё не принята в работу — менеджер не получит по ней ответ.";
        if (MessageBox.Show(this,
            warning + "\n\nЗаявка «" + ShortText(job.CurrentJobText) + "» исчезнет из списков у всех пользователей — у вас и у всех менеджеров. Восстановить её будет нельзя. Удалить?",
            "Удалить заявку", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        AppLog.Info($"Заявку удалил мастер: {job.CurrentJobId} · {ShortText(job.CurrentJobText)}");
        DeleteJobForEveryone(state, job.CurrentJobId, "Мастер");
        WindowsNotifications.Show("Заявка удалена", ShortText(job.CurrentJobText));
    }

    private void RemoveJobEverywhere(ManagerState state, string jobId)
    {
        state.Jobs.Remove(jobId);
        _pendingAlerts.Remove(jobId);
        _postponed.Remove(jobId);
        CloseChatSession(state.JobSessionKey(jobId));
        if (_activeJobId == jobId) _activeJobId = "";
        RefreshManagerList(state.ClientId);
    }

    private void RefreshManagerList(string? selectClientId = null, string? selectJobId = null)
    {
        if (selectClientId is not null && _managers.ContainsKey(selectClientId))
            _activeManagerId = selectClientId;
        if (selectJobId is not null && SelectedManager?.Jobs.ContainsKey(selectJobId) == true)
            _activeJobId = selectJobId;
        if (SelectedManager is null)
        {
            var state = _managers.Values
                .OrderByDescending(m => m.Jobs.Values.Any(job => !job.ReportSaved))
                .ThenByDescending(m => m.Connection is not null)
                .ThenBy(m => m.UserName)
                .FirstOrDefault();
            _activeManagerId = state?.ClientId ?? "";
            _activeJobId = "";
        }
        if (SelectedJob is null && SelectedManager is not null)
        {
            _activeJobId = VisibleJobs().FirstOrDefault(item => item.Manager.ClientId == _activeManagerId)
                .Job?.CurrentJobId ?? "";
        }
        RefreshJobsView();
        PersistManagerJobs();
    }

    private void SelectJob(string clientId, string jobId)
    {
        if (!_managers.TryGetValue(clientId, out var state) || !state.Jobs.ContainsKey(jobId)) return;
        _activeManagerId = clientId;
        _activeJobId = jobId;
        RefreshJobsView();
        if (_chatTabs.SelectedIndex == 0) BindJobChat();
    }

    private void RefreshJobsView(bool persist = true)
    {
        var jobs = VisibleJobs();
        _jobsList.Children.Clear();
        if (jobs.Count == 0)
        {
            _jobsEmpty.Text = _jobsTabIndex == 0
                ? "Активных заявок нет.\nНовые появятся здесь со звуком и уведомлением."
                : "Завершённых заявок пока нет.";
            _jobsList.Children.Add(_jobsEmpty);
        }
        foreach (var item in jobs) _jobsList.Children.Add(BuildJobRow(item.Manager, item.Job));
        RefreshDetail();
        UpdateStatusBar();
        if (persist) PersistManagerJobs();
    }

    private FrameworkElement BuildJobRow(ManagerState state, MasterJobRecord job)
    {
        var selected = job.CurrentJobId == _activeJobId && state.ClientId == _activeManagerId;
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
        grid.Children.Add(Ui.StatusDot(statusColor, 8));
        var texts = new StackPanel
        {
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        texts.Children.Add(new TextBlock
        {
            Text = ShortText(job.CurrentJobText),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = selected ? Ui.Accent : Ui.Ink,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var online = state.Connection is not null;
        var statusLine = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var baseLine = $"{state.UserName}{(online ? "" : " · не в сети")} · {JobTimeLine(job)}";
        if (job.Urgent)
        {
            statusLine.Inlines.Add(new System.Windows.Documents.Run(baseLine) { Foreground = Ui.Muted });
            statusLine.Inlines.Add(new System.Windows.Documents.Run("  ·  СРОЧНО")
            {
                Foreground = Ui.Danger,
                FontWeight = FontWeights.SemiBold
            });
        }
        else
        {
            statusLine.Text = baseLine;
            statusLine.Foreground = Ui.Muted;
        }
        texts.Children.Add(statusLine);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        card.Child = grid;
        var clientId = state.ClientId;
        var jobId = job.CurrentJobId;
        card.MouseEnter += (_, _) =>
        {
            if (jobId != _activeJobId || clientId != _activeManagerId) card.Background = Ui.FillSubtle;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = jobId == _activeJobId && clientId == _activeManagerId
                ? Ui.AccentSoft : Brushes.Transparent;
        };
        card.MouseLeftButtonDown += (_, _) => SelectJob(clientId, jobId);
        return card;
    }

    private static (string, Brush) JobBadge(MasterJobRecord job)
    {
        if (job.ReportSaved) return ("Завершён", Ui.Green);
        if (job.AwaitingReport) return ("Ожидает отчёт", Ui.Warning);
        if (job.IsJobAccepted) return ("В работе", Ui.Accent);
        return ("Ожидает", Ui.Warning);
    }

    private static string JobTimeLine(MasterJobRecord job)
    {
        if (job.ReportSaved)
            return "завершён " + (job.CompletedAt == default ? DateTime.Now : job.CompletedAt).ToString("dd.MM.yyyy HH:mm");
        if (job.AwaitingReport) return "отчёт сохраняется";
        if (job.IsJobAccepted)
            return $"в работе {JobTiming.Elapsed(job.StartedAt, DateTime.Now)}";
        return "создана " + job.ReceivedAt.ToString("dd.MM.yyyy HH:mm");
    }

    private void RefreshDetail()
    {
        var state = SelectedManager;
        var job = SelectedJob;
        if (state is null)
        {
            // Карточка с чатом видна всегда, как у менеджера: вкладки и поле ввода на месте,
            // а внутри — подсказка, что чат ждёт подключения менеджера
            if (_detailHost.Children.Count == 0 || !ReferenceEquals(_detailHost.Children[0], _detailCard))
            {
                _detailHost.Children.Clear();
                _detailHost.Children.Add(_detailCard);
            }
            _detailCard.Visibility = Visibility.Visible;
            _cardTop.Visibility = Visibility.Collapsed;
            _chatPanel.IsEnabled = true;
            if (_chatTabs.SelectedIndex == 0)
            {
                // по аналогии с менеджером: без собеседника открываем вкладку общего чата
                _chatTabs.Select(1, false);
            }
            OnChatTabChanged(_chatTabs.SelectedIndex);
            return;
        }
        // Карточка с чатом видна всегда, пока есть менеджер: общий чат и файлы доступны и без заявок
        if (_detailHost.Children.Count == 0 || !ReferenceEquals(_detailHost.Children[0], _detailCard))
        {
            _detailHost.Children.Clear();
            _detailHost.Children.Add(_detailCard);
        }
        _detailCard.Visibility = Visibility.Visible;
        if (job is null)
        {
            _cardTop.Visibility = Visibility.Collapsed;
            _chatPanel.IsEnabled = true;
            if (_chatTabs.SelectedIndex == 0)
            {
                // чата по заявке без заявки нет — показываем общий чат с менеджером
                _chatTabs.Select(1, false);
                ShowChatContent(1);
            }
            // если чат ещё не привязан (менеджер только что подключился) — привязываем
            // к автору выбранной заявки или к выбранному в переключателе менеджеру
            if (_chatTabs.SelectedIndex == 1) OpenGeneralChatForCurrentPeer();
            return;
        }
        _cardTop.Visibility = Visibility.Visible;
        // если чат отвязан (заявки не было) — привязываем к выбранной заявке;
        // если уже привязан — просто обновляем доступность отправки по статусу заявки
        if (_chatTabs.SelectedIndex == 0)
        {
            if (_chatPanel.SessionId != state.JobSessionKey(job.CurrentJobId)) BindJobChat();
            else _chatPanel.SetSendEnabled(job.IsJobAccepted);
        }
        // (2.38) режима «общий чат следует за выбранной заявкой» больше нет:
        // собеседник общего чата выбирается только переключателем менеджеров
        var fullText = job.CurrentJobText;
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
        _detailManager.Text = $"{state.UserName} · {state.MachineName} · {(state.Connection is null ? "не в сети — данные сохранены" : "в сети")}";
        _detailTimers.Text = DetailStatusLine(state, job);
        var rest = fullText.Length > title.Length
            ? fullText[title.Length..].TrimStart('\r', '\n', ' ')
            : "";
        _detailDescription.Text = rest;
        _detailDescription.Visibility = rest.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var fieldsLine = ClientForm.JobFieldsLine(job.Client, job.Device, job.Serial);
        _detailFields.Text = fieldsLine;
        _detailFields.Visibility = fieldsLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _detailComment.Text = job.Comment.Length > 0 ? "Что сделано: " + job.Comment : "";
        _detailComment.Visibility = job.Comment.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _detailActions.Children.Clear();
        var waiting = !job.IsJobAccepted && !job.AwaitingReport && !job.ReportSaved;
        if (waiting)
        {
            var accept = Ui.ActionButton("Принять", "SuccessButton");
            accept.MinWidth = 150;
            accept.Click += (_, _) => AcceptJob(state.ClientId, job.CurrentJobId);
            _detailActions.Children.Add(accept);
            var show = Ui.ActionButton("Показать поверх окон", "WhiteButton");
            show.MinWidth = 170;
            show.Margin = new Thickness(8, 0, 0, 0);
            show.Click += (_, _) => ShowRequestAlertFor(state.ClientId, job.CurrentJobId);
            _detailActions.Children.Add(show);
            var postpone = Ui.ActionButton("Отложить ▾", "WhiteButton");
            postpone.MinWidth = 140;
            postpone.Margin = new Thickness(8, 0, 0, 0);
            postpone.ToolTip = "Выбрать, через сколько напомнить о заявке снова";
            var postponeJobId = job.CurrentJobId;
            postpone.Click += (_, _) =>
            {
                var menu = new ContextMenu();
                foreach (var (label, mins) in new[] { ("5 минут", 5), ("15 минут", 15), ("30 минут", 30), ("1 час", 60) })
                {
                    var item = new MenuItem { Header = label };
                    var chosen = mins;
                    item.Click += (_, _) =>
                    {
                        PostponeJob(postponeJobId, chosen);
                        if (_requestAlert is not null && !_requestAlert.IsDisposed) _requestAlert.Close();
                    };
                    menu.Items.Add(item);
                }
                menu.PlacementTarget = postpone;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            _detailActions.Children.Add(postpone);
            if (_postponed.TryGetValue(job.CurrentJobId, out var until) && until > DateTime.Now)
            {
                _detailActions.Children.Add(new TextBlock
                {
                    Text = "напомню в " + until.ToString("HH:mm"),
                    Foreground = Ui.Muted,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                });
            }
        }
        else if (job.IsJobAccepted)
        {
            var complete = Ui.ActionButton("Завершить", "SuccessButton");
            complete.MinWidth = 150;
            complete.IsEnabled = state.Connection is not null;
            complete.Click += (_, _) =>
            {
                var sessionKey = state.JobSessionKey(job.CurrentJobId);
                if (HasPendingMessages(sessionKey))
                {
                    MessageBox.Show(this, "По этой заявке ещё есть сообщения в очереди. Дождитесь отметки «Доставлено», затем завершите ремонт.",
                        "Переписка ещё доставляется", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                ShowCompleteSheet(state, job);
            };
            _detailActions.Children.Add(complete);
            if (state.Connection is null)
            {
                var note = new TextBlock
                {
                    Text = "Менеджер не в сети — завершить можно после его подключения.",
                    Foreground = Ui.Muted,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                _detailActions.Children.Add(note);
            }
        }
        else if (job.AwaitingReport)
        {
            var note = Ui.ActionButton("Отчёт сохраняется…", "WhiteButton");
            note.MinWidth = 190;
            note.IsEnabled = false;
            _detailActions.Children.Add(note);
        }
        // Правка доступна для заявки любого состояния — изменения увидят все пользователи (2.35)
        var edit = Ui.ActionButton("Изменить", "WhiteButton");
        edit.MinWidth = 120;
        edit.Margin = new Thickness(8, 0, 0, 0); // тот же зазор, что у соседних кнопок (2.39)
        edit.ToolTip = "Изменить описание и реквизиты заявки — обновится у всех пользователей";
        var editState = state;
        var editJob = job;
        edit.Click += (_, _) => ShowJobEditSheet(editState, editJob);
        _detailActions.Children.Add(edit);
        // Удаление доступно для заявки любого состояния — действует сразу у всех пользователей
        var delete = Ui.ActionButton("Удалить заявку", "DangerButton");
        delete.MinWidth = 150;
        delete.Margin = new Thickness(8, 0, 0, 0);
        delete.ToolTip = "Удалить заявку у всех пользователей — у вас и у всех менеджеров. Действие нельзя отменить";
        var deleteState = state;
        var deleteJob = job;
        delete.Click += (_, _) => DeleteJobByMaster(deleteState, deleteJob);
        _detailActions.Children.Add(delete);
        // Панель чата остаётся активной всегда: общий чат, чтение, выделение
        // и копирование сообщений доступны и после завершения заявки.
        // Блокируется только отправка в чат неактивной заявки (см. BindJobChat)
    }

    private string DetailStatusLine(ManagerState state, MasterJobRecord job)
    {
        if (job.ReportSaved) return job.StatusMessage;
        if (job.AwaitingReport) return "Ремонт завершён. Ждём подтверждение сохранения отчёта у менеджера…";
        if (job.IsJobAccepted && job.StartedAt != default)
        {
            var delivery = job.StatusMessage.StartsWith("Принято, но", StringComparison.Ordinal)
                ? job.StatusMessage
                : state.Connection is null
                    ? "Менеджер не в сети. Статус и время сохранены; подтверждение будет отправлено после подключения."
                    : "Менеджер уведомлён. Доступны чат, файлы и завершение ремонта.";
            return $"Принято в {job.StartedAt:dd.MM.yyyy HH:mm} · в работе {JobTiming.Elapsed(job.StartedAt, DateTime.Now)}\n{delivery}";
        }
        return job.StatusMessage.Length > 0 ? job.StatusMessage : "Заявка ожидает обработки.";
    }

    private void UpdateStatusBar()
    {
        var online = _managers.Values.Count(m => m.Connection is not null);
        var known = _managers.Count;
        _statusDot.Background = online > 0 ? Ui.Green : Ui.Muted;
        // Видно всех известных менеджеров и сколько из них сейчас на связи (2.35)
        _statusText.Text = known == 0
            ? "Сервер запущен · ожидание менеджеров"
            : $"Сервер запущен · менеджеров в сети: {online} из {known}";
        var waiting = _managers.Values.SelectMany(m => m.Jobs.Values)
            .Count(job => !job.IsJobAccepted && !job.AwaitingReport && !job.ReportSaved);
        var working = _managers.Values.SelectMany(m => m.Jobs.Values).Count(job => job.IsJobAccepted);
        var parts = new List<string>();
        if (waiting > 0) parts.Add("ожидают: " + waiting);
        if (working > 0) parts.Add("в работе: " + working);
        _queueText.Text = string.Join(" · ", parts);
        UpdateTrayStatus($"менеджеров: {online}" + (parts.Count > 0 ? " · " + string.Join(" · ", parts) : ""));
        RefreshChatPeerPicker();
    }

    /// <summary>Переключатель общего чата: в списке только менеджеры в сети (2.38).
    /// Один менеджер в сети — показывается сразу, зелёным; несколько — выбираются
    /// из раскрывающегося списка; никого в сети — поле неактивно, раскрыть его нельзя,
    /// и оно показывает серую подсказку «Менеджер не в сети». Список перестраивается
    /// только при реальном изменении состава, чтобы не сбрасывать выбор.</summary>
    private void RefreshChatPeerPicker()
    {
        var previousPeerId = _chatPeerId;
        var online = _managers.Values
            .Where(state => state.Connection is not null)
            .OrderBy(state => state.UserName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(state => state.ClientId, StringComparer.Ordinal)
            .ToList();
        var items = online.Count == 0
            ? new List<ChatPeerItem> { new("", "Менеджер не в сети") }
            : online.Select(state => new ChatPeerItem(state.ClientId,
                (state.UserName.Length > 0 ? state.UserName : "Менеджер") +
                " · " + (state.MachineName.Length > 0 ? state.MachineName : "ПК"))).ToList();
        var signature = string.Join("\n", items.Select(item => item.Id + "=" + item.Label));
        _chatPeerSync = true;
        try
        {
            if (signature != _chatPeerSignature)
            {
                _chatPeerSignature = signature;
                _chatPeerPicker.ItemsSource = items;
            }
            if (online.Count == 0)
            {
                // Никого в сети: поле заблокировано и показывает серую подсказку
                _chatPeerId = "";
                _chatPeerPicker.IsEnabled = false;
                _chatPeerPicker.Foreground = Ui.Muted;
                _chatPeerPicker.SelectedIndex = 0;
            }
            else
            {
                _chatPeerPicker.IsEnabled = true;
                // имя выбранного менеджера — зелёным, в общем стиле программы
                _chatPeerPicker.Foreground = Ui.Green;
                // выбранный менеджер мог выйти из сети — показываем первого из доступных;
                // при единственном менеджере в сети он отображается сразу
                if (_chatPeerId.Length == 0 || items.All(item => item.Id != _chatPeerId))
                    _chatPeerId = items[0].Id;
                _chatPeerPicker.SelectedValue = _chatPeerId;
            }
        }
        finally { _chatPeerSync = false; }
        // собеседник сменился сам (вышел из сети и т.п.) — переключаем открытый общий чат
        if (_chatPeerId != previousPeerId && _chatTabs.SelectedIndex == 1)
            OpenGeneralChatForCurrentPeer();
    }

    private void OnChatPeerPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_chatPeerSync) return;
        _chatPeerId = _chatPeerPicker.SelectedValue as string ?? "";
        if (_chatTabs.SelectedIndex == 1) OpenGeneralChatForCurrentPeer();
    }

    /// <summary>С кем сейчас общий чат: с выбранным в переключателе менеджером,
    /// если он ещё в сети; иначе — с первым менеджером в сети. Никого в сети — null (2.38).</summary>
    private ManagerState? ResolveChatPeerState()
    {
        if (_chatPeerId.Length > 0 &&
            _managers.TryGetValue(_chatPeerId, out var pinned) &&
            pinned.Connection is not null)
            return pinned;
        return _managers.Values
            .Where(state => state.Connection is not null)
            .OrderBy(state => state.UserName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(state => state.ClientId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Открыть общий чат с текущим собеседником (выбор переключателя).</summary>
    private void OpenGeneralChatForCurrentPeer()
    {
        var peer = ResolveChatPeerState();
        if (peer is null)
        {
            _chatPanel.Unbind();
            _chatPanel.SetEmptyHint("Менеджеры ещё не подключились — общий чат появится здесь. Адрес этого компьютера и код сопряжения: «Настройки».");
            _chatPanel.SetSendEnabled(false);
            return;
        }
        if (_chatPanel.SessionId != peer.GeneralSessionKey)
            OpenChatForSession(peer.GeneralSessionKey, "Общий чат — " + peer.UserName);
    }

    private void BindJobChat()
    {
        var state = SelectedManager;
        var job = SelectedJob;
        if (state is not null && job is not null)
        {
            OpenChatForSession(state.JobSessionKey(job.CurrentJobId),
                $"Чат по заявке — {state.UserName}: {job.CurrentJobText}");
            // Писать в чат заявки можно только по активному ремонту;
            // история при этом читается и копируется
            _chatPanel.SetSendEnabled(job.IsJobAccepted);
        }
        else
        {
            _chatPanel.Unbind();
            _chatPanel.SetEmptyHint("Заявки нет — чат по заявке появится, когда менеджер её пришлёт.");
            _chatPanel.SetSendEnabled(false);
        }
    }

    protected override void OnChatSessionShown(string sessionId)
    {
        if (TryGetSession(sessionId, out var state, out var jobId))
        {
            if (jobId == GeneralChatId)
            {
                // Показываем в переключателе того, чей чат открыли (например, из уведомления)
                _chatPeerId = state.ClientId;
                RefreshChatPeerPicker();
                _chatTabs.Select(1, false);
                ShowChatContent(1);
            }
            else
            {
                if (_activeManagerId != state.ClientId || _activeJobId != jobId)
                {
                    _activeManagerId = state.ClientId;
                    _activeJobId = jobId;
                    RefreshJobsView(false);
                }
                _chatTabs.Select(0, false);
                ShowChatContent(0);
            }
        }
    }

    private void OnChatTabChanged(int index)
    {
        ShowChatContent(index);
        if (index == 0) BindJobChat();
        else if (index == 1) OpenGeneralChatForCurrentPeer();
        else BuildFilesView();
    }

    private void ShowChatContent(int index)
    {
        _chatPanel.Visibility = index == 2 ? Visibility.Collapsed : Visibility.Visible;
        _filesView.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        _chatPeerRow.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 1) RefreshChatPeerPicker();
    }

    private void BuildFilesView()
    {
        _filesList.Children.Clear();
        var state = SelectedManager;
        var job = SelectedJob;
        var entries = new List<ChatEntry>();
        if (state is not null)
        {
            if (job is not null) entries.AddRange(GetHistoryForSession(state.JobSessionKey(job.CurrentJobId)));
            entries.AddRange(GetHistoryForSession(state.GeneralSessionKey));
        }
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

    private void AddPendingAlert(string jobId)
    {
        if (!_pendingAlerts.Contains(jobId)) _pendingAlerts.Add(jobId);
    }

    private bool TryFindJob(string jobId, out ManagerState state, out MasterJobRecord job)
    {
        foreach (var candidate in _managers.Values)
        {
            if (candidate.Jobs.TryGetValue(jobId, out job!))
            {
                state = candidate;
                return true;
            }
        }
        state = null!;
        job = null!;
        return false;
    }

    private void ShowNextRequestAlert()
    {
        if (_requestAlert is not null && !_requestAlert.IsDisposed) return;
        var now = DateTime.Now;
        var jobId = _pendingAlerts.FirstOrDefault(id =>
            TryFindJob(id, out _, out var job) && !job.IsJobAccepted && !job.AwaitingReport && !job.ReportSaved &&
            (!_postponed.TryGetValue(id, out var until) || until <= now));
        if (jobId is not null && TryFindJob(jobId, out var state, out _))
            ShowRequestAlertFor(state.ClientId, jobId);
    }

    private void ShowRequestAlertFor(string clientId, string jobId)
    {
        if ((_requestAlert is not null && !_requestAlert.IsDisposed) ||
            !_managers.TryGetValue(clientId, out var state) ||
            !state.Jobs.TryGetValue(jobId, out var job) ||
            job.IsJobAccepted || job.AwaitingReport || job.ReportSaved) return;
        SelectJob(clientId, jobId);
        var text = job.CurrentJobText + $"\n\nПользователь: {state.UserName}\nКомпьютер: {state.MachineName}";
        _requestAlert = new RequestAlertForm(text);
        _requestAlert.Accepted += () => AcceptJob(clientId, jobId, false);
        _requestAlert.Postponed += minutes => PostponeJob(jobId, minutes);
        _requestAlert.Muted += minutes => _requestAlerts.MuteFor(TimeSpan.FromMinutes(minutes));
        _requestAlert.FormClosed += (_, _) =>
        {
            _requestAlert = null;
            if (!_closing) Dispatcher.BeginInvoke(ShowNextRequestAlert);
        };
        _requestAlerts.Start(AlertKind.Request);
        _requestAlert.ShowAtCorner(this);
    }

    private void PostponeJob(string jobId, int minutes)
    {
        _postponed[jobId] = DateTime.Now.AddMinutes(minutes);
        _requestAlerts.Stop();
        RefreshDetail();
    }

    // ---- Мини-панель (список заявок из трея) ----

    protected override MiniForm? CreateMiniPanel() =>
        new MiniForm("Мастер", MiniStatus, BuildMiniBody, ShowMainFromMini,
            chatButton: ("Чат с менеджером", OpenManagerChatFromMini));

    /// <summary>Из мини-панели: полное окно с общим чатом выбранного (или первого) менеджера.</summary>
    private void OpenManagerChatFromMini()
    {
        ShowMainFromMini();
        var state = SelectedManager ?? _managers.Values.FirstOrDefault();
        if (state is null) return; // менеджеров пока нет — просто открыли полное окно
        _activeManagerId = state.ClientId;
        OpenChatForSession(state.GeneralSessionKey, "Общий чат — " + state.UserName);
    }

    private (string, Brush) MiniStatus()
    {
        var waiting = _managers.Values.SelectMany(state => state.Jobs.Values)
            .Count(job => !job.IsJobAccepted && !job.AwaitingReport && !job.ReportSaved);
        if (waiting > 0) return ("Ожидают принятия: " + waiting, Ui.Warning);
        var online = _managers.Values.Count(state => state.Connection is not null);
        return online > 0
            ? ("Менеджеров на связи: " + online, Ui.Green)
            : ("Сервер работает · менеджеров нет в сети", Ui.Muted);
    }

    private void BuildMiniBody(StackPanel body, StackPanel jobs)
    {
        var items = _managers.Values
            .SelectMany(state => state.Jobs.Values.Select(job => (Manager: state, Job: job)))
            .Where(item => !item.Job.ReportSaved)
            .OrderBy(item => item.Job.AwaitingReport ? 2 : item.Job.IsJobAccepted ? 1 : 0)
            .ThenByDescending(item => item.Job.IsJobAccepted ? item.Job.StartedAt : item.Job.ReceivedAt)
            .ThenBy(item => item.Manager.UserName)
            .ToList();
        if (items.Count == 0)
        {
            body.Children.Add(MiniForm.EmptyLabel("Активных заявок нет — новые появятся со звуком и уведомлением."));
            return;
        }
        body.Children.Add(MiniForm.SectionLabel("Заявки"));
        foreach (var (manager, job) in items)
        {
            var (badgeText, color) = JobBadge(job);
            var waiting = !job.IsJobAccepted && !job.AwaitingReport;
            var subtitle = manager.UserName + " · " + badgeText + (job.Urgent ? " · СРОЧНО" : "");
            var clientId = manager.ClientId;
            var jobId = job.CurrentJobId;
            Button? action = null;
            if (waiting)
            {
                var accept = Ui.ActionButton("Принять", "ChipButton");
                accept.Height = 28;
                accept.MinHeight = 28;
                accept.FontSize = 12;
                accept.Click += (_, _) =>
                {
                    AcceptJob(clientId, jobId, closeAlert: false);
                    MiniPanel?.RefreshBody();
                };
                action = accept;
            }
            jobs.Children.Add(MiniForm.JobRow(color, ShortText(job.CurrentJobText), subtitle, () =>
            {
                ShowMainFromMini();
                SelectJob(clientId, jobId);
            }, action));
        }
    }

    private void AcceptJob(string clientId, string jobId, bool closeAlert = true)
    {
        if (!_managers.TryGetValue(clientId, out var state) ||
            !state.Jobs.TryGetValue(jobId, out var job) ||
            job.IsJobAccepted || job.AwaitingReport || job.ReportSaved) return;
        _requestAlerts.Stop();
        _pendingAlerts.Remove(jobId);
        _postponed.Remove(jobId);
        job.IsJobAccepted = true;
        job.AwaitingReport = false;
        job.ReportSaved = false;
        job.StartedAt = DateTime.Now;
        job.CompletedAt = default;
        job.StatusMessage = "Работа принята. Доступны чат, файлы и завершение ремонта.";
        job.StatusSuccess = true;
        RefreshManagerList(clientId, jobId);
        if (closeAlert && _requestAlert is not null && !_requestAlert.IsDisposed)
            _requestAlert.Close();
        else
            ShowNextRequestAlert();
        _ = SendAcceptedAsync(state, job.CurrentJobId, job.StartedAt);
        BroadcastJobInfo(job);
    }

    private async Task SendAcceptedAsync(ManagerState state, string jobId, DateTime startedAt)
    {
        try { if (state.Connection is not null) await state.Connection.SendAsync(AppMessage.JobAccepted(jobId, startedAt)); }
        catch (Exception)
        {
            if (state.Jobs.TryGetValue(jobId, out var job))
            {
                job.StatusMessage = "Принято, но подтверждение менеджеру не отправлено.";
                RefreshDetail();
            }
        }
    }

    private async Task CompleteJobAsync(ManagerState state, MasterJobRecord job, string comment)
    {
        if (!job.IsJobAccepted || state.Connection is null) return;
        try
        {
            var completedAt = DateTime.Now;
            await state.Connection.SendAsync(AppMessage.JobCompleted(job.CurrentJobId, job.CurrentJobText,
                job.StartedAt, completedAt, comment));
            job.IsJobAccepted = false;
            job.AwaitingReport = true;
            job.ReportSaved = false;
            job.CompletedAt = completedAt;
            job.Comment = comment;
            job.StatusMessage = "Ремонт завершён. Ждём подтверждение сохранения отчёта…";
            job.StatusSuccess = false;
            CloseChatSession(state.JobSessionKey(job.CurrentJobId));
            RefreshManagerList(state.ClientId, job.CurrentJobId);
            BroadcastJobInfo(job);
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось отправить завершение: " + ex.Message); }
    }

    private static string ShortText(string text)
    {
        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length > 68 ? singleLine[..68] + "…" : singleLine;
    }

    private bool TryGetSession(string sessionId, out ManagerState state, out string jobId)
    {
        state = null!;
        jobId = "";
        var split = sessionId.IndexOf(':');
        if (split <= 0) return false;
        var clientId = sessionId[..split];
        jobId = sessionId[(split + 1)..];
        return _managers.TryGetValue(clientId, out state!);
    }

    protected override async Task SendSessionMessageAsync(string sessionId, AppMessage message)
    {
        if (!TryGetSession(sessionId, out var state, out var jobId) || state.Connection is null)
            throw new InvalidOperationException("Менеджер не подключён.");
        message.JobId = jobId;
        message.Sender = "Мастер";
        await state.Connection.SendAsync(message);
    }

    protected override async Task SendSessionAckAsync(string sessionId, string messageId)
    {
        if (!TryGetSession(sessionId, out var state, out var jobId) || state.Connection is null)
            throw new InvalidOperationException("Менеджер не подключён.");
        await state.Connection.SendAsync(AppMessage.Ack(jobId, messageId));
    }

    protected override void SendReadReceipt(string sessionId, IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0) return;
        if (!TryGetSession(sessionId, out var state, out var jobId) || state.Connection is null) return;
        _ = SendSafeDirectAsync(state.Connection, AppMessage.ReadReceipt(jobId, messageIds));
    }

    private void HandleJobReminder(ManagerState state, AppMessage message)
    {
        if (!state.Jobs.TryGetValue(message.JobId, out var job) || job.IsJobAccepted || job.ReportSaved)
            return;
        AppLog.Info($"Напоминание по заявке {message.JobId} от {state.UserName}");
        if (_postponed.TryGetValue(message.JobId, out var until) && until > DateTime.Now)
            return;
        RefreshJobsView();
        ShowRequestAlertFor(state.ClientId, message.JobId);
    }

    protected override async Task SendSessionChunkAsync(string sessionId, FileChunk chunk)
    {
        if (!TryGetSession(sessionId, out var state, out _) || state.Connection is null)
            throw new InvalidOperationException("Менеджер не подключён.");
        await state.Connection.SendChunkAsync(chunk.MessageId, chunk.Offset, chunk.Data, chunk.Data.Length);
    }

    /// <summary>Обновляет блок «Подключение» в настройках: код, отсчёт и сообщения режима добавления.</summary>
    private void UpdatePairingView()
    {
        if (_pairCodeText is null || _pairHintText is null) return;
        if (_pairing.LockedOut)
        {
            _pairCodeText.Text = "";
            _pairHintText.Text = PairingSession.LockoutMessage;
            return;
        }
        if (_pairing.IsActive)
        {
            var left = _pairing.TimeLeft;
            _pairCodeText.Text = "Код сопряжения: " + _pairing.CurrentCode;
            _pairHintText.Text = $"Код одноразовый. Осталось {left.Minutes}:{left.Seconds:00} — " +
                "после сопряжения или истечения времени он погаснет.";
            return;
        }
        _pairCodeText.Text = "";
        _pairHintText.Text = "Нажмите «Добавить компьютер», чтобы показать одноразовый код на 5 минут. " +
            "Пока кода нет, новые устройства подключиться не могут.";
    }

    private void RefreshDevicesList()
    {
        _devicesList.Children.Clear();
        var devices = TrustedDevicesStore.All();
        if (devices.Count == 0)
        {
            _devicesList.Children.Add(Ui.Note("Сопряжённых устройств пока нет."));
            return;
        }
        foreach (var device in devices)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = $"{device.UserName} · {device.MachineName}\nс {device.PairedAt:dd.MM.yyyy}",
                Foreground = Ui.Muted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17,
                VerticalAlignment = VerticalAlignment.Center
            });
            var forget = Ui.ActionButton("Забыть", "QuietButton");
            forget.MinWidth = 80;
            forget.MinHeight = 30;
            forget.FontSize = 11;
            var clientId = device.ClientId;
            forget.Click += (_, _) =>
            {
                if (MessageBox.Show(this, "Устройство «" + device.UserName + " · " + device.MachineName +
                        "» больше не сможет подключаться без нового кода. Забыть его?",
                        "Забыть устройство", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                TrustedDevicesStore.Remove(clientId);
                if (_managers.TryGetValue(clientId, out var state)) state.Connection?.Disconnect();
                RefreshDevicesList();
            };
            Grid.SetColumn(forget, 1);
            row.Children.Add(forget);
            _devicesList.Children.Add(row);
        }
    }

    private void SafeUi(Action action)
    {
        if (_closing || IsDisposed) return;
        try { if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(action); else action(); } catch (InvalidOperationException) { }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => SafeUi(() =>
    {
        _networkRefreshTimer.Stop();
        _networkRefreshTimer.Start();
    });

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (TryHideToTray(e)) return;
        _closing = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _networkRefreshTimer.Stop();
        _jobElapsedTimer.Stop();
        _postponeTimer.Stop();
        PersistManagerJobs();
        try { _listener?.Stop(); } catch { }
        try { _discovery?.Dispose(); _discovery = null; } catch { }
        foreach (var connection in _allConnections.ToArray()) connection.Dispose();
        _requestAlert?.Close();
        _requestAlerts.Dispose();
        Alerts.Dispose();
        base.OnClosing(e);
    }
}
