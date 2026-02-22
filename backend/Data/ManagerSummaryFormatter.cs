namespace KhajikiSort.Data;

public static class ManagerSummaryFormatter
{
    public static string Build(string summary, string recommendation, int maxLength = 180)
    {
        var s = Normalize(summary);
        var r = Normalize(recommendation);

        var baseText = string.IsNullOrWhiteSpace(r)
            ? s
            : $"{s} Action: {r}";

        if (baseText.Length <= maxLength)
        {
            return baseText;
        }

        var cut = baseText[..Math.Max(0, maxLength - 3)].TrimEnd();
        return $"{cut}...";
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace("  ", " ")
            .Trim();
    }
}
