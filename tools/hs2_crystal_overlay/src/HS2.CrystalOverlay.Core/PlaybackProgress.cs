namespace HS2.CrystalOverlay.Core;

public static class PlaybackProgress
{
    public static double Calculate(
        TimeSpan position,
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        return Math.Clamp(
            position.TotalMilliseconds / duration.TotalMilliseconds,
            0,
            1);
    }
}
