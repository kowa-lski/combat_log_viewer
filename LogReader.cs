using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CombatLogViewer;

/// <summary>
/// Reads every &lt;date&gt;/Battle N folder: the team from endbattle_*_loadout.json, the score from the
/// endbattle_*.pb settlement and agent names, damage and duration from combat-log.json.
/// </summary>
public static partial class LogReader
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")] private static partial Regex DateRe();
    [GeneratedRegex(@"^Battle (\d+)(?: \((\d+)\))?$")] private static partial Regex BattleRe();

    sealed record RawBattle(
        string Date, string Name, string Folder, int Number, int Copy, DateTime? StartedAt, string? Recording, JsonElement? Loadout, long? Score,
        Dictionary<int, Dictionary<string, int>> NameVotes, Dictionary<string, double> DamageBy, double? Duration, double? EnemyHp);

    /// <summary>A "Battle N" folder inside a "YYYY-MM-DD" folder.</summary>
    public static bool IsBattleFolder(string dir) =>
        BattleRe().IsMatch(Path.GetFileName(dir)) && DateRe().IsMatch(Path.GetFileName(Path.GetDirectoryName(dir)) ?? "");

    /// <summary>The battle-clock duration from combat-log.json, or null if the file or field is missing.</summary>
    public static double? ReadDuration(string combatLog)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(combatLog));
            return doc.RootElement.TryGetProperty("durationSeconds", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
        }
        catch { return null; }
    }

    /// <summary>A "YYYY-MM-DD" folder.</summary>
    public static bool LooksLikeDateFolder(string dir) => DateRe().IsMatch(Path.GetFileName(dir));

    public static bool LooksLikeLogsFolder(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any(d => DateRe().IsMatch(Path.GetFileName(d)));

    public static List<Battle> ReadAll(string root)
    {
        var folders = new List<(string Date, string Dir, int Number, int Copy)>();
        foreach (var dateDir in Directory.EnumerateDirectories(root))
        {
            string date = Path.GetFileName(dateDir);
            if (!DateRe().IsMatch(date)) continue;
            foreach (var b in Directory.EnumerateDirectories(dateDir))
            {
                var m = BattleRe().Match(Path.GetFileName(b));
                if (!m.Success) continue;
                folders.Add((date, b, int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1));
            }
        }

        var raw = new ConcurrentBag<RawBattle>();
        Parallel.ForEach(folders, f =>
        {
            try { raw.Add(ReadFolder(f.Date, f.Dir, f.Number, f.Copy)); }
            catch (Exception e) { Console.Error.WriteLine($"{f.Dir}: {e.Message}"); }
        });

        // Agent names: from this battle's combat log, else from any other battle's.
        var globalVotes = new Dictionary<int, Dictionary<string, int>>();
        foreach (var r in raw)
            foreach (var (aid, votes) in r.NameVotes)
                foreach (var (name, n) in votes)
                    Add(Get(globalVotes, aid), name, n);

        string NameFor(int aid, RawBattle r)
        {
            var votes = r.NameVotes.GetValueOrDefault(aid) ?? globalVotes.GetValueOrDefault(aid);
            return votes is { Count: > 0 } ? votes.MaxBy(kv => kv.Value).Key : $"#{aid}";
        }

        return raw
            .OrderBy(r => r.Date, StringComparer.Ordinal).ThenBy(r => r.Number).ThenBy(r => r.Copy)
            .Select(r =>
            {
                List<Member>? members = null;
                if (r.Loadout is { } lo && lo.TryGetProperty("avatars", out var avatars))
                {
                    members = [];
                    foreach (var a in avatars.EnumerateArray())
                    {
                        int aid = a.GetProperty("avatar_id").GetInt32();
                        string name = NameFor(aid, r);
                        var weapon = a.TryGetProperty("weapon", out var w) && w.ValueKind == JsonValueKind.Object ? w : (JsonElement?)null;
                        members.Add(new Member(
                            aid, name,
                            a.TryGetProperty("mindscape", out var ms) ? IntOrNull(ms, "level") : null,
                            weapon is { } w1 ? IntOrNull(w1, "refine") : null,
                            weapon is { } w2 ? IntOrNull(w2, "id") : null,
                            weapon is { } w3 ? IntOrNull(w3, "level") : null,
                            r.DamageBy.Count > 0 ? r.DamageBy.GetValueOrDefault(name) : null,
                            ReadDiscs(a)));
                    }
                }
                double? damage = members is { Count: > 0 } && r.DamageBy.Count > 0 ? members.Sum(m => m.Damage ?? 0) : null;
                return new Battle(r.Date, r.Name, r.Folder, r.Number, r.Copy, r.StartedAt, r.Score, r.Duration, damage, r.EnemyHp, members, r.Recording);
            })
            .ToList();
    }

    static RawBattle ReadFolder(string date, string dir, int number, int copy)
    {
        JsonElement? loadout = null;
        var loadoutFile = Directory.EnumerateFiles(dir, "endbattle_*_loadout.json").Order().FirstOrDefault();
        if (loadoutFile != null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(loadoutFile));
            loadout = doc.RootElement.Clone();
        }

        long? score = null;
        var pb = Directory.EnumerateFiles(dir, "endbattle_*.pb").Order().FirstOrDefault();
        if (pb != null) score = ReadScore(File.ReadAllBytes(pb));

        var votes = new Dictionary<int, Dictionary<string, int>>();
        var damageBy = new Dictionary<string, double>();
        double? duration = null, enemyHp = null;
        var log = Path.Combine(dir, "combat-log.json");
        if (File.Exists(log))
            (duration, enemyHp) = ReadCombatLog(File.ReadAllBytes(log), votes, damageBy);

        return new RawBattle(date, Path.GetFileName(dir), Path.GetFullPath(dir), number, copy, ReadStartTime(dir), FindRecording(dir), loadout, score, votes, damageBy, duration, enemyHp);
    }

    /// <summary>The battle's video: recording.mp4 (saved by Recorder), else any .mp4 in the folder.</summary>
    static string? FindRecording(string dir)
    {
        string main = Path.Combine(dir, Recorder.RecordingName);
        return File.Exists(main) ? main : Directory.EnumerateFiles(dir, "*.mp4").Order().FirstOrDefault();
    }

    /// <summary>When the battle started: awake_unix_ms in battle.txt, else when the folder was created.</summary>
    static DateTime? ReadStartTime(string dir)
    {
        var txt = Path.Combine(dir, "battle.txt");
        if (File.Exists(txt))
            foreach (var line in File.ReadLines(txt))
                if (line.StartsWith("awake_unix_ms=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(14), out long ms))
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        try { return Directory.GetCreationTime(dir); } catch { return null; }
    }

    static List<Disc> ReadDiscs(JsonElement avatar)
    {
        var discs = new List<Disc>();
        if (!avatar.TryGetProperty("drive_discs", out var arr) || arr.ValueKind != JsonValueKind.Array) return discs;
        foreach (var d in arr.EnumerateArray())
        {
            var props = d.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => new DiscStat(
                    x.GetProperty("key").GetInt32(), x.GetProperty("base_value").GetInt32(), x.GetProperty("add_value").GetInt32())).ToList()
                : [];
            discs.Add(new Disc(
                IntOrNull(d, "slot") ?? 0, IntOrNull(d, "id") ?? 0, IntOrNull(d, "level") ?? 0, IntOrNull(d, "star") ?? 0,
                props.FirstOrDefault(), props.Skip(1).ToList()));
        }
        return discs;
    }

    /// <summary>
    /// Hits per (avatar id from skill id, attacker) and damage per attacker. Returns the duration and the
    /// enemy's full HP (largest HP-before-hit, enemy_hp_after + damage; logs from 2026-10-10 on).
    /// </summary>
    static (double? Duration, double? EnemyHp) ReadCombatLog(byte[] bytes, Dictionary<int, Dictionary<string, int>> votes, Dictionary<string, double> damageBy)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        double? duration = root.TryGetProperty("durationSeconds", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetDouble() : null;
        double? enemyHp = null;
        if (!root.TryGetProperty("hits", out var hits)) return (duration, enemyHp);

        void Count(string? attacker, JsonElement skill, JsonElement damage, JsonElement hpAfter)
        {
            if (attacker == null) return;
            if (skill.ValueKind == JsonValueKind.Number && skill.TryGetInt64(out long sid) && sid >= 1_000_000)
                Add(Get(votes, (int)(sid / 1000)), attacker, 1);
            if (damage.ValueKind == JsonValueKind.Number)
            {
                damageBy[attacker] = damageBy.GetValueOrDefault(attacker) + damage.GetDouble();
                if (hpAfter.ValueKind == JsonValueKind.Number)
                    enemyHp = Math.Max(enemyHp ?? 0, hpAfter.GetDouble() + damage.GetDouble());
            }
        }

        if (hits.ValueKind == JsonValueKind.Array)
        {
            // combat-log/1: one object per hit
            foreach (var h in hits.EnumerateArray())
                Count(h.TryGetProperty("attacker", out var a) ? a.GetString() : null,
                      h.TryGetProperty("skill_id", out var s) ? s : default,
                      h.TryGetProperty("damage", out var d) ? d : default,
                      h.TryGetProperty("enemy_hp_after", out var hp) ? hp : default);
        }
        else if (hits.ValueKind == JsonValueKind.Object)
        {
            // combat-log/2: columns + rows, repeated text stored as an index into values[column]
            var cols = hits.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
            int ia = cols.IndexOf("attacker"), isk = cols.IndexOf("skill_id"), idm = cols.IndexOf("damage"), ihp = cols.IndexOf("enemy_hp_after");
            if (ia < 0) return (duration, enemyHp);
            // Any column can be stored this way (newer logs do it for skill_id too), so decode each one used.
            JsonElement[]? Values(string column) =>
                hits.TryGetProperty("values", out var vals) && vals.TryGetProperty(column, out var v) ? v.EnumerateArray().ToArray() : null;
            JsonElement Cell(JsonElement row, int col, JsonElement[]? values) =>
                col < 0 ? default
                : values != null && row[col].ValueKind == JsonValueKind.Number ? values[row[col].GetInt32()]
                : row[col];
            JsonElement[]? attackerValues = Values("attacker"), skillValues = Values("skill_id"), damageValues = Values("damage"), hpValues = Values("enemy_hp_after");
            foreach (var row in hits.GetProperty("rows").EnumerateArray())
            {
                var attacker = Cell(row, ia, attackerValues);
                Count(attacker.ValueKind == JsonValueKind.String ? attacker.GetString() : null,
                      Cell(row, isk, skillValues), Cell(row, idm, damageValues), Cell(row, ihp, hpValues));
            }
        }
        return (duration, enemyHp);
    }

    /// <summary>The battle score in the settlement protobuf: field 27 of the message in top-level field 9.</summary>
    public static long? ReadScore(byte[] buf)
    {
        try
        {
            foreach (var (f, wire, val, start, len) in Fields(buf, 0, buf.Length))
                if (f == 9 && wire == 2)
                    foreach (var (f2, w2, v2, _, _) in Fields(buf, start, start + len))
                        if (f2 == 27 && w2 == 0) return (long)v2;
        }
        catch (IndexOutOfRangeException) { }
        return null;
    }

    static IEnumerable<(int Field, int Wire, ulong Value, int Start, int Length)> Fields(byte[] b, int i, int end)
    {
        while (i < end)
        {
            ulong key = Varint(b, ref i);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            switch (wire)
            {
                case 0: yield return (field, wire, Varint(b, ref i), 0, 0); break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    int n = (int)Varint(b, ref i);
                    yield return (field, wire, 0, i, n);
                    i += n;
                    break;
                default: yield break;
            }
        }
    }

    static ulong Varint(byte[] b, ref int i)
    {
        ulong result = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte c = b[i++];
            result |= (ulong)(c & 0x7F) << shift;
            if (c < 0x80) return result;
        }
    }

    static int? IntOrNull(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    static Dictionary<string, int> Get(Dictionary<int, Dictionary<string, int>> d, int key) =>
        d.TryGetValue(key, out var v) ? v : d[key] = [];

    static void Add(Dictionary<string, int> d, string key, int n) => d[key] = d.GetValueOrDefault(key) + n;
}
