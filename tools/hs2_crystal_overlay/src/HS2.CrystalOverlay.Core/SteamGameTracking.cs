using System.Globalization;
using System.Text.RegularExpressions;

namespace HS2.CrystalOverlay.Core;

public sealed record SteamRunningGame(
    uint AppId,
    DateTimeOffset StartedAt);

public sealed record SteamGameMetadata(
    uint AppId,
    string Name,
    string? InstallDirectory);

public static partial class SteamGameProcessLogParser
{
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
    ];

    public static IReadOnlyList<SteamRunningGame> Parse(
        string text,
        TimeSpan localOffset)
    {
        var running = new Dictionary<uint, DateTimeOffset>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (ClientStarted().IsMatch(line))
            {
                running.Clear();
                continue;
            }

            var added = Added().Match(line);
            if (added.Success &&
                TryTimestamp(
                    added.Groups["time"].Value,
                    localOffset,
                    out var startedAt) &&
                uint.TryParse(
                    added.Groups["app"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var addedAppId))
            {
                running.TryAdd(addedAppId, startedAt);
                continue;
            }

            var removed = Removed().Match(line);
            if (removed.Success &&
                uint.TryParse(
                    removed.Groups["app"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var removedAppId))
            {
                running.Remove(removedAppId);
            }
        }

        return running
            .Select(pair => new SteamRunningGame(pair.Key, pair.Value))
            .OrderByDescending(game => game.StartedAt)
            .ThenBy(game => game.AppId)
            .ToArray();
    }

    private static bool TryTimestamp(
        string value,
        TimeSpan offset,
        out DateTimeOffset result)
    {
        result = default;
        if (!DateTime.TryParseExact(
                value,
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        result = new DateTimeOffset(
            DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified),
            offset);
        return true;
    }

    [GeneratedRegex(
        @"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] Client version:",
        RegexOptions.CultureInvariant)]
    private static partial Regex ClientStarted();

    [GeneratedRegex(
        @"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] AppID (?<app>\d+) adding PID ",
        RegexOptions.CultureInvariant)]
    private static partial Regex Added();

    [GeneratedRegex(
        @"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] Remove (?<app>\d+) from running list",
        RegexOptions.CultureInvariant)]
    private static partial Regex Removed();
}

public static partial class SteamManifestParser
{
    public static SteamGameMetadata? Parse(string text)
    {
        var fields = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Field().Matches(text))
        {
            fields[match.Groups["key"].Value] =
                Unescape(match.Groups["value"].Value);
        }

        if (!fields.TryGetValue("appid", out var appText) ||
            !uint.TryParse(
                appText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var appId) ||
            !fields.TryGetValue("name", out var name) ||
            string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        fields.TryGetValue("installdir", out var installDirectory);
        return new SteamGameMetadata(
            appId,
            name.Trim(),
            string.IsNullOrWhiteSpace(installDirectory)
                ? null
                : installDirectory.Trim());
    }

    private static string Unescape(string value) =>
        value.Replace(
                @"\\",
                @"\",
                StringComparison.Ordinal)
            .Replace(
                "\\\"",
                "\"",
                StringComparison.Ordinal);

    [GeneratedRegex(
        @"(?m)^\s*""(?<key>appid|name|installdir)""\s*""(?<value>(?:\\.|[^""])*)""\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Field();
}
