using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RepairAlert;

/// <summary>
/// Карточка входящего сообщения в стиле мини-панели: появляется справа внизу
/// над областью уведомлений, со звуком, и НЕ исчезает сама — только по кнопке
/// «Открыть чат», «Позже», «Заглушить» или ✕. Фокус не перехватывает.
/// </summary>
internal sealed class ChatNotificationForm : BaseWindow
{
    public ChatNotificationForm(string sender, string text, Action openChat, Action dismiss,
        Action<int> muteMinutes)
    {
        Title = "Новое сообщение — СЕРВИС ЛЕГКО";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 384;
        SizeToContent = SizeToContent.Height;
        // Важно: Manual — иначе Windows игнорирует Left/Top и центрирует окно
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Foreground = Ui.Ink;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        // Карточка со скруглением и тенью — как мини-панель
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
        Content = card;
        var root = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
        card.Child = root;

        // Шапка: значок, отправитель, кнопка скрытия (работает как «Позже»)
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.LogoMark(22));
        var senderText = new TextBlock
        {
            Text = "Сообщение от: " + sender,
            Foreground = Ui.Accent,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(senderText, 1);
        header.Children.Add(senderText);
        var hide = Ui.ActionButton("✕", "QuietButton");
        hide.Width = 26;
        hide.Height = 26;
        hide.MinHeight = 26;
        hide.Padding = new Thickness(0);
        hide.FontSize = 11;
        hide.ToolTip = "Скрыть до следующего сообщения";
        System.Windows.Automation.AutomationProperties.SetName(hide, "Скрыть уведомление");
        hide.Click += (_, _) => { dismiss(); Close(); };
        Grid.SetColumn(hide, 2);
        header.Children.Add(hide);
        root.Children.Add(header);

        // Текст сообщения (длинное — с прокруткой, карточка не растёт бесконечно)
        var preview = text.Length > 200 ? text[..200] + "…" : text;
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 96,
            Margin = new Thickness(0, 0, 0, 8)
        };
        scroll.Content = new TextBlock
        {
            Text = preview,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            FontSize = 13
        };
        root.Children.Add(scroll);

        // Кнопки действий
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var open = Ui.ActionButton("Открыть чат", "PrimaryButton");
        open.Height = 32;
        open.MinHeight = 32;
        open.FontSize = 12;
        open.Margin = new Thickness(0, 0, 4, 0);
        open.Click += (_, _) => { openChat(); Close(); };
        main.Children.Add(open);
        var later = Ui.ActionButton("Позже", "WhiteButton");
        later.Height = 32;
        later.MinHeight = 32;
        later.FontSize = 12;
        later.Margin = new Thickness(4, 0, 0, 0);
        later.Click += (_, _) => { dismiss(); Close(); };
        Grid.SetColumn(later, 1);
        main.Children.Add(later);
        root.Children.Add(main);

        var muteRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void AddMute(string label, int minutes)
        {
            var button = Ui.ActionButton(label, "QuietButton");
            button.Height = 24;
            button.MinHeight = 24;
            button.FontSize = 11;
            button.Padding = new Thickness(6, 0, 6, 0);
            button.Margin = new Thickness(0, 0, 10, 0);
            button.Click += (_, _) => { muteMinutes(minutes); Close(); };
            muteRow.Children.Add(button);
        }
        AddMute("Заглушить 1 мин", 1);
        AddMute("Заглушить 5 мин", 5);
        root.Children.Add(muteRow);
    }

    /// <summary>Показать карточку в правом нижнем углу, над областью уведомлений.
    /// Карточка остаётся на экране, пока пользователь не нажмёт кнопку.</summary>
    public void ShowAtCorner(Window owner)
    {
        Owner = owner;
        Show();
        // размер известен только после Show — теперь прижимаем к углу над треем
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }
}
