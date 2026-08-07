namespace HS2.CrystalOverlay.Core;

/// <summary>
/// Keeps a source's last confirmed battery reading through transient probe
/// failures. Only a completed successful null result means the source has
/// explicitly reported that the phone is no longer available.
/// </summary>
public sealed class PhoneBatteryProbeReadingCache
{
    private PhoneBatteryReading? confirmed;

    public PhoneBatteryReading? Apply(
        PhoneBatteryProbeAttempt<PhoneBatteryReading?> attempt)
    {
        if (attempt.Status != PhoneBatteryProbeAttemptStatus.Succeeded)
        {
            return confirmed;
        }

        if (attempt.Value is null)
        {
            confirmed = null;
            return null;
        }

        // ObservedAt remains the source evidence time, while ConfirmedAt is
        // the last completed poll that successfully re-confirmed it. This
        // keeps background-file fallbacks alive without letting an old log
        // outrank a materially newer source.
        confirmed = attempt.Value with
        {
            ConfirmedAt = attempt.CompletedAt ?? attempt.Value.ObservedAt,
        };
        return confirmed;
    }
}
