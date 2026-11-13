using System.Security.Cryptography;
using System.Text;

namespace RepairAlert;

/// <summary>Результат проверки кода сопряжения на стороне мастера.</summary>
internal enum PairingCheck
{
    /// <summary>Код верный — устройство сопрягается, код гаснет.</summary>
    Ok,
    /// <summary>Код неверный — попытка засчитана, после пяти режим отключается.</summary>
    WrongCode,
    /// <summary>Активного кода нет: кнопку ещё не нажимали, код истёк или режим отключён.</summary>
    Inactive
}

/// <summary>
/// Одноразовый код сопряжения на стороне мастера.
/// Пока не нажата кнопка «Добавить компьютер», кода нет и новые устройства
/// подключиться не могут. Код действует 5 минут, гаснет после первого
/// успешного сопряжения; пять неверных попыток отключают режим добавления.
/// Уже сопряжённые устройства сюда не попадают — они подключаются
/// по сохранённому ключу через Hello-доказательство.
/// </summary>
internal sealed class PairingSession
{
    public const int CodeLifetimeSeconds = 300;
    public const int MaxBadAttempts = 5;
    public const string LockoutMessage =
        "Режим добавления отключён: были неверные попытки. Нажмите «Добавить компьютер» ещё раз";

    private readonly Func<DateTime> _clock;
    private string? _code;
    private DateTime _expiresAt;
    private int _badAttempts;

    public PairingSession(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.Now);

    /// <summary>Режим отключён из-за пяти неверных попыток (до нового нажатия кнопки).</summary>
    public bool LockedOut { get; private set; }

    /// <summary>Почему сейчас нет активного кода — для журнала и точных подсказок:
    /// «кнопку ещё не нажимали», «код истёк по времени», «режим отключён после неверных попыток».</summary>
    public string InactiveReason { get; private set; } = "кнопку «Добавить компьютер» ещё не нажимали";

    /// <summary>Сколько неверных кодов введено за время действия текущего кода.</summary>
    public int BadAttempts => _badAttempts;

    /// <summary>Код существует, не истёк и режим не отключён.</summary>
    public bool IsActive
    {
        get
        {
            ExpireIfNeeded();
            return _code is not null && !LockedOut;
        }
    }

    /// <summary>Оставшееся время действия кода (для отсчёта на экране мастера).</summary>
    public TimeSpan TimeLeft => IsActive ? _expiresAt - _clock() : TimeSpan.Zero;

    /// <summary>Текущий код — только для показа на экране мастера. В журнал не писать!</summary>
    public string? CurrentCode => IsActive ? _code : null;

    /// <summary>Нажатие «Добавить компьютер»: новый одноразовый код на 5 минут.</summary>
    public string Begin()
    {
        _code = SecurityService.GenerateCode();
        _expiresAt = _clock().AddSeconds(CodeLifetimeSeconds);
        _badAttempts = 0;
        LockedOut = false;
        InactiveReason = "";
        return _code;
    }

    /// <summary>
    /// Проверка подписи Pair от менеджера. Подпись строится из кода, поэтому
    /// сравнение подписей через CryptographicOperations.FixedTimeEquals равносильно
    /// сравнению самого кода, но без его передачи по сети. При успехе код гаснет
    /// (одноразовый), при ошибке попытка засчитывается; после пяти режим отключается.
    /// </summary>
    public PairingCheck TryVerify(string clientId, string nonce, string receivedSig, out byte[]? secret)
    {
        secret = null;
        ExpireIfNeeded();
        if (_code is null || LockedOut) return PairingCheck.Inactive;
        var candidate = SecurityService.DeriveSecret(_code, clientId);
        var expected = SecurityService.PairProof(candidate, clientId, nonce);
        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(receivedSig ?? ""));
        if (ok)
        {
            secret = candidate;
            _code = null;          // одноразовый: погас после первого успешного сопряжения
            _badAttempts = 0;
            return PairingCheck.Ok;
        }
        _badAttempts++;
        if (_badAttempts >= MaxBadAttempts)
        {
            _code = null;
            LockedOut = true;      // режим добавления отключён до нового нажатия кнопки
            InactiveReason = "режим отключён после 5 неверных попыток";
        }
        return PairingCheck.WrongCode;
    }

    private void ExpireIfNeeded()
    {
        if (_code is not null && _clock() >= _expiresAt)
        {
            _code = null;
            InactiveReason = "код истёк по времени (5 минут)";
        }
    }
}
