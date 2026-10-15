using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CombatLogViewer;

/// <summary>
/// Starts OBS without showing it (in the system tray, Replay Buffer running) and closes it again.
/// OBS has no true headless mode; this is the closest it gets.
/// </summary>
static class ObsProcess
{
    /// <summary>The configured obs64.exe, else the installed one (from the registry or the default folder).</summary>
    public static string? FindExe(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var candidates = new List<string>();
        try
        {
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\OBS Studio", "", null) is string dir)
                candidates.Add(Path.Combine(dir, "bin", "64bit", "obs64.exe"));
        }
        catch { }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit", "obs64.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsRunning => Process.GetProcessesByName("obs64").Length > 0;

    /// <summary>Starts OBS minimized to the tray with the Replay Buffer on.</summary>
    public static Process? Start(string exe) =>
        Process.Start(new ProcessStartInfo(exe)
        {
            // OBS must run from its own folder or it can't find its locale files and refuses to start.
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            // --disable-missing-files-check: no "some sources' files are missing" prompt blocking a hidden start.
            // (OBS 32 has no flag to skip its safe-mode prompt after a crash; a clean close never triggers it.)
            Arguments = "--minimize-to-tray --startreplaybuffer --disable-missing-files-check",
            UseShellExecute = false,
        });

    /// <summary>
    /// Asks OBS to close as if its window's X were clicked (it saves its settings), even while it's hidden
    /// in the tray. True if it exited within <paramref name="wait"/>.
    /// </summary>
    public static async Task<bool> CloseAsync(Process obs, TimeSpan wait)
    {
        if (obs.HasExited) return true;
        foreach (var hwnd in TopLevelWindows(obs.Id))
            if (WindowTitle(hwnd).StartsWith("OBS ", StringComparison.Ordinal)) // "OBS 32.1.0 - Profile: …"
                PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        using var timeout = new CancellationTokenSource(wait);
        try { await obs.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { }
        return obs.HasExited;
    }

    static List<IntPtr> TopLevelWindows(int pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == pid) found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
