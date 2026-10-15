using System.ComponentModel;
using System.Globalization;

namespace CombatLogViewer;

public sealed class BattleVM
{
    public BattleVM(Battle b, bool isBest, bool showDate = false)
    {
        Battle = b;
        IsBest = isBest;
        ShowDate = showDate;
        Members = b.Members?.Select(m => new MemberVM(m, b.Damage)).ToList() ?? [];
        Team = b.Members is null ? "No loadout file for this run" : string.Join(", ", Members.Select(m => m.TeamPart));
    }

    public Battle Battle { get; }
    public bool IsBest { get; }
    public string Name => Battle.Name;
    public string Score => Fmt.Int(Battle.Score);
    public string Damage => Fmt.Int(Battle.Damage);
    public bool ShowDate { get; } // search results come from several days
    public bool HasRecording => Battle.Recording != null;
    public string StartTime =>
        (ShowDate ? Battle.Date + " " : "") +
        (Battle.StartedAt is { } t ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : "");
    public string Duration => Battle.Duration is { } d ? $"{d.ToString("0.#", CultureInfo.InvariantCulture)}s" : "—";
    public string Team { get; }
    public IReadOnlyList<MemberVM> Members { get; }

    // Fixed slots instead of a list: while scrolling, WPF reuses a card's slot visuals for the next
    // run, where a nested list would rebuild every teammate from scratch.
    public MemberVM? M1 => Members.ElementAtOrDefault(0);
    public MemberVM? M2 => Members.ElementAtOrDefault(1);
    public MemberVM? M3 => Members.ElementAtOrDefault(2);
}

public sealed class MemberVM
{
    public MemberVM(Member m, double? teamDamage)
    {
        Member = m;
        Agent = AssetStore.Get(AssetKind.Agent, m.AvatarId);
        WEngine = m.WeaponId is { } wid ? AssetStore.Get(AssetKind.WEngine, wid) : null;
        Investment = $"{Fmt.Q(m.Mindscape)}{Fmt.Q(m.Refine)}";
        TeamPart = $"{Investment} {m.Name}";

        string share = m.Damage is { } d && teamDamage is > 0 ? $" ({d / teamDamage.Value:P0})" : "";
        Info = $"M{Fmt.Q(m.Mindscape)}" + (m.Damage is { } dmg ? $" · {Fmt.Short(dmg)} dmg{share}" : "");
        WEngineInfo = $"R{Fmt.Q(m.Refine)}" + (m.WeaponLevel is { } lv ? $" · Lv{lv}" : "");

        Sets = m.Discs
            .GroupBy(d => d.SetId)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .Select(g => new SetVM(AssetStore.Get(AssetKind.DiscSet, g.Key), g.Count()))
            .ToList();

        // One string per section (one TextBlock each) keeps the card's visual tree small for smooth scrolling.
        MainStats = string.Join("\n", m.Discs
            .Where(d => d.Slot >= 4 && d.Main != null)
            .OrderBy(d => d.Slot)
            .Select(d => $"[{d.Slot}] {Stats.Name(d.Main!.Key)}"));

        // Substat rolls summed per stat over all discs; stats with 2 rolls or fewer are filler and left out.
        Substats = string.Join("\n", m.Discs
            .SelectMany(d => d.Subs)
            .GroupBy(s => s.Key)
            .Select(g => (Key: g.Key, Rolls: g.Sum(s => s.AddValue)))
            .Where(s => s.Rolls > 2)
            .OrderByDescending(s => s.Rolls)
            .Select(s => $"{Stats.Name(s.Key)} ×{s.Rolls}"));
    }

    public Member Member { get; }
    public string Name => Member.Name;
    public Asset Agent { get; }
    public Asset? WEngine { get; }
    public string Investment { get; }
    public string TeamPart { get; }
    public string Info { get; }
    public string WEngineInfo { get; }
    public IReadOnlyList<SetVM> Sets { get; }
    public SetVM? Set1 => Sets.ElementAtOrDefault(0); // six discs make at most three sets
    public SetVM? Set2 => Sets.ElementAtOrDefault(1);
    public SetVM? Set3 => Sets.ElementAtOrDefault(2);
    public string MainStats { get; }
    public string Substats { get; }
    public bool HasSets => Sets.Count > 0;
    public bool HasMainStats => MainStats.Length > 0;
    public bool HasSubstats => Substats.Length > 0;
}

public sealed class SetVM : INotifyPropertyChanged
{
    public SetVM(Asset set, int pieces)
    {
        Set = set;
        Pieces = pieces;
        // The set's name and bonuses can arrive after the card is built (first download).
        // Listen only until they do, so the shared Asset doesn't keep every card alive.
        if (set.Bonus2 == null) set.PropertyChanged += OnSetChanged;
    }

    void OnSetChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
        if (Set.Bonus2 != null) Set.PropertyChanged -= OnSetChanged;
    }

    public Asset Set { get; }
    public int Pieces { get; }
    public string Label => Pieces >= 4 ? "4pc" : "2pc";
    public bool HasFour => Pieces >= 4;

    /// <summary>Hover text. A plain string makes WPF build the ToolTip only when it opens.</summary>
    public string ToolTip => $"{Set.Title}\n\n2-Piece: {Set.Bonus2}" + (HasFour ? $"\n\n4-Piece: {Set.Bonus4}" : "");

    public event PropertyChangedEventHandler? PropertyChanged;
}

static class Fmt
{
    public static string Q(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "?";

    public static string Int(double? v) => v is { } x ? Math.Round(x).ToString("N0", CultureInfo.InvariantCulture) : "—";
    public static string Int(long? v) => v is { } x ? x.ToString("N0", CultureInfo.InvariantCulture) : "—";

    public static string Short(double v) => v switch
    {
        >= 1e9 => (v / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "B",
        >= 1e6 => (v / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1e3 => (v / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => v.ToString("0", CultureInfo.InvariantCulture),
    };
}
