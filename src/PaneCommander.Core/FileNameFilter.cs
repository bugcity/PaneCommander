namespace PaneCommander.Core;

public readonly record struct NameMatch(int Start, int Length);
public static class FileNameFilter
{
    public static bool Matches(string name, string query) => string.IsNullOrEmpty(query) || name.Contains(query, StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<NameMatch> Ranges(string name, string query)
    {
        if (string.IsNullOrEmpty(query)) return Array.Empty<NameMatch>();
        var result = new List<NameMatch>();
        for (var next = 0; next <= name.Length - query.Length;)
        {
            var index = name.IndexOf(query, next, StringComparison.OrdinalIgnoreCase); if (index < 0) break;
            if (result.Count > 0 && index <= result[^1].Start + result[^1].Length)
            { var previous = result[^1]; result[^1] = new(previous.Start, index + query.Length - previous.Start); }
            else result.Add(new(index, query.Length));
            next = index + 1;
        }
        return result;
    }
}
