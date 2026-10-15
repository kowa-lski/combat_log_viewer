using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CombatLogViewer;

public enum AssetKind { Agent, WEngine, DiscSet }

/// <summary>
/// An agent, W-Engine or Drive Disc set from zzz.gachabase.net: name, icon and (for sets) the
/// 2-piece and 4-piece bonuses. One shared instance per item, so every card updates when it loads.
/// </summary>
public sealed class Asset : INotifyPropertyChanged
{
    public AssetKind Kind { get; init; }
    public int Id { get; init; }

    string? title, bonus2, bonus4;
    ImageSource? icon;

    public string? Title { get => title; set => Set(ref title, value); }
    public string? Bonus2 { get => bonus2; set => Set(ref bonus2, value); }
    public string? Bonus4 { get => bonus4; set => Set(ref bonus4, value); }
    public ImageSource? Icon { get => icon; set => Set(ref icon, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Downloads gachabase data on demand and caches it in %LOCALAPPDATA%\CombatLogViewer\cache.</summary>
public static partial class AssetStore
{
    const string Site = "https://zzz.gachabase.net";

    static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CombatLogViewer", "cache");
    static readonly string IndexPath = Path.Combine(CacheDir, "index.json");

    static readonly HttpClient Http = CreateClient();
    static readonly SemaphoreSlim Throttle = new(4);
    static readonly Dictionary<string, Asset> Assets = [];
    static readonly object IndexLock = new();
    static Dictionary<string, CacheEntry> index = LoadIndex();

    sealed record CacheEntry(string? Title, string? File, string? Bonus2, string? Bonus4);

    [GeneratedRegex("<meta property=\"og:image\" content=\"([^\"]+)\"")] private static partial Regex OgImage();
    [GeneratedRegex("<meta property=\"og:title\" content=\"([^\"]+)\"")] private static partial Regex OgTitle();
    [GeneratedRegex(@"\s*\((Agents|W-Engines|Drive Discs)\)$")] private static partial Regex TitleSuffix();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (CombatLogViewer)");
        return c;
    }

    static Dictionary<string, CacheEntry> LoadIndex()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(IndexPath)) ?? []; }
        catch { return []; }
    }

    /// <summary>The shared asset for an item; starts loading it the first time it is asked for. UI thread only.</summary>
    public static Asset Get(AssetKind kind, int id)
    {
        string key = $"{kind}:{id}";
        if (Assets.TryGetValue(key, out var asset)) return asset;
        asset = new Asset { Kind = kind, Id = id };
        Assets[key] = asset;

        CacheEntry? entry;
        lock (IndexLock) index.TryGetValue(key, out entry);
        if (entry != null && (entry.File == null || File.Exists(Path.Combine(CacheDir, entry.File))))
            Apply(asset, entry);
        else
            _ = DownloadAsync(asset, key);
        return asset;
    }

    static void Apply(Asset asset, CacheEntry e)
    {
        asset.Title = e.Title;
        asset.Bonus2 = e.Bonus2;
        asset.Bonus4 = e.Bonus4;
        if (e.File == null) return;
        string path = Path.Combine(CacheDir, e.File);
        asset.Icon = LoadImage(path);
        // Icons cached by earlier versions were saved full size (512px, ~200 KB); shrink them once.
        if (new FileInfo(path).Length > ShrunkMaxBytes)
            Task.Run(() => { try { File.WriteAllBytes(path, Shrink(File.ReadAllBytes(path))); } catch { } });
    }

    /// <summary>Icons are shown at 64px or less; 192px keeps them sharp up to 300% display scaling.</summary>
    const int IconPixels = 192;
    const long ShrunkMaxBytes = 80_000;

    /// <summary>Re-encodes an image as a PNG no larger than IconPixels on its longer side.</summary>
    static byte[] Shrink(byte[] image)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(image), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        double scale = Math.Min(1.0, (double)IconPixels / Math.Max(frame.PixelWidth, frame.PixelHeight));
        BitmapSource result = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    static ImageSource? LoadImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 160; // icons are shown at most ~80px; keeps memory small
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    static async Task DownloadAsync(Asset asset, string key)
    {
        await Throttle.WaitAsync();
        try
        {
            string page = asset.Kind switch { AssetKind.Agent => "agents", AssetKind.WEngine => "w-engines", _ => "drive-discs" };
            string html = await Http.GetStringAsync($"{Site}/{page}/{asset.Id}");

            string? title = OgTitle().Match(html) is { Success: true } t ? TitleSuffix().Replace(WebUtility.HtmlDecode(t.Groups[1].Value), "") : null;
            string? bonus2 = null, bonus4 = null;
            if (asset.Kind == AssetKind.DiscSet)
            {
                bonus2 = SetBonus(html, ">2-Piece Set<");
                bonus4 = SetBonus(html, ">4-Piece Set<");
            }

            string? file = null;
            if (OgImage().Match(html) is { Success: true } img)
            {
                // i.gachabase.net/<signature>/q:50/f:webp/plain/<path> serves WebP; the same file is on the CDN as PNG.
                string url = WebUtility.HtmlDecode(img.Groups[1].Value);
                int plain = url.IndexOf("/plain/", StringComparison.Ordinal);
                if (plain >= 0) url = "https://cdn.gachabase.net/" + url[(plain + 7)..];
                byte[] full = await Http.GetByteArrayAsync(url);
                byte[] bytes = await Task.Run(() => Shrink(full));
                Directory.CreateDirectory(CacheDir);
                file = $"{asset.Kind}_{asset.Id}.png";
                await File.WriteAllBytesAsync(Path.Combine(CacheDir, file), bytes);
            }

            var entry = new CacheEntry(title, file, bonus2, bonus4);
            lock (IndexLock)
            {
                index[key] = entry;
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(IndexPath, JsonSerializer.Serialize(index));
            }
            Application.Current?.Dispatcher.Invoke(() => Apply(asset, entry));
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"gachabase {key}: {e.Message}");
        }
        finally
        {
            Throttle.Release();
        }
    }

    /// <summary>The bonus text that follows a "2-Piece Set" / "4-Piece Set" heading on a set page.</summary>
    static string? SetBonus(string html, string heading)
    {
        int i = html.IndexOf(heading, StringComparison.Ordinal);
        if (i < 0) return null;
        var m = Regex.Match(html[i..], "<span class=\"gb-wrap-text text-sm \"[^>]*>(.*?)</span><!---->", RegexOptions.Singleline);
        return m.Success ? WebUtility.HtmlDecode(Tags().Replace(m.Groups[1].Value, "")).Trim() : null;
    }
}
