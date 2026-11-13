using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace RepairAlert;

internal sealed record DiscoveredMaster(string Name, string Address, int Port, string Version)
{
    public string DisplayName => Name.Length > 0 ? $"{Name} · {Address}" : Address;
}

/// <summary>
/// Текстовый кодек широковещательного обнаружения мастера в LAN.
/// Формат запроса:  SERVIS-LEGKO-DISCOVER|&lt;protocolVersion&gt;
/// Формат ответа:   SERVIS-LEGKO-HERE|&lt;tcpPort&gt;|&lt;appVersion&gt;|&lt;name&gt;
/// </summary>
internal static class DiscoveryProtocol
{
    public const int DiscoveryPort = 47892;
    public const int MaxDatagramBytes = 512;
    private const string RequestMagic = "SERVIS-LEGKO-DISCOVER";
    private const string ResponseMagic = "SERVIS-LEGKO-HERE";

    public static byte[] EncodeRequest() =>
        Encoding.UTF8.GetBytes(RequestMagic + "|" + SecurityService.ProtocolVersion);

    public static bool IsRequest(byte[] data)
    {
        var text = TryText(data);
        if (text is null) return false;
        var parts = text.Split('|');
        return parts.Length == 2 && parts[0] == RequestMagic &&
               int.TryParse(parts[1], out var version) && version == SecurityService.ProtocolVersion;
    }

    public static byte[] EncodeResponse(string name, int tcpPort, string appVersion)
    {
        var safeName = SanitizeName(name);
        return Encoding.UTF8.GetBytes(string.Join('|', ResponseMagic, tcpPort, appVersion, safeName));
    }

    public static DiscoveredMaster? TryDecodeResponse(byte[] data, string address)
    {
        var text = TryText(data);
        if (text is null || text.Length > MaxDatagramBytes) return null;
        var parts = text.Split('|');
        if (parts.Length != 4 || parts[0] != ResponseMagic) return null;
        if (!int.TryParse(parts[1], out var port) || port <= 0 || port > 65535) return null;
        return new DiscoveredMaster(parts[3], address, port, parts[2]);
    }

    public static string SanitizeName(string name)
    {
        var cleaned = name.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    private static string? TryText(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxDatagramBytes) return null;
        try { return Encoding.UTF8.GetString(data); }
        catch { return null; }
    }
}

/// <summary>Слушает широковещательные запросы и отвечает данными мастера.</summary>
internal sealed class DiscoveryResponder : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private DiscoveryResponder(UdpClient udp, string name, int tcpPort, string appVersion)
    {
        _udp = udp;
        _loop = Task.Run(() => ReceiveLoopAsync(name, tcpPort, appVersion));
    }

    public static DiscoveryResponder? TryStart(string name, int tcpPort, string appVersion)
    {
        try
        {
            var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.DiscoveryPort));
            return new DiscoveryResponder(udp, name, tcpPort, appVersion);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось запустить автообнаружение мастера (UDP " + DiscoveryProtocol.DiscoveryPort + ")", ex);
            return null;
        }
    }

    private async Task ReceiveLoopAsync(string name, int tcpPort, string appVersion)
    {
        var response = DiscoveryProtocol.EncodeResponse(name, tcpPort, appVersion);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var packet = await _udp.ReceiveAsync(_cts.Token);
                if (!DiscoveryProtocol.IsRequest(packet.Buffer)) continue;
                await _udp.SendAsync(response, packet.RemoteEndPoint);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { }
    }
}

/// <summary>Однократный поиск мастеров в локальной сети широковещательным запросом.</summary>
internal static class DiscoveryClient
{
    public static async Task<List<DiscoveredMaster>> DiscoverAsync(int listenMilliseconds = 1500)
    {
        var found = new Dictionary<string, DiscoveredMaster>(StringComparer.Ordinal);
        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var request = DiscoveryProtocol.EncodeRequest();
            var broadcast = new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.DiscoveryPort);
            await udp.SendAsync(request, broadcast);
            await Task.Delay(80);
            await udp.SendAsync(request, broadcast);
            var deadline = Environment.TickCount64 + listenMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
                using var timeout = new CancellationTokenSource(remaining);
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(timeout.Token); }
                catch (OperationCanceledException) { break; }
                var master = DiscoveryProtocol.TryDecodeResponse(packet.Buffer,
                    packet.RemoteEndPoint.Address.ToString());
                if (master is not null) found[master.Address] = master;
            }
        }
        catch { }
        return found.Values.OrderBy(m => m.Address, StringComparer.Ordinal).ToList();
    }
}

/// <summary>Адреса локальных интерфейсов (LAN и Tailscale) для показа мастером.</summary>
internal static class LocalNetwork
{
    public static List<string> GetAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var address in ni.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address))
                        result.Add(address.Address.ToString());
            }
        }
        catch { }
        return result.Distinct().OrderByDescending(IsTailscale).ThenBy(ip => ip, StringComparer.Ordinal).ToList();
    }

    public static bool IsTailscale(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4 && parts[0] == "100" &&
               int.TryParse(parts[1], out var second) && second >= 64 && second <= 127;
    }
}
