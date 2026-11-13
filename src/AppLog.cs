using System.Text;

namespace RepairAlert;

internal static class AppVersion
{
    public static string Text
    {
        get
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var informational = assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (informational.Length > 0 &&
                    informational[0] is System.Reflection.AssemblyInformationalVersionAttribute attribute &&
                    !string.IsNullOrWhiteSpace(attribute.InformationalVersion))
                    return attribute.InformationalVersion;
                var version = assembly.GetName().Version;
                if (version is not null) return version.ToString();
            }
            catch { }
            return "2.0";
        }
    }
}

internal static class AppLog
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert");
    private static readonly object Sync = new();
    private static long _maxBytes = 1024 * 1024;

    public static string LogFilePath => Path.Combine(DirectoryPath, "app.log");

    public static long MaxBytes
    {
        get => _maxBytes;
        set => _maxBytes = Math.Max(1024, value);
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = LogFilePath;
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        File.Copy(path, path + ".bak", true);
                        File.Delete(path);
                    }
                }
                catch { }
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (exception is not null)
                    line += " | " + exception;
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    public static string ReadTail(int maxBytes = 16 * 1024)
    {
        try
        {
            var path = LogFilePath;
            if (!File.Exists(path)) return "(журнал пока пуст)";
            var bytes = File.ReadAllBytes(path);
            var start = Math.Max(0, bytes.Length - maxBytes);
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start).Trim();
        }
        catch (Exception ex)
        {
            return "(не удалось прочитать журнал: " + ex.Message + ")";
        }
    }

    public static string BuildDiagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine("СЕРВИС ЛЕГКО — диагностика");
        builder.AppendLine("Версия программы: " + AppVersion.Text);
        builder.AppendLine("Дата сбора: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));
        builder.AppendLine("ОС: " + Environment.OSVersion);
        builder.AppendLine("64-разрядный процесс: " + (Environment.Is64BitProcess ? "да" : "нет"));
        builder.AppendLine("Платформа .NET: " + Environment.Version);
        builder.AppendLine("Пользователь: " + Environment.UserDomainName + "\\" + Environment.UserName);
        builder.AppendLine("Компьютер: " + Environment.MachineName);
        builder.AppendLine("Общая папка: " + StoragePaths.RootFolder);
        builder.AppendLine("Файл журнала: " + LogFilePath);
        builder.AppendLine();
        builder.AppendLine("Последние записи журнала:");
        builder.AppendLine(ReadTail());
        return builder.ToString();
    }

    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Error("Необработанное исключение приложения", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Error("Необработанное исключение фоновой задачи", args.Exception);
            args.SetObserved();
        };
    }
}
