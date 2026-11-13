namespace RepairAlert;

internal static class VersionCompare
{
    public static bool IsNewerVersion(string current, string candidate)
    {
        var c = Parse(current);
        var n = Parse(candidate);
        for (var i = 0; i < Math.Max(c.Length, n.Length); i++)
        {
            var a = i < c.Length ? c[i] : 0;
            var b = i < n.Length ? n[i] : 0;
            if (b != a) return b > a;
        }
        return false;
    }

    private static int[] Parse(string version)
    {
        var parts = version.Trim().TrimStart('v', 'V').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<int>();
        foreach (var part in parts)
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            result.Add(digits.Length > 0 ? int.Parse(digits) : 0);
        }
        return result.Count > 0 ? result.ToArray() : new[] { 0 };
    }

    public static string Normalize(string version)
    {
        var parts = Parse(version).ToList();
        while (parts.Count > 2 && parts[^1] == 0) parts.RemoveAt(parts.Count - 1);
        return string.Join('.', parts);
    }
}
