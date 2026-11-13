using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RepairAlert;

internal sealed class RequestAlertForm : BaseWindow
{
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _countdown;
    private int _secondsLeft = 180;

    public event Action? Accepted;
    public event Action<int>? Postponed;
    public event Action<int>? Muted;

    public RequestAlertForm(string repairText)
    {
        Ui.Prepare(this, "Новая заявка — СЕРВИС ЛЕГКО", 420, 380, 380, 320);
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        var root = new Grid { Background = Ui.Surface };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;
        var stripe = new Border { Background = Ui.Accent };
        root.Children.Add(stripe);

        var content = new Grid { Margin = new Thickness(20, 14, 20, 16) };
        Grid.SetRow(content, 1);
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(content);
        content.Children.Add(new TextBlock
        {
            Text = "Новая заявка на ремонт",
            Foreground = Ui.Accent,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2)
        });
        var subtitle = new TextBlock
        {
            Text = "Подтвердите получение работы",
            Style = (Style)FindResource("MutedText"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(subtitle, 1);
        content.Children.Add(subtitle);
        var textBox = new TextBox
        {
            Text = repairText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Background = Ui.Background,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            MinHeight = 80,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(textBox, 2);
        content.Children.Add(textBox);
        _countdown = new TextBlock
        {
            Text = "Звук: 03:00",
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(_countdown, 3);
        content.Children.Add(_countdown);
        var accept = Ui.ActionButton("Принять заявку", "SuccessButton");
        accept.Height = 40;
        Grid.SetRow(accept, 4);
        accept.Click += (_, _) =>
        {
            _countdownTimer.Stop();
            Accepted?.Invoke();
            Close();
        };
        content.Children.Add(accept);
        var secondary = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        secondary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        secondary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var postpone = Ui.ActionButton("Отложить ▾", "WhiteButton");
        postpone.Height = 32;
        postpone.MinHeight = 32;
        postpone.FontSize = 12;
        postpone.Margin = new Thickness(0, 0, 4, 0);
        postpone.ToolTip = "Выбрать, через сколько напомнить о заявке снова";
        postpone.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var (label, mins) in new[] { ("5 минут", 5), ("15 минут", 15), ("30 минут", 30), ("1 час", 60) })
            {
                var item = new MenuItem { Header = label };
                var chosen = mins;
                item.Click += (_, _) =>
                {
                    _countdownTimer.Stop();
                    Postponed?.Invoke(chosen);
                    Close();
                };
                menu.Items.Add(item);
            }
            menu.PlacementTarget = postpone;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        };
        secondary.Children.Add(postpone);
        var mute = Ui.ActionButton("Заглушить 5 мин", "WhiteButton");
        mute.Height = 32;
        mute.MinHeight = 32;
        mute.FontSize = 12;
        mute.Margin = new Thickness(4, 0, 0, 0);
        mute.Click += (_, _) => Muted?.Invoke(5);
        Grid.SetColumn(mute, 1);
        secondary.Children.Add(mute);
        Grid.SetRow(secondary, 5);
        content.Children.Add(secondary);

        _countdownTimer.Tick += (_, _) =>
        {
            _secondsLeft = Math.Max(0, _secondsLeft - 1);
            _countdown.Text = _secondsLeft > 0
                ? $"Звук: {_secondsLeft / 60:00}:{_secondsLeft % 60:00}"
                : "Звук остановлен — заявка ждёт подтверждения";
            if (_secondsLeft == 0) _countdownTimer.Stop();
        };
        Shown += (_, _) => _countdownTimer.Start();
        Closed += (_, _) => _countdownTimer.Stop();
    }

    public void ShowAtCorner(Window owner)
    {
        Owner = owner;
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 18;
        Top = area.Bottom - Height - 18;
        Show();
    }
}
