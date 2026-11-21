using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepairAlert;

internal static class SecurityService
{
    public const int ProtocolVersion = 2;
    public const string AppVersion = "2.41";

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static string GenerateCode()
    {
        var value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D6");
    }

    public static byte[] DeriveSecret(string code, string clientId)
    {
        var salt = Encoding.UTF8.GetBytes("servis-legko-pair|" + clientId);
        return Rfc2898DeriveBytes.Pbkdf2(code.Trim(), salt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    private static string Hmac(byte[] secret, string data)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    public static string HelloProof(byte[] secret, string clientId, string nonce) =>
        Hmac(secret, "client|" + clientId + "|" + nonce);

    public static string ServerProof(byte[] secret, string clientNonce, string serverNonce) =>
        Hmac(secret, "server|" + clientNonce + "|" + serverNonce);

    public static string PairProof(byte[] secret, string clientId, string nonce) =>
        Hmac(secret, "pair|" + clientId + "|" + nonce);

    public static string PairOkProof(byte[] secret, string clientId, string nonce) =>
        Hmac(secret, "pair-ok|" + clientId + "|" + nonce);

    public static string Sign(byte[] secret, AppMessage message)
    {
        var copy = JsonSerializer.Deserialize<AppMessage>(JsonSerializer.Serialize(message))!;
        copy.Sig = "";
        return Hmac(secret, JsonSerializer.Serialize(copy));
    }

    public static bool Verify(byte[] secret, AppMessage message) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Sign(secret, message)),
            Encoding.UTF8.GetBytes(message.Sig));
}

internal sealed class TrustedDevice
{
    public string ClientId { get; set; } = "";
    public string Secret { get; set; } = "";
    public string UserName { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTime PairedAt { get; set; } = DateTime.Now;
}

internal static class TrustedDevicesStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RepairAlert", "trusted-devices.json");
    private static readonly object Gate = new();
    private static List<TrustedDevice>? _cache;

    private static List<TrustedDevice> Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;
            var text = AtomicFileStore.ReadAllText(FilePath) ?? AtomicFileStore.ReadAllText(FilePath + ".bak");
            var list = new List<TrustedDevice>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { list = JsonSerializer.Deserialize<List<TrustedDevice>>(text) ?? new List<TrustedDevice>(); }
                catch { list = new List<TrustedDevice>(); }
            }
            _cache = list;
            return _cache;
        }
    }

    private static void Save()
    {
        lock (Gate)
        {
            AtomicFileStore.WriteAllText(FilePath, JsonSerializer.Serialize(_cache ?? new List<TrustedDevice>()));
        }
    }

    public static byte[]? GetSecret(string clientId)
    {
        var device = Load().FirstOrDefault(item => item.ClientId == clientId);
        if (device is null || device.Secret.Length == 0) return null;
        try { return Convert.FromBase64String(device.Secret); }
        catch { return null; }
    }

    public static void Add(string clientId, byte[] secret, string userName, string machineName)
    {
        var list = Load();
        list.RemoveAll(item => item.ClientId == clientId);
        list.Add(new TrustedDevice
        {
            ClientId = clientId,
            Secret = Convert.ToBase64String(secret),
            UserName = userName,
            MachineName = machineName,
            PairedAt = DateTime.Now
        });
        Save();
        AppLog.Info($"Сопряжено устройство: {userName} · {machineName}");
    }

    public static bool Remove(string clientId)
    {
        var list = Load();
        var removed = list.RemoveAll(item => item.ClientId == clientId) > 0;
        if (removed)
        {
            Save();
            AppLog.Info("Устройство забыто: " + clientId);
        }
        return removed;
    }

    public static IReadOnlyList<TrustedDevice> All() => Load().ToList();
}
