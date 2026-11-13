using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RepairAlert;

/// <summary>
/// Мини-панель над областью уведомлений (как всплывающая панель OneDrive):
/// у менеджера — быстрая заявка и список активных заявок, у мастера — список
/// заявок с быстрым «Принять». Открывается кликом по иконке в трее, закрывается
/// повторным кликом, клавишей Esc или при потере фокуса.
/// </summary>
internal sealed class MiniForm : Window
{
    private readonly Func<(string Text, Brush Color)> _status;
    private readonly Action<StackPanel, StackPanel> _buildBody;
    private readonly Func<Control?>? _focusTarget;
    private readonly TextBlock _statusText;
    private readonly Border _statusDot;
    private readonly StackPanel _body;
    private readonly StackPanel _jobs;
    private readonly ScrollViewer _jobsScroll;
    private DateTime _hiddenByDeactivateUtc = DateTime.MinValue;
    private bool _forceClose;

    /// <summary>Сколько заявок видно без прокрутки: дальше список
    /// перестаёт расти и получает полосу прокрутки.</summary>
    private const int MaxVisibleJobs = 3;

    public MiniForm(string role, Func<(string Text, Brush Color)> status,
        Action<StackPanel, StackPanel> buildBody, Action openFull, Func<Control?>? focusTarget = null,
        (string Label, Action Click)? chatButton = null)
    {
        _status = status;
        _buildBody = buildBody;
        _focusTarget = focusTarget;

        Title = "СЕРВИС ЛЕГКО — мини-панель";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 384;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Foreground = Ui.Ink;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        var card = new Border
        {
            Background = Ui.Surface,
            BorderBrush = Ui.Separator,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(6),
            SnapsToDevicePixels = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28,
                Opacity = 0.28,
                ShadowDepth = 3,
                Color = Colors.Black
            }
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = layout;
        Content = card;

        // Шапка: значок, название, статус, кнопка скрытия
        var header = new Grid { Margin = new Thickness(14, 12, 8, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.LogoMark(24));
        var titles = new StackPanel
        {
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        titles.Children.Add(new TextBlock
        {
            Text = "СЕРВИС ЛЕГКО",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
        _statusDot = Ui.StatusDot(Ui.Muted, 7);
        statusRow.Children.Add(_statusDot);
        _statusText = new TextBlock
        {
            Text = role,
            Foreground = Ui.Muted,
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        statusRow.Children.Add(_statusText);
        titles.Children.Add(statusRow);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        var hide = Ui.ActionButton("✕", "QuietButton");
        hide.Width = 28;
        hide.Height = 28;
        hide.MinHeight = 28;
        hide.Padding = new Thickness(0);
        hide.FontSize = 12;
        hide.ToolTip = "Скрыть панель";
        hide.VerticalAlignment = VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetName(hide, "Скрыть мини-панель");
        hide.Click += (_, _) => Hide();
        Grid.SetColumn(hide, 2);
        header.Children.Add(hide);
        layout.Children.Add(header);

        // Тело: наполняется формой-хозяином при каждом показе.
        // _body — постоянная часть (поле ввода, подписи), _jobs — список заявок
        // с собственной прокруткой после MaxVisibleJobs строк.
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var content = new StackPanel { Margin = new Thickness(14, 0, 14, 4) };
        _body = new StackPanel();
        content.Children.Add(_body);
        _jobs = new StackPanel();
        _jobsScroll = new ScrollViewer
        {
            Content = _jobs,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        content.Children.Add(_jobsScroll);
        scroll.Content = content;
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        // Низ: кнопка полного окна (как «Открыть папку» у OneDrive)
        var footer = new Border
        {
            BorderBrush = Ui.Separator,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 8, 10, 10)
        };
        var open = Ui.ActionButton("Открыть полное окно", "QuietButton");
        open.Height = 30;
        open.MinHeight = 30;
        open.FontSize = 12;
        open.Click += (_, _) =>
        {
            Hide();
            openFull();
        };
        if (chatButton is null)
        {
            footer.Child = open;
        }
        else
        {
            // рядом с кнопкой полного окна — быстрый переход в чат
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var chat = Ui.ActionButton(chatButton.Value.Label, "WhiteButton");
            chat.Height = 30;
            chat.MinHeight = 30;
            chat.FontSize = 12;
            chat.Margin = new Thickness(0, 0, 4, 0);
            var chatClick = chatButton.Value.Click;
            chat.Click += (_, _) =>
            {
                Hide();
                chatClick();
            };
            open.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(chat, 0);
            Grid.SetColumn(open, 1);
            row.Children.Add(chat);
            row.Children.Add(open);
            footer.Child = row;
        }
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        Deactivated += (_, _) =>
        {
            if (IsVisible)
            {
                _hiddenByDeactivateUtc = DateTime.UtcNow;
                Hide();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Hide();
            }
        };
        Closing += (_, e) =>
        {
            if (_forceClose) return;
            e.Cancel = true;
            Hide();
        };

        // При изменении высоты (новая заявка, подтверждение отправки и т.п.)
        // панель растёт ВВЕРХ: нижний край остаётся на месте над треем
        SizeChanged += (_, _) =>
        {
            if (IsVisible) AnchorAboveTray();
        };
    }

    /// <summary>Прижимает панель к правому нижнему углу рабочей области
    /// (над областью уведомлений), не меняя нижнюю кромку.</summary>
    private void AnchorAboveTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = Math.Max(area.Top, area.Bottom - ActualHeight - 12);
    }

    /// <summary>Переключение по клику на иконку в трее. Защита от случая,
    /// когда клик по трею сначала снимает фокус с панели (панель прячется),
    /// а затем приходит событие клика — панель должна остаться скрытой.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        if (DateTime.UtcNow - _hiddenByDeactivateUtc < TimeSpan.FromMilliseconds(450))
        {
            _hiddenByDeactivateUtc = DateTime.MinValue;
            return;
        }
        ShowPanel();
    }

    public void ShowPanel()
    {
        RefreshBody();
        var area = SystemParameters.WorkArea;
        MaxHeight = Math.Max(320, area.Height - 48);
        Show();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            AnchorAboveTray();
            Activate();
            _focusTarget?.Invoke()?.Focus();
        }));
    }

    public void RefreshBody()
    {
        try
        {
            var (text, color) = _status();
            _statusText.Text = text;
            _statusDot.Background = color;
        }
        catch { }
        _body.Children.Clear();
        _jobs.Children.Clear();
        _buildBody(_body, _jobs);
        CapJobsList();
    }

    /// <summary>После трёх заявок список перестаёт расти и получает прокрутку.</summary>
    private void CapJobsList()
    {
        if (_jobs.Children.Count <= MaxVisibleJobs)
        {
            _jobsScroll.MaxHeight = double.PositiveInfinity;
            _jobsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            return;
        }
        double cap = 0;
        for (var i = 0; i < MaxVisibleJobs; i++)
        {
            if (_jobs.Children[i] is FrameworkElement row)
            {
                row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                cap += row.DesiredSize.Height;
            }
        }
        _jobsScroll.MaxHeight = cap + 2;
        _jobsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    public void CloseForce()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>Компактная строка заявки для мини-панели: цветная точка,
    /// заголовок и подпись; необязательная кнопка быстрого действия справа.</summary>
    public static Border JobRow(Brush color, string title, string subtitle,
        Action click, Button? action = null)
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (action is not null)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Ui.StatusDot(color, 8));
        var texts = new StackPanel
        {
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Ink,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        texts.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = Ui.Muted,
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            action.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
        }
        card.Child = grid;
        card.MouseEnter += (_, _) => card.Background = Ui.FillSubtle;
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseLeftButtonDown += (_, _) => click();
        return card;
    }

    public static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = Ui.Muted,
        Margin = new Thickness(2, 12, 0, 4),
        TextWrapping = TextWrapping.NoWrap
    };

    public static TextBlock EmptyLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Ui.Tertiary,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 12, 0, 8)
    };
}
