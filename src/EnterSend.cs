namespace RepairAlert;

/// <summary>
/// Единое правило клавиши Enter для всех полей отправки и подтверждения:
/// Enter — отправить (или подтвердить), Shift+Enter — новая строка
/// в многострочных полях. Важно: в WPF основной Enter приходит как
/// Key.Return, а Key.Enter — только цифровой блок клавиатуры, поэтому
/// вызывающий код обязан передавать признак «это Enter» как
/// (e.Key == Key.Return || e.Key == Key.Enter) — это закреплено
/// автотестами-стражами. Решение «отправлять или новая строка» принимается
/// только здесь, чтобы поведение не «слетало» при правках интерфейса.
/// </summary>
public static class EnterSend
{
    /// <summary>true — отправлять/подтверждать; false — позволить полю вставить новую строку (Shift+Enter).</summary>
    public static bool ShouldSend(bool isEnterKey, bool shiftPressed) => isEnterKey && !shiftPressed;
}
