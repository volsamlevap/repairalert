using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RepairAlert;

/// <summary>Строка файла для вкладки «Файлы» (2.41): плитка с миниатюрой или
/// расширением, имя с полной подсказкой, кнопки «Открыть» и «Показать в папке».
/// Общая для окон мастера и менеджера — раньше строка собиралась в двух местах
/// одинаковым кодом и была без иконки и действий.</summary>
internal static class FileRowUi
{
    public static Border BuildRow(ChatEntry entry)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(BuildTile(entry));

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = entry.FileName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // Полное имя — в подсказке: в строке оно обрезается многоточием (3.2)
            ToolTip = entry.FileName
        };
        info.Children.Add(name);
        info.Children.Add(new TextBlock
        {
            Text = $"{(entry.Own ? "Вы" : entry.Sender)} · {entry.Time:dd.MM.yyyy HH:mm} · {(entry.Delivery == "✓" ? "получен" : "в передаче")}",
            Foreground = Ui.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var path = entry.FilePath;
        var available = path.Length > 0 && File.Exists(path);

        var open = Ui.ActionButton("Открыть", "ChipButton");
        open.Height = 28;
        open.MinHeight = 28;
        open.VerticalAlignment = VerticalAlignment.Center;
        open.Click += (_, _) =>
        {
            try
            {
                if (available)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                else FileTransferService.OpenReceivedDirectory();
            }
            catch { }
        };
        buttons.Children.Add(open);

        // «Показать в папке» — файл уже автоматически сохранён на этом компьютере,
        // отдельное «скачивание» не нужно: открываем проводник с выделенным файлом
        var folder = Ui.ActionButton("Показать в папке", "ChipButton");
        folder.Height = 28;
        folder.MinHeight = 28;
        folder.Margin = new Thickness(6, 0, 0, 0);
        folder.VerticalAlignment = VerticalAlignment.Center;
        folder.ToolTip = available ? null : "Файл ещё передаётся — откроется общая папка полученных файлов";
        folder.Click += (_, _) =>
        {
            try
            {
                if (available)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else FileTransferService.OpenReceivedDirectory();
            }
            catch { }
        };
        buttons.Children.Add(folder);

        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);

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
        return card;
    }

    /// <summary>Плитка 44×44: миниатюра для картинок, иначе бейдж с расширением.</summary>
    private static Border BuildTile(ChatEntry entry)
    {
        var extension = Path.GetExtension(entry.FileName).TrimStart('.').ToUpperInvariant();
        if (extension.Length > 5) extension = extension[..5];
        var tile = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(
                Ui.IsDarkTheme ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0)),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = extension.Length > 0 ? extension : "ФАЙЛ",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var thumbnail = CreateThumbnailBrush(entry.FilePath);
        if (thumbnail is not null)
        {
            tile.Child = null;
            tile.Background = thumbnail;
        }
        return tile;
    }

    /// <summary>Скопировать файл в выбранное пользователем место («Сохранить как…»).
    /// Используется из вкладки «Файлы» и из контекстного меню сообщения в чате.</summary>
    internal static void SaveCopy(string path, string fileName)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить копию файла",
                FileName = fileName,
                Filter = "Все файлы|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            File.Copy(path, dialog.FileName, overwrite: false);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Не удалось сохранить копию: " + ex.Message,
                "СЕРВИС ЛЕГКО", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static ImageBrush? CreateThumbnailBrush(string path)
    {
        if (!IsImageFile(path) || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch { return null; }
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
}
