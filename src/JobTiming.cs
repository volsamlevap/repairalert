namespace RepairAlert;

internal static class JobTiming
{
    public static string Elapsed(DateTime startedAt, DateTime now)
    {
        if (startedAt == default) return "00:00";
        var elapsed = now - startedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}";
    }
}
