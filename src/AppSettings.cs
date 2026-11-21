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
    /// <summary>Полностью закрываться при «Сменить пользователя» Windows (и отключении RDP).
    /// При выходе из учётной записи и выключении программа закрывается всегда.</summary>
    public bool CloseOnSessionSwitch { get; set; } = true;
    public string Theme { get; set; } = "system";
    public List<string> RecentMasters { get; set; } = new();
    public int ReminderMinutes { get; set; } = 5;
    /// <summary>Мастер: всплывающее уведомление Windows, когда менеджер сам завершает заявку (2.41).
    /// Менеджер шлёт manager_completed при каждом переподключении — у мастера показывается
    /// только один раз на факт завершения (см. MasterForm.CompleteJobFromManager).</summary>
    public bool NotifyManagerCompleted { get; set; } = true;

    public void RememberMaster(string address) => RecentMastersLogic.Remember(RecentMasters, address);
}

internal static class SettingsUi
{
    public const string CloseOnSessionSwitchText =
        "Полностью закрываться при смене пользователя или выходе из Windows";

    public const string CloseOnSessionSwitchHint =
        "При выходе из Windows и выключении компьютера программа закрывается всегда. " +
        "Если галочку снять, при «Сменить пользователя» программа продолжит работать в фоне.";

    /// <summary>Третья галочка автозапуска — общая для мастера первого запуска и панели «Настройки».</summary>
    public static System.Windows.Controls.CheckBox CloseOnSessionSwitchCheckBox(AppSettings settings,
        System.Windows.Thickness margin)
    {
        var box = new System.Windows.Controls.CheckBox
        {
            Content = CloseOnSessionSwitchText,
            ToolTip = CloseOnSessionSwitchHint,
            IsChecked = settings.CloseOnSessionSwitch,
            Foreground = Ui.Muted,
            FontSize = 12,
            Margin = margin
        };
        box.Checked += (_, _) => { settings.CloseOnSessionSwitch = true; SettingsStore.Save(settings); };
        box.Unchecked += (_, _) => { settings.CloseOnSessionSwitch = false; SettingsStore.Save(settings); };
        return box;
    }
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
