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
        Assert.Equal(new PixelRect(44, 814, 706, 190), placement.Bounds);
        AssertPlanInvariants(alone, Hs2Display);
    }

    [Fact]
    public void TwoCompactCards_FillBottomLeftThenBottomRightBeforeTheSide()
    {
        var plan = CompositionPlanner.Plan(
            Hs2Display,
            [
                new(
                    "first",
                    OverlayCardKind.Transient,
                    0,
                    OverlayCardWidthPreference.Compact),
                new(
                    "second",
                    OverlayCardKind.Transient,
                    1,
                    OverlayCardWidthPreference.Compact),
            ]);
        var cards = plan.Cards.ToDictionary(card => card.EventId);

        AssertPlacement(
            cards["first"],
            new PixelRect(44, 814, 706, 190),
            OverlayDeckRegion.Front,
            0);
        AssertPlacement(
            cards["second"],
            new PixelRect(774, 814, 707, 190),
            OverlayDeckRegion.Front,
            0);
    }

    [Fact]
    public void AudioHud_DefaultsToTheBottomLeftWithoutUsingAFixedPosition()
    {
        var plan = CompositionPlanner.Plan(
            Hs2Display,
            [
                new(
                    "audio-hud",
                    OverlayCardKind.Transient,
                    0,
                    OverlayCardWidthPreference.Compact,
                    OverlayCardPlacementPreference.BottomLeft),
            ]);

        var audio = Assert.Single(plan.Cards);
        AssertPlacement(
            audio,
            new PixelRect(44, 814, 706, 190),
            OverlayDeckRegion.Front,
            0);
    }

    [Fact]
    public void LatestPhoneOwnsTheBottomFrontAndAudioAdaptsToTheSide()
    {
        var plan = CompositionPlanner.Plan(
            Hs2Display,
            [
                new(
                    "phone-latest",
                    OverlayCardKind.Notification,
                    0,
                    OverlayCardWidthPreference.Auto,
                    OverlayCardPlacementPreference.BottomStack,
                    0),
                new(
                    "audio-hud",
                    OverlayCardKind.Transient,
                    1,
                    OverlayCardWidthPreference.Compact,
                    OverlayCardPlacementPreference.BottomLeft),
            ]);
        var cards = plan.Cards.ToDictionary(card => card.EventId);

        AssertPlacement(
            cards["phone-latest"],
            new PixelRect(44, 744, 1437, 260),
            OverlayDeckRegion.Front,
            0);
        AssertPlacement(
            cards["audio-hud"],
            new PixelRect(1505, 744, 675, 260),
            OverlayDeckRegion.Side,
            0);
    }

    [Fact]
    public void PhoneNotifications_StackLatestAtTheBottomAndOlderOnesAbove()
    {
        var plan = CompositionPlanner.Plan(
            Hs2Display,
            [
                Phone("phone-latest", stackOrder: 0),
                Phone("phone-second", stackOrder: 1),
                Phone("phone-third", stackOrder: 2),
            ]);
        var cards = plan.Cards.ToDictionary(card => card.EventId);

        Assert.Equal(0, cards["phone-latest"].Row);
        Assert.Equal(1, cards["phone-second"].Row);
        Assert.Equal(2, cards["phone-third"].Row);
        Assert.All(cards.Values, card =>
            Assert.Equal(OverlayDeckRegion.Front, card.Region));
        Assert.True(
            cards["phone-latest"].Bounds.Top >
            cards["phone-second"].Bounds.Top);
        Assert.True(
            cards["phone-second"].Bounds.Top >
            cards["phone-third"].Bounds.Top);
    }

    [Fact]
    public void AudioWithWideCardAndNoPhone_KeepsBottomLeftAndMovesWideCardUp()
    {
        var plan = CompositionPlanner.Plan(
            Hs2Display,
            [
                new(
                    "audio-hud",
                    OverlayCardKind.Transient,
                    0,
                    OverlayCardWidthPreference.Compact,
                    OverlayCardPlacementPreference.BottomLeft),
                new(
                    "media",
                    OverlayCardKind.Media,
                    1,
                    OverlayCardWidthPreference.Wide),
            ]);
        var cards = plan.Cards.ToDictionary(card => card.EventId);

        Assert.Equal(0, cards["audio-hud"].Row);
        Assert.Equal(44, cards["audio-hud"].Bounds.Left);
        Assert.Equal(706, cards["audio-hud"].Bounds.Width);
        Assert.Equal(1, cards["media"].Row);
        Assert.Equal(OverlayDeckRegion.Front, cards["media"].Region);
    }

    [Fact]
    public void VerificationCodeStaysOnReadableFrontUnderMaximumPressure()
    {
        var plan = CompositionPlanner.Plan(
            Hs2Display,
            [
                new(
                    "verification",
                    OverlayCardKind.Verification,
                    0,
                    OverlayCardWidthPreference.Compact,
                    OverlayCardPlacementPreference.BottomLeft),
                Phone("phone-latest", 0),
                Phone("phone-second", 1),
                new(
                    "alert",
                    OverlayCardKind.Alert,
                    3,
                    OverlayCardWidthPreference.Wide),
                new(
                    "game",
                    OverlayCardKind.Activity,
                    4,
                    OverlayCardWidthPreference.Wide),
                new(
                    "media",
                    OverlayCardKind.Media,
                    5,
                    OverlayCardWidthPreference.Wide),
            ]);

        var verification = Assert.Single(plan.Cards, card =>
            card.EventId == "verification");
        Assert.Equal(OverlayDeckRegion.Front, verification.Region);
        Assert.InRange(verification.Bounds.Width, 675, 750);
        AssertPlanInvariants(plan, Hs2Display);
    }

    [Fact]
    public void VerificationCodeUsesCompactFrontCardAcrossAdaptiveContexts()
    {
        var code = new OverlayCardLayoutRequest(
            "verification",
            OverlayCardKind.Verification,
            0,
            OverlayCardWidthPreference.Compact,
            OverlayCardPlacementPreference.BottomLeft);
        OverlayCardLayoutRequest[][] contexts =
        [
            [code],
            [
                code,
                new(
                    "phone-latest",
                    OverlayCardKind.Notification,
                    1,
                    PlacementPreference:
                        OverlayCardPlacementPreference.BottomStack,
                    StackOrder: 0),
            ],
            [
                code,
                new(
                    "audio-hud",
                    OverlayCardKind.Transient,
                    1,
                    OverlayCardWidthPreference.Compact,
                    OverlayCardPlacementPreference.BottomLeft),
            ],
            [
                code,
                new(
                    "phone-latest",
                    OverlayCardKind.Notification,
                    1,
                    PlacementPreference:
                        OverlayCardPlacementPreference.BottomStack,
                    StackOrder: 0),
                new(
                    "phone-second",
                    OverlayCardKind.Notification,
                    2,
                    PlacementPreference:
                        OverlayCardPlacementPreference.BottomStack,
                    StackOrder: 1),
                new(
                    "phone-third",
                    OverlayCardKind.Notification,
                    3,
                    PlacementPreference:
                        OverlayCardPlacementPreference.BottomStack,
                    StackOrder: 2),
                new(
                    "media",
                    OverlayCardKind.Media,
                    4,
                    OverlayCardWidthPreference.Wide),
                new(
                    "game",
                    OverlayCardKind.Activity,
                    5,
                    OverlayCardWidthPreference.Wide),
            ],
        ];

        foreach (var context in contexts)
        {
            var plan = CompositionPlanner.Plan(Hs2Display, context);
            var verification = Assert.Single(plan.Cards, card =>
                card.EventId == code.EventId);
            Assert.Equal(OverlayDeckRegion.Front, verification.Region);
            Assert.InRange(verification.Bounds.Width, 675, 750);
            AssertPlanInvariants(plan, Hs2Display);
        }
    }

    [Fact]
    public void EveryAdaptiveSubset_PreservesBottomOrderWithoutPlaceholders()
    {
        var source = new[]
        {
            "phone-latest",
            "phone-second",
            "phone-third",
            "audio-hud",
            "game",
            "media",
            "task",
        };

        for (var mask = 0; mask < 1 << source.Length; mask++)
        {
            var included = source
                .Where((_, index) => (mask & (1 << index)) != 0)
                .ToArray();
            var phones = included
                .Where(id => id.StartsWith("phone-", StringComparison.Ordinal))
                .ToArray();
            var requests = new List<OverlayCardLayoutRequest>();
            requests.AddRange(phones.Select((id, stackOrder) =>
                Phone(id, stackOrder)));
            if (included.Contains("audio-hud"))
            {
                requests.Add(new(
                    "audio-hud",
                    OverlayCardKind.Transient,
                    requests.Count,
                    OverlayCardWidthPreference.Compact,
                    OverlayCardPlacementPreference.BottomLeft));
            }

            if (included.Contains("game"))
            {
                requests.Add(new(
                    "game",
                    OverlayCardKind.Activity,
                    requests.Count,
                    OverlayCardWidthPreference.Wide));
            }

            if (included.Contains("media"))
            {
                requests.Add(new(
                    "media",
                    OverlayCardKind.Media,
                    requests.Count,
                    OverlayCardWidthPreference.Wide));
            }

            if (included.Contains("task"))
            {
                requests.Add(new(
                    "task",
                    OverlayCardKind.Progress,
                    requests.Count,
                    OverlayCardWidthPreference.Wide));
            }

            var plan = CompositionPlanner.Plan(Hs2Display, requests);
            AssertPlanInvariants(plan, Hs2Display);
            var cards = plan.Cards.ToDictionary(card => card.EventId);
            for (var index = 0; index < phones.Length; index++)
            {
                Assert.Equal(index, cards[phones[index]].Row);
            }

            if (cards.TryGetValue("audio-hud", out var audio))
            {
                Assert.Equal(0, audio.Row);
                var wideFrontCards = included.Count(id =>
                    id is "game" or "media" or "task");
                if (phones.Length == 0 && wideFrontCards < 3)
                {
                    Assert.True(
                        audio.Bounds.Left == 44,
                        $"Audio was not bottom-left for: {string.Join(", ", included)}");
                    Assert.Equal(OverlayDeckRegion.Front, audio.Region);
                }
                else if (phones.Length > 0)
                {
                    Assert.Equal(0, cards[phones[0]].Row);
                    Assert.Equal(
                        OverlayDeckRegion.Front,
                        cards[phones[0]].Region);
                }
            }
        }
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
        Assert.Throws<ArgumentException>(() => CompositionPlanner.Plan(
            Hs2Display,
            [
                new(
                    "phone",
                    OverlayCardKind.Notification,
                    0,
                    PlacementPreference:
                        OverlayCardPlacementPreference.BottomStack,
                    StackOrder: 2),
            ]));

        var ignoresTruncatedInvalidStackCard = CompositionPlanner.Plan(
            Hs2Display,
            [
                Phone("phone-0", 0),
                Phone("phone-1", 1),
                Phone("phone-2", 2),
                new("generic-0", OverlayCardKind.Generic, 3),
                new("generic-1", OverlayCardKind.Generic, 4),
                new("generic-2", OverlayCardKind.Generic, 5),
                new(
                    "truncated-invalid",
                    OverlayCardKind.Notification,
                    99,
                    PlacementPreference:
                        OverlayCardPlacementPreference.BottomStack,
                    StackOrder: 99),
            ]);
        Assert.Equal(6, ignoresTruncatedInvalidStackCard.Cards.Count);
        Assert.DoesNotContain(
            ignoresTruncatedInvalidStackCard.Cards,
            card => card.EventId == "truncated-invalid");
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

    private static OverlayCardLayoutRequest Phone(
        string eventId,
        int stackOrder) => new(
            eventId,
            OverlayCardKind.Notification,
            stackOrder,
            OverlayCardWidthPreference.Auto,
            OverlayCardPlacementPreference.BottomStack,
            stackOrder);

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
