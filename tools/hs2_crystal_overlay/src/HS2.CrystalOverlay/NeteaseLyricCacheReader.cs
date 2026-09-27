using System.Text.Json;

namespace HS2_CrystalOverlay;

internal sealed record NeteaseLyricCacheSnapshot(
    string CacheKey,
    string Path,
    DateTimeOffset WrittenAt);

internal sealed class NeteaseLyricCacheReader
{
    private sealed record CachedLyricFile(
        DateTime LastWriteTimeUtc,
        long Length,
        NeteaseLyricCacheSnapshot Snapshot);

    private readonly string folder = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "NetEase",
        "CloudMusic",
        "Temp");
    private readonly Dictionary<string, CachedLyricFile> cached = new(
        StringComparer.OrdinalIgnoreCase);

    internal NeteaseLyricCacheSnapshot? Read(string? cacheKey)
    {
        if (!IsCacheKey(cacheKey))
        {
            return null;
        }

        var normalizedCacheKey = cacheKey!.ToLowerInvariant();
        var path = Path.Combine(folder, normalizedCacheKey);
        _ = cached.TryGetValue(path, out var previous);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 64 or >= 2_000_000)
            {
                return null;
            }

            if (previous is not null &&
                file.LastWriteTimeUtc == previous.LastWriteTimeUtc &&
                file.Length == previous.Length)
            {
                return previous.Snapshot;
            }

            var lyrics = TryRead(file.FullName);
            if (!lyrics)
            {
                return previous?.Snapshot;
            }

            var snapshot = new NeteaseLyricCacheSnapshot(
                normalizedCacheKey,
                file.FullName,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            cached[path] = new CachedLyricFile(
                file.LastWriteTimeUtc,
                file.Length,
                snapshot);
            return snapshot;
        }
        catch (IOException)
        {
            return previous?.Snapshot;
        }
        catch (UnauthorizedAccessException)
        {
            return previous?.Snapshot;
        }

        static bool IsCacheKey(string? value)
        {
            return value is { Length: 32 } &&
                   value.All(Uri.IsHexDigit);
        }
    }

    private static bool TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("lrc", out var part) &&
                part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("lyric", out var lyric) &&
                lyric.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(lyric.GetString());
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
