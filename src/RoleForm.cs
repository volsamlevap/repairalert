using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RepairAlert;

internal sealed class RoleForm : BaseWindow
{
    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly WindowsNotifications _windowsNotifications;
    private readonly StackPanel _content;
    private bool _allowRealClose;
    private bool _trayHintShown;

    public RoleForm()
    {
        _windowsNotifications = new WindowsNotifications(this, ExitApplication);
        Ui.Prepare(this, "СЕРВИС ЛЕГКО", 620, 640, 460, 520);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var header = new Border { Background = Ui.Dark, Padding = new Thickness(30, 24, 30, 24) };
        var headerText = new StackPanel();
        headerText.Children.Add(new TextBlock
        {
            Text = "СЕРВИС ЛЕГКО",
            Foreground = Brushes.White,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        headerText.Children.Add(new TextBlock
        {
            Text = "Ремонты, чат, уведомления. Первый запуск займёт минуту.",
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        header.Child = headerText;
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        _content = new StackPanel { Margin = new Thickness(30), MaxWidth = 620 };
        scroll.Content = _content;
        ShowRoleStep();
    }

    private static TextBlock StepTitle(string text) => new()
    {
        Text = text,
        FontSize = 21,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 14)
    };

    private void ShowRoleStep()
    {
        _content.Children.Clear();
        _content.Children.Add(StepTitle("Как будет использоваться этот компьютер?"));
        var master = Ui.ActionButton("Я мастер — принимаю заявки", "DarkButton");
        master.Click += (_, _) => ShowMasterStep();
        _content.Children.Add(master);
        var manager = Ui.ActionButton("Я менеджер — отправляю заявки", "PrimaryButton");
        manager.Click += (_, _) => ShowManagerConnectStep();
        _content.Children.Add(manager);
        _content.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedText"),
            Text = "Работает в локальной сети и через интернет с Tailscale. При смене сети активный ремонт и переписка сохраняются.",
            Margin = new Thickness(0, 20, 0, 8)
        });
        _content.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedText"),
            Text = "Выбранная роль откроется автоматически при следующем запуске. Её всегда можно изменить кнопкой «Сменить роль».",
            Margin = new Thickness(0, 0, 0, 8)
        });
    }

    private void ShowMasterStep()
    {
        _content.Children.Clear();
        _content.Children.Add(StepTitle("Ваши адреса"));
        _content.Children.Add(Ui.Note("Менеджеры найдут этот компьютер автоматически. Если поиск не сработает, они введут адрес вручную:"));
        var addresses = LocalNetwork.GetAddresses();
        if (addresses.Count == 0)
            _content.Children.Add(Ui.Note("Сетевые адреса не найдены — проверьте подключение к сети."));
        foreach (var address in addresses)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = address + (LocalNetwork.IsTailscale(address) ? "  ·  Tailscale" : "  ·  локальная сеть"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            var copy = Ui.ActionButton("Копировать", "WhiteButton");
            copy.MinWidth = 100;
            copy.MinHeight = 30;
            copy.Height = 30;
            copy.FontSize = 11;
            var captured = address;
            copy.Click += (_, _) => Clipboard.SetText(captured);
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);
            _content.Children.Add(row);
        }
        _content.Children.Add(Ui.Note("Новый компьютер менеджера подключается по одноразовому коду: " +
            "нажмите «Добавить компьютер» в настройках (раздел «Подключение») и назовите менеджеру 6 цифр. " +
            "Код действует 5 минут."));
        _content.Children.Add(BuildAutoStartCheckboxes());
        var start = Ui.ActionButton("Начать работу — ожидаем менеджеров", "PrimaryButton");
        start.Margin = new Thickness(0, 18, 0, 0);
        start.Click += (_, _) => Finish(new MasterForm(), "Master");
        _content.Children.Add(start);
        var back = Ui.ActionButton("← Назад", "QuietButton");
        back.Margin = new Thickness(0, 8, 0, 0);
        back.Click += (_, _) => ShowRoleStep();
        _content.Children.Add(back);
    }

    private void ShowManagerConnectStep()
    {
        _content.Children.Clear();
        _content.Children.Add(StepTitle("Подключение к мастеру"));
        _content.Children.Add(Ui.Note("Обычно мастер находится автоматически. Выберите его из списка или введите адрес вручную."));
        var status = Ui.Note("Ищем мастера в локальной сети…");
        _content.Children.Add(status);
        var results = new StackPanel();
        _content.Children.Add(results);
        var refresh = Ui.ActionButton("Обновить поиск", "WhiteButton");
        refresh.Margin = new Thickness(0, 6, 0, 10);
        _content.Children.Add(refresh);

        var address = new TextBox
        {
            Text = _settings.MasterAddress,
            MinHeight = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 6)
        };
        System.Windows.Automation.AutomationProperties.SetName(address, "Адрес мастера: IP или имя Tailscale");
        _content.Children.Add(Ui.Note("Адрес мастера (IP или имя Tailscale):"));
        _content.Children.Add(address);

        if (_settings.RecentMasters.Count > 0)
        {
            _content.Children.Add(Ui.Note("Недавние мастера:"));
            var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            foreach (var recent in _settings.RecentMasters)
            {
                var chip = Ui.ActionButton(recent, "WhiteButton");
                chip.Height = 28;
                chip.MinHeight = 28;
                chip.FontSize = 11;
                chip.Padding = new Thickness(10, 0, 10, 0);
                chip.Margin = new Thickness(0, 0, 6, 6);
                var captured = recent;
                chip.Click += (_, _) => address.Text = captured;
                chips.Children.Add(chip);
            }
            _content.Children.Add(chips);
        }

        var next = Ui.ActionButton("Далее", "PrimaryButton");
        next.Margin = new Thickness(0, 10, 0, 0);
        next.Click += (_, _) =>
        {
            _settings.MasterAddress = address.Text.Trim();
            SettingsStore.Save(_settings);
            ShowManagerFolderStep();
        };
        void UpdateNext() => next.IsEnabled = address.Text.Trim().Length > 0;
        address.TextChanged += (_, _) => UpdateNext();
        UpdateNext();
        _content.Children.Add(next);
        var back = Ui.ActionButton("← Назад", "QuietButton");
        back.Margin = new Thickness(0, 8, 0, 0);
        back.Click += (_, _) => ShowRoleStep();
        _content.Children.Add(back);

        async void RunDiscovery()
        {
            refresh.IsEnabled = false;
            status.Text = "Ищем мастера в локальной сети…";
            results.Children.Clear();
            var found = await DiscoveryClient.DiscoverAsync();
            if (IsDisposed) return;
            refresh.IsEnabled = true;
            if (found.Count == 0)
            {
                status.Text = "Мастер пока не найден. Убедитесь, что программа запущена на компьютере мастера — или введите адрес вручную ниже.";
                return;
            }
            status.Text = found.Count == 1 ? "Найден мастер — нажмите, чтобы выбрать:" : "Найдены мастера:";
            foreach (var master in found)
            {
                var pick = Ui.ActionButton(master.DisplayName + " · версия " + master.Version, "WhiteButton");
                pick.HorizontalContentAlignment = HorizontalAlignment.Left;
                pick.Margin = new Thickness(0, 2, 0, 0);
                var captured = master.Address;
                pick.Click += (_, _) => address.Text = captured;
                results.Children.Add(pick);
            }
        }
        refresh.Click += (_, _) => RunDiscovery();
        RunDiscovery();
    }

    private void ShowManagerFolderStep()
    {
        _content.Children.Clear();
        _content.Children.Add(StepTitle("Папка отчётов и файлов"));
        _content.Children.Add(Ui.Note("Здесь будут общие отчёты, полученные файлы и журнал ремонтов. Все менеджеры должны использовать одну и ту же папку."));
        var current = _settings.SharedFolder.Length > 0 ? _settings.SharedFolder : StoragePaths.DefaultRootFolder;
        var status = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 6)
        };
        void Recheck()
        {
            current = _settings.SharedFolder.Length > 0 ? _settings.SharedFolder : StoragePaths.DefaultRootFolder;
            var ok = StoragePaths.TryInitialize(current, out var error);
            status.Text = ok
                ? "✓ Папка проверена, запись возможна: " + StoragePaths.RootFolder
                : "✗ Не удалось записать в папку: " + current + "\n" + error;
            status.Foreground = ok ? Ui.Green : Ui.Danger;
        }
        Recheck();
        _content.Children.Add(status);
        var choose = Ui.ActionButton("Выбрать другую папку…", "WhiteButton");
        choose.Click += (_, _) =>
        {
            var dialog = new StorageSetupForm(current, true) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _settings.SharedFolder = StoragePaths.RootFolder;
                SettingsStore.Save(_settings);
            }
            Recheck();
        };
        _content.Children.Add(choose);
        var next = Ui.ActionButton("Далее", "PrimaryButton");
        next.Margin = new Thickness(0, 12, 0, 0);
        next.Click += (_, _) =>
        {
            if (!StoragePaths.TryInitialize(current, out var error))
            {
                MessageBox.Show(this, "Папка недоступна для записи. Выберите другую.\n\n" + error,
                    "Общая папка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _settings.SharedFolder = StoragePaths.RootFolder;
            SettingsStore.Save(_settings);
            ShowManagerFinishStep();
        };
        _content.Children.Add(next);
        var back = Ui.ActionButton("← Назад", "QuietButton");
        back.Margin = new Thickness(0, 8, 0, 0);
        back.Click += (_, _) => ShowManagerConnectStep();
        _content.Children.Add(back);
    }

    private void ShowManagerFinishStep()
    {
        _content.Children.Clear();
        _content.Children.Add(StepTitle("Всё готово"));
        _content.Children.Add(Ui.Note("После подключения к мастеру один раз появится запрос кода сопряжения — он показан на экране мастера."));
        _content.Children.Add(BuildAutoStartCheckboxes());
        var start = Ui.ActionButton("Начать работу", "PrimaryButton");
        start.Margin = new Thickness(0, 18, 0, 0);
        start.Click += (_, _) => Finish(new ClientForm(), "Manager");
        _content.Children.Add(start);
        var back = Ui.ActionButton("← Назад", "QuietButton");
        back.Margin = new Thickness(0, 8, 0, 0);
        back.Click += (_, _) => ShowManagerFolderStep();
        _content.Children.Add(back);
    }

    private FrameworkElement BuildAutoStartCheckboxes()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var autoStart = new CheckBox
        {
            Content = "Запускать вместе с Windows",
            IsChecked = _settings.AutoStart,
            Foreground = Ui.Muted,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        };
        autoStart.Checked += (_, _) => { _settings.AutoStart = true; SettingsStore.Save(_settings); };
        autoStart.Unchecked += (_, _) => { _settings.AutoStart = false; SettingsStore.Save(_settings); };
        panel.Children.Add(autoStart);
        var startHidden = new CheckBox
        {
            Content = "При автозапуске открываться только в трее",
            IsChecked = _settings.StartHidden,
            Foreground = Ui.Muted,
            FontSize = 12
        };
        startHidden.Checked += (_, _) => { _settings.StartHidden = true; SettingsStore.Save(_settings); };
        startHidden.Unchecked += (_, _) => { _settings.StartHidden = false; SettingsStore.Save(_settings); };
        panel.Children.Add(startHidden);
        return panel;
    }

    private void Finish(Window form, string role)
    {
        _settings.Role = role;
        SettingsStore.Save(_settings);
        _allowRealClose = true;
        form.Show();
        Close();
    }

    private void ExitApplication()
    {
        _allowRealClose = true;
        ChatHostForm.PrepareForWindowsShutdown();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowRealClose && !ChatHostForm.IsApplicationExiting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _windowsNotifications.Show(
                    "СЕРВИС ЛЕГКО",
                    "Выбор роли скрыт. Программа продолжает работать в области уведомлений.");
            }
            return;
        }
        _windowsNotifications.Dispose();
        base.OnClosing(e);
    }
}
