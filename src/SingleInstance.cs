using System.IO.Pipes;
using System.Security.Principal;

namespace RepairAlert;

internal static class SingleInstance
{
    private static Mutex? _mutex;

    private static string Key
    {
        get
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                return identity.User?.Value ?? Environment.UserName;
            }
            catch { return Environment.UserName; }
        }
    }

    private static string MutexName => @"Global\RepairAlert.SingleInstance." + Key;
    private static string PipeName => "RepairAlert.Wake." + Key;

    public static bool TryAcquire()
    {
        try
        {
            var mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
            _mutex = mutex;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Проверка единственного экземпляра не удалась", ex);
            return true;
        }
    }

    public static void SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(700);
            client.WriteByte(1);
            client.Flush();
        }
        catch { }
    }

    public static void StartWakeListener(Action wake)
    {
        var thread = new Thread(() => WakeLoop(wake)) { IsBackground = true, Name = "SingleInstanceWake" };
        thread.Start();
    }

    private static void WakeLoop(Action wake)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnection();
                try { wake(); } catch { }
                try { if (server.IsConnected) server.Disconnect(); } catch { }
            }
            catch
            {
                Thread.Sleep(1000);
            }
        }
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }
}
