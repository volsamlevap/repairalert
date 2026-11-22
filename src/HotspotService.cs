using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace RepairAlert;

/// <summary>
/// Управление мобильным хот-спотом Windows (2.43) через WinRT
/// NetworkOperatorTetheringManager: телефон подключается напрямую к сети компьютера —
/// обход изоляции клиентов в офисных Wi-Fi, без Tailscale и настроек роутера.
/// Имя и пароль сети постоянные (AppSettings): телефон запоминает сеть.
/// </summary>
internal static class HotspotService
{
    /// <summary>Кто включил раздачу в этом окне: выключаем её при закрытии,
    /// только если включали мы.</summary>
    public sealed class Session : IDisposable
    {
        public required NetworkOperatorTetheringManager Manager { get; init; }
        public required HotspotCredentials Credentials { get; init; }
        public required string Address { get; init; }
        public required bool StartedByUs { get; init; }

        public void Dispose()
        {
            if (!StartedByUs) return;
            try { _ = Manager.StopTetheringAsync(); }
            catch (Exception ex) { AppLog.Error("Не удалось выключить раздачу Wi-Fi", ex); }
        }
    }

    /// <summary>Включить раздачу (или подхватить уже включённую) и вернуть сессию
    /// с именем/паролем сети и адресом компьютера в ней. Ошибка — текст для пользователя.</summary>
    public static async Task<(Session? Session, string? Error)> StartAsync(AppSettings settings)
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
                return (null, "Нет подключения к интернету — раздавать нечего. " +
                               "Подключите компьютер к сети и попробуйте снова.");
            NetworkOperatorTetheringManager manager;
            try
            {
                manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            }
            catch
            {
                return (null, "Wi-Fi адаптер этого компьютера не умеет раздавать сеть.");
            }

            // Постоянные имя/пароль — телефон запоминает сеть, повторно нужен только QR страницы
            if (settings.HotspotSsid.Length == 0 || settings.HotspotPass.Length < 8)
            {
                var generated = HotspotCredentials.Generate();
                settings.HotspotSsid = generated.Ssid;
                settings.HotspotPass = generated.Passphrase;
                SettingsStore.Save(settings);
            }
            var credentials = new HotspotCredentials(settings.HotspotSsid, settings.HotspotPass);

            var alreadyOn = manager.TetheringOperationalState == TetheringOperationalState.On;
            // Задать наши имя/пароль; если система не даёт настроить точку — берём ту,
            // что уже задана в параметрах Windows (там тоже стабильные имя и пароль)
            try
            {
                await manager.ConfigureAccessPointAsync(new NetworkOperatorTetheringAccessPointConfiguration
                {
                    Ssid = credentials.Ssid,
                    Passphrase = credentials.Passphrase
                });
            }
            catch
            {
                try
                {
                    var current = manager.GetCurrentAccessPointConfiguration();
                    credentials = new HotspotCredentials(current.Ssid, current.Passphrase);
                    settings.HotspotSsid = credentials.Ssid;
                    settings.HotspotPass = credentials.Passphrase;
                    SettingsStore.Save(settings);
                }
                catch { /* покажем QR с тем, что есть */ }
            }

            if (!alreadyOn)
            {
                var result = await manager.StartTetheringAsync();
                if (result.Status != TetheringOperationStatus.Success)
                {
                    var detail = result.AdditionalErrorMessage;
                    return (null, "Windows не включила раздачу" +
                                   (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail) +
                                   " Проверьте «Параметры → Сеть и Интернет → Мобильный хот-спот».");
                }
            }

            // Адрес раздачи появляется не мгновенно — ждём до 5 секунд
            var address = "";
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var found = HotspotCredentials.FindHotspotAddresses();
                if (found.Count > 0) { address = found[0]; break; }
                await Task.Delay(500);
            }
            if (address.Length == 0)
                return (null, "Раздача включилась, но адрес сети не появился. " +
                               "Перезапустите хот-спот в параметрах Windows и попробуйте снова.");

            AppLog.Info("Раздача Wi-Fi включена: " + credentials.Ssid + " (" + address + ")");
            return (new Session
            {
                Manager = manager,
                Credentials = credentials,
                Address = address,
                StartedByUs = !alreadyOn
            }, null);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось включить раздачу Wi-Fi", ex);
            return (null, "Не удалось включить раздачу: " + ex.Message);
        }
    }
}
