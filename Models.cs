namespace CombatLogViewer;

public sealed record DiscStat(int Key, int BaseValue, int AddValue);

public sealed record Disc(int Slot, int Id, int Level, int Star, DiscStat? Main, IReadOnlyList<DiscStat> Subs)
{
    /// <summary>Drive Disc set id as gachabase numbers it: 33041 (set 330, S-rank, slot 1) -> 33000.</summary>
    public int SetId => Id / 100 * 100;
}

public sealed record Member(
    int AvatarId, string Name, int? Mindscape, int? Refine,
    int? WeaponId, int? WeaponLevel, double? Damage, IReadOnlyList<Disc> Discs);

public sealed record Battle(
    string Date, string Name, string Folder, int SortNumber, int SortCopy, DateTime? StartedAt,
    long? Score, double? Duration, double? Damage, double? EnemyHp, IReadOnlyList<Member>? Members,
    string? Recording = null)
{
    /// <summary>
    /// A run against the training dummy (~3.35 billion HP), by either of two HP readings: the enemy's HP
    /// in the log (logs from 2026-10-10 on), or an estimate from the score, since a full-HP kill scores
    /// 90,000: HP ≈ damage × 90,000 / score. The estimate runs high for fast kills that hit the 65,000 score
    /// cap (≤1.2B seen), and no other enemy has even 1 billion HP, so 2 billion separates them with a wide
    /// margin. Runs with neither reading (quit before scoring, older logs) count as not dummy.
    /// </summary>
    public bool IsDummy => EnemyHp > 2e9 || (Score > 0 && Damage > 0 && Damage * 90_000 / Score > 2e9);

    /// <summary>A run that went (nearly) the full timer rather than ending early.</summary>
    public bool IsComplete => Duration > CompleteSeconds;

    /// <summary>Runs longer than this on the battle clock count as completed (filter and OBS auto-save).</summary>
    public const double CompleteSeconds = 160;
}

/// <summary>Names of the game's property keys used on Drive Discs.</summary>
public static class Stats
{
    static readonly Dictionary<int, string> Known = new()
    {
        [11102] = "HP%", [11103] = "HP",
        [12102] = "ATK%", [12103] = "ATK",
        [13102] = "DEF%", [13103] = "DEF",
        [12202] = "Impact%",
        [20103] = "Crit Rate", [21103] = "Crit DMG",
        [23103] = "PEN Ratio", [23203] = "PEN",
        [30502] = "Energy Regen%", [30503] = "Energy Regen",
        [31203] = "Anomaly Proficiency",
        [31402] = "Anomaly Mastery%", [31403] = "Anomaly Mastery",
        [31503] = "Physical DMG", [31603] = "Fire DMG",
        [31703] = "Ice DMG", [31803] = "Electric DMG",
        [31903] = "Ether DMG",
    };

    public static string Name(int key) => Known.TryGetValue(key, out var name) ? name : $"Stat {key}";
}
