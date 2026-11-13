using System.Windows.Media;
using System.Windows.Threading;

namespace RepairAlert;

internal sealed class AlertService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private MediaPlayer? _player;
    private AlertKind _currentKind;
    private string _currentSoundId = "standard";
    private bool _looping;
    private bool _playing;
    private DateTime _mutedUntil = DateTime.MinValue;

    public AlertService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _timer.Tick += (_, _) => Stop();
    }

    public bool IsPlaying => _playing;

    public void MuteFor(TimeSpan duration)
    {
        _mutedUntil = DateTime.Now + duration;
        Stop();
    }

    public bool IsMuted => DateTime.Now < _mutedUntil;

    private bool IsSuppressed()
    {
        if (IsMuted) return true;
        var settings = SettingsStore.Load();
        return SoundCatalog.QuietHoursActive(settings.QuietFrom, settings.QuietTo, DateTime.Now.TimeOfDay);
    }

    public void Start(AlertKind kind)
    {
        if (IsSuppressed()) return;
        var soundId = SelectedSound(kind);
        if (IsPlaying && _looping && _currentKind == kind && _currentSoundId == soundId) return;
        PreparePlayer(kind, soundId, true);
        _timer.Interval = TimeSpan.FromMinutes(3);
        _timer.Start();
    }

    public void PlayOnce(AlertKind kind)
    {
        if (IsSuppressed()) return;
        PreparePlayer(kind, SelectedSound(kind), false);
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Start();
    }

    private void PreparePlayer(AlertKind kind, string soundId, bool looping)
    {
        Stop();
        _currentKind = kind;
        _currentSoundId = soundId;
        _looping = looping;
        var player = new MediaPlayer { Volume = looping ? 0.35 : 1.0 };
        _player = player;
        player.MediaEnded += OnMediaEnded;
        player.MediaFailed += (_, _) =>
        {
            _playing = false;
            System.Media.SystemSounds.Exclamation.Play();
        };
        player.Open(new Uri(SoundCatalog.GetFile(kind, soundId, CustomPath(kind)), UriKind.Absolute));
        _playing = true;
        player.Play();
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player) || _player is null) return;
        if (_looping && _playing)
        {
            _player.Volume = Math.Min(1.0, _player.Volume + 0.13);
            _player.Position = TimeSpan.Zero;
            _player.Play();
        }
        else
        {
            Stop();
        }
    }

    public void Stop()
    {
        _timer.Stop();
        _playing = false;
        var player = _player;
        _player = null;
        if (player is not null)
        {
            player.MediaEnded -= OnMediaEnded;
            try { player.Stop(); } catch { }
            try { player.Close(); } catch { }
        }
        _looping = false;
        _timer.Interval = TimeSpan.FromMinutes(3);
    }

    public void Dispose() => Stop();

    private static string SelectedSound(AlertKind kind)
    {
        var settings = SettingsStore.Load();
        return kind switch
        {
            AlertKind.Request => SoundCatalog.Normalize(settings.RequestSound),
            AlertKind.JobChat => SoundCatalog.Normalize(settings.JobChatSound),
            AlertKind.GeneralChat => SoundCatalog.Normalize(settings.GeneralChatSound),
            AlertKind.Completed => SoundCatalog.Normalize(settings.CompletedSound),
            _ => "standard"
        };
    }

    private static string CustomPath(AlertKind kind)
    {
        var settings = SettingsStore.Load();
        return kind switch
        {
            AlertKind.Request => settings.RequestSoundCustom,
            AlertKind.JobChat => settings.JobChatSoundCustom,
            AlertKind.GeneralChat => settings.GeneralChatSoundCustom,
            AlertKind.Completed => settings.CompletedSoundCustom,
            _ => ""
        };
    }
}
