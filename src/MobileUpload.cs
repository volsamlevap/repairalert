using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepairAlert;

// ============================================================================================
//  Загрузка файлов с телефона по QR-коду (2.40).
//
//  Телефон (Safari/Chrome) → встроенный HTTP-модуль этого же экземпляра программы (порт 47893)
//  → готовый файл передаётся обратно программе (делегат Deliver), а та кладёт его в обычный
//  механизм вложений: чат заявки / общий чат / список вложений новой заявки.
//
//  Модуль слушает сеть, только пока открыт хотя бы один QR-код. Доступ — по одноразовому
//  случайному токену сессии; токен в журнал не пишется. Файлы принимаются частями
//  (chunked/resumable): каждая часть пишется сразу на диск по своему смещению и проверяется
//  CRC32; после сборки считается SHA-256. Весь файл в память не читается.
// ============================================================================================

/// <summary>CRC32 (IEEE 802.3) — контроль целостности каждой части. В браузере по http
/// нет crypto.subtle, поэтому страница считает CRC32 на JS, а SHA-256 всего файла — компьютер.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public const uint Initial = 0xFFFFFFFFu;

    public static uint Update(uint state, byte[] buffer, int offset, int count)
    {
        for (var i = offset; i < offset + count; i++)
            state = Table[(state ^ buffer[i]) & 0xFF] ^ (state >> 8);
        return state;
    }

    public static uint Finish(uint state) => ~state;

    public static uint Compute(byte[] data) => Finish(Update(Initial, data, 0, data.Length));
}

internal enum MobileSessionState { Waiting, Connected, Receiving, Done }

/// <summary>Один файл, который телефон передаёт частями.</summary>
internal sealed class MobileUploadFile
{
    public required string Id { get; init; }
    public required string ClientKey { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required int ChunkSize { get; init; }
    public required string PartPath { get; init; }
    public HashSet<int> Received { get; } = new();
    public bool Completed { get; set; }
    public bool Completing { get; set; }
    public string Error { get; set; } = "";

    public int ChunkCount => Size == 0 ? 0 : (int)((Size + ChunkSize - 1) / ChunkSize);

    public long ChunkLength(int index) =>
        index == ChunkCount - 1 ? Size - (long)index * ChunkSize : ChunkSize;

    public long ReceivedBytes => Received.Sum(index => ChunkLength(index));
}

/// <summary>Короткая сводка сессии для окна с QR-кодом.</summary>
internal sealed record MobileSessionSnapshot(MobileSessionState State, int FilesTotal, int FilesDone,
    long BytesTotal, long BytesReceived, string LastError, bool Expired);

/// <summary>
/// Сессия загрузки: один QR-код = одна цель (чат или новая заявка). Цель фиксируется на
/// компьютере при создании; телефон не может её поменять.
/// </summary>
internal sealed class MobileUploadSession
{
    internal readonly object Gate = new();
    internal readonly Dictionary<string, MobileUploadFile> Files = new(StringComparer.Ordinal);

    public required string Token { get; init; }
    /// <summary>Короткий номер для журнала (токен в журнал не пишем).</summary>
    public required string LogId { get; init; }
    public required string Title { get; init; }
    public required string Directory { get; init; }
    /// <summary>Передать собранный файл программе: (путь, имя, sha256, размер) → null при успехе
    /// или текст ошибки. Вызывается из фонового потока.</summary>
    public required Func<string, string, string, long, Task<string?>> Deliver { get; init; }
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
    public MobileSessionState State { get; private set; } = MobileSessionState.Waiting;
    public bool Closed { get; internal set; }
    public string LastError { get; private set; } = "";

    /// <summary>Изменился статус/прогресс (фоновый поток — окно само переходит в UI-поток).</summary>
    public event Action? Changed;

    public bool Expired => DateTime.UtcNow - LastActivityUtc > MobileUploadService.IdleTimeout;

    internal void Touch(MobileSessionState? state = null)
    {
        lock (Gate)
        {
            LastActivityUtc = DateTime.UtcNow;
            if (state is { } s && s > State) State = s;
        }
        RaiseChanged();
    }

    internal void SetError(string error)
    {
        lock (Gate) LastError = error;
        RaiseChanged();
    }

    internal void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    public MobileSessionSnapshot Snapshot()
    {
        lock (Gate)
        {
            var files = Files.Values.ToList();
            return new MobileSessionSnapshot(State, files.Count, files.Count(f => f.Completed),
                files.Sum(f => f.Size), files.Sum(f => f.Completed ? f.Size : f.ReceivedBytes), LastError, Expired);
        }
    }
}

/// <summary>Адрес компьютера, который можно зашить в QR-код.</summary>
internal sealed record PhoneAddress(string Ip, string Label, bool IsTailscale)
{
    public override string ToString() => Label;
}

internal static class MobileUploadService
{
    public const int Port = 47893;
    /// <summary>Размер части файла. 8 МБ — компромисс между числом запросов и памятью Safari.</summary>
    public static int ChunkBytes { get; set; } = 8 * 1024 * 1024;
    /// <summary>Лимит на один файл — тот же, что у обычной скрепки (5 ГБ).</summary>
    public static long MaxFileBytes => FileTransferService.MaxFileBytes;
    /// <summary>Сколько файлов можно передать за одну сессию (защита от злоупотреблений).</summary>
    public const int MaxFilesPerSession = 200;
    /// <summary>Сессия гаснет, если с телефона нет запросов дольше этого времени.</summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, MobileUploadSession> Sessions = new(StringComparer.Ordinal);
    private static MobileUploadServer? _server;

    /// <summary>Переопределение папки временных частей (для тестов).</summary>
    public static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert", "MobileUploads");

    /// <summary>Создать сессию и при необходимости запустить HTTP-модуль.
    /// Бросает исключение с понятным текстом, если порт занят.</summary>
    public static MobileUploadSession CreateSession(string title,
        Func<string, string, string, long, Task<string?>> deliver)
    {
        var token = NewToken();
        var session = new MobileUploadSession
        {
            Token = token,
            LogId = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant(),
            Title = title,
            Directory = Path.Combine(Root, Guid.NewGuid().ToString("N")),
            Deliver = deliver
        };
        System.IO.Directory.CreateDirectory(session.Directory);
        lock (Gate)
        {
            if (_server is null)
            {
                var server = new MobileUploadServer(Port);
                try { server.Start(); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    TryDeleteDirectory(session.Directory);
                    throw new InvalidOperationException(
                        $"Порт {Port} занят — возможно, QR-код уже открыт у другого пользователя Windows на этом компьютере. " +
                        "Закройте его там и попробуйте снова.");
                }
                catch
                {
                    TryDeleteDirectory(session.Directory);
                    throw;
                }
                _server = server;
                AppLog.Info("Приём файлов с телефона включён, порт " + Port);
            }
            Sessions[token] = session;
        }
        AppLog.Info($"QR-сессия {session.LogId} открыта: {title}");
        return session;
    }

    /// <summary>Закрыть сессию: незавершённые части удаляются, уже переданные файлы остаются у программы.</summary>
    public static void CloseSession(MobileUploadSession session)
    {
        MobileUploadServer? toStop = null;
        lock (Gate)
        {
            Sessions.Remove(session.Token);
            session.Closed = true;
            if (Sessions.Count == 0 && _server is not null)
            {
                toStop = _server;
                _server = null;
            }
        }
        TryDeleteDirectory(session.Directory);
        AppLog.Info($"QR-сессия {session.LogId} закрыта");
        if (toStop is not null)
        {
            toStop.Stop();
            AppLog.Info("Приём файлов с телефона выключен");
        }
    }

    public static MobileUploadSession? FindSession(string token)
    {
        lock (Gate)
        {
            if (!Sessions.TryGetValue(token, out var session)) return null;
            return session.Expired || session.Closed ? null : session;
        }
    }

    /// <summary>При запуске программы: удалить временные части от прошлых запусков
    /// (сессии живут только в памяти и после перезапуска недействительны).</summary>
    public static void CleanupAtStartup()
    {
        try
        {
            if (!System.IO.Directory.Exists(Root)) return;
            foreach (var dir in System.IO.Directory.EnumerateDirectories(Root)) TryDeleteDirectory(dir);
            foreach (var file in System.IO.Directory.EnumerateFiles(Root))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch (Exception ex) { AppLog.Error("Не удалось очистить временные загрузки с телефона", ex); }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string BuildUrl(string ip, string token) => $"http://{ip}:{Port}/m/{token}";

    /// <summary>
    /// Адреса компьютера для QR. Первым — адрес обычной сети (Wi‑Fi/Ethernet) с основным шлюзом:
    /// его увидит телефон в той же Wi‑Fi сети. Tailscale — последним: он работает, только если
    /// телефон сам подключён к той же tailnet.
    /// </summary>
    public static List<PhoneAddress> GetPhoneAddresses()
    {
        var result = new List<(PhoneAddress Address, int Rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in props.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                    var ip = address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue; // нет DHCP — бесполезен
                    var tailscale = LocalNetwork.IsTailscale(ip);
                    var label = tailscale
                        ? $"{ip} — Tailscale (только если телефон в той же tailnet)"
                        : $"{ip} — {ni.Name}";
                    var rank = tailscale ? 2 : hasGateway ? 0 : 1;
                    result.Add((new PhoneAddress(ip, label, tailscale), rank));
                }
            }
        }
        catch { }
        var ordered = result
            .GroupBy(item => item.Address.Ip).Select(g => g.OrderBy(item => item.Rank).First())
            .OrderBy(item => item.Rank).ThenBy(item => item.Address.Ip, StringComparer.Ordinal)
            .Select(item => item.Address).ToList();
        // Первый в списке (обычная сеть со шлюзом) — тот, что нужен телефону в той же Wi‑Fi
        if (ordered.Count > 0 && !ordered[0].IsTailscale)
            ordered[0] = ordered[0] with { Label = ordered[0].Label + " — рекомендуемый" };
        return ordered;
    }
}

/// <summary>
/// Минимальный HTTP/1.1-сервер на TcpListener (HttpListener для адреса в сети требует прав
/// администратора). Поддерживает только то, что нужно странице загрузки: GET/POST/PUT,
/// Content-Length, keep-alive.
/// </summary>
internal sealed class MobileUploadServer
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxJsonBody = 64 * 1024;
    private const int MaxConnections = 32;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    private readonly int _port;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private int _connections;

    public MobileUploadServer(int port) => _port = port;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                try { client.Close(); } catch { }
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(client); }
                catch { }
                finally
                {
                    Interlocked.Decrement(ref _connections);
                    try { client.Close(); } catch { }
                }
            });
        }
    }

    // ---------------------------------------------------------------- HTTP-соединение

    private sealed class Request
    {
        public string Method = "";
        public string Path = "";
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public long ContentLength;
        public bool KeepAlive = true;
    }

    private sealed class Connection
    {
        private readonly NetworkStream _stream;
        private readonly CancellationToken _stop;
        private readonly byte[] _buffer = new byte[256 * 1024];
        private int _start;
        private int _end;

        public Connection(NetworkStream stream, CancellationToken stop)
        {
            _stream = stream;
            _stop = stop;
        }

        private async Task<int> FillAsync()
        {
            if (_start > 0 && _start == _end) { _start = 0; _end = 0; }
            if (_end == _buffer.Length)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop);
            timeout.CancelAfter(ReadTimeout);
            var read = await _stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), timeout.Token);
            _end += read;
            return read;
        }

        /// <summary>Прочитать заголовки запроса; null — клиент закрыл соединение.</summary>
        public async Task<Request?> ReadRequestAsync()
        {
            while (true)
            {
                var index = IndexOfHeaderEnd();
                if (index >= 0)
                {
                    var text = Encoding.ASCII.GetString(_buffer, _start, index - _start);
                    _start = index + 4;
                    return Parse(text);
                }
                if (_end - _start >= MaxHeaderBytes) throw new InvalidDataException("Слишком большие заголовки");
                if (await FillAsync() == 0) return null;
            }
        }

        private int IndexOfHeaderEnd()
        {
            for (var i = _start; i + 3 < _end; i++)
                if (_buffer[i] == '\r' && _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
                    return i;
            return -1;
        }

        private static Request Parse(string text)
        {
            var lines = text.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 3) throw new InvalidDataException("Неверная строка запроса");
            var request = new Request { Method = first[0].ToUpperInvariant(), Path = first[1].Split('?')[0] };
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            if (request.Headers.TryGetValue("Content-Length", out var length) &&
                (!long.TryParse(length, out request.ContentLength) || request.ContentLength < 0))
                throw new InvalidDataException("Неверный Content-Length");
            if (request.Headers.ContainsKey("Transfer-Encoding"))
                throw new InvalidDataException("Transfer-Encoding не поддерживается");
            if (request.Headers.TryGetValue("Connection", out var connection) &&
                connection.Equals("close", StringComparison.OrdinalIgnoreCase))
                request.KeepAlive = false;
            return request;
        }

        /// <summary>Прочитать тело запроса порциями: каждая порция передаётся в sink.</summary>
        public async Task ReadBodyAsync(long length, Func<byte[], int, int, Task> sink)
        {
            var remaining = length;
            while (remaining > 0)
            {
                if (_start == _end && await FillAsync() == 0)
                    throw new IOException("Соединение закрыто во время передачи");
                var take = (int)Math.Min(remaining, _end - _start);
                await sink(_buffer, _start, take);
                _start += take;
                remaining -= take;
            }
        }

        public Task DrainAsync(long length) => ReadBodyAsync(length, (_, _, _) => Task.CompletedTask);

        public async Task<byte[]> ReadSmallBodyAsync(long length)
        {
            if (length > MaxJsonBody) throw new InvalidDataException("Слишком большой запрос");
            var result = new byte[length];
            var position = 0;
            await ReadBodyAsync(length, (buffer, offset, count) =>
            {
                Buffer.BlockCopy(buffer, offset, result, position, count);
                position += count;
                return Task.CompletedTask;
            });
            return result;
        }

        public async Task WriteAsync(int status, string contentType, byte[] body, bool keepAlive)
        {
            var reason = status switch
            {
                200 => "OK", 400 => "Bad Request", 404 => "Not Found", 409 => "Conflict",
                410 => "Gone", 413 => "Payload Too Large", 422 => "Unprocessable Entity",
                507 => "Insufficient Storage", _ => "Error"
            };
            var head = $"HTTP/1.1 {status} {reason}\r\n" +
                       $"Content-Type: {contentType}\r\n" +
                       $"Content-Length: {body.Length}\r\n" +
                       "Cache-Control: no-store\r\n" +
                       "X-Content-Type-Options: nosniff\r\n" +
                       "Referrer-Policy: no-referrer\r\n" +
                       $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await _stream.WriteAsync(headBytes, _stop);
            if (body.Length > 0) await _stream.WriteAsync(body, _stop);
            await _stream.FlushAsync(_stop);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        client.NoDelay = true;
        var connection = new Connection(client.GetStream(), _cts.Token);
        while (!_cts.IsCancellationRequested)
        {
            Request? request;
            try { request = await connection.ReadRequestAsync(); }
            catch { return; }
            if (request is null) return;
            Response response;
            try { response = await RouteAsync(request, connection); }
            catch (IOException) { return; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLog.Error("Ошибка обработки запроса с телефона", ex);
                response = Json(400, new { error = "Ошибка запроса" });
                response.Close = true;
            }
            var keepAlive = request.KeepAlive && !response.Close;
            try { await connection.WriteAsync(response.Status, response.ContentType, response.Body, keepAlive); }
            catch { return; }
            if (!keepAlive) return;
        }
    }

    private sealed class Response
    {
        public int Status;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = Array.Empty<byte>();
        /// <summary>Тело запроса не дочитано — соединение нужно закрыть.</summary>
        public bool Close;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static Response Json(int status, object value) => new()
    {
        Status = status,
        Body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)
    };

    private static Response Html(int status, string html) => new()
    {
        Status = status,
        ContentType = "text/html; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(html)
    };

    // ---------------------------------------------------------------- маршруты

    private async Task<Response> RouteAsync(Request request, Connection connection)
    {
        var parts = request.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // GET /m/{token} — страница для телефона
        if (request.Method == "GET" && parts.Length == 2 && parts[0] == "m")
        {
            await connection.DrainAsync(request.ContentLength);
            var session = MobileUploadService.FindSession(parts[1]);
            if (session is null) return Html(410, MobileUploadPage.ExpiredHtml);
            session.Touch(MobileSessionState.Connected);
            return Html(200, MobileUploadPage.Html);
        }

        // Всё остальное — /api/m/{token}/...
        if (parts.Length < 4 || parts[0] != "api" || parts[1] != "m")
        {
            await connection.DrainAsync(Math.Min(request.ContentLength, MaxJsonBody));
            return new Response { Status = 404, Close = request.ContentLength > MaxJsonBody };
        }
        var api = MobileUploadService.FindSession(parts[2]);
        if (api is null)
        {
            var bigBody = request.ContentLength > MaxJsonBody;
            if (!bigBody) await connection.DrainAsync(request.ContentLength);
            return new Response
            {
                Status = 410,
                Close = bigBody,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "QR-код устарел. Создайте новый на компьютере." }, JsonOptions)
            };
        }
        var route = parts[3];

        if (request.Method == "GET" && route == "status" && parts.Length == 4)
        {
            await connection.DrainAsync(request.ContentLength);
            api.Touch(MobileSessionState.Connected);
            return Json(200, StatusOf(api));
        }

        if (route == "files")
        {
            // POST /api/m/{t}/files — объявить новый файл
            if (request.Method == "POST" && parts.Length == 4)
                return await CreateFileAsync(api, request, connection);

            if (parts.Length >= 5)
            {
                MobileUploadFile? file;
                lock (api.Gate) api.Files.TryGetValue(parts[4], out file);
                if (file is null)
                {
                    var bigBody = request.ContentLength > MaxJsonBody;
                    if (!bigBody) await connection.DrainAsync(request.ContentLength);
                    return new Response { Status = 404, Close = bigBody, Body = Encoding.UTF8.GetBytes("{}") };
                }
                // GET /api/m/{t}/files/{id} — какие части уже приняты
                if (request.Method == "GET" && parts.Length == 5)
                {
                    await connection.DrainAsync(request.ContentLength);
                    api.Touch();
                    return Json(200, FileStatus(api, file));
                }
                // PUT /api/m/{t}/files/{id}/chunks/{index}
                if (request.Method == "PUT" && parts.Length == 7 && parts[5] == "chunks" &&
                    int.TryParse(parts[6], out var index))
                    return await ReceiveChunkAsync(api, file, index, request, connection);
                // POST /api/m/{t}/files/{id}/complete
                if (request.Method == "POST" && parts.Length == 6 && parts[5] == "complete")
                {
                    await connection.DrainAsync(request.ContentLength);
                    api.Touch();
                    return await CompleteFileAsync(api, file);
                }
            }
        }

        // POST /api/m/{t}/cancel — телефон отменил отправку: убрать незавершённые файлы
        if (request.Method == "POST" && route == "cancel" && parts.Length == 4)
        {
            await connection.DrainAsync(request.ContentLength);
            CancelIncomplete(api);
            return Json(200, StatusOf(api));
        }

        var big = request.ContentLength > MaxJsonBody;
        if (!big) await connection.DrainAsync(request.ContentLength);
        return new Response { Status = 404, Close = big };
    }

    private static object StatusOf(MobileUploadSession session)
    {
        lock (session.Gate)
        {
            return new
            {
                title = session.Title,
                maxFileBytes = MobileUploadService.MaxFileBytes,
                chunkBytes = MobileUploadService.ChunkBytes,
                files = session.Files.Values.Select(f => new
                {
                    id = f.Id,
                    name = f.Name,
                    size = f.Size,
                    receivedBytes = f.Completed ? f.Size : f.ReceivedBytes,
                    completed = f.Completed,
                    error = f.Error
                }).ToList()
            };
        }
    }

    private static object FileStatus(MobileUploadSession session, MobileUploadFile file)
    {
        lock (session.Gate)
        {
            return new
            {
                id = file.Id,
                chunkSize = file.ChunkSize,
                chunkCount = file.ChunkCount,
                received = file.Received.OrderBy(i => i).ToList(),
                completed = file.Completed,
                processing = file.Completing,
                error = file.Error
            };
        }
    }

    private sealed class CreateFileBody
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public string Key { get; set; } = "";
    }

    private static async Task<Response> CreateFileAsync(MobileUploadSession session, Request request, Connection connection)
    {
        var raw = await connection.ReadSmallBodyAsync(request.ContentLength);
        CreateFileBody? body;
        try { body = JsonSerializer.Deserialize<CreateFileBody>(raw, JsonOptions); }
        catch { body = null; }
        if (body is null || body.Size < 0) return Json(400, new { error = "Неверные данные файла" });
        if (body.Size > MobileUploadService.MaxFileBytes)
            return Json(413, new { error = "Файл больше 5 ГБ — такой файл передать нельзя." });
        var name = FileTransferService.SanitizeFileName(body.Name);
        var key = (body.Key.Length > 0 ? body.Key : name + "|" + body.Size);
        if (key.Length > 600) key = key[..600];

        MobileUploadFile file;
        lock (session.Gate)
        {
            // Повторное объявление того же файла (страницу открыли заново) — продолжаем его же
            var existing = session.Files.Values.FirstOrDefault(f =>
                f.ClientKey == key && f.Size == body.Size && f.Error.Length == 0);
            if (existing is not null)
            {
                session.Touch(MobileSessionState.Receiving);
                return Json(200, FileStatus(session, existing));
            }
            if (session.Files.Count >= MobileUploadService.MaxFilesPerSession)
                return Json(409, new { error = "Слишком много файлов за один раз. Создайте новый QR-код." });
        }

        // Место на диске: размер файла + запас (как у обычного приёма файлов)
        try { EnsureFreeSpace(session.Directory, body.Size); }
        catch (IOException)
        {
            session.SetError("Недостаточно места на диске компьютера");
            return Json(507, new { error = "На компьютере недостаточно места для этого файла." });
        }

        var id = Guid.NewGuid().ToString("N");
        file = new MobileUploadFile
        {
            Id = id,
            ClientKey = key,
            Name = name,
            Size = body.Size,
            ChunkSize = MobileUploadService.ChunkBytes,
            PartPath = Path.Combine(session.Directory, id + ".part")
        };
        using (var stream = new FileStream(file.PartPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            stream.SetLength(file.Size);
        lock (session.Gate) session.Files[id] = file;
        session.Touch(MobileSessionState.Receiving);
        return Json(200, FileStatus(session, file));
    }

    private static void EnsureFreeSpace(string directory, long size)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (root is null) return;
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (free < size + 64L * 1024 * 1024) throw new IOException("Недостаточно места");
    }

    private static async Task<Response> ReceiveChunkAsync(MobileUploadSession session, MobileUploadFile file,
        int index, Request request, Connection connection)
    {
        if (index < 0 || index >= file.ChunkCount || request.ContentLength != file.ChunkLength(index))
        {
            // Тело не читаем — соединение закрываем, чтобы не принимать мусор
            return new Response
            {
                Status = 400, Close = true,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Неверная часть файла" }, JsonOptions)
            };
        }
        bool already;
        lock (session.Gate) already = file.Completed || file.Received.Contains(index);
        if (already)
        {
            // Идемпотентно: часть уже принята — просто дочитываем и подтверждаем
            await connection.DrainAsync(request.ContentLength);
            session.Touch();
            return Json(200, new { ok = true, index });
        }
        if (!request.Headers.TryGetValue("X-Crc32", out var crcHeader) ||
            !uint.TryParse(crcHeader, System.Globalization.NumberStyles.HexNumber, null, out var expectedCrc))
        {
            return new Response
            {
                Status = 400, Close = true,
                Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Нет контрольной суммы части" }, JsonOptions)
            };
        }

        var crc = Crc32.Initial;
        var lastNotify = Environment.TickCount64;
        await using (var stream = new FileStream(file.PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                         1024 * 1024, FileOptions.Asynchronous))
        {
            stream.Position = (long)index * file.ChunkSize;
            await connection.ReadBodyAsync(request.ContentLength, async (buffer, offset, count) =>
            {
                crc = Crc32.Update(crc, buffer, offset, count);
                await stream.WriteAsync(buffer.AsMemory(offset, count));
                if (Environment.TickCount64 - lastNotify > 1000)
                {
                    lastNotify = Environment.TickCount64;
                    session.Touch();
                }
            });
            await stream.FlushAsync();
        }
        if (Crc32.Finish(crc) != expectedCrc)
        {
            AppLog.Error($"QR-сессия {session.LogId}: часть {index} файла пришла повреждённой, будет отправлена снова");
            return Json(422, new { error = "Часть файла повреждена при передаче", index });
        }
        lock (session.Gate) file.Received.Add(index);
        session.Touch(MobileSessionState.Receiving);
        return Json(200, new { ok = true, index });
    }

    /// <summary>
    /// Завершение файла. Проверка SHA-256 и передача в чат для 5 ГБ может занять минуты,
    /// поэтому выполняется в фоне: ответ сразу «обрабатывается», телефон опрашивает статус файла.
    /// Повторный вызов идемпотентен.
    /// </summary>
    private static Task<Response> CompleteFileAsync(MobileUploadSession session, MobileUploadFile file)
    {
        lock (session.Gate)
        {
            if (file.Completed || file.Completing || file.Error.Length > 0)
                return Task.FromResult(Json(200, FileStatus(session, file)));
            var missing = Enumerable.Range(0, file.ChunkCount).Where(i => !file.Received.Contains(i)).Take(50).ToList();
            if (missing.Count > 0)
                return Task.FromResult(Json(409, new { error = "Переданы не все части файла", missing }));
            file.Completing = true;
        }
        _ = Task.Run(() => FinishFileAsync(session, file));
        return Task.FromResult(Json(200, FileStatus(session, file)));
    }

    private static async Task FinishFileAsync(MobileUploadSession session, MobileUploadFile file)
    {
        string? error;
        try
        {
            var info = new FileInfo(file.PartPath);
            if (!info.Exists || info.Length != file.Size)
            {
                error = "Размер собранного файла не совпадает — отправьте файл ещё раз";
            }
            else
            {
                string hash;
                await using (var stream = new FileStream(file.PartPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var sha = SHA256.Create())
                    hash = Convert.ToHexString(await sha.ComputeHashAsync(stream)).ToLowerInvariant();
                // Успех — только когда программа зарегистрировала вложение в чате/заявке
                error = session.Closed ? "Окно загрузки на компьютере закрыто"
                    : await session.Deliver(file.PartPath, file.Name, hash, file.Size);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"QR-сессия {session.LogId}: не удалось обработать полученный файл", ex);
            error = "Компьютер не смог сохранить файл: " + ex.Message;
        }
        lock (session.Gate)
        {
            file.Completing = false;
            if (error is null) file.Completed = true;
            else file.Error = error;
        }
        if (error is not null)
        {
            try { File.Delete(file.PartPath); } catch { }
            session.SetError(error);
            return;
        }
        AppLog.Info($"QR-сессия {session.LogId}: получен файл ({file.Size / 1024} КБ)");
        session.Touch(MobileSessionState.Done);
    }

    private static void CancelIncomplete(MobileUploadSession session)
    {
        List<MobileUploadFile> removed;
        lock (session.Gate)
        {
            removed = session.Files.Values.Where(f => !f.Completed && !f.Completing).ToList();
            foreach (var file in removed) session.Files.Remove(file.Id);
        }
        foreach (var file in removed)
        {
            try { File.Delete(file.PartPath); } catch { }
        }
        session.Touch();
    }
}
