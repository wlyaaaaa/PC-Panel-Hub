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
        Assert.Equal(placement.Card.Width, placement.Direct.Width);
        Assert.True(placement.Direct.Height >= 190);
    }

    [Fact]
    public void NotificationLayout_UsesTwoNonOverlappingWideCards()
    {
        var region = new PixelRect(56, 280, 1413, 480);

        Assert.Equal(2, NotificationLayoutPlanner.Capacity(region, false));
        var slots = NotificationLayoutPlanner.PlanSlots(region, 2);

        Assert.Equal(2, slots.Count);
        Assert.True(slots[0].Right < slots[1].Left);
        Assert.Equal(region.Left, slots[0].Left);
        Assert.Equal(region.Right, slots[1].Right);
        Assert.Equal(region.Bottom, slots[0].Bottom);
        Assert.Equal(region.Bottom, slots[1].Bottom);
    }

    [Fact]
    public void NotificationLayout_UsesFullWidthForOneAndQueuesDuringAlert()
    {
        var region = new PixelRect(56, 280, 1413, 480);

        var slot = Assert.Single(
            NotificationLayoutPlanner.PlanSlots(region, 1));
        Assert.Equal(region.Width, slot.Width);
        Assert.Equal(0, NotificationLayoutPlanner.Capacity(region, true));
    }
}
