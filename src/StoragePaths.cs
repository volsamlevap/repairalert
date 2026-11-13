namespace RepairAlert;

internal static class StoragePaths
{
    private static string _rootFolder = "";

    public static string DefaultRootFolder
    {
        get
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            if (string.IsNullOrWhiteSpace(common)) common = @"C:\Users\Public\Documents";
            return Path.Combine(common, "СЕРВИС ЛЕГКО");
        }
    }

    public static string RootFolder => string.IsNullOrWhiteSpace(_rootFolder) ? DefaultRootFolder : _rootFolder;
    public static string ReportsDirectory => Path.Combine(RootFolder, "Отчёты");
    public static string ReceivedDirectory => Path.Combine(RootFolder, "Полученные файлы");
    public static string JournalPath => Path.Combine(RootFolder, "Журнал ремонтов.csv");

    public static void Configure(string path) => _rootFolder = path.Trim();

    public static bool TryInitialize(string path, out string error)
    {
        error = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("Укажите путь к папке.");
            var fullPath = Path.GetFullPath(path.Trim());
            Directory.CreateDirectory(fullPath);
            Directory.CreateDirectory(Path.Combine(fullPath, "Отчёты"));
            Directory.CreateDirectory(Path.Combine(fullPath, "Полученные файлы"));
            var testFile = Path.Combine(fullPath, $".test_{Environment.UserName}_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "write-test");
            File.Delete(testFile);
            Configure(fullPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
