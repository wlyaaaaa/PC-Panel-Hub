using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class DisplayLayoutTests
{
    [Fact]
    public void ExactDeviceName_WinsOverResolutionFallback()
    {
        var displays = new[]
        {
            new DisplayGeometry(@"\\.\DISPLAY19", 0, 0, 2288, 1048, false),
            new DisplayGeometry(@"\\.\DISPLAY20", 3840, -1048, 2288, 1048, false),
        };

        var selected = DisplayTargetSelector.Select(
            displays,
            @"\\.\DISPLAY20",
            2288,
            1048);

        Assert.Equal(@"\\.\DISPLAY20", selected?.DeviceName);
        Assert.Equal(3840, selected?.X);
    }

    [Fact]
    public void ResolutionFallback_NeverChoosesPrimaryWhenSecondaryMatches()
    {
        var displays = new[]
        {
            new DisplayGeometry(@"\\.\DISPLAY1", 0, 0, 2288, 1048, true),
            new DisplayGeometry(@"\\.\DISPLAY9", 2288, 0, 2288, 1048, false),
        };

        var selected = DisplayTargetSelector.Select(
            displays,
            @"\\.\MISSING",
            2288,
            1048);

        Assert.Equal(@"\\.\DISPLAY9", selected?.DeviceName);
    }

    [Fact]
    public void PreferredName_IsRejectedWhenItNowBelongsToPrimaryDisplay()
    {
        var displays = new[]
        {
            new DisplayGeometry(
                @"\\.\DISPLAY20",
                0,
                0,
                3840,
                2160,
                true),
            new DisplayGeometry(
                @"\\.\DISPLAY9",
                3840,
                -1048,
                2288,
                1048,
                false),
        };

        var selected = DisplayTargetSelector.Select(
            displays,
            @"\\.\DISPLAY20",
            2288,
            1048);

        Assert.Equal(@"\\.\DISPLAY9", selected?.DeviceName);
    }

    [Fact]
    public void Hs2Placement_UsesFrontTwoThirdsAndKeepsCardsOffSidePanel()
    {
        var display = new DisplayGeometry(
            @"\\.\DISPLAY20",
            3840,
            -1048,
            2288,
            1048,
            false);

        var placement = OverlayLayoutPlanner.Plan(display);

        Assert.Equal(1525, placement.FrontRegion.Width);
        Assert.Equal(763, placement.SideRegion.Width);
        Assert.True(placement.Card.Right <= placement.FrontRegion.Right);
        Assert.True(placement.Card.Width >= 1200);
        Assert.True(placement.Direct.Top >= display.Y);
        Assert.True(placement.Direct.Right <= placement.FrontRegion.Right);
    }
}
