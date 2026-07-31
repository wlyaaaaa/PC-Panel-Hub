using System.Text.Json;
using System.Text.RegularExpressions;

namespace HS2.CrystalOverlay.Core;

public enum ImportantTaskState
{
    Active,
    Completed,
    Cancelled,
}

public sealed record ImportantTaskUpdate(
    string Id,
    string Title,
    string? Detail,
    double? Progress,
    TimeSpan? Remaining,
    ImportantTaskState State);

public static partial class ImportantTaskProtocol
{
    public static ImportantTaskUpdate? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var id = Text(root, "id");
            var title = Text(root, "title");
            if (id is null ||
                title is null ||
                !SafeId().IsMatch(id))
            {
                return null;
            }

            var state = ParseState(Text(root, "state"));
            if (state is null)
            {
                return null;
            }

            var progress = Number(root, "progress");
            if (progress is > 1 and <= 100)
            {
                progress /= 100;
            }

            if (progress is < 0 or > 1)
            {
                progress = null;
            }

            var remainingSeconds = Number(
                root,
                "remaining_seconds");
            TimeSpan? remaining =
                remainingSeconds is >= 0 and <= 31_536_000
                    ? TimeSpan.FromSeconds(remainingSeconds.Value)
                    : null;
            return new ImportantTaskUpdate(
                id,
                title,
                Text(root, "detail"),
                progress,
                remaining,
                state.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ImportantTaskState? ParseState(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "active" or "running" =>
                ImportantTaskState.Active,
            "completed" or "complete" or "done" =>
                ImportantTaskState.Completed,
            "cancelled" or "canceled" or "cancel" =>
                ImportantTaskState.Cancelled,
            _ => null,
        };

    private static string? Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static double? Number(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) &&
               value.TryGetDouble(out var number)
            ? number
            : null;
    }

    [GeneratedRegex(
        @"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
