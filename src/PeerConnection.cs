using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RepairAlert;

internal sealed record FileChunk(Guid MessageId, long Offset, byte[] Data);

internal sealed class PeerConnection : IDisposable
{
    private const byte FrameJson = 0x01;
    private const byte FrameChunk = 0x02;
    private const int HeaderBytes = 5;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _lastReceived;
    private int _activeWrites;
    private int _closed;
    private byte[]? _signKey;

    public event Action<AppMessage>? MessageReceived;
    public event Action<FileChunk>? ChunkReceived;
    public event Action? Disconnected;
    public event Action? SignatureViolation;

    public static int MaxControlBytes { get; set; } = 1024 * 1024;
    public static int MaxChunkPayload { get; set; } = FileTransferService.ChunkBytes + 64;

    public PeerConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        _lastReceived = Environment.TickCount64;
        _ = ReceiveLoop();
        _ = HeartbeatLoop();
    }

    public bool IsConnected => _closed == 0 && _client.Connected;

    /// <summary>IP удалённой стороны — для записи событий безопасности в журнал.</summary>
    public string RemoteAddress
    {
        get
        {
            try { return (_client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "неизвестен"; }
            catch { return "неизвестен"; }
        }
    }

    public void SetSignKey(byte[]? secret) => _signKey = secret is null ? null : (byte[])secret.Clone();

    public async Task SendAsync(AppMessage message)
    {
        if (_closed != 0) throw new InvalidOperationException("Соединение закрыто.");
        var key = _signKey;
        if (key is not null && message.Sig.Length == 0) message.Sig = SecurityService.Sign(key, message);
        var payload = Encoding.UTF8.GetBytes(message.ToJson());
        await SendFrameAsync(FrameJson, payload, payload.Length);
    }

    public async Task SendChunkAsync(Guid messageId, long offset, byte[] data, int length)
    {
        if (_closed != 0) throw new InvalidOperationException("Соединение закрыто.");
        if (length > FileTransferService.ChunkBytes) throw new InvalidOperationException("Чанк слишком большой.");
        var payload = new byte[24 + length];
        messageId.TryWriteBytes(payload.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), offset);
        Buffer.BlockCopy(data, 0, payload, 24, length);
        await SendFrameAsync(FrameChunk, payload, payload.Length);
    }

    private async Task SendFrameAsync(byte type, byte[] payload, int length)
    {
        Interlocked.Increment(ref _activeWrites);
        try
        {
            await _writeLock.WaitAsync();
            try
            {
                if (_closed != 0) throw new InvalidOperationException("Соединение закрыто.");
                var header = new byte[HeaderBytes];
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), length + 1);
                header[4] = type;
                await _stream.WriteAsync(header, 0, header.Length);
                await _stream.WriteAsync(payload, 0, length);
            }
            catch
            {
                Close(true);
                throw;
            }
            finally { _writeLock.Release(); }
        }
        finally { Interlocked.Decrement(ref _activeWrites); }
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (_closed == 0)
            {
                var header = await ReadExactAsync(HeaderBytes);
                if (header is null) break;
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4));
                var type = header[4];
                var max = type == FrameJson ? MaxControlBytes + 1 :
                    type == FrameChunk ? MaxChunkPayload + 1 : -1;
                if (length <= 1 || length > max)
                    throw new InvalidDataException("Входящий кадр превышает допустимый размер или имеет неизвестный тип.");
                var payload = await ReadExactAsync(length - 1);
                if (payload is null) break;
                Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
                if (type == FrameJson)
                {
                    var message = AppMessage.FromJson(Encoding.UTF8.GetString(payload));
                    if (message is null || message.Type == MessageTypes.Ping) continue;
                    var key = _signKey;
                    if (key is not null && !SecurityService.Verify(key, message))
                    {
                        AppLog.Error("Отклонено сообщение с неверной подписью: " + message.Type);
                        try { SignatureViolation?.Invoke(); } catch { }
                        break;
                    }
                    MessageReceived?.Invoke(message);
                }
                else
                {
                    if (payload.Length < 24) continue;
                    var id = new Guid(payload.AsSpan(0, 16));
                    var offset = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(16, 8));
                    var data = new byte[payload.Length - 24];
                    Buffer.BlockCopy(payload, 24, data, 0, data.Length);
                    ChunkReceived?.Invoke(new FileChunk(id, offset, data));
                }
            }
        }
        catch (Exception) when (_closed != 0) { }
        catch (Exception) { }
        finally { Close(true); }
    }

    private async Task<byte[]?> ReadExactAsync(int count)
    {
        var buffer = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            var read = await _stream.ReadAsync(buffer, filled, count - filled);
            if (read == 0) return null;
            filled += read;
            Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
        }
        return buffer;
    }

    private async Task HeartbeatLoop()
    {
        try
        {
            while (_closed == 0)
            {
                await Task.Delay(5000);
                if (_closed != 0) break;
                if (Volatile.Read(ref _activeWrites) > 0) continue;
                if (Environment.TickCount64 - Interlocked.Read(ref _lastReceived) > 25_000)
                {
                    Close(true);
                    break;
                }
                try
                {
                    var ping = new AppMessage { Type = MessageTypes.Ping };
                    if (_signKey is not null) ping.Sig = SecurityService.Sign(_signKey, ping);
                    await SendAsync(ping);
                }
                catch { }
            }
        }
        catch { }
    }

    public void Disconnect() => Close(true);

    private void Close(bool notify)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            try { _client.Close(); } catch { }
            if (notify)
            {
                try { Disconnected?.Invoke(); } catch { }
            }
        }
    }

    public void Dispose() => Close(false);
}
