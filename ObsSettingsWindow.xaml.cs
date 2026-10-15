using System.Globalization;
using System.Windows;

namespace CombatLogViewer;

public partial class ObsSettingsWindow : Window
{
    public ObsSettingsWindow(AppSettings s)
    {
        InitializeComponent();
        AutoSaveBox.IsChecked = s.ObsAutoSave;
        HostBox.Text = s.ObsHost;
        PortBox.Text = s.ObsPort.ToString(CultureInfo.InvariantCulture);
        PasswordBox.Password = s.ObsPassword;
        DelayBox.Text = s.SaveDelaySeconds.ToString(CultureInfo.InvariantCulture);
        LeadInBox.Text = s.LeadInSeconds.ToString(CultureInfo.InvariantCulture);
        FfmpegBox.Text = s.FfmpegPath ?? "";
        StartObsBox.IsChecked = s.StartObs;
        CompletedOnlyBox.IsChecked = s.SaveOnlyCompleted;
        CloseObsBox.IsChecked = s.CloseObsOnExit;
        obsPath = s.ObsPath;
        string? obsExe = ObsProcess.FindExe(s.ObsPath);
        ObsPathNote.Text = obsExe != null
            ? $"Only an OBS the viewer started is closed; an OBS you opened yourself is left alone. Using {obsExe}."
            : "OBS wasn't found in its usual install folder, so it can't be started automatically.";
        BufferNote.Text = "A clip can only reach back as far as OBS's Replay Buffer (Settings → Output → Replay Buffer → "
            + "Maximum Replay Time). It needs about battle length + both times above: a 270s battle with 25s and 10s needs 305s. "
            + "With a shorter buffer the clip just starts a little later.";
        string? found = Recorder.FindFfmpeg(null);
        FfmpegNote.Text = found != null
            ? $"Leave blank to use the one found at {found}."
            : "No ffmpeg found. Install it (winget install Gyan.FFmpeg) or enter the path to ffmpeg.exe. Without it, recordings are saved untrimmed.";
    }

    /// <summary>The settings as edited, applied to <paramref name="s"/>; null (with a message) if a number is invalid.</summary>
    public AppSettings? Apply(AppSettings s)
    {
        if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535) return Fail("Port must be a number from 1 to 65535.");
        if (!double.TryParse(DelayBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double delay) || delay is < 0 or > 120)
            return Fail("The save delay must be 0 to 120 seconds.");
        if (!double.TryParse(LeadInBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lead) || lead is < 0 or > 120)
            return Fail("The lead-in must be 0 to 120 seconds.");
        return (s with
        {
            ObsAutoSave = AutoSaveBox.IsChecked == true,
            ObsHost = HostBox.Text.Trim().Length > 0 ? HostBox.Text.Trim() : "localhost",
            ObsPort = port,
            SaveDelaySeconds = delay,
            LeadInSeconds = lead,
            FfmpegPath = string.IsNullOrWhiteSpace(FfmpegBox.Text) ? null : FfmpegBox.Text.Trim().Trim('"'),
            StartObs = StartObsBox.IsChecked == true,
            SaveOnlyCompleted = CompletedOnlyBox.IsChecked == true,
            CloseObsOnExit = CloseObsBox.IsChecked == true,
            ObsPath = obsPath,
        }).WithObsPassword(PasswordBox.Password);

        AppSettings? Fail(string message)
        {
            MessageBox.Show(this, message, "OBS recording");
            return null;
        }
    }

    public AppSettings? Result { get; private set; }

    readonly string? obsPath;

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out int port)) { TestResult.Text = "Port must be a number."; return; }
        TestResult.Text = "Connecting…";
        string? problem = await Recorder.TestAsync(HostBox.Text.Trim(), port, PasswordBox.Password);
        TestResult.Text = problem ?? "Connected to OBS. Replay Buffer is available.";
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = Apply(AppSettings.Load());
        if (Result != null) DialogResult = true;
    }
}
