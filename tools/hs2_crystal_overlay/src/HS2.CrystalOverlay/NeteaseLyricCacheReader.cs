using System.Text.Json;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed record NeteaseLyricCacheSnapshot(
    string CacheKey,
    string Path,
    DateTimeOffset WrittenAt,
    NeteaseLyricDocument Lyrics);

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
            if (lyrics is null)
            {
                return previous?.Snapshot;
            }

            var snapshot = new NeteaseLyricCacheSnapshot(
                normalizedCacheKey,
                file.FullName,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                lyrics);
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

    private static NeteaseLyricDocument? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!TryReadPart(
                    document.RootElement,
                    "lrc",
                    out var original,
                    out var originalOffset))
            {
                return null;
            }

            _ = TryReadPart(
                document.RootElement,
                "tlyric",
                out var translation,
                out var translationOffset);
            return NeteaseLyricDocument.Parse(
                original,
                translation,
                originalOffset,
                translationOffset);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryReadPart(
        JsonElement root,
        string propertyName,
        out string lyric,
        out int offset)
    {
        lyric = string.Empty;
        offset = 0;
        if (!root.TryGetProperty(propertyName, out var part) ||
            part.ValueKind != JsonValueKind.Object ||
            !part.TryGetProperty("lyric", out var lyricValue))
        {
            return false;
        }

        lyric = lyricValue.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(lyric))
        {
            return false;
        }

        offset = part.TryGetProperty("offset", out var offsetValue) &&
                 offsetValue.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        return true;
    }
}
