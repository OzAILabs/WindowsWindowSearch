using System.Text.RegularExpressions;

namespace WindowSearcher.Core;

public static partial class FtsQuery
{
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Token();

    /// <summary>
    /// Turns free text into a safe FTS5 query: every word becomes a quoted prefix term,
    /// and all terms must match ("token cost" -> "token"* "cost"*).
    /// Returns null when the input has no searchable words.
    /// </summary>
    public static string? Build(string? input, int maxTerms = 8)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var terms = Token().Matches(input)
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct()
            .Take(maxTerms)
            .Select(t => $"\"{t}\"*")
            .ToList();
        return terms.Count == 0 ? null : string.Join(' ', terms);
    }

    /// <summary>Lower-cased search words, used for highlighting and fuzzy matching.</summary>
    public static IReadOnlyList<string> Terms(string? input) =>
        string.IsNullOrWhiteSpace(input)
            ? []
            : Token().Matches(input).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
}
