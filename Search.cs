using System.Text.RegularExpressions;

namespace CombatLogViewer;

/// <summary>
/// The sidebar search. Every word must match: a number is a battle number ("15" or "battle 15" is
/// Battle 15, not 150), "(2)" picks the copy ("1 (2)" is Battle 1 (2)), and any other word is part
/// of a teammate's name ("yan" finds Yanagi; "phoenix yuzuha" needs both).
/// </summary>
public sealed partial class RunSearch
{
    [GeneratedRegex(@"\((\d+)\)|[^\s,()]+")] private static partial Regex Tokens();

    readonly int? number, copy;
    readonly List<string> names = [];

    public RunSearch(string query)
    {
        foreach (Match m in Tokens().Matches(query))
        {
            if (m.Groups[1].Success) copy = int.Parse(m.Groups[1].Value);
            else if (int.TryParse(m.Value, out int n)) number = n;
            else if (!m.Value.Equals("battle", StringComparison.OrdinalIgnoreCase)) names.Add(m.Value);
        }
        IsEmpty = number == null && copy == null && names.Count == 0;
    }

    public bool IsEmpty { get; }

    public bool Matches(Battle b) =>
        (number == null || b.SortNumber == number) &&
        (copy == null || b.SortCopy == copy) &&
        names.All(n => b.Members?.Any(m => m.Name.Contains(n, StringComparison.OrdinalIgnoreCase)) == true);
}
