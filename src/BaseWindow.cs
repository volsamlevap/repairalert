using System.Windows;

namespace RepairAlert;

internal abstract class BaseWindow : Window
{
    public bool IsDisposed { get; private set; }
    public bool Visible => IsVisible;
    public event EventHandler? Shown;
    public event EventHandler? FormClosed;

    protected BaseWindow()
    {
        ContentRendered += (_, _) => Shown?.Invoke(this, EventArgs.Empty);
        Closed += (_, _) =>
        {
            IsDisposed = true;
            FormClosed?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Show(Window owner)
    {
        Owner = owner;
        Show();
    }
}
