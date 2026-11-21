using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RepairAlert;

/// <summary>
/// Окно «Загрузить с телефона»: QR-код со ссылкой на страницу загрузки, выбор адреса
/// (Wi‑Fi/Ethernet или Tailscale), статус и прогресс. Окно модальное: сессия живёт ровно
/// пока оно открыто — так цель загрузки (чат или новая заявка) не может «перепутаться».
/// </summary>
internal sealed class PhoneUploadWindow : BaseWindow
{
    private readonly MobileUploadSession _session;
    private readonly Image _qrImage;
    private readonly TextBox _urlBox;
    private readonly ComboBox _addressBox;
    private readonly TextBlock _statusText;
    private readonly TextBlock _hintText;
    private readonly Border _firewallBar;
    private readonly TextBlock _firewallText;
    private readonly Button _firewallButton;
    private readonly ProgressBar _progress;
    private readonly Button _closeButton;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _openedAt = DateTime.Now;
    private int _uiUpdatePending;

    /// <summary>Показать окно загрузки для цели title. deliver вызывается в UI-потоке
    /// для каждого готового файла: (путь, имя) → null при успехе или текст ошибки.</summary>
    public static void ShowFor(Window owner, string title, Func<string, string, Task<string?>> deliverOnUi)
    {
        var addresses = MobileUploadService.GetPhoneAddresses();
        if (addresses.Count == 0)
        {
            MessageBox.Show(owner,
                "Компьютер не подключён ни к одной сети. Подключите его к Wi‑Fi или кабелю — телефон должен быть в той же сети.",
                "Загрузка с телефона", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dispatcher = owner.Dispatcher;
        MobileUploadSession session;
        try
        {
            session = MobileUploadService.CreateSession(title, (path, name, _, _) =>
                dispatcher.InvokeAsync(() => deliverOnUi(path, name)).Task.Unwrap());
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось включить приём файлов с телефона", ex);
            MessageBox.Show(owner, "Не удалось включить приём файлов с телефона.\n\n" + ex.Message,
                "Загрузка с телефона", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var window = new PhoneUploadWindow(session, title, addresses) { Owner = owner };
            window.ShowDialog();
        }
        finally
        {
            MobileUploadService.CloseSession(session);
        }
    }

    private PhoneUploadWindow(MobileUploadSession session, string title, List<PhoneAddress> addresses)
    {
        _session = session;
        Ui.Prepare(this, "Загрузка с телефона — СЕРВИС ЛЕГКО", 460, 720, 400, 560);
        ResizeMode = ResizeMode.CanResize;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        scroll.Content = root;
        Content = scroll;

        root.Children.Add(new TextBlock
        {
            Text = "Загрузить с телефона",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Куда: " + title,
            Foreground = Ui.Muted,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // QR всегда на белом фоне — так его надёжно читает камера и в тёмной теме
        _qrImage = new Image
        {
            Width = 240,
            Height = 240,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_qrImage, BitmapScalingMode.NearestNeighbor);
        root.Children.Add(new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _qrImage,
            Margin = new Thickness(0, 0, 0, 12)
        });

        root.Children.Add(Ui.Note("Отсканируйте камерой iPhone (или Android) и выберите фото, видео или файлы. " +
                                  "Телефон должен быть в той же Wi‑Fi сети, что и этот компьютер. До 5 ГБ на файл."));

        root.Children.Add(new TextBlock
        {
            Text = "Адрес компьютера",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 2)
        });
        _addressBox = new ComboBox { ItemsSource = addresses, SelectedIndex = 0, Margin = new Thickness(0, 2, 0, 6) };
        _addressBox.SelectionChanged += (_, _) => UpdateQr();
        root.Children.Add(_addressBox);

        _urlBox = new TextBox
        {
            IsReadOnly = true,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 10),
            ToolTip = "Эту ссылку можно открыть на телефоне вручную"
        };
        root.Children.Add(_urlBox);

        // Предупреждение о брандмауэре: показывается, только если правила нет (проверка ниже)
        _firewallText = new TextBlock
        {
            Text = "Windows может блокировать приём файлов с телефона. Если страница на телефоне " +
                   "не открывается — разрешите порт в брандмауэре (один раз, понадобится подтверждение администратора).",
            FontSize = 12,
            Foreground = Ui.Ink,
            TextWrapping = TextWrapping.Wrap
        };
        _firewallButton = Ui.ActionButton("Разрешить в брандмауэре", "WhiteButton");
        _firewallButton.HorizontalAlignment = HorizontalAlignment.Left;
        _firewallButton.Margin = new Thickness(0, 6, 0, 0);
        _firewallButton.Click += OnFirewallClick;
        var firewallStack = new StackPanel();
        firewallStack.Children.Add(_firewallText);
        firewallStack.Children.Add(_firewallButton);
        _firewallBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(31, 255, 159, 10)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            Child = firewallStack
        };
        root.Children.Add(_firewallBar);

        _statusText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 6)
        };
        root.Children.Add(_statusText);
        _progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(_progress);
        _hintText = Ui.Note("");
        _hintText.Visibility = Visibility.Collapsed;
        root.Children.Add(_hintText);

        _closeButton = Ui.ActionButton("Готово", "PrimaryButton");
        _closeButton.Margin = new Thickness(0, 12, 0, 0);
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(_closeButton);

        UpdateQr();
        UpdateStatus();
        CheckFirewallRule();
        _session.Changed += OnSessionChanged;
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
        Closing += OnClosingWindow;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _session.Changed -= OnSessionChanged;
        };
    }

    private void OnSessionChanged()
    {
        // Из фонового потока; не чаще одного обновления за раз
        if (Interlocked.Exchange(ref _uiUpdatePending, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _uiUpdatePending, 0);
            UpdateStatus();
        });
    }

    private void UpdateQr()
    {
        if (_addressBox.SelectedItem is not PhoneAddress address) return;
        var url = MobileUploadService.BuildUrl(address.Ip, _session.Token);
        _urlBox.Text = url;
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(10);
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(png))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            _qrImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось построить QR-код", ex);
        }
    }

    private void UpdateStatus()
    {
        var snapshot = _session.Snapshot();
        if (snapshot.Expired)
        {
            _statusText.Text = "QR-код устарел (10 минут без действий). Закройте окно и откройте загрузку с телефона заново.";
            _statusText.Foreground = Ui.Danger;
            _progress.Visibility = Visibility.Collapsed;
            _qrImage.Opacity = 0.25;
            _timer.Stop();
            return;
        }
        var receiving = snapshot.FilesTotal > snapshot.FilesDone;
        _statusText.Foreground = Ui.Ink;
        switch (snapshot.State)
        {
            case MobileSessionState.Waiting:
                _statusText.Text = "Ожидаем подключения телефона…";
                break;
            case MobileSessionState.Connected:
                _statusText.Text = "Телефон подключён — выберите файлы на телефоне";
                _statusText.Foreground = Ui.Green;
                break;
            default:
                _statusText.Text = receiving
                    ? $"Получение файлов: {snapshot.FilesDone} из {snapshot.FilesTotal} · " +
                      $"{FormatSize(snapshot.BytesReceived)} из {FormatSize(snapshot.BytesTotal)}"
                    : $"✓ Файлы получены: {snapshot.FilesDone}";
                if (!receiving) _statusText.Foreground = Ui.Green;
                break;
        }
        _progress.Visibility = snapshot.FilesTotal > 0 ? Visibility.Visible : Visibility.Collapsed;
        _progress.Value = snapshot.BytesTotal > 0 ? 100.0 * snapshot.BytesReceived / snapshot.BytesTotal : 0;
        _closeButton.Content = receiving ? "Прервать и закрыть" : "Готово";

        if (snapshot.LastError.Length > 0)
        {
            _hintText.Text = "Ошибка: " + snapshot.LastError;
            _hintText.Foreground = Ui.Danger;
            _hintText.Visibility = Visibility.Visible;
        }
        else if (snapshot.State == MobileSessionState.Waiting && DateTime.Now - _openedAt > TimeSpan.FromSeconds(40))
        {
            _hintText.Text = "Телефон не открывает страницу? Проверьте: телефон в той же Wi‑Fi сети (не гостевой); " +
                             "выбран правильный адрес выше; Windows не заблокировал программу в брандмауэре " +
                             $"(нужно разрешить входящие подключения на порт {MobileUploadService.Port} для частной сети).";
            _hintText.Foreground = Ui.Muted;
            _hintText.Visibility = Visibility.Visible;
        }
        else
        {
            _hintText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Проверить правило брандмауэра в фоне (netsh может занять долю секунды).
    /// Правила нет — показываем плашку с кнопкой; проверить не удалось — не мешаем работе.</summary>
    private void CheckFirewallRule()
    {
        Task.Run(() =>
        {
            var exists = FirewallService.RuleExists(MobileUploadService.Port);
            if (exists != false) return;
            Dispatcher.BeginInvoke(() => _firewallBar.Visibility = Visibility.Visible);
        });
    }

    private void OnFirewallClick(object sender, RoutedEventArgs e)
    {
        _firewallButton.IsEnabled = false;
        _firewallButton.Content = "Настраиваем…";
        Task.Run(() =>
        {
            var error = FirewallService.EnsureRule(MobileUploadService.Port);
            Dispatcher.BeginInvoke(() =>
            {
                _firewallButton.IsEnabled = true;
                _firewallButton.Content = "Разрешить в брандмауэре";
                if (error is null)
                {
                    _firewallBar.Visibility = Visibility.Collapsed;
                    AppLog.Info("Создано правило брандмауэра для приёма файлов с телефона");
                }
                else
                {
                    _firewallText.Text = error;
                }
            });
        });
    }

    private void OnClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var snapshot = _session.Snapshot();
        if (!snapshot.Expired && snapshot.FilesTotal > snapshot.FilesDone)
        {
            var answer = MessageBox.Show(this,
                "С телефона ещё идёт передача. Если закрыть окно, незавершённые файлы не будут получены. Закрыть?",
                "Загрузка с телефона", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " Б";
        double value = bytes;
        string[] units = { "КБ", "МБ", "ГБ" };
        var unit = -1;
        do { value /= 1024; unit++; } while (value >= 1024 && unit < units.Length - 1);
        return value.ToString(value >= 100 ? "0" : "0.0") + " " + units[unit];
    }
}
