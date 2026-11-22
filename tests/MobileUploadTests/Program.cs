using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RepairAlert;

// Сквозной тест приёма файлов с телефона: настоящий HTTP по сети, временные папки,
// реальные данные программы (%LocalAppData%\RepairAlert) не используются (AppLog — заглушка).
var root = Path.Combine(Path.GetTempPath(), "RA_MobileTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
MobileUploadService.RootOverride = Path.Combine(root, "uploads");
StoragePaths.Configure(Path.Combine(root, "shared"));
Directory.CreateDirectory(Path.Combine(root, "shared"));
MobileUploadService.ChunkBytes = 4 * 1024 * 1024;

if (args.Length > 0 && args[0] == "serve")
{
    // Режим для проверки настоящей страницы в браузере: ждём файлы, сверяем содержимое.
    // Страница (через JS в браузере) отправляет файлы с детерминированным содержимым: byte[i] = i % 251.
    var got = new List<(string Name, string Hash, long Size)>();
    var s = MobileUploadService.CreateSession("Чат по заявке: проверка в браузере", (part, name, hash, size) =>
    {
        var saved = FileTransferService.SaveCompletedPart(part, name);
        lock (got) got.Add((name, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(saved))).ToLowerInvariant(), size));
        return Task.FromResult<string?>(null);
    });
    Console.WriteLine("URL http://127.0.0.1:" + MobileUploadService.Port + "/m/" + s.Token);
    var deadline = DateTime.Now.AddMinutes(4);
    while (DateTime.Now < deadline && got.Count < 3) await Task.Delay(200);
    await Task.Delay(15000);
    string Expected(long size) { var b = new byte[size]; for (long i = 0; i < size; i++) b[i] = (byte)(i % 251); return Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant(); }
    foreach (var g in got)
        Console.WriteLine($"{(g.Hash == Expected(g.Size) ? "OK  " : "FAIL")} {g.Name} {g.Size} байт, SHA-256 {(g.Hash == Expected(g.Size) ? "совпадает" : "НЕ совпадает")}");
    Console.WriteLine($"Получено файлов: {got.Count}");
    MobileUploadService.CloseSession(s);
    try { Directory.Delete(root, true); } catch { }
    return got.Count == 3 && got.All(g => g.Hash == Expected(g.Size)) ? 0 : 1;
}

var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what);
    if (!ok) failures++;
}

var delivered = new List<(string Path, string Name, string Hash, long Size)>();
var session = MobileUploadService.CreateSession("Чат по заявке: тест", (part, name, hash, size) =>
{
    var saved = FileTransferService.SaveCompletedPart(part, name);
    lock (delivered) delivered.Add((saved, name, hash, size));
    return Task.FromResult<string?>(null);
});
var baseUrl = $"http://127.0.0.1:{MobileUploadService.Port}";
var api = $"{baseUrl}/api/m/{session.Token}";
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

Console.WriteLine("1. Страница и статус");
var page = await http.GetAsync($"{baseUrl}/m/{session.Token}");
var pageText = await page.Content.ReadAsStringAsync();
Check(page.StatusCode == System.Net.HttpStatusCode.OK && pageText.Contains("СЕРВИС ЛЕГКО") && pageText.Contains("crc32"),
    "страница по действующему токену отдаётся (200, есть CRC32)");
var bad = await http.GetAsync($"{baseUrl}/m/neverexisted");
Check((int)bad.StatusCode == 410, "чужой/выдуманный токен — 410 «ссылка недействительна»");
var badApi = await http.GetAsync($"{baseUrl}/api/m/neverexisted/status");
Check((int)badApi.StatusCode == 410, "API с выдуманным токеном — 410");
var status = await http.GetAsync($"{api}/status");
var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
Check(status.IsSuccessStatusCode && statusJson.GetProperty("title").GetString() == "Чат по заявке: тест",
    "статус сессии: цель — заданный компьютером чат");
Check(statusJson.GetProperty("maxFileBytes").GetInt64() == 5L * 1024 * 1024 * 1024, "лимит в статусе — 5 ГБ");
Check(session.Snapshot().State >= MobileSessionState.Connected, "компьютер видит «телефон подключён»");

Console.WriteLine("2. Передача файла 50 МБ частями, с повреждением и повтором");
var data = RandomNumberGenerator.GetBytes(50 * 1024 * 1024 + 12345);
var expectedHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
async Task<JsonElement> PostJson(string url, object body)
{
    var r = await http.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
    var text = await r.Content.ReadAsStringAsync();
    return JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text).RootElement.Clone();
}
var created = await PostJson($"{api}/files", new { name = "../../IMG_0001.HEIC", size = data.Length, key = "IMG_0001.HEIC|x|1" });
var fileId = created.GetProperty("id").GetString()!;
var chunk = created.GetProperty("chunkSize").GetInt32();
var count = created.GetProperty("chunkCount").GetInt32();
Check(count == (data.Length + chunk - 1) / chunk, $"файл объявлен: {count} частей по {chunk / 1048576} МБ");

async Task<int> PutChunk(int index, bool corrupt = false)
{
    var start = index * chunk;
    var length = Math.Min(chunk, data.Length - start);
    var body = new byte[length];
    Buffer.BlockCopy(data, start, body, 0, length);
    var crc = Crc32.Compute(body).ToString("x8");
    if (corrupt) body[length / 2] ^= 0xFF;
    var content = new ByteArrayContent(body);
    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    var req = new HttpRequestMessage(HttpMethod.Put, $"{api}/files/{fileId}/chunks/{index}") { Content = content };
    req.Headers.Add("X-Crc32", crc);
    var r = await http.SendAsync(req);
    return (int)r.StatusCode;
}
Check(await PutChunk(3, corrupt: true) == 422, "повреждённая часть отклонена (CRC32 не совпал)");
var order = Enumerable.Range(0, count).OrderBy(_ => Random.Shared.Next()).Where(i => i != 5).ToList();
var results = await Task.WhenAll(order.Chunk(2).Select(async pair =>
{
    var codes = new List<int>();
    foreach (var i in pair) codes.Add(await PutChunk(i));
    return codes;
}));
Check(results.SelectMany(c => c).All(c => c == 200), "все части, кроме одной, приняты (в случайном порядке, по 2 параллельно)");
Check(await PutChunk(0) == 200, "повторная отправка уже принятой части — идемпотентно 200");

var early = await http.PostAsync($"{api}/files/{fileId}/complete", null);
var earlyJson = JsonDocument.Parse(await early.Content.ReadAsStringAsync()).RootElement;
Check((int)early.StatusCode == 409 && earlyJson.GetProperty("missing")[0].GetInt32() == 5,
    "завершение до получения всех частей — 409 и номер недостающей части (5)");

Console.WriteLine("3. Продолжение после «обрыва»: страницу открыли заново и выбрали тот же файл");
var resumed = await PostJson($"{api}/files", new { name = "../../IMG_0001.HEIC", size = data.Length, key = "IMG_0001.HEIC|x|1" });
Check(resumed.GetProperty("id").GetString() == fileId, "тот же файл узнан по ключу — тот же id");
Check(resumed.GetProperty("received").GetArrayLength() == count - 1, "сервер сообщает принятые части — повторно их не слать");
Check(await PutChunk(5) == 200, "недостающая часть дослана");

var done = await http.PostAsync($"{api}/files/{fileId}/complete", null);
Check(done.IsSuccessStatusCode, "завершение принято, проверка в фоне");
JsonElement fileStatus = default;
for (var i = 0; i < 100; i++)
{
    fileStatus = JsonDocument.Parse(await http.GetStringAsync($"{api}/files/{fileId}")).RootElement.Clone();
    if (fileStatus.GetProperty("completed").GetBoolean() || fileStatus.GetProperty("error").GetString()!.Length > 0) break;
    await Task.Delay(100);
}
Check(fileStatus.GetProperty("completed").GetBoolean(), "файл собран и передан программе");
Check(delivered.Count == 1, "программа получила ровно один файл");
if (delivered.Count == 1)
{
    var d = delivered[0];
    Check(d.Name == "IMG_0001.HEIC", "имя очищено от «../../», расширение HEIC сохранено без изменений");
    Check(d.Hash == expectedHash, "SHA-256 собранного файла совпадает с исходным");
    Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(d.Path))).ToLowerInvariant() == expectedHash,
        "сохранённый файл побайтно совпадает с исходным");
    Check(d.Path.StartsWith(Path.Combine(root, "shared"), StringComparison.OrdinalIgnoreCase),
        "файл сохранён в «Полученные файлы» общей папки");
}
var again = await http.PostAsync($"{api}/files/{fileId}/complete", null);
await Task.Delay(300);
Check(again.IsSuccessStatusCode && delivered.Count == 1, "повторное завершение не создаёт дубликат вложения");

Console.WriteLine("4. Границы и защита");
var tooBig = await http.PostAsync($"{api}/files",
    new StringContent(JsonSerializer.Serialize(new { name = "big.mov", size = 6L * 1024 * 1024 * 1024 }), Encoding.UTF8, "application/json"));
Check((int)tooBig.StatusCode == 413, "файл больше 5 ГБ отклонён до начала передачи (413)");
var exact = await http.PostAsync($"{api}/files",
    new StringContent(JsonSerializer.Serialize(new { name = "exact.mov", size = 5L * 1024 * 1024 * 1024 }), Encoding.UTF8, "application/json"));
Check((int)exact.StatusCode == 200 || (int)exact.StatusCode == 507,
    $"файл ровно 5 ГБ разрешён (код {(int)exact.StatusCode}: 200 — принят, 507 — не хватило места на диске теста)");
var empty = await PostJson($"{api}/files", new { name = "empty.txt", size = 0, key = "empty" });
var emptyId = empty.GetProperty("id").GetString();
await http.PostAsync($"{api}/files/{emptyId}/complete", null);
await Task.Delay(500);
Check(delivered.Any(d => d.Name == "empty.txt"), "пустой файл (0 байт) тоже передаётся");

// Неверная длина части — сырой HTTP, тело не дочитывается, соединение закрывается
using (var tcp = new TcpClient())
{
    await tcp.ConnectAsync("127.0.0.1", MobileUploadService.Port);
    var stream = tcp.GetStream();
    var other = await PostJson($"{api}/files", new { name = "x.bin", size = 10 * 1024 * 1024, key = "x" });
    var otherId = other.GetProperty("id").GetString();
    var head = $"PUT /api/m/{session.Token}/files/{otherId}/chunks/0 HTTP/1.1\r\nHost: x\r\nContent-Length: 10\r\nX-Crc32: 00000000\r\n\r\n0123456789";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
    var buf = new byte[4096];
    var n = await stream.ReadAsync(buf);
    Check(Encoding.ASCII.GetString(buf, 0, n).StartsWith("HTTP/1.1 400"), "часть неверной длины отклонена (400)");
}
using (var tcp = new TcpClient())
{
    await tcp.ConnectAsync("127.0.0.1", MobileUploadService.Port);
    var stream = tcp.GetStream();
    await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /m/x HTTP/1.1\r\n" + new string('A', 20000)));
    var buf = new byte[256];
    int n;
    try { n = await stream.ReadAsync(buf); } catch { n = 0; }
    Check(n == 0, "слишком большие заголовки — соединение закрыто без ответа");
}
var traversal = await http.GetAsync($"{baseUrl}/../../windows/win.ini");
Check(!traversal.IsSuccessStatusCode, "попытка выйти из маршрутов — не отдаётся");

Console.WriteLine("5. Отмена с телефона и истечение срока");
var cancelTarget = await PostJson($"{api}/files", new { name = "cancel.bin", size = 9 * 1024 * 1024, key = "c" });
var cancelPart = Path.Combine(session.Directory, cancelTarget.GetProperty("id").GetString() + ".part");
Check(File.Exists(cancelPart), "временная часть создана во временной папке");
await http.PostAsync($"{api}/cancel", null);
Check(!File.Exists(cancelPart), "после отмены незавершённые части удалены");
Check(delivered.Count == 2, "уже переданные файлы отменой не затронуты");

MobileUploadService.IdleTimeout = TimeSpan.FromMilliseconds(300);
await Task.Delay(500);
var expired = await http.GetAsync($"{api}/status");
Check((int)expired.StatusCode == 410, "после простоя сессия гаснет (410)");
Check(session.Snapshot().Expired, "окно на компьютере видит «QR-код устарел»");
MobileUploadService.IdleTimeout = TimeSpan.FromMinutes(10);

Console.WriteLine("6. Закрытие окна");
var sessionDir = session.Directory;
MobileUploadService.CloseSession(session);
Check(!Directory.Exists(sessionDir), "временная папка сессии удалена");
await Task.Delay(200);
var portClosed = false;
try
{
    using var probe = new TcpClient();
    await probe.ConnectAsync("127.0.0.1", MobileUploadService.Port);
}
catch (SocketException) { portClosed = true; }
Check(portClosed, $"последняя сессия закрыта — порт {MobileUploadService.Port} больше не слушается");

Console.WriteLine("7. Сеть: адреса для QR");
var addresses = MobileUploadService.GetPhoneAddresses();
foreach (var a in addresses) Console.WriteLine("       " + a.Label);
Check(addresses.All(a => a.Ip != "127.0.0.1" && !a.Ip.StartsWith("169.254.")), "в QR не попадают localhost и адреса без сети");
Check(addresses.Count == 0 || !addresses[0].IsTailscale || addresses.All(a => a.IsTailscale),
    "первым предлагается обычная сеть, не Tailscale");

Console.WriteLine("8. Хот-спот (2.43): имя/пароль и QR подключения к Wi-Fi");
var creds = HotspotCredentials.Generate();
Check(creds.Ssid.StartsWith("SERVIS-") && creds.Ssid.Length == 11, "имя сети вида SERVIS-XXXX");
Check(creds.Passphrase.Length >= 8 && creds.Passphrase.Length <= 63, "пароль в пределах WPA2 (8..63)");
var payload = creds.BuildWifiQrPayload();
Check(payload.StartsWith("WIFI:T:WPA;S:") && payload.EndsWith(";;"), "QR формата WIFI: распознаётся камерой телефона");
Check(payload.Contains(";S:" + creds.Ssid + ";") || payload.Contains(";S:" + creds.Ssid + "\\;"),
    "в QR зашито имя сети");
Check(payload.Contains(";P:" + creds.Passphrase + ";;"), "в QR зашит пароль");
var tricky = new HotspotCredentials("My;Net\\Work", "p:a\"s,s");
Check(tricky.BuildWifiQrPayload() == "WIFI:T:WPA;S:My\\;Net\\\\Work;P:p\\:a\\\"s\\,s;;",
    "спецсимволы в имени и пароле экранируются по стандарту WIFI:");
var hotspotAddresses = HotspotCredentials.FindHotspotAddresses();
Check(hotspotAddresses.All(ip => ip.StartsWith("192.168.137.")),
    "адреса раздачи — только из сети ICS 192.168.137.x");
Console.WriteLine("       сейчас раздача " + (hotspotAddresses.Count > 0 ? "включена: " + hotspotAddresses[0] : "выключена (норма для теста)"));

try { Directory.Delete(root, true); } catch { }
Console.WriteLine(failures == 0 ? "\nВСЕ ПРОВЕРКИ ПРОЙДЕНЫ" : $"\nПРОВАЛЕНО ПРОВЕРОК: {failures}");
return failures == 0 ? 0 : 1;

namespace RepairAlert
{
    internal sealed record ChatEntry(string Sender, string Text, DateTime Time, bool Own,
        string FileName = "", string FilePath = "", long FileSize = 0, string Id = "", string Delivery = "");

    // Заглушка журнала: тест не пишет в настоящий app.log программы
    internal static class AppLog
    {
        public static void Info(string message) { }
        public static void Error(string message, Exception? exception = null) =>
            Console.WriteLine("       [журнал] " + message + (exception is null ? "" : " | " + exception.Message));
    }
}
