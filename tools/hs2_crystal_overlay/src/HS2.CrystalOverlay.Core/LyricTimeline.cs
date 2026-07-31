using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HS2.CrystalOverlay.Core;

public sealed record LyricFrame(
    string Text,
    double LineProgress,
    TimeSpan Start,
    TimeSpan End);

public sealed record BilingualLyricFrame(
    LyricFrame? Original,
    LyricFrame? Translation);

public sealed class NeteaseLyricDocument
{
    private static readonly TimeSpan TranslationAlignmentTolerance =
        TimeSpan.FromMilliseconds(250);

    public NeteaseLyricDocument(
        LyricTimeline original,
        LyricTimeline? translation = null)
    {
        Original = original;
        Translation = translation;
    }

    public LyricTimeline Original { get; }

    public LyricTimeline? Translation { get; }

    public static NeteaseLyricDocument Parse(
        string original,
        string? translation = null,
        int originalOffsetMilliseconds = 0,
        int translationOffsetMilliseconds = 0) =>
        new(
            LyricTimeline.Parse(
                original,
                originalOffsetMilliseconds),
            string.IsNullOrWhiteSpace(translation)
                ? null
                : LyricTimeline.Parse(
                    translation,
                    translationOffsetMilliseconds));

    public BilingualLyricFrame At(TimeSpan position)
    {
        var original = Original.At(position);
        var translation = Translation?.At(position);
        if (original is null ||
            translation is null ||
            (original.Start - translation.Start).Duration() >
            TranslationAlignmentTolerance)
        {
            translation = null;
        }

        return new BilingualLyricFrame(original, translation);
    }
}

public sealed partial class LyricTimeline
{
    private static readonly TimeSpan LastLineDuration =
        TimeSpan.FromSeconds(4);

    private readonly LyricLine[] lines;

    private LyricTimeline(IEnumerable<LyricLine> lines)
    {
        this.lines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.Start)
            .GroupBy(line => line.Start)
            .Select(group => group.First())
            .ToArray();
    }

    public static LyricTimeline Parse(
        string text,
        int offsetMilliseconds = 0)
    {
        var offset = TimeSpan.FromMilliseconds(offsetMilliseconds);
        var lines = new List<LyricLine>();
        foreach (var rawLine in text.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            if (TryParseNeteaseJson(rawLine, offset, out var jsonLine))
            {
                lines.Add(jsonLine);
                continue;
            }

            ParseTraditionalLine(rawLine, offset, lines);
        }

        return new LyricTimeline(lines);
    }

    public LyricFrame? At(TimeSpan position)
    {
        if (lines.Length == 0 || position < lines[0].Start)
        {
            return null;
        }

        var index = Array.BinarySearch(
            lines,
            new LyricLine(position, string.Empty),
            LyricLineStartComparer.Instance);
        if (index < 0)
        {
            index = ~index - 1;
        }

        var current = lines[index];
        var end = index + 1 < lines.Length
            ? lines[index + 1].Start
            : current.Start + LastLineDuration;
        var duration = end - current.Start;
        var progress = duration <= TimeSpan.Zero
            ? 1
            : Math.Clamp(
                (position - current.Start).TotalMilliseconds /
                duration.TotalMilliseconds,
                0,
                1);
        return new LyricFrame(
            current.Text,
            progress,
            current.Start,
            end);
    }

    private static bool TryParseNeteaseJson(
        string rawLine,
        TimeSpan offset,
        out LyricLine line)
    {
        line = default;
        if (!rawLine.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("t", out var timestamp) ||
                !timestamp.TryGetInt64(out var milliseconds) ||
                !root.TryGetProperty("c", out var fragments) ||
                fragments.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var text = string.Concat(
                fragments
                    .EnumerateArray()
                    .Select(fragment =>
                        fragment.TryGetProperty("tx", out var value)
                            ? value.GetString()
                            : null));
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            line = new LyricLine(
                TimeSpan.FromMilliseconds(milliseconds) + offset,
                text.Trim());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ParseTraditionalLine(
        string rawLine,
        TimeSpan offset,
        ICollection<LyricLine> destination)
    {
        var matches = LrcTimestamp().Matches(rawLine);
        if (matches.Count == 0)
        {
            return;
        }

        var text = rawLine[
            (matches[^1].Index + matches[^1].Length)..].Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match match in matches)
        {
            if (!int.TryParse(
                    match.Groups["minutes"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var minutes) ||
                !int.TryParse(
                    match.Groups["seconds"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var seconds))
            {
                continue;
            }

            var fractionText = match.Groups["fraction"].Value;
            var milliseconds = fractionText.Length switch
            {
                1 when int.TryParse(fractionText, out var value) =>
                    value * 100,
                2 when int.TryParse(fractionText, out var value) =>
                    value * 10,
                3 when int.TryParse(fractionText, out var value) =>
                    value,
                _ => 0,
            };
            destination.Add(new LyricLine(
                TimeSpan.FromMinutes(minutes) +
                TimeSpan.FromSeconds(seconds) +
                TimeSpan.FromMilliseconds(milliseconds) +
                offset,
                text));
        }
    }

    private readonly record struct LyricLine(
        TimeSpan Start,
        string Text);

    private sealed class LyricLineStartComparer :
        IComparer<LyricLine>
    {
        internal static readonly LyricLineStartComparer Instance = new();

        public int Compare(LyricLine x, LyricLine y) =>
            x.Start.CompareTo(y.Start);
    }

    [GeneratedRegex(
        @"\[(?<minutes>\d{1,3}):(?<seconds>\d{2})(?:[\.:](?<fraction>\d{1,3}))?\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex LrcTimestamp();
}
