namespace HS2.CrystalOverlay.Core;

public enum PhoneBatteryProvider
{
    AndroidAdb,
}

public sealed record PhoneBatteryReading(
    PhoneBatteryProvider Provider,
    int Percentage,
    bool? IsCharging,
    bool IsConnected,
    DateTimeOffset ObservedAt,
    string? Evidence = null,
    DateTimeOffset? ConfirmedAt = null);

public static class PhoneBatterySelection
{
    public static PhoneBatteryReading? Select(
        PhoneBatteryReading? reading,
        DateTimeOffset now,
        TimeSpan maximumAge) =>
        IsUsable(reading, now, maximumAge) ? reading : null;

    private static bool IsUsable(
        PhoneBatteryReading? reading,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (reading is null ||
            !reading.IsConnected ||
            reading.Percentage is < 0 or > 100)
        {
            return false;
        }

        var freshnessAt = reading.ConfirmedAt ?? reading.ObservedAt;
        var age = now - freshnessAt;
        return age >= TimeSpan.FromSeconds(-5) && age <= maximumAge;
    }
}
