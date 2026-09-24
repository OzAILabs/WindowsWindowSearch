namespace WindowSearcher.Core;

/// <summary>
/// Scores how well a short query matches window titles, process names and URLs.
/// Every query word must match somewhere; word-start matches beat mid-word matches,
/// which beat loose subsequence matches ("tcrnch" -> "techcrunch").
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Returns a score in [0, 1]; 0 means no match.</summary>
    public static double Score(string query, string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var terms = FtsQuery.Terms(query);
        if (terms.Count == 0) return 0;

        var hay = text.ToLowerInvariant();
        double total = 0;
        foreach (var term in terms)
        {
            var s = TermScore(term, hay);
            if (s <= 0) return 0;
            total += s;
        }
        var score = total / terms.Count;

        // Reward the whole query appearing as one phrase.
        var phrase = string.Join(' ', terms);
        if (terms.Count > 1 && hay.Contains(phrase, StringComparison.Ordinal))
            score = Math.Min(1.0, score + 0.15);
        return score;
    }

    /// <summary>Best score across the fields of one item, weighting the title highest.</summary>
    public static double ScoreFields(string query, string? title, string? process, string? url)
    {
        var titleScore = Score(query, title);
        // Query words may be spread across fields ("edge techcrunch").
        var combined = Score(query, $"{title} {process} {UrlForMatching(url)}") * 0.85;
        return Math.Max(titleScore, combined);
    }

    private static double TermScore(string term, string hay)
    {
        var best = 0.0;
        var idx = hay.IndexOf(term, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var atWordStart = idx == 0 || !char.IsLetterOrDigit(hay[idx - 1]);
            best = Math.Max(best, atWordStart ? 1.0 : 0.7);
            if (best >= 1.0) return best;
            idx = hay.IndexOf(term, idx + 1, StringComparison.Ordinal);
        }
        if (best > 0) return best;

        // Subsequence match, only for words long enough to be meaningful, and only
        // when the letters are reasonably close together.
        if (term.Length < 3) return 0;
        var start = -1;
        var pos = 0;
        foreach (var c in term)
        {
            pos = hay.IndexOf(c, pos);
            if (pos < 0) return 0;
            if (start < 0) start = pos;
            pos++;
        }
        var span = pos - start;
        if (span > term.Length * 3) return 0;
        return 0.25 + 0.25 * term.Length / span;
    }

    private static string UrlForMatching(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        var s = url;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        return s.Length > 200 ? s[..200] : s;
    }
}
