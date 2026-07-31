using System.Text;
using System.Text.RegularExpressions;
using HS2.CrystalOverlay.Core;
using Microsoft.Win32;

namespace HS2_CrystalOverlay;

internal sealed partial class SteamGameSourceCoordinator : IDisposable
{
    private const int TailBytes = 16 * 1024 * 1024;
    private const uint WallpaperEngineAppId = 431960;
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CatalogRefreshInterval =
        TimeSpan.FromMinutes(5);

    private readonly IOverlayPublisher publisher;
    private readonly string? steamPath;
    private readonly string? processLogPath;
    private readonly HttpClient artworkClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    private readonly CancellationTokenSource cancellation = new();
    private readonly Timer timer;
    private IReadOnlyDictionary<uint, SteamGameMetadata> catalog =
        new Dictionary<uint, SteamGameMetadata>();
    private DateTimeOffset catalogReadAt = DateTimeOffset.MinValue;
    private uint? activeAppId;
    private string? activeName;
    private string? activeArtwork;
    private DateTimeOffset activeStartedAt;
    private int polling;
    private bool disposed;

    internal SteamGameSourceCoordinator(IOverlayPublisher publisher)
    {
        this.publisher = publisher;
        steamPath = ReadSteamPath();
        processLogPath = string.IsNullOrWhiteSpace(steamPath)
            ? null
            : Path.Combine(steamPath, "logs", "gameprocess_log.txt");
        timer = new Timer(
            Poll,
            null,
            TimeSpan.Zero,
            PollInterval);
    }

    private async void Poll(object? state)
    {
        if (disposed || Interlocked.Exchange(ref polling, 1) != 0)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            if (string.IsNullOrWhiteSpace(processLogPath) ||
                !File.Exists(processLogPath))
            {
                EndActive(now);
                return;
            }

            if (now - catalogReadAt >= CatalogRefreshInterval)
            {
                catalog = ReadCatalog(steamPath!);
                catalogReadAt = now;
            }

            var text = ReadTail(processLogPath, TailBytes);
            var running = SteamGameProcessLogParser.Parse(
                text,
                now.Offset);
            var current = running.FirstOrDefault(game =>
                game.AppId != WallpaperEngineAppId &&
                catalog.TryGetValue(game.AppId, out var metadata) &&
                IsGame(metadata));
            if (current is null)
            {
                EndActive(now);
                return;
            }

            var metadata = catalog[current.AppId];
            if (activeAppId != current.AppId)
            {
                EndActive(now, publishSummary: false);
                activeAppId = current.AppId;
                activeName = metadata.Name;
                activeStartedAt = current.StartedAt;
                activeArtwork = await CacheArtworkAsync(
                    current.AppId,
                    cancellation.Token);
            }

            var elapsed = NonNegative(now - activeStartedAt);
            _ = publisher.Publish(OverlayRequest.Active(
                "game-active",
                OverlayKind.GameActive,
                OverlaySource.Steam,
                metadata.Name,
                $"已游玩 {FormatDuration(elapsed)}",
                dedupKey: $"steam:{current.AppId}",
                visual: new OverlayVisualData(
                    Eyebrow: "STEAM 游戏",
                    Meta: $"启动于 {activeStartedAt:HH:mm}",
                    ArtworkPath: activeArtwork,
                    AccentHex: "#83CAFF")));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Steam game probe failed: {exception.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref polling, 0);
        }
    }

    private void EndActive(
        DateTimeOffset now,
        bool publishSummary = true)
    {
        if (activeAppId is null)
        {
            return;
        }

        _ = publisher.Publish(OverlayRequest.End(
            "game-active",
            OverlayKind.GameActive,
            OverlaySource.Steam));
        if (publishSummary)
        {
            var elapsed = NonNegative(now - activeStartedAt);
            _ = publisher.Publish(OverlayRequest.Timed(
                "game-summary",
                OverlayKind.GameSummary,
                OverlaySource.Steam,
                activeName ?? "游戏已结束",
                $"本次游玩 {FormatDuration(elapsed)}",
                dedupKey: $"steam-summary:{activeAppId}:{activeStartedAt:O}",
                visual: new OverlayVisualData(
                    Eyebrow: "本次游戏",
                    ArtworkPath: activeArtwork,
                    AccentHex: "#83CAFF")));
        }

        activeAppId = null;
        activeName = null;
        activeArtwork = null;
        activeStartedAt = default;
    }

    private async Task<string?> CacheArtworkAsync(
        uint appId,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "HS2.CrystalOverlay",
            "steam-artwork");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{appId}.jpg");
        if (File.Exists(path) &&
            new FileInfo(path).Length is > 1024 and < 5 * 1024 * 1024)
        {
            return path;
        }

        try
        {
            var uri = new Uri(
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg");
            var bytes = await artworkClient.GetByteArrayAsync(
                uri,
                cancellationToken);
            if (bytes.Length is <= 1024 or >= 5 * 1024 * 1024)
            {
                return null;
            }

            await File.WriteAllBytesAsync(
                path,
                bytes,
                cancellationToken);
            return path;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<uint, SteamGameMetadata> ReadCatalog(
        string steamPath)
    {
        var libraries = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            steamPath,
        };
        var libraryFile = Path.Combine(
            steamPath,
            "steamapps",
            "libraryfolders.vdf");
        try
        {
            if (File.Exists(libraryFile))
            {
                var text = File.ReadAllText(libraryFile);
                foreach (Match match in LibraryPath().Matches(text))
                {
                    var path = match.Groups["path"].Value.Replace(
                        @"\\",
                        @"\",
                        StringComparison.Ordinal);
                    if (Directory.Exists(path))
                    {
                        libraries.Add(path);
                    }
                }
            }
        }
        catch (IOException)
        {
        }

        var result = new Dictionary<uint, SteamGameMetadata>();
        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps))
            {
                continue;
            }

            try
            {
                foreach (var manifest in Directory.EnumerateFiles(
                             steamApps,
                             "appmanifest_*.acf",
                             SearchOption.TopDirectoryOnly))
                {
                    var metadata = SteamManifestParser.Parse(
                        File.ReadAllText(manifest));
                    if (metadata is not null)
                    {
                        result[metadata.AppId] = metadata;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private static bool IsGame(SteamGameMetadata metadata)
    {
        var name = metadata.Name;
        return !name.Contains(
                   "Wallpaper Engine",
                   StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(
                   "Redistributable",
                   StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(
                   "Dedicated Server",
                   StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(
                   "SteamVR",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTail(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(maximumBytes, stream.Length);
        if (length <= 0)
        {
            return string.Empty;
        }

        stream.Seek(-length, SeekOrigin.End);
        var bytes = new byte[length];
        var read = stream.Read(bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    private static string? ReadSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Valve\Steam");
            var value = key?.GetValue("SteamPath") as string;
            return string.IsNullOrWhiteSpace(value)
                ? null
                : Path.GetFullPath(value);
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours} 小时 {value.Minutes} 分钟";
        }

        return $"{Math.Max(1, (int)value.TotalMinutes)} 分钟";
    }

    [GeneratedRegex(
        @"""path""\s*""(?<path>(?:\\.|[^""])*)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        timer.Dispose();
        artworkClient.Dispose();
        cancellation.Dispose();
    }
}
