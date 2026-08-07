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
    ImportantTaskState State,
    TimeSpan? Lease = null);

public static partial class ImportantTaskProtocol
{
    public static ImportantTaskUpdate? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var id = Text(root, "id");
            var title = Text(root, "title");
            if (id is null ||
                title is null ||
                title.Length > 256 ||
                !SafeId().IsMatch(id))
            {
                return null;
            }

            var detail = Text(root, "detail");
            if (detail?.Length > 1024)
            {
                return null;
            }

            var state = ParseState(Text(root, "state"));
            if (state is null)
            {
                return null;
            }

            if (!OptionalNumber(root, "progress_percent", out var progressPercent) ||
                !OptionalNumber(root, "progress", out var normalizedProgress) ||
                !OptionalNumber(
                    root,
                    "remaining_seconds",
                    out var remainingSeconds) ||
                !OptionalNumber(root, "lease_seconds", out var leaseSeconds))
            {
                return null;
            }

            if (progressPercent is not null &&
                normalizedProgress is not null)
            {
                return null;
            }

            double? progress = null;
            if (progressPercent is not null)
            {
                if (progressPercent is < 0 or > 100)
                {
                    return null;
                }

                progress = progressPercent / 100;
            }
            else if (normalizedProgress is not null)
            {
                if (normalizedProgress is < 0 or > 1)
                {
                    return null;
                }

                progress = normalizedProgress;
            }

            if (remainingSeconds is < 0 or > 31_536_000 ||
                leaseSeconds is < 5 or > 86_400)
            {
                return null;
            }

            TimeSpan? remaining = remainingSeconds is null
                ? null
                : TimeSpan.FromSeconds(remainingSeconds.Value);
            TimeSpan? lease = leaseSeconds is null
                ? null
                : TimeSpan.FromSeconds(leaseSeconds.Value);
            return new ImportantTaskUpdate(
                id,
                title,
                detail,
                progress,
                remaining,
                state.Value,
                lease);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException)
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

    private static bool OptionalNumber(
        JsonElement root,
        string name,
        out double? number)
    {
        number = null;
        if (!root.TryGetProperty(name, out var value))
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var parsed) ||
            !double.IsFinite(parsed))
        {
            return false;
        }

        number = parsed;
        return true;
    }

    [GeneratedRegex(
        @"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
