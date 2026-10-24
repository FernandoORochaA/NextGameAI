using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NextGameAI.Web.Services.Igdb;

public static class IgdbSearchRelevance
{
    public static IReadOnlyList<IgdbGameResult> Rank(IEnumerable<IgdbGameResult> candidates, string term)
    {
        var query = Normalize(term);
        // LINQ OrderBy is stable: equal scores retain the external order, never release-year order.
        return Array.AsReadOnly(candidates.OrderByDescending(game => Score(game, query)).Take(10).ToArray());
    }

    private static int Score(IgdbGameResult game, string query)
    {
        var title = Normalize(game.Title);
        var combined = Normalize(game.Title + " " + game.EditionTitle);
        var text = title == query ? 1000 : combined == query ? 950 :
            Math.Max(Match(title, query), Match(combined, query) - 50);
        return text + (game.GameTypeId switch
        {
            0 => 40, // Main Game
            8 => 38, // Remake
            9 => 28, // Remaster
            10 => 26, // Expanded Game
            11 => 20, // Port
            4 => 15, // Standalone Expansion
            1 or 2 => 5, // DLC / Expansion
            3 or 13 => 0, // Bundle / Pack
            _ => 10 // Unknown types and other content remain eligible.
        });
    }

    private static int Match(string title, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        if (words.Length == 0) return 0;
        var tokens = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var hits = words.Count(tokens.Contains);
        var prefix = title.StartsWith(query + " ", StringComparison.Ordinal) ? 40 : 0;
        if (hits == words.Length) return 700 + prefix + 30 * hits / Math.Max(tokens.Count, 1);
        if (hits > 0) return 300 * hits / words.Length + prefix;
        return title.Contains(query, StringComparison.Ordinal) ? 100 : 0;
    }

    private static string Normalize(string text)
    {
        var result = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            result.Append(Rune.IsLetterOrDigit(rune) ? Rune.ToUpperInvariant(rune).ToString() : " ");
        }
        return Regex.Replace(result.ToString(), @"\s+", " ").Trim();
    }
}
