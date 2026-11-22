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
        // Основной путь — COM-API брандмауэра (HNetCfg.FwPolicy2): чтение правил не требует
        // прав администратора. Раньше проверка шла только через netsh, а «netsh advfirewall
        // firewall show rule» без повышения на части систем отвечает ошибкой — отсюда ложные
        // «Windows не подтвердила создание правила» при реально созданном правиле (2.42).
        var viaCom = RuleExistsCom(port);
        if (viaCom.HasValue) return viaCom;
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

    /// <summary>Проверка правила через COM-API «Windows Firewall with Advanced Security».
    /// null — API недоступен, тогда вызывающий код откатывается на netsh.</summary>
    private static bool? RuleExistsCom(int port)
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null) return null;
            dynamic policy = Activator.CreateInstance(policyType)!;
            var portText = port.ToString();
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    if ((string)rule.Name != RuleName) continue;
                    if (!(bool)rule.Enabled) continue;
                    if ((int)rule.Direction != 1) continue; // NET_FW_RULE_DIR_IN
                    var localPorts = (string?)rule.LocalPorts ?? "";
                    if (localPorts.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Contains(portText, StringComparer.Ordinal))
                        return true;
                }
                catch { /* отдельное правило не прочиталось — смотрим остальные */ }
            }
            return false;
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
