using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RepairAlert;

/// <summary>
/// Чистая логика хот-спота (без WinRT — тестируется в MobileUploadTests): генерация
/// постоянных имени/пароля сети, QR-код подключения к Wi-Fi (стандарт WIFI:), поиск
/// адреса раздачи. Управление раздачей — в HotspotService (2.43).
/// </summary>
internal sealed record HotspotCredentials(string Ssid, string Passphrase)
{
    // Без неоднозначных символов (0/O, 1/l) — пароль виден в настройках Windows
    private const string PassChars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Постоянные имя и пароль: телефон запоминает сеть после первого
    /// подключения, дальше нужен только QR страницы.</summary>
    public static HotspotCredentials Generate()
    {
        var random = Random.Shared;
        var suffix = new char[4];
        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = (char)('0' + random.Next(10));
        var pass = new char[10]; // WPA2: 8..63 символа
        for (var i = 0; i < pass.Length; i++)
            pass[i] = PassChars[random.Next(PassChars.Length)];
        return new HotspotCredentials("SERVIS-" + new string(suffix), new string(pass));
    }

    /// <summary>QR для автоподключения телефона к сети: iPhone (iOS 11+) и Android
    /// показывают «Подключиться к сети» прямо из камеры.</summary>
    public string BuildWifiQrPayload() =>
        "WIFI:T:WPA;S:" + EscapeWifiQr(Ssid) + ";P:" + EscapeWifiQr(Passphrase) + ";;";

    /// <summary>Экранирование спецсимволов стандарта WIFI: — \ ; , : "</summary>
    private static string EscapeWifiQr(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or ';' or ',' or ':' or '"') builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>IPv4-адрес компьютера в сети раздачи (ICS, обычно 192.168.137.1
    /// на виртуальном адаптере Wi-Fi Direct). Пусто — раздача не запущена.</summary>
    public static List<string> FindHotspotAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = address.ToString();
                    // Windows ICS раздаёт сеть из 192.168.137.0/24, компьютер — .1
                    if (ip.StartsWith("192.168.137.", StringComparison.Ordinal))
                        result.Add(ip);
                }
            }
        }
        catch { }
        return result;
    }
}
