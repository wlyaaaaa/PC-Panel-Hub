using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HS2.CrystalOverlay.Core;

public sealed record NeteaseTrackMetadata(
    string Id,
    string CacheKey,
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    Uri? ArtworkUri,
    string? TranslatedTitle = null);

public sealed class NeteasePlayingList
{
    private readonly IReadOnlyDictionary<string, NeteaseTrackMetadata>
        tracksByCacheKey;
    private readonly IReadOnlyList<NeteaseTrackMetadata> tracks;

    private NeteasePlayingList(
        IReadOnlyDictionary<string, NeteaseTrackMetadata>
            tracksByCacheKey,
        IReadOnlyList<NeteaseTrackMetadata> tracks)
    {
        this.tracksByCacheKey = tracksByCacheKey;
        this.tracks = tracks;
    }

    public static NeteasePlayingList Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var tracks = new Dictionary<string, NeteaseTrackMetadata>(
            StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(
                "list",
                out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return new NeteasePlayingList(tracks, []);
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (!TryReadTrack(entry, out var track))
            {
                continue;
            }

            tracks[track.CacheKey] = track;
        }

        return new NeteasePlayingList(
            tracks,
            tracks.Values.ToArray());
    }

    public NeteaseTrackMetadata? FindByCacheKey(string? cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return null;
        }

        return tracksByCacheKey.TryGetValue(cacheKey, out var track)
            ? track
            : null;
    }

    public NeteaseTrackMetadata? FindByWindowTitle(string? windowTitle)
    {
        var matches = FindAllByWindowTitle(windowTitle);
        return matches.Count == 1
            ? matches[0]
            : null;
    }

    public IReadOnlyList<NeteaseTrackMetadata> FindAllByWindowTitle(
        string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return [];
        }

        var normalizedTitle = NormalizeWindowTitle(windowTitle);
        var matches = new List<NeteaseTrackMetadata>();
        foreach (var track in tracks)
        {
            var candidate = NormalizeWindowTitle(
                $"{track.Title} - {track.Artist}");
            if (!string.Equals(
                    candidate,
                    normalizedTitle,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add(track);
        }

        return matches;
    }

    private static string NormalizeWindowTitle(string value) =>
        string.Join(
            "/",
            value
                .Trim()
                .Split('/')
                .Select(part => part.Trim()));

    private static bool TryReadTrack(
        JsonElement entry,
        out NeteaseTrackMetadata track)
    {
        track = default!;
        if (!entry.TryGetProperty("track", out var trackElement) ||
            trackElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var id = ReadString(entry, "id") ??
                 ReadString(trackElement, "id");
        var title = ReadString(trackElement, "name");
        if (string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var artist = ReadArtists(trackElement);
        var album = string.Empty;
        Uri? artworkUri = null;
        if (trackElement.TryGetProperty(
                "album",
                out var albumElement) &&
            albumElement.ValueKind == JsonValueKind.Object)
        {
            album = ReadString(albumElement, "name") ?? string.Empty;
            var artwork = ReadString(albumElement, "picUrl") ??
                          ReadString(albumElement, "cover");
            if (Uri.TryCreate(
                    artwork,
                    UriKind.Absolute,
                    out var parsedArtwork) &&
                parsedArtwork.Scheme is "http" or "https")
            {
                artworkUri = parsedArtwork;
            }
        }

        var duration = TimeSpan.Zero;
        if (trackElement.TryGetProperty(
                "duration",
                out var durationElement) &&
            durationElement.TryGetInt64(out var milliseconds) &&
            milliseconds > 0)
        {
            duration = TimeSpan.FromMilliseconds(milliseconds);
        }

        var cacheKey = Convert.ToHexStringLower(
            MD5.HashData(Encoding.UTF8.GetBytes(id)));
        track = new NeteaseTrackMetadata(
            id,
            cacheKey,
            title.Trim(),
            artist,
            album.Trim(),
            duration,
            artworkUri,
            ReadTranslatedTitle(trackElement, title));
        return true;
    }

    private static string? ReadTranslatedTitle(
        JsonElement track,
        string originalTitle)
    {
        if (!track.TryGetProperty("transNames", out var translations) ||
            translations.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return translations
            .EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim())
            .FirstOrDefault(value =>
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(
                    value,
                    originalTitle.Trim(),
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadArtists(JsonElement track)
    {
        if (!track.TryGetProperty("artists", out var artists) ||
            artists.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            " / ",
            artists
                .EnumerateArray()
                .Select(artist => ReadString(artist, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim()));
    }

    private static string? ReadString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }
}

public static class NeteasePlaybackPositionDecoder
{
    public const int SnapshotSize = 32;

    public static bool TryDecode(
        ReadOnlySpan<byte> snapshot,
        out TimeSpan position)
    {
        position = TimeSpan.Zero;
        if (snapshot.Length < SnapshotSize)
        {
            return false;
        }

        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(
            snapshot[..sizeof(int)]);
        var channels = snapshot[4];
        var bytesPerSample = BinaryPrimitives.ReadUInt16LittleEndian(
            snapshot.Slice(8, sizeof(ushort)));
        var decodedBytes = BinaryPrimitives.ReadUInt64LittleEndian(
            snapshot.Slice(24, sizeof(ulong)));
        if (sampleRate is < 8_000 or > 768_000 ||
            channels is < 1 or > 16 ||
            bytesPerSample is < 1 or > 16)
        {
            return false;
        }

        var bytesPerSecond =
            (double)sampleRate * channels * bytesPerSample;
        var seconds = decodedBytes / bytesPerSecond;
        if (!double.IsFinite(seconds) ||
            seconds < 0 ||
            seconds > TimeSpan.FromDays(1).TotalSeconds)
        {
            return false;
        }

        position = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
