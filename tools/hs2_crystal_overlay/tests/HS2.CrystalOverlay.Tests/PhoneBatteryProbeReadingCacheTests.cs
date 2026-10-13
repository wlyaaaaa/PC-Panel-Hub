using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PhoneBatteryProbeReadingCacheTests
{
    [Fact]
    public void TimedOutAndFaultedAttemptsRetainLastConfirmedReadingUntilItAgesOut()
    {
        var cache = new PhoneBatteryProbeReadingCache();
        var completedAt = new DateTimeOffset(
            2026,
            8,
            6,
            12,
            0,
            0,
            TimeSpan.Zero);
        var reading = new PhoneBatteryReading(
            PhoneBatteryProvider.XiaomiHyperConnect,
            97,
            true,
            true,
            completedAt.AddSeconds(-1),
            "xiaomi-ui");

        var confirmed = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.Succeeded,
            reading,
            completedAt));
        var afterTimeout = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.TimedOut,
            null));
        var afterFault = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.Faulted,
            null,
            Error: new IOException()));

        Assert.NotNull(confirmed);
        Assert.Equal(reading.ObservedAt, confirmed.ObservedAt);
        Assert.Equal(confirmed, afterTimeout);
        Assert.Equal(confirmed, afterFault);
        Assert.Equal(
            PhoneBatteryProvider.XiaomiHyperConnect,
            PhoneBatterySelection.Select(
                afterFault,
                completedAt.AddSeconds(14),
                TimeSpan.FromSeconds(15))?.Provider);
        Assert.Null(PhoneBatterySelection.Select(
            afterFault,
            completedAt.AddSeconds(16),
            TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void SuccessfulNullReadingExplicitlyClearsLastConfirmedReading()
    {
        var cache = new PhoneBatteryProbeReadingCache();
        var completedAt = DateTimeOffset.UtcNow;
        _ = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.Succeeded,
            new PhoneBatteryReading(
                PhoneBatteryProvider.XiaomiHyperConnect,
                80,
                false,
                true,
                completedAt,
                "xiaomi-ui"),
            completedAt));

        var disconnected = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.Succeeded,
            null,
            completedAt.AddSeconds(1)));

        Assert.Null(disconnected);
    }

    [Fact]
    public void SuccessfulReadingRetainsItsOwnEvidenceTimestamp()
    {
        var cache = new PhoneBatteryProbeReadingCache();
        var completedAt = DateTimeOffset.UtcNow;
        var confirmed = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.Succeeded,
            new PhoneBatteryReading(
                PhoneBatteryProvider.XiaomiHyperConnect,
                80,
                null,
                true,
                completedAt,
                "xiaomi-ui"),
            completedAt));

        var replacement = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.Succeeded,
            new PhoneBatteryReading(
                PhoneBatteryProvider.XiaomiHyperConnect,
                81,
                null,
                true,
                completedAt.AddMinutes(-1),
                "xiaomi-ui")));

        Assert.NotEqual(confirmed, replacement);
        Assert.Equal(81, replacement?.Percentage);
        Assert.Equal(
            completedAt.AddMinutes(-1),
            replacement?.ObservedAt);
    }

    [Fact]
    public void FreshConfirmationKeepsReadingUsableWithoutRewritingEvidenceTime()
    {
        var cache = new PhoneBatteryProbeReadingCache();
        var now = DateTimeOffset.UtcNow;
        var evidenceAt = now.AddMinutes(-2);
        var confirmed = cache.Apply(new PhoneBatteryProbeAttempt<
            PhoneBatteryReading?>(
            PhoneBatteryProbeAttemptStatus.Succeeded,
            new PhoneBatteryReading(
                PhoneBatteryProvider.XiaomiHyperConnect,
                92,
                true,
                true,
                evidenceAt,
                "xiaomi-log"),
            now));

        Assert.Equal(evidenceAt, confirmed?.ObservedAt);
        Assert.Equal(now, confirmed?.ConfirmedAt);
        Assert.Equal(
            PhoneBatteryProvider.XiaomiHyperConnect,
            PhoneBatterySelection.Select(
                confirmed,
                now.AddSeconds(14),
                TimeSpan.FromSeconds(15))?.Provider);
    }
}
