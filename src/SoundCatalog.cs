namespace RepairAlert;

internal enum AlertKind { Request, JobChat, GeneralChat, Completed }

internal sealed record SoundOption(string Id, string Name)
{
    public override string ToString() => Name;
}

internal static class SoundCatalog
{
    public const string CustomId = "custom";

    public static readonly IReadOnlyList<SoundOption> Options = new[]
    {
        new SoundOption("standard", "Стандартный"),
        new SoundOption("soft", "Мягкий"),
        new SoundOption("short", "Короткий")
    };

    /// <summary>Варианты для настроек: встроенные + «Свой звук…» (выбор файла).</summary>
    public static readonly IReadOnlyList<SoundOption> OptionsWithCustom = Options
        .Concat(new[] { new SoundOption(CustomId, "Свой звук…") }).ToArray();

    public static string Normalize(string? soundId) =>
        soundId is "standard" or "soft" or "short" or CustomId ? soundId : "standard";

    public static bool QuietHoursActive(string from, string to, TimeSpan now)
    {
        if (!TryParseTime(from, out var start) || !TryParseTime(to, out var end))
            return false;
        if (start == end) return false;
        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    private static bool TryParseTime(string value, out TimeSpan time)
    {
        time = default;
        var parts = (value ?? "").Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) ||
            h is < 0 or > 23 || m is < 0 or > 59)
            return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    public static string GetFile(AlertKind kind, string? soundId, string? customPath = null)
    {
        soundId = Normalize(soundId);
        if (soundId == CustomId)
        {
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath)) return customPath;
            soundId = "standard"; // свой файл пропал или не выбран — играем стандартный
        }
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepairAlert", "Sounds");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{kind}-{soundId}.wav");
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, CreateWave(kind, soundId));
        File.Copy(temp, path, true);
        File.Delete(temp);
        return path;
    }

    /// <summary>Копирует выбранный пользователем звуковой файл в папку программы
    /// и возвращает новый путь (null при ошибке).</summary>
    public static string? StoreCustomFile(AlertKind kind, string sourcePath)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RepairAlert", "Sounds");
            Directory.CreateDirectory(directory);
            var extension = Path.GetExtension(sourcePath);
            if (extension.Length == 0 || extension.Length > 5) extension = ".wav";
            var target = Path.Combine(directory, $"custom-{kind}{extension}");
            File.Copy(sourcePath, target, true);
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось сохранить свой звук", ex);
            return null;
        }
    }

    public static byte[] CreateWave(AlertKind kind, string? soundId)
    {
        const int sampleRate = 44100;
        soundId = Normalize(soundId);
        var (samples, amplitude) = Samples(kind, soundId);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            var dataSize = samples.Sum(tone => (int)(sampleRate * tone.Seconds)) * 2;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            foreach (var tone in samples)
            {
                var count = (int)(sampleRate * tone.Seconds);
                for (var i = 0; i < count; i++)
                {
                    var fade = Math.Min(1.0, Math.Min(i, count - 1 - i) / 240.0);
                    var value = tone.Frequency == 0
                        ? (short)0
                        : (short)(Math.Sin(2 * Math.PI * tone.Frequency * i / sampleRate) * amplitude * fade);
                    writer.Write(value);
                }
            }
        }
        return stream.ToArray();
    }

    private static (Tone[] Samples, int Amplitude) Samples(AlertKind kind, string soundId)
    {
        if (soundId == "soft")
        {
            return kind switch
            {
                AlertKind.Request => (new[] { new Tone(700, 0.28), new Tone(1050, 0.28), new Tone(700, 0.28), new Tone(1050, 0.28), new Tone(0, 0.60) }, 15000),
                AlertKind.JobChat => (new[] { new Tone(520, 0.16), new Tone(0, 0.08), new Tone(780, 0.30), new Tone(0, 0.65) }, 14000),
                AlertKind.GeneralChat => (new[] { new Tone(330, 0.20), new Tone(0, 0.80) }, 14000),
                AlertKind.Completed => (new[] { new Tone(392, 0.18), new Tone(0, 0.05), new Tone(523, 0.18), new Tone(0, 0.05), new Tone(659, 0.45) }, 15000),
                _ => (new[] { new Tone(440, 0.20), new Tone(0, 0.30) }, 14000)
            };
        }
        if (soundId == "short")
        {
            return kind switch
            {
                AlertKind.Request => (new[] { new Tone(1200, 0.12), new Tone(0, 0.05), new Tone(1200, 0.12), new Tone(0, 0.45) }, 19000),
                AlertKind.JobChat => (new[] { new Tone(900, 0.14), new Tone(0, 0.55) }, 17000),
                AlertKind.GeneralChat => (new[] { new Tone(520, 0.10), new Tone(0, 0.06), new Tone(520, 0.10), new Tone(0, 0.55) }, 16000),
                AlertKind.Completed => (new[] { new Tone(880, 0.12), new Tone(0, 0.04), new Tone(1175, 0.28) }, 17000),
                _ => (new[] { new Tone(660, 0.15), new Tone(0, 0.25) }, 17000)
            };
        }
        return kind switch
        {
            AlertKind.Request => (new[] { new Tone(950, 0.24), new Tone(1400, 0.24), new Tone(950, 0.24), new Tone(1400, 0.24), new Tone(0, 0.55) }, 21000),
            AlertKind.JobChat => (new[] { new Tone(660, 0.18), new Tone(0, 0.07), new Tone(990, 0.28), new Tone(0, 0.60) }, 19000),
            AlertKind.GeneralChat => (new[] { new Tone(440, 0.13), new Tone(0, 0.08), new Tone(440, 0.13), new Tone(0, 0.70) }, 17000),
            AlertKind.Completed => (new[] { new Tone(523, 0.16), new Tone(0, 0.04), new Tone(659, 0.16), new Tone(0, 0.04), new Tone(784, 0.18), new Tone(0, 0.04), new Tone(1046, 0.42) }, 18000),
            _ => (new[] { new Tone(660, 0.18), new Tone(0, 0.20) }, 17000)
        };
    }

    private readonly record struct Tone(int Frequency, double Seconds);
}
