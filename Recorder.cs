using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CombatLogViewer;

/// <summary>
/// Links OBS Replay Buffer clips to battles. When a battle's log appears it waits until
/// SaveDelaySeconds after the battle ended (time for the stats showcase), asks OBS to save the replay
/// buffer, then trims the clip to start LeadInSeconds before the battle and moves it into the Battle
/// folder as recording.mp4. Replays saved with the OBS hotkey are linked the same way. The untrimmed
/// original goes to the Recycle Bin.
/// </summary>
public sealed partial class Recorder : IAsyncDisposable
{
    public const string RecordingName = "recording.mp4";

    /// <summary>A replay saved this long after a battle ended still belongs to it.</summary>
    static readonly TimeSpan MatchWindow = TimeSpan.FromMinutes(3);

    readonly string logsDir;
    readonly AppSettings settings;
    readonly FileSystemWatcher watcher;
    readonly CancellationTokenSource stop = new();
    readonly HashSet<string> busy = new(StringComparer.OrdinalIgnoreCase); // battles being saved or processed
    readonly Dictionary<string, DateTime> seen = new(StringComparer.OrdinalIgnoreCase);
    ObsClient? obs;

    /// <summary>One-line OBS connection state for the sidebar.</summary>
    public event Action<string>? StatusChanged;
    /// <summary>Something worth telling the user (a recording saved, a replay that matched nothing, an error).</summary>
    public event Action<string>? Message;
    /// <summary>A battle log appeared or a recording was linked: the list should be re-read.</summary>
    public event Action? BattlesChanged;

    public Recorder(string logsDir, AppSettings settings)
    {
        this.logsDir = logsDir;
        this.settings = settings;
        watcher = new FileSystemWatcher(logsDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => OnLogsChanged(e.FullPath);
        watcher.Changed += (_, e) => OnLogsChanged(e.FullPath);
        watcher.Renamed += (_, e) => OnLogsChanged(e.FullPath);
    }

    /// <summary>Starts watching for battles and, if auto-save is on, connecting to OBS. Call after subscribing to events.</summary>
    public void Start()
    {
        watcher.EnableRaisingEvents = true;
        _ = Task.Run(() => PollLoop(stop.Token));
        if (!settings.ObsAutoSave)
        {
            StatusChanged?.Invoke("Auto-save is off");
            return;
        }
        _ = Task.Run(() => ConnectLoop(stop.Token)); // starts OBS first if StartObs is on and it isn't running
    }

    /// <summary>The OBS this viewer started (kept across settings changes), so only that one is closed on exit.</summary>
    static Process? startedObs;
    static DateTime lastLaunch = DateTime.MinValue;

    /// <summary>Whether OBS is running, each time the connection is checked; false shows the Start OBS button.</summary>
    public event Action<bool>? ObsRunningChanged;

    /// <summary>Starts OBS hidden in the system tray, Replay Buffer on, unless it's already running.</summary>
    public void LaunchObs()
    {
        if (ObsProcess.IsRunning) return;
        lastLaunch = DateTime.Now;
        if (ObsProcess.FindExe(settings.ObsPath) is not { } exe)
        {
            Message?.Invoke("Couldn't find OBS to start it (looked in its usual install folder).");
            return;
        }
        try
        {
            startedObs = ObsProcess.Start(exe);
            ObsRunningChanged?.Invoke(true);
            StatusChanged?.Invoke("Starting OBS…");
            Message?.Invoke("Started OBS in the system tray.");
        }
        catch (Exception e) { Message?.Invoke($"Couldn't start OBS ({e.Message})."); }
    }

    // ---- OBS connection ------------------------------------------------------------------------

    async Task ConnectLoop(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            bool running = ObsProcess.IsRunning;
            // Keep OBS running while the viewer is open; wait between attempts so a failing start can't loop.
            if (!running && settings.StartObs && DateTime.Now - lastLaunch > TimeSpan.FromSeconds(30))
            {
                LaunchObs();
                running = ObsProcess.IsRunning;
            }
            ObsRunningChanged?.Invoke(running);

            if (obs is not { IsConnected: true })
            {
                try
                {
                    var client = new ObsClient();
                    var lost = new TaskCompletionSource();
                    client.Disconnected += _ => lost.TrySetResult();
                    client.ReplayBufferSaved += path => _ = Task.Run(() => OnReplaySaved(path));
                    await client.ConnectAsync(settings.ObsHost, settings.ObsPort, settings.ObsPassword, cancel);
                    obs = client;

                    var status = await client.RequestAsync("GetReplayBufferStatus");
                    if (status?["outputActive"]?.GetValue<bool>() != true)
                    {
                        await client.RequestAsync("StartReplayBuffer");
                        Message?.Invoke("Started the OBS Replay Buffer.");
                    }
                    StatusChanged?.Invoke("Connected · Replay Buffer on");
                    await Task.WhenAny(lost.Task, Task.Delay(Timeout.Infinite, cancel));
                    if (!cancel.IsCancellationRequested) StatusChanged?.Invoke("Lost connection to OBS, retrying…");
                }
                catch (UnauthorizedAccessException e) { StatusChanged?.Invoke(e.Message); }
                catch (Exception e) when (!cancel.IsCancellationRequested)
                {
                    StatusChanged?.Invoke(
                        e is InvalidOperationException ? e.Message
                        : !ObsProcess.IsRunning ? (settings.StartObs ? "OBS isn't running; starting it again shortly." : "OBS isn't running.")
                        : startedObs is { HasExited: false } && DateTime.Now - startedObs.StartTime < TimeSpan.FromSeconds(30) ? "Starting OBS…"
                        : "OBS is running, but its WebSocket server is off (Tools → WebSocket Server Settings → Enable → Apply).");
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancel); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Tries a connection with the given settings once; returns null on success or the problem.</summary>
    public static async Task<string?> TestAsync(string host, int port, string password)
    {
        await using var client = new ObsClient();
        try
        {
            await client.ConnectAsync(host, port, password, CancellationToken.None);
            await client.RequestAsync("GetReplayBufferStatus");
            return null;
        }
        catch (InvalidOperationException e) when (e.Message.Contains("Replay")) { return "Connected, but the Replay Buffer isn't set up in OBS (Settings → Output)."; }
        catch (UnauthorizedAccessException e) { return e.Message; }
        catch (Exception) when (!ObsProcess.IsRunning) { return "OBS isn't running. Open it (or tick Start OBS above and save), then test again."; }
        catch (Exception)
        {
            return "OBS is running, but nothing answered on that port. Its WebSocket server is probably off: in OBS, "
                 + "Tools → WebSocket Server Settings → tick Enable WebSocket server → Apply. (Show Connect Info doesn't turn it on.)";
        }
    }

    // ---- New battle logs -----------------------------------------------------------------------

    // The logger fills the Battle folder elsewhere during the fight and moves it into
    // <logs>\<date>\ when the battle ends (creating the date folder if needed); combat-log.json lands a
    // few seconds later. A moved-in folder raises one event for the folder, none for the files in it, so
    // every change is mapped to the Battle folder it belongs to, and a poll backs the watcher up.
    void OnLogsChanged(string path)
    {
        foreach (var folder in BattleFoldersFor(path))
            _ = Task.Run(() => CheckBattle(folder));
    }

    static IEnumerable<string> BattleFoldersFor(string path)
    {
        if (LogReader.IsBattleFolder(path)) return [path];                     // the Battle folder itself
        if (Path.GetDirectoryName(path) is { } parent && LogReader.IsBattleFolder(parent)) return [parent]; // a file in it
        if (Directory.Exists(path) && LogReader.LooksLikeDateFolder(path))      // a date folder moved/created
            return Directory.EnumerateDirectories(path).Where(LogReader.IsBattleFolder).ToList();
        return [];
    }

    /// <summary>Backup for missed watcher events: checks today's and yesterday's battles every 10 seconds.</summary>
    async Task PollLoop(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), cancel); } catch (OperationCanceledException) { return; }
            try
            {
                foreach (var day in new[] { DateTime.Now.Date, DateTime.Now.Date.AddDays(-1) })
                {
                    string dateDir = Path.Combine(logsDir, day.ToString("yyyy-MM-dd"));
                    if (Directory.Exists(dateDir)) OnLogsChanged(dateDir);
                }
            }
            catch { /* folder briefly unavailable; try again next time */ }
        }
    }

    async Task CheckBattle(string folder)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1)); // let the write or move finish
            if (ReadTimes(folder) is not { Ended: { } ended }) return; // battle still running
            lock (seen)
            {
                if (seen.ContainsKey(folder)) return; // many events per battle; act once
                seen[folder] = ended;
            }
            if (DateTime.Now - ended > MatchWindow) return; // an old battle, not one that just ended
            await OnBattleEnded(folder, ended);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Message?.Invoke($"{Describe(folder)}: something went wrong handling this battle ({e.Message})."); }
    }

    async Task OnBattleEnded(string folder, DateTime ended)
    {
        // Refresh the list once the combat log is written, so the new run shows complete.
        string log = Path.Combine(folder, "combat-log.json");
        for (int i = 0; i < 30 && !File.Exists(log); i++) await Task.Delay(500);
        await Task.Delay(1000);
        BattlesChanged?.Invoke();
        if (!settings.ObsAutoSave) return;

        // Same rule as the Completed runs filter. (Replays saved with the OBS hotkey are always kept.)
        if (settings.SaveOnlyCompleted)
        {
            double? duration = File.Exists(log) ? LogReader.ReadDuration(log) : null;
            if (duration is { } d && d <= Battle.CompleteSeconds)
            {
                Message?.Invoke($"{Describe(folder)}: not a completed run ({d:0.#}s of battle, needs over {Battle.CompleteSeconds:0}s), so no recording was saved.");
                return;
            }
            // No combat log to check: save anyway, a recording is easier to delete than to get back.
        }

        var saveAt = ended + TimeSpan.FromSeconds(settings.SaveDelaySeconds);
        var wait = saveAt - DateTime.Now;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, stop.Token);

        // The OBS hotkey may already have saved (and linked) this battle in the meantime.
        lock (busy) if (busy.Contains(folder) || HasRecording(folder)) return;
        if (obs is not { IsConnected: true } client)
        {
            Message?.Invoke($"{Describe(folder)}: OBS isn't connected, so no recording was saved.");
            return;
        }
        try { await client.RequestAsync("SaveReplayBuffer"); } // OBS answers with ReplayBufferSaved → OnReplaySaved
        catch (Exception e) { Message?.Invoke($"{Describe(folder)}: OBS couldn't save the replay ({e.Message})."); }
    }

    // ---- Saved replays -------------------------------------------------------------------------

    async Task OnReplaySaved(string replay)
    {
        DateTime savedAt = File.Exists(replay) ? File.GetCreationTime(replay) : DateTime.Now; // the clip ends here
        string? folder;
        lock (busy)
        {
            folder = FindBattle(savedAt);
            if (folder != null) busy.Add(folder);
        }
        if (folder == null)
        {
            Message?.Invoke($"Replay saved, but no battle ended in the {MatchWindow.TotalMinutes:0} minutes before it, so it was left in {Path.GetDirectoryName(replay)}.");
            return;
        }
        try
        {
            string result = await LinkAsync(replay, folder, savedAt);
            Message?.Invoke($"{Describe(folder)}: {result}");
            BattlesChanged?.Invoke();
        }
        catch (Exception e)
        {
            Message?.Invoke($"{Describe(folder)}: couldn't save the recording ({e.Message}). The replay is still at {replay}.");
        }
        finally { lock (busy) busy.Remove(folder); }
    }

    /// <summary>The battle a replay saved at <paramref name="savedAt"/> belongs to: the latest one that
    /// ended up to MatchWindow before the save and has no recording yet.</summary>
    string? FindBattle(DateTime savedAt)
    {
        var candidates = new List<(string Folder, DateTime Ended)>();
        foreach (var dateDir in Directory.EnumerateDirectories(logsDir))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(dateDir), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || day < savedAt.Date.AddDays(-1) || day > savedAt.Date) continue;
            foreach (var folder in Directory.EnumerateDirectories(dateDir))
                if (LogReader.IsBattleFolder(folder) && !busy.Contains(folder) && !HasRecording(folder)
                    && ReadTimes(folder) is { Ended: { } ended }
                    && ended <= savedAt.AddSeconds(2) && savedAt - ended <= MatchWindow)
                    candidates.Add((folder, ended));
        }
        return candidates.OrderByDescending(c => c.Ended).Select(c => c.Folder).FirstOrDefault();
    }

    /// <summary>Trims the replay to start LeadInSeconds before the battle and moves it into the folder.</summary>
    async Task<string> LinkAsync(string replay, string folder, DateTime savedAt)
    {
        await WaitUntilReadable(replay);
        string target = Path.Combine(folder, RecordingName);
        var times = ReadTimes(folder);
        string? ffmpeg = FindFfmpeg(settings.FfmpegPath);

        if (ffmpeg == null || times?.Started is not { } started)
        {
            File.Move(replay, target);
            return ffmpeg == null
                ? "recording saved (untrimmed: ffmpeg wasn't found; set its path in OBS settings)."
                : "recording saved (untrimmed: the log has no start time).";
        }

        double length = await DurationAsync(ffmpeg, replay);
        // The clip covers [savedAt - length, savedAt]; cut where the lead-in before the battle begins.
        double cut = Math.Max(0, (started.AddSeconds(-settings.LeadInSeconds) - savedAt.AddSeconds(-length)).TotalSeconds);
        string temp = Path.Combine(folder, "recording.partial.mp4");
        // Stream copy (no re-encode): the cut snaps back to the keyframe before it, so a few seconds more lead-in.
        var (code, log) = await RunAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-ss", cut.ToString("0.000", CultureInfo.InvariantCulture),
             "-i", replay, "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", temp]);
        if (code != 0 || !File.Exists(temp) || new FileInfo(temp).Length < 100_000)
        {
            try { File.Delete(temp); } catch { }
            File.Move(replay, target);
            return $"recording saved untrimmed (ffmpeg failed: {log.Trim().Split('\n').LastOrDefault()}).";
        }
        File.Move(temp, target);
        RecycleBin.Send(replay);
        string note = cut > 0 ? $"trimmed {TimeSpan.FromSeconds(cut):m\\:ss} off the start" : "kept whole (the battle starts near the beginning of the clip)";
        return $"recording saved ({note}; original sent to the Recycle Bin).";
    }

    // ---- Helpers -------------------------------------------------------------------------------

    public static bool HasRecording(string folder) => File.Exists(Path.Combine(folder, RecordingName));

    static string Describe(string folder) => $"{Path.GetFileName(Path.GetDirectoryName(folder))} {Path.GetFileName(folder)}";

    record struct Times(DateTime? Started, DateTime? Ended);

    [GeneratedRegex(@"^(awake|destroy)_unix_ms=(\d+)", RegexOptions.Multiline)] private static partial Regex TimeLine();

    static Times? ReadTimes(string folder)
    {
        try
        {
            DateTime? started = null, ended = null;
            foreach (Match m in TimeLine().Matches(File.ReadAllText(Path.Combine(folder, "battle.txt"))))
            {
                var t = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(m.Groups[2].Value)).LocalDateTime;
                if (m.Groups[1].Value == "awake") started = t; else ended = t;
            }
            return new Times(started, ended);
        }
        catch { return null; }
    }

    static async Task WaitUntilReadable(string path)
    {
        for (int i = 0; i < 60; i++)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) return; }
            catch (IOException) { await Task.Delay(500); }
        }
    }

    /// <summary>The configured ffmpeg, else one on PATH or installed by winget.</summary>
    public static string? FindFfmpeg(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");
        string links = Path.Combine(winget, "Links", "ffmpeg.exe");
        if (File.Exists(links)) return links;
        try
        {
            var packages = Path.Combine(winget, "Packages");
            if (Directory.Exists(packages))
                return Directory.EnumerateDirectories(packages, "*FFmpeg*")
                    .SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories)).FirstOrDefault();
        }
        catch { }
        return null;
    }

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")] private static partial Regex DurationLine();

    static async Task<double> DurationAsync(string ffmpeg, string file)
    {
        var (_, log) = await RunAsync(ffmpeg, ["-hide_banner", "-i", file]); // prints the info, then "no output" (non-zero exit)
        var m = DurationLine().Match(log);
        if (!m.Success) throw new InvalidOperationException("couldn't read the clip's length");
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
             + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    static async Task<(int Code, string Log)> RunAsync(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stderr);
    }

    public ValueTask DisposeAsync() => ShutdownAsync(closeObs: false);

    /// <summary>Stops watching and disconnects; with <paramref name="closeObs"/> (the viewer is closing) also
    /// closes OBS if this viewer started it. The Replay Buffer is stopped first so OBS exits without asking.</summary>
    public async ValueTask ShutdownAsync(bool closeObs)
    {
        if (closeObs && settings.CloseObsOnExit && startedObs is { HasExited: false } started)
        {
            if (obs is { IsConnected: true } client)
                try { await client.RequestAsync("StopReplayBuffer"); await Task.Delay(1000); } catch { }
            await ObsProcess.CloseAsync(started, TimeSpan.FromSeconds(15));
            startedObs = null;
        }
        stop.Cancel();
        watcher.Dispose();
        if (obs != null) await obs.DisposeAsync();
    }
}

/// <summary>Moves a file to the Recycle Bin (recoverable), like deleting it in Explorer.</summary>
static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo;
        public ushort fFlags; public int fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public static void Send(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE, pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        SHFileOperation(ref op);
    }
}
