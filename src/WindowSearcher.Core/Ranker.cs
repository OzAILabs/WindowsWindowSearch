namespace WindowSearcher.Core;

public static class Ranker
{
    /// <summary>
    /// Converts a content hit's position in the FTS result list (0 = best BM25) into a score
    /// comparable with fuzzy title scores.
    /// </summary>
    public static double ContentScore(int position, int count) =>
        count <= 0 ? 0 : 0.9 - 0.35 * position / Math.Max(1, count);

    /// <summary>
    /// Final score: relevance dominates, recency breaks ties. Items matching both by title
    /// and by content get a small bonus.
    /// </summary>
    public static double Combine(double fuzzy, double content, TimeSpan age)
    {
        var relevance = Math.Max(fuzzy, content);
        if (relevance <= 0) return 0;
        if (fuzzy > 0 && content > 0) relevance = Math.Min(1.0, relevance + 0.1);
        return relevance * 0.8 + Recency(age) * 0.2;
    }

    /// <summary>1 for just now, 0.5 after an hour, approaching 0 for old/never-seen items.</summary>
    public static double Recency(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return 1.0 / (1.0 + age.TotalMinutes / 60.0);
    }

    /// <summary>
    /// Strips U+FFFC placeholders (embedded objects) and collapses whitespace in extracted text.
    /// </summary>
    public static string CleanText(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, maxChars));
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (sb.Length >= maxChars) break;
            if (ch == '￼' || ch == '​' || char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
