using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RepairAlert;

internal sealed record ChatEntry(string Sender, string Text, DateTime Time, bool Own,
    string FileName = "", string FilePath = "", long FileSize = 0,
    string Id = "", string Delivery = "");

internal sealed class ChatPanel : UserControl
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _history;
    private readonly TextBox _input;
    private readonly Button _attachButton;
    private readonly Button _sendButton;
    private readonly TextBlock _queueStatus;
    private readonly Button _cancelFilesButton;
    private readonly Border _dropOverlay;
    private readonly Border _emptyHint;
    private readonly TextBlock _emptyHintText;
    private readonly Grid _root;
    private readonly Dictionary<string, TextBlock> _deliveryLabels = new();
    private readonly Dictionary<string, Border> _messageBubbles = new();
    private readonly Dictionary<string, TextBlock> _fileProgressLabels = new();
    private readonly Dictionary<string, ProgressBar> _fileProgressBars = new();
    private readonly Dictionary<string, StackPanel> _fileContentPanels = new();
    private readonly Dictionary<string, UIElement> _deleteMarks = new();
    private readonly HashSet<string> _thumbnailsShown = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ownAccentLabels = new(StringComparer.Ordinal);
    private readonly List<string> _unreadIncoming = new();
    private readonly Dictionary<string, TextBox> _textBodies = new();
    private readonly Dictionary<string, Border> _bubblesById = new();
    private readonly Border _pinnedBar;
    private readonly TextBlock _pinnedBarText;
    private readonly Border _editBar;
    private string _pinnedMessageId = "";
    private Border? _pinnedTarget;
    private string _editingMessageId = "";
    private string _sessionId = "";

    private const string DefaultEmptyHint = "Сообщений пока нет — напишите первым";

    public event Action<string, string>? MessageSend;
    public event Action<string, string>? FileSend;
    public event Action<string>? CancelFiles;
    public event Action<string, string>? MessageDeleteRequested;
    public event Action? Read;
    public event Action<IReadOnlyList<string>>? MessagesRead;
    /// <summary>Пользователь отредактировал своё сообщение: сессия, сообщение, новый текст.</summary>
    public event Action<string, string, string>? MessageEditRequested;
    /// <summary>Пользователь выбрал «Закрепить»/«Открепить»: сессия, сообщение.</summary>
    public event Action<string, string>? PinToggled;

    public string SessionId => _sessionId;

    private static readonly Brush OnAccentText = Frozen(Color.FromArgb(235, 255, 255, 255));
    private static readonly Brush OnAccentMuted = Frozen(Color.FromArgb(175, 255, 255, 255));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public ChatPanel()
    {
        // Фон панели = фону окна: отдельной серой плашки за полем ввода нет,
        // скруглённое поле с кнопками «плавает» на общем фоне, как в Telegram
        _root = new Grid { Background = Ui.Background, UseLayoutRounding = true, SnapsToDevicePixels = true };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 0: статус очереди
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 1: закреплённое сообщение
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: история
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 3: панель редактирования
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });              // 4: поле ввода
        Content = _root;

        // Полоса закреплённого сообщения (как в Telegram): клик прокручивает к нему
        _pinnedBar = new Border
        {
            Background = Ui.AccentSoft,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(22, 2, 14, 0),
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand
        };
        _pinnedBarText = new TextBlock
        {
            FontSize = 11,
            Foreground = Ui.Accent,
            TextWrapping = TextWrapping.Wrap
        };
        _pinnedBar.Child = _pinnedBarText;
        _pinnedBar.MouseLeftButtonDown += (_, _) => _pinnedTarget?.BringIntoView();
        Grid.SetRow(_pinnedBar, 1);
        _root.Children.Add(_pinnedBar);

        // Полоса режима редактирования: Enter — сохранить, Esc — отмена
        _editBar = new Border
        {
            Background = Ui.AccentSoft,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(22, 2, 14, 0),
            Visibility = Visibility.Collapsed
        };
        _editBar.Child = new TextBlock
        {
            Text = "Редактирование сообщения · Enter — сохранить, Esc — отмена",
            FontSize = 11,
            Foreground = Ui.Accent,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_editBar, 3);
        _root.Children.Add(_editBar);

        var queuePanel = new Grid { Margin = new Thickness(22, 8, 14, 4) };
        queuePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        queuePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _queueStatus = new TextBlock
        {
            Text = "● Все сообщения доставлены",
            Foreground = Ui.Green,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        queuePanel.Children.Add(_queueStatus);
        _cancelFilesButton = Ui.ActionButton("Остановить файлы", "DangerButton");
        _cancelFilesButton.Height = 28;
        _cancelFilesButton.MinHeight = 28;
        _cancelFilesButton.FontSize = 11;
        _cancelFilesButton.Padding = new Thickness(10, 0, 10, 0);
        _cancelFilesButton.Margin = new Thickness(8, 0, 0, 0);
        _cancelFilesButton.Visibility = Visibility.Collapsed;
        _cancelFilesButton.Click += (_, _) => CancelFiles?.Invoke(_sessionId);
        Grid.SetColumn(_cancelFilesButton, 1);
        queuePanel.Children.Add(_cancelFilesButton);
        Grid.SetRow(queuePanel, 0);
        _root.Children.Add(queuePanel);

        var historyBorder = new Border
        {
            Background = Ui.Background,
            Margin = new Thickness(10, 2, 10, 8)
        };
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12)
        };
        _history = new StackPanel();
        _scroll.Content = _history;
        historyBorder.Child = _scroll;
        Grid.SetRow(historyBorder, 2);
        _root.Children.Add(historyBorder);

        var composer = new Border
        {
            Background = Ui.ComposerFill,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(10, 4, 10, 10)
        };
        var composerGrid = new Grid();
        composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _attachButton = new Button
        {
            Width = 34,
            Height = 34,
            MinWidth = 34,
            MinHeight = 34,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Background = Ui.Accent,
            ToolTip = "Прикрепить файлы до 5 ГБ каждый"
        };
        System.Windows.Automation.AutomationProperties.SetName(_attachButton, "Прикрепить файлы");
        var attachTemplate = new ControlTemplate(typeof(Button));
        var attachChrome = new FrameworkElementFactory(typeof(Border));
        attachChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        attachChrome.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        var clipIcon = new FrameworkElementFactory(typeof(Border));
        clipIcon.SetValue(FrameworkElement.WidthProperty, 17.0);
        clipIcon.SetValue(FrameworkElement.HeightProperty, 17.0);
        clipIcon.SetValue(Border.BackgroundProperty, Brushes.White);
        clipIcon.SetValue(Border.OpacityMaskProperty, new ImageBrush(
            new BitmapImage(new Uri("pack://application:,,,/assets/paperclip.png", UriKind.Absolute)))
        { Stretch = Stretch.Uniform });
        clipIcon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        clipIcon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        attachChrome.AppendChild(clipIcon);
        attachTemplate.VisualTree = attachChrome;
        var attachHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        attachHover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
        attachTemplate.Triggers.Add(attachHover);
        _attachButton.Template = attachTemplate;
        _attachButton.Click += (_, _) => ChooseFiles();
        composerGrid.Children.Add(_attachButton);
        _input = new TextBox
        {
            MinHeight = 36,
            MaxHeight = 110,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // Курсор и текст по вертикальному центру поля, как в Telegram/Apple
            VerticalContentAlignment = VerticalAlignment.Center,
            // Свой узкий отступ: глобальный стиль TextBox даёт 12px и курсор «уезжает» вправо
            Padding = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(6, 1, 6, 1),
            FontSize = 14
        };
        System.Windows.Automation.AutomationProperties.SetName(_input, "Сообщение. Enter — отправить, Shift+Enter — новая строка");
        Grid.SetColumn(_input, 1);
        composerGrid.Children.Add(_input);
        _sendButton = new Button
        {
            Width = 34,
            Height = 34,
            MinHeight = 34,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Background = Ui.Accent,
            ToolTip = "Отправить (Enter)"
        };
        System.Windows.Automation.AutomationProperties.SetName(_sendButton, "Отправить");
        var sendTemplate = new ControlTemplate(typeof(Button));
        var sendChrome = new FrameworkElementFactory(typeof(Border));
        sendChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        sendChrome.SetValue(Border.BackgroundProperty,
            new TemplateBindingExtension(Control.BackgroundProperty));
        var sendArrow = new FrameworkElementFactory(typeof(TextBlock));
        sendArrow.SetValue(TextBlock.TextProperty, "↑");
        sendArrow.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        sendArrow.SetValue(TextBlock.FontSizeProperty, 16.0);
        sendArrow.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        sendArrow.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        sendArrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        sendChrome.AppendChild(sendArrow);
        sendTemplate.VisualTree = sendChrome;
        var sendHover = new Trigger { Property = IsMouseOverProperty, Value = true };
        sendHover.Setters.Add(new Setter(OpacityProperty, 0.85));
        sendTemplate.Triggers.Add(sendHover);
        _sendButton.Template = sendTemplate;
        _sendButton.Click += (_, _) => SendCurrent();
        Grid.SetColumn(_sendButton, 2);
        composerGrid.Children.Add(_sendButton);

        composer.Child = composerGrid;
        Grid.SetRow(composer, 4);
        _root.Children.Add(composer);

        _emptyHint = new Border
        {
            Background = Ui.Background,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _emptyHintText = new TextBlock
        {
            Text = DefaultEmptyHint,
            Foreground = Ui.Tertiary,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
        _emptyHint.Child = _emptyHintText;
        Grid.SetRow(_emptyHint, 2);
        _root.Children.Add(_emptyHint);

        _dropOverlay = new Border
        {
            Background = new SolidColorBrush(Ui.IsDarkTheme
                ? Color.FromArgb(238, 44, 44, 46)
                : Color.FromArgb(238, 255, 255, 255)),
            BorderBrush = Ui.Accent,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(8),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        var dropText = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        dropText.Children.Add(new TextBlock
        {
            Text = "Отпустите файлы, чтобы отправить",
            Foreground = Ui.Accent,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center
        });
        dropText.Children.Add(new TextBlock
        {
            Text = "До 5 ГБ на каждый файл",
            Foreground = Ui.Muted,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 7, 0, 0)
        });
        _dropOverlay.Child = dropText;
        Grid.SetRowSpan(_dropOverlay, 5);
        _root.Children.Add(_dropOverlay);

        _root.AllowDrop = true;
        _root.DragEnter += OnFileDrag;
        _root.DragOver += OnFileDrag;
        _root.DragLeave += (_, _) => _dropOverlay.Visibility = Visibility.Collapsed;
        _root.Drop += OnFileDrop;

        // ВАЖНО: именно PreviewKeyDown, а не KeyDown! У многострочного TextBox
        // (AcceptsReturn=true) встроенная обработка забирает Enter ДО KeyDown —
        // тогда Enter молча вставляет новую строку. PreviewKeyDown (туннельное
        // событие) срабатывает раньше внутренностей поля и гарантированно видит клавишу.
        _input.PreviewKeyDown += (_, e) =>
        {
            // Esc отменяет режим редактирования сообщения
            if (e.Key == Key.Escape && _editingMessageId.Length > 0)
            {
                e.Handled = true;
                CancelEdit();
                return;
            }
            // Enter — отправить, Shift+Enter — новая строка; единое правило — EnterSend
            if (EnterSend.ShouldSend(e.Key == Key.Return || e.Key == Key.Enter,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            {
                e.Handled = true;
                SendCurrent();
            }
        };
        _input.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, OnPaste, OnCanPaste));
        _input.GotFocus += (_, _) => Read?.Invoke();
        IsVisibleChanged += (_, _) => ScheduleReadReceipt();
        UpdateEmptyHint();
    }

    public void Bind(string sessionId, IEnumerable<ChatEntry> history)
    {
        _sessionId = sessionId;
        _emptyHintText.Text = DefaultEmptyHint;
        SetSendEnabled(true);
        ClearAll();
        foreach (var entry in history) Append(entry);
        _input.Focus();
        Read?.Invoke();
    }

    public void Unbind()
    {
        _sessionId = "";
        ClearAll();
    }

    public void ClearAll()
    {
        _history.Children.Clear();
        _deliveryLabels.Clear();
        _messageBubbles.Clear();
        _fileProgressLabels.Clear();
        _fileProgressBars.Clear();
        _fileContentPanels.Clear();
        _deleteMarks.Clear();
        _thumbnailsShown.Clear();
        _ownAccentLabels.Clear();
        _unreadIncoming.Clear();
        _textBodies.Clear();
        _bubblesById.Clear();
        CancelEdit();
        SetPinned("", "");
        UpdateEmptyHint();
    }

    private bool IsChatOnScreen()
    {
        if (!IsVisible) return false;
        var window = Window.GetWindow(this);
        return window is { IsVisible: true, WindowState: not WindowState.Minimized };
    }

    private void ScheduleReadReceipt()
    {
        if (_unreadIncoming.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_unreadIncoming.Count == 0 || !IsChatOnScreen()) return;
            var ids = _unreadIncoming.Count > 50
                ? _unreadIncoming.Skip(_unreadIncoming.Count - 50).ToList()
                : _unreadIncoming.ToList();
            _unreadIncoming.Clear();
            MessagesRead?.Invoke(ids);
        });
    }

    public void Append(ChatEntry entry)
    {
        var bubble = new Border
        {
            Background = entry.Own ? Ui.Accent : Ui.FillSubtle,
            CornerRadius = entry.Own ? new CornerRadius(18, 18, 6, 18) : new CornerRadius(18, 18, 18, 6),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(entry.Own ? 56 : 2, 4, entry.Own ? 2 : 56, 4),
            HorizontalAlignment = entry.Own ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 560
        };
        var content = new StackPanel();
        var metaGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaGrid.Children.Add(new TextBlock
        {
            Text = $"{(entry.Own ? "Вы" : entry.Sender)}  ·  {entry.Time:dd.MM.yyyy HH:mm}",
            Foreground = entry.Own ? OnAccentMuted : Ui.Muted,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (entry.Own && entry.Id.Length > 0 && entry.Delivery is not ("✓" or "✓✓"))
        {
            var id = entry.Id;
            var deleteMark = new TextBlock
            {
                Text = "✕",
                Foreground = OnAccentMuted,
                FontSize = 11,
                Cursor = Cursors.Hand,
                ToolTip = "Удалить из очереди",
                Margin = new Thickness(8, 0, 0, 0)
            };
            deleteMark.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                MessageDeleteRequested?.Invoke(_sessionId, id);
            };
            Grid.SetColumn(deleteMark, 1);
            metaGrid.Children.Add(deleteMark);
            _deleteMarks[entry.Id] = deleteMark;
        }
        content.Children.Add(metaGrid);
        // Текст сообщения можно выделять мышью и копировать (Ctrl+C или «Копировать»
        // в контекстном меню). Это TextBox только для чтения с пустым шаблоном,
        // поэтому выглядит он точно как раньше, без рамки и фона
        var body = new TextBox
        {
            Text = entry.FileName.Length > 0
                ? $"Файл: {entry.FileName}\n{FormatSize(entry.FileSize)}"
                : entry.Text,
            Foreground = entry.Own ? Brushes.White : Ui.Ink,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            MinHeight = 0,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Cursor = Cursors.IBeam
        };
        var bodyTemplate = new ControlTemplate(typeof(TextBox));
        var bodyHost = new FrameworkElementFactory(typeof(ScrollViewer));
        bodyHost.Name = "PART_ContentHost";
        bodyTemplate.VisualTree = bodyHost;
        body.Template = bodyTemplate;
        content.Children.Add(body);
        if (entry.Id.Length > 0)
        {
            _bubblesById[entry.Id] = bubble;
            if (entry.FileName.Length == 0) _textBodies[entry.Id] = body;
        }
        // Контекстное меню как в Telegram: Ответить, Изменить, Закрепить, Копировать
        bubble.PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowBubbleMenu(bubble, entry);
        };
        if (entry.FileName.Length > 0 && entry.FilePath.Length > 0)
        {
            var thumbnail = CreateThumbnail(entry.FilePath);
            if (thumbnail is not null)
            {
                content.Children.Insert(1, thumbnail);
                if (entry.Id.Length > 0) _thumbnailsShown.Add(entry.Id);
            }
        }
        if (entry.FileName.Length > 0)
        {
            if (entry.Id.Length > 0) _fileContentPanels[entry.Id] = content;
        }
        if (entry.FileName.Length > 0 && entry.FilePath.Length > 0)
        {
            var path = entry.FilePath;
            var open = Ui.ActionButton(File.Exists(path) ? "Открыть файл" : "Открыть папку с файлами",
                entry.Own ? "QuietButton" : "ChipButton");
            if (entry.Own) open.Foreground = Brushes.White;
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.MinHeight = 32;
            open.Height = 32;
            open.FontSize = 12;
            open.Margin = new Thickness(0, 8, 0, 0);
            open.Click += (_, _) =>
            {
                try
                {
                    if (File.Exists(path))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    else FileTransferService.OpenReceivedDirectory();
                }
                catch { }
            };
            content.Children.Add(open);
        }
        if (entry.FileName.Length > 0)
        {
            var done = entry.Delivery is "✓" or "✓✓" || (entry.FilePath.Length > 0 && entry.Delivery != "receiving");
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 5,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = entry.Own ? Brushes.White : Ui.Accent,
                Visibility = done ? Visibility.Collapsed : Visibility.Visible
            };
            content.Children.Add(bar);
            var progress = new TextBlock
            {
                Text = done
                    ? (entry.Own
                        ? (entry.Delivery == "✓✓" ? "✓✓ Прочитано" : "✓ Доставлено")
                        : "✓ Файл получен")
                    : (entry.Own
                        ? (entry.Delivery.StartsWith("Ошибка", StringComparison.Ordinal) ? entry.Delivery : "○ В очереди")
                        : "Ожидание начала передачи…"),
                Foreground = entry.Own
                    ? OnAccentMuted
                    : done ? Ui.Green
                    : entry.Delivery.StartsWith("Ошибка", StringComparison.Ordinal) ? Ui.Danger : Ui.Muted,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            content.Children.Add(progress);
            if (entry.Id.Length > 0)
            {
                _fileProgressLabels[entry.Id] = progress;
                _fileProgressBars[entry.Id] = bar;
                if (entry.Own)
                {
                    _deliveryLabels[entry.Id] = progress;
                    _ownAccentLabels.Add(entry.Id);
                }
            }
        }
        else if (entry.Own)
        {
            var delivery = new TextBlock
            {
                Text = entry.Delivery switch
                {
                    "✓" => "✓ Доставлено",
                    "✓✓" => "✓✓ Прочитано",
                    _ => "○ В очереди"
                },
                Foreground = OnAccentMuted,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            content.Children.Add(delivery);
            if (entry.Id.Length > 0)
            {
                _deliveryLabels[entry.Id] = delivery;
                _ownAccentLabels.Add(entry.Id);
            }
        }
        bubble.Child = content;
        if (entry.Id.Length > 0) _messageBubbles[entry.Id] = bubble;
        _history.Children.Add(bubble);
        if (!entry.Own && entry.Id.Length > 0)
        {
            _unreadIncoming.Add(entry.Id);
            ScheduleReadReceipt();
        }
        UpdateEmptyHint();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _scroll.ScrollToEnd());
    }

    /// <summary>Обновить текст сообщения после редактирования (своя или пришедшая правка).</summary>
    public void UpdateMessageText(string id, string newText)
    {
        if (_textBodies.TryGetValue(id, out var body)) body.Text = newText;
    }

    /// <summary>Показать/скрыть полосу закреплённого сообщения над историей.</summary>
    public void SetPinned(string messageId, string snippet)
    {
        _pinnedMessageId = messageId;
        if (messageId.Length == 0)
        {
            _pinnedTarget = null;
            _pinnedBar.Visibility = Visibility.Collapsed;
            return;
        }
        _pinnedBarText.Text = "Закреплено: " + snippet;
        _bubblesById.TryGetValue(messageId, out _pinnedTarget);
        _pinnedBar.Visibility = Visibility.Visible;
    }

    // Векторные иконки меню (контур 24×24) — не зависят от системных шрифтов и эмодзи
    private const string IconReply = "M10,9V5l-7,7 7,7v-4.1c5,0 8.5,1.6 11,5.1-1-5.5-4.5-9.5-11-11z";
    private const string IconEdit = "M20.71,7.04c0.39-0.39 0.39-1.04 0-1.41l-2.34-2.34c-0.37-0.39-1.02-0.39-1.41,0l-1.84,1.83 3.75,3.75M3,17.25V21h3.75L17.81,9.93l-3.75-3.75L3,17.25z";
    private const string IconPin = "M16,9V4h1c0.55,0 1-0.45 1-1s-0.45-1-1-1H7C6.45,2 6,2.45 6,3s0.45,1 1,1h1v5c0,1.66-1.34,3-3,3v2h5.97v7l1,1 1-1v-7H19v-2c-1.66,0-3-1.34-3-3z";
    private const string IconCopy = "M16,1H4C2.9,1 2,1.9 2,3v14h2V3h12V1zm3,4H8C6.9,5 6,5.9 6,7v14c0,1.1 0.9,2 2,2h11c1.1,0 2-0.9 2-2V7C21,5.9 20.1,5 19,5zm0,16H8V7h11v14z";

    /// <summary>Пункт меню с векторной иконкой: цвета берутся из текущей темы оформления.</summary>
    private static MenuItem BuildMenuItem(string text, string iconGeometry)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(iconGeometry),
            Fill = Ui.Ink,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform
        };
        return new MenuItem { Header = text, Icon = icon };
    }

    /// <summary>Контекстное меню сообщения — как в Telegram.</summary>
    private void ShowBubbleMenu(FrameworkElement target, ChatEntry entry)
    {
        var menu = new ContextMenu();

        var reply = BuildMenuItem("Ответить", IconReply);
        reply.Click += (_, _) => QuoteIntoComposer(entry);
        menu.Items.Add(reply);

        // Изменять можно только свои текстовые сообщения
        if (entry.Own && entry.FileName.Length == 0 && entry.Id.Length > 0)
        {
            var edit = BuildMenuItem("Изменить", IconEdit);
            edit.Click += (_, _) => BeginEdit(entry);
            menu.Items.Add(edit);
        }

        if (entry.Id.Length > 0)
        {
            var pin = BuildMenuItem(entry.Id == _pinnedMessageId ? "Открепить" : "Закрепить", IconPin);
            var id = entry.Id;
            pin.Click += (_, _) => PinToggled?.Invoke(_sessionId, id);
            menu.Items.Add(pin);
        }

        var copy = BuildMenuItem("Копировать", IconCopy);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(entry.FileName.Length > 0 ? entry.FileName : entry.Text); }
            catch { }
        };
        menu.Items.Add(copy);

        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>«Ответить»: цитата сообщения вставляется в поле ввода.</summary>
    private void QuoteIntoComposer(ChatEntry entry)
    {
        var source = entry.FileName.Length > 0 ? "Файл: " + entry.FileName : entry.Text;
        var singleLine = source.Replace("\r", " ").Replace("\n", " ").Trim();
        if (singleLine.Length > 80) singleLine = singleLine[..80] + "…";
        var author = entry.Own ? "Вы" : entry.Sender;
        _input.Text = $"{author}: «{singleLine}»\n" + _input.Text;
        _input.Focus();
        _input.CaretIndex = _input.Text.Length;
    }

    /// <summary>«Изменить»: текст сообщения возвращается в поле ввода для правки.</summary>
    private void BeginEdit(ChatEntry entry)
    {
        _editingMessageId = entry.Id;
        _input.Text = entry.Text;
        _editBar.Visibility = Visibility.Visible;
        _input.Focus();
        _input.CaretIndex = _input.Text.Length;
    }

    private void CancelEdit()
    {
        if (_editingMessageId.Length == 0) return;
        _editingMessageId = "";
        _input.Clear();
        _editBar.Visibility = Visibility.Collapsed;
    }

    public void UpdateDelivery(string id, string text)
    {
        if (_deliveryLabels.TryGetValue(id, out var label))
        {
            label.Text = text;
            label.Foreground = _ownAccentLabels.Contains(id) ? OnAccentText : Ui.Green;
        }
        if (_fileProgressBars.TryGetValue(id, out var bar)) bar.Visibility = Visibility.Collapsed;
        if (_deleteMarks.Remove(id, out var mark)) mark.Visibility = Visibility.Collapsed;
    }

    public void UpdateFileProgress(string id, string text, int percent = -1, bool done = false, bool error = false)
    {
        if (_fileProgressLabels.TryGetValue(id, out var label))
        {
            label.Text = text;
            label.Foreground = _ownAccentLabels.Contains(id)
                ? OnAccentMuted
                : error ? Ui.Danger : done ? Ui.Green : Ui.Accent;
        }
        if (_fileProgressBars.TryGetValue(id, out var bar))
        {
            if (done || error) bar.Visibility = Visibility.Collapsed;
            else if (percent >= 0)
            {
                bar.Visibility = Visibility.Visible;
                bar.Value = Math.Clamp(percent, 0, 100);
            }
        }
    }

    public void ShowThumbnail(string id, string path)
    {
        if (_thumbnailsShown.Contains(id)) return;
        if (!_fileContentPanels.TryGetValue(id, out var content)) return;
        var thumbnail = CreateThumbnail(path);
        if (thumbnail is null) return;
        content.Children.Insert(1, thumbnail);
        _thumbnailsShown.Add(id);
    }

    private static bool IsImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static Image? CreateThumbnail(string path)
    {
        if (!IsImageFile(path) || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 320;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            var image = new Image
            {
                Source = bitmap,
                MaxWidth = 260,
                MaxHeight = 180,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 2),
                Cursor = Cursors.Hand,
                ToolTip = "Открыть изображение"
            };
            image.MouseLeftButtonDown += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch { }
            };
            return image;
        }
        catch { return null; }
    }

    private static void OnCanPaste(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void OnPaste(object sender, ExecutedRoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (files.Length > 0) SendFiles(files);
                e.Handled = true;
                return;
            }
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is not null)
                {
                    var path = SaveClipboardImage(image);
                    if (path is not null) FileSend?.Invoke(_sessionId, path);
                }
                e.Handled = true;
                return;
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this),
                "Не удалось вставить из буфера обмена: " + ex.Message,
                "Вставка", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
            return;
        }
        if (Clipboard.ContainsText())
        {
            _input.Paste();
            e.Handled = true;
        }
    }

    private static string? SaveClipboardImage(BitmapSource image)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RepairAlert", "PasteCache");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                "Скриншот-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                Guid.NewGuid().ToString("N")[..4] + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                encoder.Save(stream);
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось сохранить изображение из буфера обмена", ex);
            return null;
        }
    }

    public void SetPendingCount(int count, int fileCount)
    {
        _queueStatus.Text = count == 0
            ? "● Все сообщения доставлены"
            : $"● В очереди: {count}" + (fileCount > 0 ? $" · файлов: {fileCount}" : "");
        _queueStatus.Foreground = count == 0 ? Ui.Green : Ui.Accent;
        _cancelFilesButton.Visibility = fileCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RemoveMessages(IEnumerable<string> messageIds)
    {
        foreach (var id in messageIds)
        {
            if (_messageBubbles.Remove(id, out var bubble)) _history.Children.Remove(bubble);
            _deliveryLabels.Remove(id);
            _fileProgressLabels.Remove(id);
            _fileProgressBars.Remove(id);
            _deleteMarks.Remove(id);
        }
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint() =>
        _emptyHint.Visibility = _history.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ChooseFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите файлы",
            Filter = "Все файлы|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        SendFiles(dialog.FileNames);
    }

    private void OnFileDrag(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        _dropOverlay.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        _dropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) SendFiles(paths);
        e.Handled = true;
    }

    private void SendFiles(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folders = paths.Count(path => !File.Exists(path) && Directory.Exists(path));
        var tooLarge = new List<string>();
        foreach (var path in files)
        {
            var info = new FileInfo(path);
            if (info.Length > FileTransferService.MaxFileBytes)
            {
                tooLarge.Add($"{info.Name} ({FormatSize(info.Length)})");
                continue;
            }
            FileSend?.Invoke(_sessionId, path);
        }
        var owner = Window.GetWindow(this);
        if (tooLarge.Count > 0)
        {
            System.Windows.MessageBox.Show(owner,
                "Эти файлы превышают 5 ГБ и не были отправлены:\n" + string.Join("\n", tooLarge),
                "Файл слишком большой", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (files.Count == 0 && folders > 0)
        {
            System.Windows.MessageBox.Show(owner, "Папки отправить нельзя. Выберите или перетащите отдельные файлы.",
                "Вложения", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        _input.Focus();
    }

    /// <summary>Включает или выключает поле ввода и кнопки (когда чат не выбран).</summary>
    public void SetSendEnabled(bool enabled)
    {
        _input.IsEnabled = enabled;
        _sendButton.IsEnabled = enabled;
        _attachButton.IsEnabled = enabled;
    }

    /// <summary>Меняет подсказку в пустой истории (например, когда заявки нет).</summary>
    public void SetEmptyHint(string text) => _emptyHintText.Text = text;

    private void SendCurrent()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _sessionId.Length == 0) return;
        if (text.Length > 4000)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this),
                "Сообщение слишком длинное (максимум 4000 символов).", "СЕРВИС ЛЕГКО");
            return;
        }
        // В режиме редактирования Enter сохраняет правку вместо нового сообщения
        if (_editingMessageId.Length > 0)
        {
            var editId = _editingMessageId;
            CancelEdit();
            MessageEditRequested?.Invoke(_sessionId, editId, text);
            _input.Focus();
            return;
        }
        _input.Clear();
        MessageSend?.Invoke(_sessionId, text);
        _input.Focus();
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} ГБ"
        : bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.0} МБ"
            : $"{Math.Max(1, bytes / 1024)} КБ";
}
