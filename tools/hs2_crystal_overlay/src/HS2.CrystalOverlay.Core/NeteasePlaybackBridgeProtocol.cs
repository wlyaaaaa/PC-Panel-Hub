using System.Text.Json;
using System.Text.Json.Serialization;

namespace HS2.CrystalOverlay.Core;

public static class NeteasePlaybackBridgeProtocol
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string Serialize(
        NeteasePlaybackMemorySample? sample,
        DateTimeOffset observedAt)
    {
        var message = sample is null
            ? new BridgeMessage(
                SchemaVersion,
                observedAt.ToUnixTimeMilliseconds(),
                null,
                null)
            : new BridgeMessage(
                SchemaVersion,
                observedAt.ToUnixTimeMilliseconds(),
                sample.ProcessId,
                sample.Position.TotalMilliseconds);
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static NeteasePlaybackMemorySample? Parse(
        string? json,
        IReadOnlySet<int> expectedProcessIds,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            maximumAge <= TimeSpan.Zero)
        {
            return null;
        }

        try
        {
            var message = JsonSerializer.Deserialize<BridgeMessage>(
                json,
                JsonOptions);
            if (message is null ||
                message.SchemaVersion != SchemaVersion ||
                message.ProcessId is not { } processId ||
                !expectedProcessIds.Contains(processId) ||
                message.PositionMilliseconds is not { } milliseconds ||
                !double.IsFinite(milliseconds) ||
                milliseconds < 0 ||
                milliseconds >
                TimeSpan.FromDays(1).TotalMilliseconds)
            {
                return null;
            }

            var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                message.ObservedUnixMilliseconds);
            var age = now - observedAt;
            if (age < TimeSpan.FromSeconds(-2) || age > maximumAge)
            {
                return null;
            }

            return new NeteasePlaybackMemorySample(
                TimeSpan.FromMilliseconds(milliseconds),
                processId);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private sealed record BridgeMessage(
        [property: JsonPropertyName("schema_version")]
        int SchemaVersion,
        [property: JsonPropertyName("observed_unix_ms")]
        long ObservedUnixMilliseconds,
        [property: JsonPropertyName("process_id")]
        int? ProcessId,
        [property: JsonPropertyName("position_ms")]
        double? PositionMilliseconds);
}
