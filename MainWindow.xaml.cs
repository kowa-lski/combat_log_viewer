using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace CombatLogViewer;

public partial class MainWindow : Window
{
    List<Battle> battles = [];
    string? logsDir;
    bool loading;

    public MainWindow()
    {
        InitializeComponent();
        var settings = AppSettings.Load();
        MinimalBox.IsChecked = settings.Minimal;
        DummyBox.IsChecked = settings.DummyOnly;
        CompleteBox.IsChecked = settings.CompleteOnly;
        Loaded += async (_, _) =>
        {
            logsDir = FindLogsFolder();
            if (logsDir == null && !PickFolder())
            {
                StatusText.Text = "Choose your Combat Logs folder (the one with the dated folders) to get started.";
                return;
            }
            await LoadAsync();
            await StartRecorderAsync();
        };
        Closing += async (_, e) =>
        {
            // Closing OBS takes a moment: hide now, finish in the background, then really close.
            if (recorder == null) return;
            e.Cancel = true;
            Hide();
            var r = recorder;
            recorder = null;
            await r.ShutdownAsync(closeObs: true);
            Close();
        };
    }

    /// <summary>Command line, then the last folder used, then the folder the exe is in (or its parent).</summary>
    static string? FindLogsFolder()
    {
        var args = Environment.GetCommandLineArgs();
        var candidates = new List<string?> { args.Length > 1 ? args[1] : null, AppSettings.Load().LogsFolder };
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(exeDir); d != null; d = d.Parent)
            candidates.Add(d.FullName);
        candidates.Add(Environment.CurrentDirectory);
        return candidates.FirstOrDefault(c => c != null && LogReader.LooksLikeLogsFolder(c));
    }

    bool PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose your Combat Logs folder", InitialDirectory = logsDir ?? "" };
        if (dlg.ShowDialog(this) != true) return false;
        if (!LogReader.LooksLikeLogsFolder(dlg.FolderName))
        {
            MessageBox.Show(this, "That folder has no dated (YYYY-MM-DD) folders of combat logs in it.", "Combat Log Viewer");
            return false;
        }
        logsDir = dlg.FolderName;
        return true;
    }

    async Task LoadAsync()
    {
        if (loading || logsDir == null) return;
        loading = true;
        StatusText.Text = "Reading combat logs…";
        FolderText.Text = logsDir;
        var sw = Stopwatch.StartNew();
        try
        {
            string dir = logsDir;
            battles = await Task.Run(() => LogReader.ReadAll(dir));
            (AppSettings.Load() with { LogsFolder = logsDir }).Save();
        }
        catch (Exception e)
        {
            StatusText.Text = $"Could not read the logs: {e.Message}";
            loading = false;
            return;
        }
        StatusText.Text = "";
        Title = $"Combat Log Viewer — {battles.Count} runs (read in {sw.Elapsed.TotalSeconds:0.0}s)";

        string? previous = DayBox.SelectedItem as string;
        var days = battles.Select(b => b.Date).Distinct().OrderDescending().ToList();
        DayBox.ItemsSource = days;
        DayBox.SelectedItem = previous != null && days.Contains(previous) ? previous : days.FirstOrDefault();
        loading = false;
        ShowDay();
        if (days.Count == 0) StatusText.Text = "No combat logs found in this folder.";
    }

    /// <summary>Lists the selected day's runs, or, while the search box has text, matching runs from every day.</summary>
    void ShowDay()
    {
        if (loading || DayBox.SelectedItem is not string day) return;
        var search = new RunSearch(SearchBox.Text);
        bool searching = !search.IsEmpty;
        DayBox.IsEnabled = !searching;

        var pool = searching ? battles.Where(search.Matches).ToList() : battles.Where(b => b.Date == day).ToList();
        var runs = pool
            .Where(b => DummyBox.IsChecked != true || b.IsDummy)
            .Where(b => CompleteBox.IsChecked != true || b.IsComplete)
            .ToList();
        long? best = runs.Max(b => b.Score);
        IEnumerable<Battle> ordered = SortBox.SelectedIndex switch
        {
            1 => runs.OrderByDescending(b => b.Date, StringComparer.Ordinal).ThenBy(b => b.SortNumber).ThenBy(b => b.SortCopy),
            2 => runs.OrderByDescending(b => b.Score ?? -1),
            3 => runs.OrderByDescending(b => b.Damage ?? -1),
            _ => runs.OrderByDescending(b => b.StartedAt ?? DateTime.MinValue),
        };
        RunList.ItemsSource = ordered.Select(b => new BattleVM(b, best != null && b.Score == best, showDate: searching)).ToList();
        if (RunList.Items.Count > 0) RunList.ScrollIntoView(RunList.Items[0]);

        static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
        string count = searching
            ? $"{Plural(runs.Count, "matching run")} on {Plural(runs.Select(b => b.Date).Distinct().Count(), "day")}"
            : runs.Count == pool.Count ? $"{runs.Count} runs" : $"{runs.Count} of {pool.Count} runs";
        SummaryText.Text = $"{count}\nBest score: {Fmt.Int(best)}\nBest damage: {Fmt.Int(runs.Max(b => b.Damage))}";
        StatusText.Text = runs.Count > 0 ? ""
            : searching ? "No runs match the search."
            : pool.Count > 0 ? "No runs on this day match the filters." : "";
    }

    void Day_Changed(object sender, SelectionChangedEventArgs e) => ShowDay();
    void Search_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) ShowDay(); }

    void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) SearchBox.Clear();
    }
    void Sort_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) ShowDay(); }
    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        (AppSettings.Load() with { DummyOnly = DummyBox.IsChecked == true, CompleteOnly = CompleteBox.IsChecked == true }).Save();
        ShowDay();
    }

    void Minimal_Changed(object sender, RoutedEventArgs e)
    {
        bool minimal = MinimalBox.IsChecked == true;
        RunList.ItemTemplate = (DataTemplate)FindResource(minimal ? "MinimalTemplate" : "BattleTemplate");
        if (IsLoaded) (AppSettings.Load() with { Minimal = minimal }).Save();
    }

    /// <summary>The run whose card contains the element under the mouse, if any (not the scrollbar).</summary>
    BattleVM? RunAt(object originalSource)
    {
        var source = originalSource as DependencyObject;
        while (source is FrameworkContentElement fce) source = fce.Parent; // a Run inside a TextBlock
        return source != null && ItemsControl.ContainerFromElement(RunList, source) is ListBoxItem { Content: BattleVM run } ? run : null;
    }

    /// <summary>Right-click a run to copy its team line or open its folder.</summary>
    void RunList_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (RunAt(e.OriginalSource) is not { } run) return;
        var menu = new ContextMenu();
        if (run.Battle.Members is not null)
        {
            var copy = new MenuItem { Header = $"Copy team line ({run.Team})" };
            copy.Click += (_, _) => Clipboard.SetText(run.Team);
            menu.Items.Add(copy);
        }
        var show = new MenuItem { Header = "Show folder in Explorer" };
        show.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{run.Battle.Folder}\"");
        menu.Items.Add(show);
        menu.IsOpen = true;
        e.Handled = true;
    }

    // Dragging a run drags its Battle folder through the shell (ShellDrag), exactly like dragging the
    // folder out of File Explorer, so it can be dropped on a browser upload box or another folder.
    Point dragStart;
    BattleVM? dragRun;

    void RunList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragRun = RunAt(e.OriginalSource);
        dragStart = e.GetPosition(RunList);
    }

    void RunList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (dragRun is not { } run || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(RunList) - dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragRun = null;
        if (!Directory.Exists(run.Battle.Folder)) return;
        if (ShellDrag.DoDragDrop(run.Battle.Folder)) return;
        var data = new DataObject(DataFormats.FileDrop, new[] { run.Battle.Folder }); // fallback: plain file drop
        DragDrop.DoDragDrop(RunList, data, DragDropEffects.Copy | DragDropEffects.Link);
    }

    // While one of our runs is dragged over this window, accept it as a Copy (dropping it here does
    // nothing) so the cursor matches the rest of the drag instead of showing "no drop". The shell helper
    // keeps the folder thumbnail drawn. Drags from other apps are not accepted.
    IntPtr Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;
    Point ScreenPos(DragEventArgs e) => PointToScreen(e.GetPosition(this));
    DragDropEffects OwnDragEffect => ShellDrag.Active ? DragDropEffects.Copy : DragDropEffects.None;

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = OwnDragEffect;
        ShellDrag.TargetHelper.Enter(Hwnd, e.Data, ScreenPos(e), (int)e.Effects);
        e.Handled = true;
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = OwnDragEffect;
        ShellDrag.TargetHelper.Over(ScreenPos(e), (int)e.Effects);
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e) => ShellDrag.TargetHelper.Leave();

    void Window_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None; // a run dropped back on the viewer: nothing to do
        ShellDrag.TargetHelper.Drop(e.Data, ScreenPos(e), (int)e.Effects);
        e.Handled = true;
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!PickFolder()) return;
        await LoadAsync();
        await StartRecorderAsync();
    }

    // ---- OBS recording (see Recorder) --------------------------------------------------------------

    Recorder? recorder;
    bool reloadQueued;

    /// <summary>(Re)starts watching the logs folder and, if enabled, the OBS connection.</summary>
    async Task StartRecorderAsync()
    {
        if (recorder != null) await recorder.DisposeAsync();
        recorder = null;
        if (logsDir == null) return;
        var r = new Recorder(logsDir, AppSettings.Load());
        r.StatusChanged += s => Dispatcher.BeginInvoke(() => ObsStatusText.Text = s);
        r.Message += m => Dispatcher.BeginInvoke(() => ObsMessageText.Text = $"{DateTime.Now:HH:mm}  {m}");
        r.BattlesChanged += () => Dispatcher.BeginInvoke(ReloadSoon);
        r.ObsRunningChanged += running => Dispatcher.BeginInvoke(() =>
            StartObsButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible);
        StartObsButton.Visibility = Visibility.Collapsed;
        recorder = r;
        r.Start();
    }

    /// <summary>Re-reads the logs after a battle is logged or a recording saved (once, if several arrive together).</summary>
    async void ReloadSoon()
    {
        if (reloadQueued) return;
        reloadQueued = true;
        await Task.Delay(500);
        while (loading) await Task.Delay(200);
        reloadQueued = false;
        await LoadAsync();
    }

    async void ObsSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ObsSettingsWindow(AppSettings.Load()) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        dlg.Result.Save();
        await StartRecorderAsync();
    }

    void StartObs_Click(object sender, RoutedEventArgs e)
    {
        StartObsButton.Visibility = Visibility.Collapsed;
        recorder?.LaunchObs();
    }

    void Recording_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BattleVM { Battle.Recording: { } video } && File.Exists(video))
            Process.Start(new ProcessStartInfo(video) { UseShellExecute = true });
    }
}
