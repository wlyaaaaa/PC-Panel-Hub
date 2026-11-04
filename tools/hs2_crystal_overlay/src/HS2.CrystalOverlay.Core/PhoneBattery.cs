namespace HS2.CrystalOverlay.Core;

public sealed record PhoneBatteryReading(
    int Percentage,
    bool? IsCharging,
    bool IsConnected,
    DateTimeOffset ObservedAt);

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

        var age = now - reading.ObservedAt;
        return age >= TimeSpan.FromSeconds(-5) && age <= maximumAge;
    }
}
