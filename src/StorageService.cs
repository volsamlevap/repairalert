using System.Windows;
using System.Windows.Controls;

namespace RepairAlert;

internal sealed class StorageSetupForm : BaseWindow
{
    private readonly TextBox _pathInput;
    public string SelectedPath => _pathInput.Text.Trim();

    public StorageSetupForm(string initialPath, bool firstStart)
    {
        Ui.Prepare(this, "Общая папка — СЕРВИС ЛЕГКО", 680, 540, 400, 460);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var root = new StackPanel { Margin = new Thickness(28) };
        scroll.Content = root;
        Content = scroll;
        root.Children.Add(new TextBlock
        {
            Text = firstStart ? "Где хранить общие данные?" : "Изменение общей папки",
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Все пользователи должны выбрать одну и ту же папку.",
            Foreground = Ui.Accent,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MutedText"),
            Text = "Здесь будут общие отчёты, файлы из чатов и журнал ремонтов. Можно указать локальный диск или сетевую папку.",
            Margin = new Thickness(0, 0, 0, 14)
        });
        _pathInput = new TextBox
        {
            Text = initialPath,
            MinWidth = 0,
            Margin = new Thickness(0, 5, 0, 8)
        };
        root.Children.Add(_pathInput);
        var browse = Ui.ActionButton("Обзор…");
        browse.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Выберите общую папку",
                SelectedPath = _pathInput.Text,
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                _pathInput.Text = dialog.SelectedPath;
        };
        root.Children.Add(browse);
        var useDefault = Ui.ActionButton("Использовать папку по умолчанию");
        useDefault.Click += (_, _) => _pathInput.Text = StoragePaths.DefaultRootFolder;
        root.Children.Add(useDefault);
        var save = Ui.ActionButton(firstStart ? "Сохранить и продолжить" : "Сохранить", "PrimaryButton");
        save.Click += (_, _) =>
        {
            if (!StoragePaths.TryInitialize(SelectedPath, out var error))
            {
                System.Windows.MessageBox.Show(this,
                    "Не удалось записать в эту папку. Выберите другую или проверьте права доступа.\n\n" + error,
                    "Общая папка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        };
        root.Children.Add(save);
    }
}

internal static class StorageService
{
    public static void EnsureConfigured(Window owner, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SharedFolder) && StoragePaths.TryInitialize(settings.SharedFolder, out _))
        {
            SettingsStore.Save(settings);
            return;
        }
        if (!string.IsNullOrWhiteSpace(settings.SharedFolder))
            System.Windows.MessageBox.Show(owner, "Сохранённая общая папка недоступна. Выберите её заново.", "СЕРВИС ЛЕГКО",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        var initial = string.IsNullOrWhiteSpace(settings.SharedFolder) ? StoragePaths.DefaultRootFolder : settings.SharedFolder;
        var dialog = new StorageSetupForm(initial, true) { Owner = owner };
        var path = dialog.ShowDialog() == true ? dialog.SelectedPath : StoragePaths.DefaultRootFolder;
        if (!StoragePaths.TryInitialize(path, out var error))
            throw new InvalidOperationException("Не удалось подготовить общую папку: " + error);
        settings.SharedFolder = StoragePaths.RootFolder;
        SettingsStore.Save(settings);
    }

    public static void ChangeFolder(Window owner, AppSettings settings)
    {
        var dialog = new StorageSetupForm(StoragePaths.RootFolder, false) { Owner = owner };
        if (dialog.ShowDialog() != true) return;
        settings.SharedFolder = StoragePaths.RootFolder;
        SettingsStore.Save(settings);
    }

    public static void OpenRootFolder()
    {
        Directory.CreateDirectory(StoragePaths.RootFolder);
        System.Diagnostics.Process.Start("explorer.exe", StoragePaths.RootFolder);
    }
}
