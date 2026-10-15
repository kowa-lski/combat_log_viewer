# Combat Log Viewer

Browse your combat-log runs by day. Each run shows its score, total damage and duration. For each agent it shows:

- the agent and W-Engine, with icons
- mindscape and refine
- Drive Disc sets, with their 2-piece and 4-piece bonuses
- disc main stats (slots 4–6)
- substat rolls per stat

## Using it

Put `CombatLogViewer.exe` in your `Combat Logs` folder (the one containing the `YYYY-MM-DD` folders) and run it. If it can't find the logs, it asks you to pick the folder, and it remembers your choice.

- **Search**: type a battle number or agent names to find runs across every day. Each card then shows its date. Every word must match:
  - `yanagi` or just `yan`: runs with that agent
  - `phoenix yuzuha`: runs with both
  - `15` or `battle 15`: Battle 15 and its copies (not Battle 150)
  - `1 (2)`: Battle 1 (2)

  The filters and sorting still apply. Esc clears the search and returns to the selected day.
- **Day**: pick a day; its runs are listed on the right. The run with the day's best score is highlighted.
- **Sort runs by**: most recent first (the default; each card shows its start time), battle number, score or total damage.
- **Minimal view**: one compact row per run with the battle, score and agent icons. The choice is remembered.
- **Show only**: tick **Dummy runs** for runs against the training dummy, and/or **Completed runs** for runs that lasted over 160 seconds. The summary then shows e.g. "19 of 21 runs". Both are remembered.
- **Refresh**: re-reads the folder after new runs.
- **Sharing a run**: drag a run (anywhere on its card, or by the ⠿ grip) onto your browser's upload box. It drops the run's whole Battle folder, the same as dragging the folder from File Explorer.
- Right-click a run to copy its team line or show its folder in Explorer.
- Hover a Drive Disc set to see its full 2-piece and 4-piece bonuses.

### OBS recordings

The viewer can save your OBS Replay Buffer after every battle and file the clip with that battle's logs.

1. In OBS, turn on **Tools → WebSocket Server Settings → Enable WebSocket server**, then click **Show Connect Info** for the port and password.
2. In the viewer, click **OBS settings…**, tick **Save the OBS Replay Buffer automatically**, enter the port and password, and click **Test connection**.
3. Keep the viewer open while you play. It starts OBS's Replay Buffer if it isn't running.

**Not opening OBS yourself:** tick **Start OBS hidden in the system tray when the viewer opens**. OBS then starts with no window, only its tray icon, with the Replay Buffer running. OBS has no true headless mode; this is the closest it gets. With **Close it again when the viewer closes**, the viewer stops the Replay Buffer and closes OBS normally on exit. An OBS you opened yourself is never closed. While the box is ticked, the viewer keeps OBS running: if OBS is closed or crashes, it's started again in the tray, at most once every 30 seconds. Whenever OBS isn't running, a **Start OBS** button also appears in the sidebar. OBS's **Settings → General → System Tray** must be enabled (it is by default).

After each battle:

- **Completed runs only:** by default, only runs over 160 seconds on the battle clock are saved, the same rule as the Completed runs filter. Shorter runs get a sidebar message instead. Replays saved with your OBS hotkey are always kept. Untick **Only save completed runs** in OBS settings to save every run.
- **Saving:** the viewer waits until 25 seconds after the battle ends, so your stats showcase is in the clip, then saves the replay. Replays you save with your OBS hotkey within 3 minutes of a battle ending are handled the same way.
- **Trimming:** the clip is cut to start 10 seconds before the battle, or up to 4 seconds earlier, because the cut must land on a keyframe. Trimming doesn't re-encode, so it takes about a second and doesn't lose quality.
- **Filing:** the trimmed clip is saved in the Battle folder as `recording.mp4`. The untrimmed original goes to the Recycle Bin.
- **In the viewer:** the run gets a **▶ Video** button, and the list refreshes by itself when a battle is logged.

Both times can be changed in OBS settings. Trimming uses ffmpeg (`winget install Gyan.FFmpeg`); without it, clips are moved untrimmed. OBS's Replay Buffer length (Settings → Output) must cover the battle plus both times, or the clip starts a little later than asked. Because the clip is in the Battle folder, dragging a run uploads the video too.

### What the numbers mean

- **Team line** (`61 Phoenix, 21 Yuzuha, 65 Remielle`): mindscape, refine, then name.
- **Score**: the score saved in the run's `endbattle_N.pb` file. It shows `—` for runs that were quit before a score was given.
- **Total damage**: the team's damage, summed from every hit in `combat-log.json`.
- **Dummy run**: the enemy had over 2 billion HP (the dummy has about 3.35 billion; no other enemy has even 1 billion). HP comes from the combat log when it has it (logs from 2026-10-10 on), otherwise it's estimated from the score: a full-HP kill scores 90,000, so HP ≈ total damage × 90,000 ÷ score. Runs quit before getting a score, in older logs, can't be identified and count as not dummy.
- **Main stats**: the main stat on slots 4, 5 and 6.
- **Substats**: rolls added up per stat over all six discs (`ATK% ×14`). Stats with 2 rolls or fewer in total are treated as filler and hidden.

Names, icons and set bonuses come from [zzz.gachabase.net](https://zzz.gachabase.net). Each one is downloaded once and cached in `%LOCALAPPDATA%\CombatLogViewer\cache`, so items you've already seen show up offline too.

## Which exe to share

There are two builds of the same app:

- **Standalone** (`dist\standalone`, ~62 MB): runs on any 64-bit Windows 10/11 PC with nothing to install. Almost all of its size is the bundled .NET runtime, already compressed. The app has no images built in.
- **Small** (`dist\small`, ~250 KB): needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed (a one-time ~55 MB install). If it's missing, Windows shows a prompt with the download link.

Icons are cached at 192px (about 70 KB each), so the cache stays a few MB.

## Building

Requires the .NET 10 SDK.

```
dotnet build                                   # debug build in bin\Debug
dotnet publish -c Release -o dist\standalone   # standalone exe
dotnet publish -c Release -o dist\small -p:SelfContained=false -p:EnableCompressionInSingleFile=false
```
