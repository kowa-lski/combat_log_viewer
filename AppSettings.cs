using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CombatLogViewer;

/// <summary>Saved in %LOCALAPPDATA%\CombatLogViewer\settings.json.</summary>
public sealed record AppSettings(
    string? LogsFolder,
    bool Minimal = false,
    bool DummyOnly = false,
    bool CompleteOnly = false,
    // OBS replay saving (see Recorder)
    bool ObsAutoSave = false,
    string ObsHost = "localhost",
    int ObsPort = 4455,
    string? ObsPasswordProtected = null,
    double SaveDelaySeconds = 25,
    double LeadInSeconds = 10,
    string? FfmpegPath = null,
    bool StartObs = false,        // start OBS hidden in the tray when the viewer opens
    bool CloseObsOnExit = true,   // close it again when the viewer closes (only if the viewer started it)
    string? ObsPath = null,
    bool SaveOnlyCompleted = true) // auto-save only runs over Battle.CompleteSeconds on the battle clock
{
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CombatLogViewer", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings((string?)null); }
        catch { return new AppSettings((string?)null); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch { /* settings are a convenience */ }
    }

    /// <summary>The OBS WebSocket password, encrypted for this Windows user (DPAPI) in the settings file.</summary>
    [JsonIgnore] // computed from ObsPasswordProtected; must never be written to the file in plain text
    public string ObsPassword => ObsPasswordProtected is { Length: > 0 } p
        ? TryUnprotect(p) : "";

    public AppSettings WithObsPassword(string password) => this with
    {
        ObsPasswordProtected = password.Length == 0 ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser)),
    };

    static string TryUnprotect(string protectedBase64)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
