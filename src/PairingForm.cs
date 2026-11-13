using System.Windows;
using System.Windows.Controls;

namespace RepairAlert;

internal sealed class PairingForm : BaseWindow
{
    private readonly TextBox _codeInput;
    private readonly TextBlock _errorText;
    private readonly Button _submitButton;

    public event Action<string>? CodeSubmitted;
    public event Action? Cancelled;

    public PairingForm()
    {
        // Высота с запасом: заголовок, подсказка, поле кода, строка ошибки и обе кнопки
        // должны помещаться целиком — «Подключить»/«Отмена» не обрезаются краем окна
        Ui.Prepare(this, "Сопряжение с мастером — СЕРВИС ЛЕГКО", 420, 340, 380, 320);
        var content = new StackPanel { Margin = new Thickness(24) };
        Content = content;
        content.Children.Add(new TextBlock
        {
            Text = "Введите код с компьютера мастера",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(new TextBlock
        {
            Text = "Попросите мастера нажать «Добавить компьютер» в настройках (раздел «Подключение») — " +
                   "он увидит одноразовый код из 6 цифр. Код действует 5 минут. " +
                   "Это нужно один раз — дальше программа подключается сама.",
            Foreground = Ui.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _codeInput = new TextBox
        {
            FontSize = 24,
            MaxLength = 6,
            TextAlignment = TextAlignment.Center,
            Height = 46,
            Margin = new Thickness(0, 0, 0, 8)
        };
        System.Windows.Automation.AutomationProperties.SetName(_codeInput, "Код сопряжения из 6 цифр");
        _codeInput.TextChanged += (_, _) =>
        {
            _errorText.Visibility = Visibility.Collapsed;
            _submitButton.IsEnabled = _codeInput.Text.Trim().Length == 6;
        };
        content.Children.Add(_codeInput);
        _errorText = new TextBlock
        {
            Text = "Код неверный",
            Foreground = Ui.Danger,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8)
        };
        content.Children.Add(_errorText);
        _submitButton = Ui.ActionButton("Подключить", "PrimaryButton");
        _submitButton.IsEnabled = false;
        _submitButton.Click += (_, _) => Submit();
        content.Children.Add(_submitButton);
        var cancel = Ui.ActionButton("Отмена", "QuietButton");
        cancel.Click += (_, _) =>
        {
            Cancelled?.Invoke();
            Close();
        };
        content.Children.Add(cancel);
        Shown += (_, _) => _codeInput.Focus();
        // PreviewKeyDown — единый надёжный перехват Enter (как в чате и быстрой заявке)
        _codeInput.PreviewKeyDown += (_, e) =>
        {
            // Enter — подтвердить код; единое правило — EnterSend
            if (EnterSend.ShouldSend(e.Key == System.Windows.Input.Key.Return || e.Key == System.Windows.Input.Key.Enter,
                    System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                && _submitButton.IsEnabled) Submit();
        };
    }

    private static int LetterSpacingOrDefault() => 0;

    private void Submit()
    {
        var code = _codeInput.Text.Trim();
        if (code.Length != 6) return;
        CodeSubmitted?.Invoke(code);
    }

    public void ShowError(string text)
    {
        _errorText.Text = text;
        _errorText.Foreground = Ui.Danger;
        _errorText.Visibility = Visibility.Visible;
        _codeInput.SelectAll();
        _codeInput.Focus();
    }

    /// <summary>Нейтральная подсказка о ходе работы («Проверяем код…») — не ошибка.</summary>
    public void ShowInfo(string text)
    {
        _errorText.Text = text;
        _errorText.Foreground = Ui.Muted;
        _errorText.Visibility = Visibility.Visible;
    }
}
