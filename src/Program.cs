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
        // Временные части загрузок с телефона от прошлого запуска (сессии живут только в памяти).
        // Только после проверки единственного экземпляра — иначе можно стереть загрузку работающей копии.
        MobileUploadService.CleanupAtStartup();
        Microsoft.Win32.SessionSwitchEventHandler? onSessionSwitch = null;
        try
        {
            var settings = SettingsStore.Load();
            Ui.ApplyTheme(settings.Theme);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Ui.ApplyResources(app);
            // Выход из учётной записи / перезагрузка / выключение: программа закрывается полностью,
            // а не прячется в трей
            app.SessionEnding += (_, e) => ChatHostForm.ShutdownForSessionEnd(
                e.ReasonSessionEnding == ReasonSessionEnding.Logoff ? "выход из учётной записи" : "выключение или перезагрузка");
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
            // «Сменить пользователя» (и отключение RDP): сеанс остаётся в фоне. Если включена
            // галочка «Полностью закрываться при смене пользователя…» (по умолчанию да), закрываем
            // программу так же, как при выходе. Простая блокировка экрана (Win+L) не закрывает.
            onSessionSwitch = (_, e) =>
            {
                if (e.Reason is not (Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
                    or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)) return;
                if (!SettingsStore.Load().CloseOnSessionSwitch)
                {
                    AppLog.Info("Смена пользователя Windows: программа продолжает работать в фоне (так выбрано в настройках)");
                    return;
                }
                app.Dispatcher.InvokeAsync(() => ChatHostForm.ShutdownForSessionEnd("смена пользователя Windows"));
            };
            Microsoft.Win32.SystemEvents.SessionSwitch += onSessionSwitch;
            app.Run(startupWindow);
        }
        finally
        {
            if (onSessionSwitch is not null) Microsoft.Win32.SystemEvents.SessionSwitch -= onSessionSwitch;
            SingleInstance.Release();
            AppLog.Info("Приложение завершено");
        }
        // Гарантированное завершение процесса: никакие оставшиеся потоки не держат его в памяти
        Environment.Exit(0);
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
