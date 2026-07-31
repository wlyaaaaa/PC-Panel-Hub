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
    private static readonly TimeSpan ScanInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumFileAge =
        TimeSpan.FromHours(2);

    private readonly string folder = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "NetEase",
        "CloudMusic",
        "Temp");
    private DateTimeOffset lastScan = DateTimeOffset.MinValue;
    private string? cachedPath;
    private DateTime cachedWriteTimeUtc;
    private NeteaseLyricCacheSnapshot? cachedSnapshot;

    internal NeteaseLyricCacheSnapshot? ReadCurrent(DateTimeOffset now)
    {
        if (now - lastScan < ScanInterval)
        {
            return cachedSnapshot;
        }

        lastScan = now;
        try
        {
            var candidates = new DirectoryInfo(folder)
                .EnumerateFiles()
                .Where(file =>
                    file.Length is > 64 and < 2_000_000 &&
                    now - new DateTimeOffset(file.LastWriteTime) <=
                    MaximumFileAge)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(24);
            foreach (var candidate in candidates)
            {
                if (string.Equals(
                        candidate.FullName,
                        cachedPath,
                        StringComparison.OrdinalIgnoreCase) &&
                    candidate.LastWriteTimeUtc == cachedWriteTimeUtc)
                {
                    return cachedSnapshot;
                }

                var lyrics = TryRead(candidate.FullName);
                if (lyrics is null)
                {
                    continue;
                }

                cachedPath = candidate.FullName;
                cachedWriteTimeUtc = candidate.LastWriteTimeUtc;
                cachedSnapshot = new NeteaseLyricCacheSnapshot(
                    candidate.Name,
                    candidate.FullName,
                    new DateTimeOffset(candidate.LastWriteTimeUtc, TimeSpan.Zero),
                    lyrics);
                return cachedSnapshot;
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        cachedPath = null;
        cachedSnapshot = null;
        return null;
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
