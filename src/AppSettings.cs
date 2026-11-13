using System.Text.Json;
using Microsoft.Win32;

namespace RepairAlert;

internal sealed class AppSettings
{
    public string Role { get; set; } = "";
    public string MasterAddress { get; set; } = "";
    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");
    public string SharedFolder { get; set; } = "";
    public bool AutoStart { get; set; } = true;
    public string RequestSound { get; set; } = "standard";
    public string CompletedSound { get; set; } = "standard";
    public string JobChatSound { get; set; } = "standard";
    public string GeneralChatSound { get; set; } = "standard";
    public string RequestSoundCustom { get; set; } = "";
    public string CompletedSoundCustom { get; set; } = "";
    public string JobChatSoundCustom { get; set; } = "";
    public string GeneralChatSoundCustom { get; set; } = "";
    public string PairSecret { get; set; } = "";
    public string QuietFrom { get; set; } = "";
    public string QuietTo { get; set; } = "";
    public bool StartHidden { get; set; } = true;
    public string Theme { get; set; } = "system";
    public List<string> RecentMasters { get; set; } = new();
    public int ReminderMinutes { get; set; } = 5;

    public void RememberMaster(string address) => RecentMastersLogic.Remember(RecentMasters, address);
}

internal static class SettingsStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RepairAlert";
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        var text = AtomicFileStore.ReadAllText(FilePath);
        var settings = TryDeserialize(text) ?? TryDeserialize(AtomicFileStore.ReadAllText(FilePath + ".bak"));
        return settings ?? new AppSettings();
    }

    private static AppSettings? TryDeserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<AppSettings>(text); }
        catch { return null; }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AtomicFileStore.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
            ApplyAutoStart(settings.AutoStart);
        }
        catch { }
    }

    public static void ApplyAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;
            if (enabled)
                key.SetValue(ValueName, $"\"{System.Windows.Forms.Application.ExecutablePath}\" --autostart");
            else
                key.DeleteValue(ValueName, false);
        }
        catch { }
    }
}
