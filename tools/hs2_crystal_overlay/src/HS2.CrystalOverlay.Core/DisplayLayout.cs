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
    private const int DesiredDirectWidth = 520;
    private const int DesiredDirectHeight = 176;

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

        var directWidth = Math.Min(
            DesiredDirectWidth,
            frontWidth - HorizontalMargin * 2);
        var direct = new PixelRect(
            display.X + HorizontalMargin,
            display.Y + DirectTopMargin,
            directWidth,
            DesiredDirectHeight);

        return new OverlayPlacement(front, side, card, direct);
    }
}
