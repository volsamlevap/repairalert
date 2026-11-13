using System.Windows;

namespace RepairAlert;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppLog.InstallGlobalHandlers();
        var autoStarted = args.Any(arg => arg.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        AppLog.Info("Запуск приложения, версия " + AppVersion.Text);
        IncomingTransferStore.CleanupOrphans(TimeSpan.FromDays(7));
        if (!SingleInstance.TryAcquire())
        {
            AppLog.Info("Второй экземпляр: пробуждаем уже запущенную программу");
            SingleInstance.SignalExisting();
            return;
        }
        try
        {
            var settings = SettingsStore.Load();
            Ui.ApplyTheme(settings.Theme);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Ui.ApplyResources(app);
            app.SessionEnding += (_, _) => ChatHostForm.PrepareForWindowsShutdown();
            app.DispatcherUnhandledException += (_, e) =>
            {
                AppLog.Error("Ошибка интерфейса", e.Exception);
                System.Windows.MessageBox.Show(e.Exception.Message, "СЕРВИС ЛЕГКО",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            Window startupWindow;
            if (settings.Role == "Master")
            {
                startupWindow = new MasterForm();
            }
            else if (settings.Role == "Manager")
            {
                var manager = new ClientForm();
                StorageService.EnsureConfigured(manager, settings);
                startupWindow = manager;
            }
            else
            {
                startupWindow = new RoleForm();
            }
            SettingsStore.Save(settings);
            if (autoStarted && settings.StartHidden && startupWindow is ChatHostForm host)
                host.Loaded += (_, _) => host.HideToTrayOnStartup();
            SingleInstance.StartWakeListener(() => app.Dispatcher.BeginInvoke(WakeWindows));
            app.Run(startupWindow);
        }
        finally
        {
            SingleInstance.Release();
            AppLog.Info("Приложение завершено");
        }
    }

    private static void WakeWindows()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null) return;
            Window? target = null;
            foreach (Window window in app.Windows)
                if (window is BaseWindow) target = window;
            target ??= app.MainWindow;
            if (target is null) return;
            target.Show();
            if (target.WindowState == WindowState.Minimized) target.WindowState = WindowState.Normal;
            target.Activate();
        }
        catch { }
    }
}
