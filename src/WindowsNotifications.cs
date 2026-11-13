using System.Windows;

namespace RepairAlert;

internal sealed class WindowsNotifications : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private Action? _openAction;

    public WindowsNotifications(Window window, Action exitAction, Action? miniToggle = null)
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "СЕРВИС ЛЕГКО",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        try { _icon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); }
        catch { _icon.Icon = System.Drawing.SystemIcons.Application; }
        void OpenWindow() => window.Dispatcher.Invoke(() =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        });
        if (miniToggle is not null)
        {
            _icon.ContextMenuStrip.Items.Add("Мини-панель (быстрые действия)", null,
                (_, _) => window.Dispatcher.Invoke(miniToggle));
            // Одиночный клик левой кнопкой — мини-панель над треем.
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
                try { window.Dispatcher.Invoke(miniToggle); } catch { }
            };
        }
        _icon.ContextMenuStrip.Items.Add("Открыть СЕРВИС ЛЕГКО", null, (_, _) => OpenWindow());
        _icon.ContextMenuStrip.Items.Add("Закрыть программу", null, (_, _) =>
            window.Dispatcher.Invoke(exitAction));
        _icon.DoubleClick += (_, _) => OpenWindow();
        _icon.BalloonTipClicked += (_, _) =>
        {
            try { if (_openAction is not null) window.Dispatcher.Invoke(_openAction); } catch { }
        };
    }

    public void Show(string title, string text, Action? openAction = null)
    {
        _openAction = openAction;
        if (text.Length > 220) text = text[..220] + "…";
        _icon.ShowBalloonTip(7000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }

    public void SetStatus(string status)
    {
        var text = "СЕРВИС ЛЕГКО" + (status.Length > 0 ? " — " + status : "");
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
