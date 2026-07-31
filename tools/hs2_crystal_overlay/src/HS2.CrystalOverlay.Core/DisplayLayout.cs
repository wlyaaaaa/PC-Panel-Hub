namespace HS2.CrystalOverlay.Core;

public sealed record DisplayGeometry(
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

public sealed record OverlayPlacement(
    PixelRect FrontRegion,
    PixelRect SideRegion,
    PixelRect Card,
    PixelRect Direct);

public static class DisplayTargetSelector
{
    public static DisplayGeometry? Select(
        IEnumerable<DisplayGeometry> displays,
        string preferredDeviceName,
        int expectedWidth,
        int expectedHeight)
    {
        var candidates = displays.ToArray();
        var exact = candidates.FirstOrDefault(display =>
            string.Equals(
                display.DeviceName,
                preferredDeviceName,
                StringComparison.OrdinalIgnoreCase) &&
            !display.IsPrimary &&
            display.Width == expectedWidth &&
            display.Height == expectedHeight);
        if (exact is not null)
        {
            return exact;
        }

        return candidates
            .Where(display =>
                !display.IsPrimary &&
                display.Width == expectedWidth &&
                display.Height == expectedHeight)
            .OrderByDescending(display => display.X)
            .ThenBy(display => display.Y)
            .FirstOrDefault();
    }
}

public static class OverlayLayoutPlanner
{
    private const int HorizontalMargin = 56;
    private const int CardBottomMargin = 56;
    private const int DirectTopMargin = 56;
    private const int DesiredCardHeight = 520;
    private const int DesiredDirectHeight = 200;

    public static OverlayPlacement Plan(DisplayGeometry display)
    {
        var frontWidth = display.Width * 2 / 3;
        var sideWidth = display.Width - frontWidth;
        var front = new PixelRect(
            display.X,
            display.Y,
            frontWidth,
            display.Height);
        var side = new PixelRect(
            display.X + frontWidth,
            display.Y,
            sideWidth,
            display.Height);

        var cardWidth = Math.Max(800, frontWidth - HorizontalMargin * 2);
        var cardHeight = Math.Min(
            DesiredCardHeight,
            Math.Max(360, display.Height - 220));
        var card = new PixelRect(
            display.X + HorizontalMargin,
            display.Y + display.Height - cardHeight - CardBottomMargin,
            cardWidth,
            cardHeight);

        var direct = new PixelRect(
            display.X + HorizontalMargin,
            display.Y + DirectTopMargin,
            cardWidth,
            DesiredDirectHeight);

        return new OverlayPlacement(front, side, card, direct);
    }
}

public static class NotificationLayoutPlanner
{
    private const int Gap = 20;
    private const int MinimumHeight = 190;
    private const int ComfortHeight = 260;
    private const int MaximumHeight = 480;

    public static int Capacity(PixelRect region, bool blocked)
    {
        if (blocked || region.Height < MinimumHeight)
        {
            return 0;
        }

        return region.Height < ComfortHeight ? 1 : 2;
    }

    public static IReadOnlyList<PixelRect> PlanSlots(
        PixelRect region,
        int count)
    {
        if (count is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0 || region.Height < MinimumHeight)
        {
            return [];
        }

        var height = Math.Min(MaximumHeight, region.Height);
        var y = region.Bottom - height;
        if (count == 1)
        {
            return [new PixelRect(region.X, y, region.Width, height)];
        }

        var leftWidth = (region.Width - Gap) / 2;
        return
        [
            new PixelRect(region.X, y, leftWidth, height),
            new PixelRect(
                region.X + leftWidth + Gap,
                y,
                region.Width - leftWidth - Gap,
                height),
        ];
    }
}
