using System.Numerics;
using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class OverlayDeckLayoutTests
{
    private static readonly DisplayGeometry Hs2Display = new(
        @"\\.\DISPLAY20",
        0,
        0,
        2288,
        1048,
        false);

    [Fact]
    public void ApprovedSixCardComposition_UsesTheThreeRowProductLayout()
    {
        var plan = CompositionPlanner.Plan(Hs2Display, ApprovedCards());
        var cards = plan.Cards.ToDictionary(card => card.EventId);

        Assert.Equal(1493, plan.FoldX);
        Assert.Equal(new PixelRect(44, 44, 1437, 198), plan.DirectRegion);
        Assert.Equal(6, cards.Count);

        AssertPlacement(
            cards["media"],
            new PixelRect(44, 814, 1437, 190),
            OverlayDeckRegion.Front,
            0);
        AssertPlacement(
            cards["short"],
            new PixelRect(1505, 814, 675, 190),
            OverlayDeckRegion.Side,
            0);
        AssertPlacement(
            cards["activity"],
            new PixelRect(44, 550, 1437, 240),
            OverlayDeckRegion.Front,
            1);
        AssertPlacement(
            cards["phone-old"],
            new PixelRect(1505, 550, 675, 240),
            OverlayDeckRegion.Side,
            1);
        AssertPlacement(
            cards["phone-latest"],
            new PixelRect(44, 266, 706, 260),
            OverlayDeckRegion.Front,
            2);
        AssertPlacement(
            cards["phone-second"],
            new PixelRect(774, 266, 707, 260),
            OverlayDeckRegion.Front,
            2);
    }

    [Fact]
    public void EverySubsetFromZeroToSix_IsDenseBottomAlignedAndSafe()
    {
        var source = ApprovedCards();

        for (var mask = 0; mask < 1 << source.Length; mask++)
        {
            var requests = source
                .Where((_, index) => (mask & (1 << index)) != 0)
                .ToArray();
            var plan = CompositionPlanner.Plan(Hs2Display, requests);

            Assert.Equal(BitOperations.PopCount((uint)mask), plan.Cards.Count);
            Assert.Equal(
                requests.Select(request => request.EventId).Order(),
                plan.Cards.Select(card => card.EventId).Order());
            AssertPlanInvariants(plan, Hs2Display);
        }
    }

    [Fact]
    public void MissingBaseCard_PullsTheRemainingRowsDownWithoutAPlaceholder()
    {
        var plan = CompositionPlanner.Plan(
            Hs2Display,
            [
                new(
                    "activity",
                    OverlayCardKind.Activity,
                    0),
                new(
                    "short",
                    OverlayCardKind.Transient,
                    1),
            ]);
        var cards = plan.Cards.ToDictionary(card => card.EventId);

        Assert.Equal(2, cards.Count);
        AssertPlacement(
            cards["activity"],
            new PixelRect(44, 764, 1437, 240),
            OverlayDeckRegion.Front,
            0);
        AssertPlacement(
            cards["short"],
            new PixelRect(1505, 764, 675, 240),
            OverlayDeckRegion.Side,
            0);
    }

    [Fact]
    public void ACompactCard_UsesTheSideOnlyWhenAFrontCardSharesItsRow()
    {
        var paired = CompositionPlanner.Plan(
            Hs2Display,
            [
                new(
                    "wide",
                    OverlayCardKind.Generic,
                    0,
                    OverlayCardWidthPreference.Wide),
                new(
                    "compact",
                    OverlayCardKind.Generic,
                    1,
                    OverlayCardWidthPreference.Compact),
            ]);

        Assert.Contains(
            paired.Cards,
            card => card.EventId == "wide" &&
                card.Region == OverlayDeckRegion.Front);
        Assert.Contains(
            paired.Cards,
            card => card.EventId == "compact" &&
                card.Region == OverlayDeckRegion.Side);

        var alone = CompositionPlanner.Plan(
            Hs2Display,
            [
                new(
                    "compact",
                    OverlayCardKind.Transient,
                    0,
                    OverlayCardWidthPreference.Compact),
            ]);

        var placement = Assert.Single(alone.Cards);
        Assert.Equal(OverlayDeckRegion.Front, placement.Region);
        Assert.Equal(1437, placement.Bounds.Width);
        AssertPlanInvariants(alone, Hs2Display);
    }

    [Fact]
    public void InputOrderDoesNotChangeStableEventIdPlacements()
    {
        var requests = ApprovedCards();
        var forward = CompositionPlanner.Plan(Hs2Display, requests);
        var reverse = CompositionPlanner.Plan(
            Hs2Display,
            requests.Reverse());

        Assert.Equal(
            forward.Cards.ToDictionary(card => card.EventId),
            reverse.Cards.ToDictionary(card => card.EventId));
    }

    [Fact]
    public void PlannerCapsAtSixBySortOrderAndRejectsDuplicateEventIds()
    {
        var requests = Enumerable.Range(0, 8)
            .Select(index => new OverlayCardLayoutRequest(
                $"event-{index}",
                OverlayCardKind.Generic,
                index))
            .Reverse()
            .ToArray();

        var plan = CompositionPlanner.Plan(Hs2Display, requests);

        Assert.Equal(CompositionPlanner.MaxCards, plan.Cards.Count);
        Assert.Equal(
            Enumerable.Range(0, 6).Select(index => $"event-{index}").Order(),
            plan.Cards.Select(card => card.EventId).Order());
        Assert.Throws<ArgumentException>(() => CompositionPlanner.Plan(
            Hs2Display,
            [
                new("same", OverlayCardKind.Generic, 0),
                new("same", OverlayCardKind.Transient, 1),
            ]));
    }

    private static OverlayCardLayoutRequest[] ApprovedCards() =>
    [
        new("phone-latest", OverlayCardKind.Notification, 0),
        new("phone-second", OverlayCardKind.Notification, 1),
        new("phone-old", OverlayCardKind.Notification, 2),
        new("short", OverlayCardKind.Transient, 3),
        new("activity", OverlayCardKind.Activity, 4),
        new("media", OverlayCardKind.Media, 5),
    ];

    private static void AssertPlacement(
        OverlayCardLayoutPlacement actual,
        PixelRect expectedBounds,
        OverlayDeckRegion expectedRegion,
        int expectedRow)
    {
        Assert.Equal(expectedBounds, actual.Bounds);
        Assert.Equal(expectedRegion, actual.Region);
        Assert.Equal(expectedRow, actual.Row);
    }

    private static void AssertPlanInvariants(
        OverlayDeckPlan plan,
        DisplayGeometry display)
    {
        var displayBounds = new PixelRect(
            display.X,
            display.Y,
            display.Width,
            display.Height);
        Assert.True(plan.Cards.Count <= CompositionPlanner.MaxCards);
        Assert.Equal(
            plan.Cards.Count,
            plan.Cards.Select(card => card.EventId).Distinct().Count());

        foreach (var card in plan.Cards)
        {
            Assert.True(card.Bounds.Width > 0);
            Assert.True(card.Bounds.Height > 0);
            Assert.True(card.Bounds.Left >= displayBounds.Left);
            Assert.True(card.Bounds.Top >= displayBounds.Top);
            Assert.True(card.Bounds.Right <= displayBounds.Right);
            Assert.True(card.Bounds.Bottom <= displayBounds.Bottom);
            if (card.Region == OverlayDeckRegion.Front)
            {
                Assert.True(card.Bounds.Right <= plan.FoldX);
            }
            else
            {
                Assert.True(card.Bounds.Left >= plan.FoldX);
                Assert.Contains(
                    plan.Cards,
                    candidate => candidate.Row == card.Row &&
                        candidate.Region == OverlayDeckRegion.Front);
            }
        }

        for (var left = 0; left < plan.Cards.Count; left++)
        {
            for (var right = left + 1; right < plan.Cards.Count; right++)
            {
                Assert.False(Overlaps(
                    plan.Cards[left].Bounds,
                    plan.Cards[right].Bounds));
            }
        }

        var rows = plan.Cards
            .GroupBy(card => card.Row)
            .OrderBy(row => row.Key)
            .ToArray();
        Assert.Equal(Enumerable.Range(0, rows.Length), rows.Select(row => row.Key));
        if (rows.Length == 0)
        {
            return;
        }

        Assert.All(
            rows,
            row =>
            {
                Assert.Single(row.Select(card => card.Bounds.Top).Distinct());
                Assert.Single(row.Select(card => card.Bounds.Height).Distinct());
            });
        Assert.All(
            rows[0],
            card => Assert.Equal(displayBounds.Bottom - 44, card.Bounds.Bottom));
        for (var index = 1; index < rows.Length; index++)
        {
            var upperBottom = rows[index].First().Bounds.Bottom;
            var lowerTop = rows[index - 1].First().Bounds.Top;
            Assert.Equal(24, lowerTop - upperBottom);
        }

        var top = rows[^1].First().Bounds.Top;
        Assert.True(plan.DirectRegion.Bottom + 24 <= top);
    }

    private static bool Overlaps(PixelRect left, PixelRect right) =>
        left.Left < right.Right &&
        left.Right > right.Left &&
        left.Top < right.Bottom &&
        left.Bottom > right.Top;
}
