using System.Diagnostics;

namespace RepairAlert;

/// <summary>
/// Правило брандмауэра Windows для приёма файлов с телефона (порт MobileUploadService.Port).
/// Без него телефон не открывает страницу загрузки: входящие подключения блокируются.
/// Правило создаётся один раз с подтверждением прав администратора (UAC) и действует
/// на всех профилях сети, включая Tailscale — доступ к порту всё равно защищён
/// одноразовым токеном QR-сессии, а сервер слушает сеть, только пока открыто окно QR.
/// </summary>
internal static class FirewallService
{
    // Имя латиницей — надёжно проходит через cmd при любом системном языке
    private const string RuleName = "RepairAlert Phone Upload";

    /// <summary>Есть ли правило для порта. null — проверить не удалось (не мешаем работе).</summary>
    public static bool? RuleExists(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{RuleName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } return null; }
            // При отсутствии правил netsh пишет «No rules match...» / «Нет правил...» —
            // номера порта в таком выводе нет. Названия полей локализованы («LocalPort» /
            // «Локальный порт»), поэтому проверяем только сам номер порта — он от языка не зависит.
            return output.Contains(port.ToString(), StringComparison.Ordinal);
        }
        catch { return null; }
    }

    /// <summary>Создать правило (старые копии с тем же именем удаляются). Запрашивает UAC.
    /// null — правило создано и подтверждено, иначе текст ошибки для пользователя.</summary>
    public static string? EnsureRule(int port)
    {
        try
        {
            var command = "advfirewall firewall delete rule name=\"" + RuleName + "\" >nul 2>&1 & " +
                          "netsh advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow " +
                          "protocol=TCP localport=" + port + " profile=any enable=yes";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c netsh " + command,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(psi);
            if (process is null) return "Не удалось запустить настройку брандмауэра.";
            if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } }
            return RuleExists(port) == true
                ? null
                : "Windows не подтвердила создание правила. Разрешите входящие подключения на порт " +
                  port + " вручную: «Брандмауэр Защитника Windows» → «Правила для входящих».";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Пользователь отклонил запрос UAC
            return "Правило не создано: в запросе Windows нужно нажать «Да» (права администратора).";
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось создать правило брандмауэра", ex);
            return "Не удалось создать правило: " + ex.Message;
        }
    }
}
