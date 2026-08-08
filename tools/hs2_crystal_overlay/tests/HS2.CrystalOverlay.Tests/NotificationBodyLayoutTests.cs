using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class NotificationBodyLayoutTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(6, true)]
    public void NotificationBody_ScrollsOnlyAfterTheSecondVisibleLine(
        int bodyLines,
        bool expected)
    {
        var layout = NotificationBodyLayout.Create(
            cardHeight: 230,
            headerHeight: 52,
            titleHeight: 54,
            titleBodyGap: 8,
            bottomReserve: 12,
            preferredLineHeight: 42);

        Assert.Equal(expected, layout.ShouldScroll(bodyLines * 42));
    }

    [Fact]
    public void CompactNotificationCard_ReservesACompleteTwoLineBodyViewport()
    {
        var layout = NotificationBodyLayout.Create(
            cardHeight: 230,
            headerHeight: 52,
            titleHeight: 54,
            titleBodyGap: 8,
            bottomReserve: 12,
            preferredLineHeight: 42);

        Assert.Equal(2, layout.VisibleLines);
        Assert.Equal(42, layout.LineHeight, 6);
        Assert.Equal(84, layout.ViewportHeight, 6);
        Assert.False(layout.ShouldScroll(84));
        // GDI+ can report a two-line paragraph a fraction of a pixel taller
        // than two independently measured lines; that noise must not start
        // the marquee early.
        Assert.False(layout.ShouldScroll(84.75));
    }

    [Fact]
    public void LongBody_UsesTheWholeOverflowSoTheLastLineIsNotClipped()
    {
        var layout = NotificationBodyLayout.Create(
            cardHeight: 230,
            headerHeight: 52,
            titleHeight: 54,
            titleBodyGap: 8,
            bottomReserve: 12,
            preferredLineHeight: 42);

        Assert.Equal(42, layout.Overflow(3 * 42), 6);
        Assert.Equal(0, layout.OffsetForProgress(0, 3 * 42), 6);
        Assert.Equal(42, layout.OffsetForProgress(1, 3 * 42), 6);
    }

    [Fact]
    public void WideNormalCard_FitsTwoMeasuredBodyLinesAt260Pixels()
    {
        var layout = NotificationBodyLayout.Create(
            cardHeight: 260,
            headerHeight: 58,
            titleHeight: 52 * 1.22,
            titleBodyGap: 10,
            bottomReserve: 16,
            preferredLineHeight: 42 * 1.22);

        Assert.Equal(2, layout.VisibleLines);
        Assert.Equal(42 * 1.22, layout.LineHeight, 6);
        Assert.False(layout.ShouldScroll(2 * 42 * 1.22 + 0.75));
    }
}
