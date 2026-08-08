namespace HS2.CrystalOverlay.Core;

/// <summary>
/// Geometry contract for the body of a stacked phone notification.
/// The title owns its own fixed area; the body gets exactly two visible lines
/// and only content beyond that viewport is allowed to scroll.
/// </summary>
public readonly record struct NotificationBodyLayout(
    double BodyTop,
    double ViewportHeight,
    double LineHeight,
    int VisibleLines)
{
    public const int DefaultVisibleLines = 2;
    private const double MeasurementTolerance = 1;

    public static NotificationBodyLayout Create(
        double cardHeight,
        double headerHeight,
        double titleHeight,
        double titleBodyGap,
        double bottomReserve,
        double preferredLineHeight,
        int visibleLines = 2)
    {
        if (visibleLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleLines));
        }

        var values = new[]
        {
            cardHeight,
            headerHeight,
            titleHeight,
            titleBodyGap,
            bottomReserve,
            preferredLineHeight,
        };
        if (values.Any(value => !double.IsFinite(value)) ||
            cardHeight <= 0 ||
            headerHeight < 0 ||
            titleHeight < 0 ||
            titleBodyGap < 0 ||
            bottomReserve < 0 ||
            preferredLineHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cardHeight),
                "Notification geometry must be finite and non-negative.");
        }

        var bodyTop = headerHeight + titleHeight + titleBodyGap;
        var availableHeight = Math.Max(
            0,
            cardHeight - bodyTop - bottomReserve);
        // Keep exactly two lines in the default viewport. On a compressed
        // row, lower the line height just enough to preserve that contract.
        var lineHeight = Math.Min(
            preferredLineHeight,
            availableHeight / visibleLines);
        var viewportHeight = lineHeight * visibleLines;
        return new(
            bodyTop,
            viewportHeight,
            lineHeight,
            visibleLines);
    }

    public int ContentLines(double contentHeight)
    {
        if (!double.IsFinite(contentHeight) || contentHeight <= 0)
        {
            return 0;
        }

        if (LineHeight <= 0 || !double.IsFinite(LineHeight))
        {
            return int.MaxValue;
        }

        return (int)Math.Ceiling(contentHeight / LineHeight);
    }

    public bool ShouldScroll(double contentHeight) =>
        ContentLines(contentHeight) > VisibleLines &&
        contentHeight > ViewportHeight + MeasurementTolerance;

    public double Overflow(double contentHeight)
    {
        if (!double.IsFinite(contentHeight) || contentHeight <= 0)
        {
            return 0;
        }

        return Math.Max(0, contentHeight - ViewportHeight);
    }

    public double OffsetForProgress(
        double progress,
        double contentHeight)
    {
        var overflow = Overflow(contentHeight);
        if (overflow <= 0 || !double.IsFinite(progress))
        {
            return 0;
        }

        return overflow * Math.Clamp(progress, 0, 1);
    }
}
