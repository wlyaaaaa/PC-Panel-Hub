namespace HS2.CrystalOverlay.Core;

public enum OverlayCardKind
{
    Notification,
    Media,
    Activity,
    Progress,
    Transient,
    Alert,
    Generic,
}

public enum OverlayCardWidthPreference
{
    Auto,
    Wide,
    Compact,
}

public enum OverlayDeckRegion
{
    Front,
    Side,
}

public sealed record OverlayCardLayoutRequest(
    string EventId,
    OverlayCardKind Kind,
    int SortOrder,
    OverlayCardWidthPreference WidthPreference =
        OverlayCardWidthPreference.Auto);

public sealed record OverlayCardLayoutPlacement(
    string EventId,
    PixelRect Bounds,
    OverlayDeckRegion Region,
    int Row);

public sealed record OverlayDeckPlan(
    PixelRect DirectRegion,
    int FoldX,
    IReadOnlyList<OverlayCardLayoutPlacement> Cards);

public static class CompositionPlanner
{
    public const int MaxCards = 6;

    private const int ReferenceWidth = 2288;
    private const int ReferenceHeight = 1048;
    private const int LeftMargin = 44;
    private const int BottomMargin = 44;
    private const int FrontWidth = 1437;
    private const int SideWidth = 675;
    private const int ColumnGap = 24;
    private const int RowGap = 24;
    private const int DirectTopMargin = 44;
    private const int DirectHeight = 198;
    private const int MaximumDeckHeight = 738;
    private const int MinimumCardHeight = 190;

    public static OverlayDeckPlan Plan(
        DisplayGeometry display,
        IEnumerable<OverlayCardLayoutRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(requests);
        if (display.Width <= 0 || display.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(display),
                "Display dimensions must be positive.");
        }

        var materialized = requests.ToArray();
        if (materialized.Any(request => request is null))
        {
            throw new ArgumentException(
                "Layout requests cannot contain null entries.",
                nameof(requests));
        }

        foreach (var request in materialized)
        {
            if (string.IsNullOrWhiteSpace(request.EventId))
            {
                throw new ArgumentException(
                    "Every layout request needs a non-empty event ID.",
                    nameof(requests));
            }
        }

        if (materialized
            .GroupBy(request => request.EventId, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Event IDs must be unique within a composition.",
                nameof(requests));
        }

        var cards = materialized
            .OrderBy(request => request.SortOrder)
            .ThenBy(request => request.EventId, StringComparer.Ordinal)
            .Take(MaxCards)
            .Select((request, priorityRank) => new LayoutCard(
                request,
                priorityRank))
            .ToArray();
        var geometry = DeckGeometry.For(display);
        if (cards.Length == 0)
        {
            return new OverlayDeckPlan(
                geometry.DirectRegion,
                geometry.FoldX,
                []);
        }

        var rowCount = (cards.Length + 1) / 2;
        var assignment = SelectBestAssignment(cards, rowCount);
        var rowHeights = CalculateRowHeights(
            assignment,
            rowCount,
            geometry);
        var rowBounds = CalculateRowBounds(
            display,
            rowHeights,
            geometry);
        var placements = assignment.Slots
            .Select((slot, index) => new OverlayCardLayoutPlacement(
                assignment.Cards[index].Request.EventId,
                CalculateCardBounds(
                    slot,
                    rowBounds[slot.Row],
                    geometry),
                slot.Kind == SlotKind.Side
                    ? OverlayDeckRegion.Side
                    : OverlayDeckRegion.Front,
                slot.Row))
            .ToArray();

        return new OverlayDeckPlan(
            geometry.DirectRegion,
            geometry.FoldX,
            placements);
    }

    private static LayoutAssignment SelectBestAssignment(
        IReadOnlyList<LayoutCard> cards,
        int rowCount)
    {
        LayoutAssignment? best = null;
        foreach (var templates in GenerateTemplates(
            rowCount,
            cards.Count))
        {
            var slots = BuildSlots(templates);
            var candidateCards = new LayoutCard[cards.Count];
            var used = new bool[cards.Count];

            Search(0, 0);

            void Search(int slotIndex, int score)
            {
                if (slotIndex == slots.Length)
                {
                    if (best is null || score > best.Score)
                    {
                        best = new LayoutAssignment(
                            score,
                            slots,
                            candidateCards.ToArray());
                    }

                    return;
                }

                for (var cardIndex = 0;
                     cardIndex < cards.Count;
                     cardIndex++)
                {
                    if (used[cardIndex])
                    {
                        continue;
                    }

                    used[cardIndex] = true;
                    candidateCards[slotIndex] = cards[cardIndex];
                    Search(
                        slotIndex + 1,
                        score + Score(
                            cards[cardIndex],
                            slots[slotIndex],
                            rowCount,
                            cards.Count));
                    used[cardIndex] = false;
                }
            }
        }

        return best ?? throw new InvalidOperationException(
            "No dense deck layout exists for the supplied cards.");
    }

    private static IEnumerable<RowTemplate[]> GenerateTemplates(
        int rowCount,
        int cardCount)
    {
        var current = new RowTemplate[rowCount];
        foreach (var result in Generate(0, 0))
        {
            yield return result;
        }

        IEnumerable<RowTemplate[]> Generate(
            int row,
            int occupiedSlots)
        {
            if (row == rowCount)
            {
                if (occupiedSlots == cardCount)
                {
                    yield return current.ToArray();
                }

                yield break;
            }

            foreach (var template in Enum.GetValues<RowTemplate>())
            {
                var nextOccupied = occupiedSlots + Capacity(template);
                var remainingRows = rowCount - row - 1;
                if (nextOccupied + remainingRows > cardCount ||
                    nextOccupied + remainingRows * 3 < cardCount)
                {
                    continue;
                }

                current[row] = template;
                foreach (var result in Generate(row + 1, nextOccupied))
                {
                    yield return result;
                }
            }
        }
    }

    private static LayoutSlot[] BuildSlots(
        IReadOnlyList<RowTemplate> templates)
    {
        var slots = new List<LayoutSlot>();
        for (var row = 0; row < templates.Count; row++)
        {
            switch (templates[row])
            {
                case RowTemplate.FrontWide:
                    slots.Add(new LayoutSlot(row, SlotKind.FrontWide));
                    break;
                case RowTemplate.FrontWideWithSide:
                    slots.Add(new LayoutSlot(row, SlotKind.FrontWide));
                    slots.Add(new LayoutSlot(row, SlotKind.Side));
                    break;
                case RowTemplate.FrontSplit:
                    slots.Add(new LayoutSlot(row, SlotKind.FrontLeft));
                    slots.Add(new LayoutSlot(row, SlotKind.FrontRight));
                    break;
                case RowTemplate.FrontSplitWithSide:
                    slots.Add(new LayoutSlot(row, SlotKind.FrontLeft));
                    slots.Add(new LayoutSlot(row, SlotKind.FrontRight));
                    slots.Add(new LayoutSlot(row, SlotKind.Side));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        return slots.ToArray();
    }

    private static int Score(
        LayoutCard card,
        LayoutSlot slot,
        int rowCount,
        int cardCount)
    {
        var score = KindSlotScore(card.Request.Kind, slot.Kind);
        score += PreferenceScore(
            card.Request.WidthPreference,
            slot.Kind);

        var desiredRow = card.Request.Kind switch
        {
            OverlayCardKind.Media => 0,
            OverlayCardKind.Activity => Math.Min(1, rowCount - 1),
            OverlayCardKind.Progress => Math.Min(1, rowCount - 1),
            OverlayCardKind.Transient => 0,
            OverlayCardKind.Notification
                when slot.Kind == SlotKind.Side =>
                    Math.Max(0, rowCount - 2),
            _ => rowCount - 1,
        };
        var rowPenalty = card.Request.Kind switch
        {
            OverlayCardKind.Media => 100,
            OverlayCardKind.Activity => 80,
            OverlayCardKind.Progress => 70,
            OverlayCardKind.Notification => 90,
            OverlayCardKind.Transient => 40,
            OverlayCardKind.Alert => 70,
            _ => 40,
        };
        score -= Math.Abs(slot.Row - desiredRow) * rowPenalty;

        if (card.Request.Kind == OverlayCardKind.Notification)
        {
            if (slot.Kind == SlotKind.Side)
            {
                score += card.PriorityRank * 25;
            }
            else
            {
                score += (cardCount - card.PriorityRank) * 25;
            }

            score += slot.Row *
                (cardCount - card.PriorityRank) *
                10;
        }

        return score;
    }

    private static int KindSlotScore(
        OverlayCardKind kind,
        SlotKind slot) => kind switch
        {
            OverlayCardKind.Media => slot switch
            {
                SlotKind.FrontWide => 400,
                SlotKind.Side => -2000,
                _ => 120,
            },
            OverlayCardKind.Activity => slot switch
            {
                SlotKind.FrontWide => 360,
                SlotKind.Side => -1600,
                _ => 170,
            },
            OverlayCardKind.Progress => slot switch
            {
                SlotKind.FrontWide => 340,
                SlotKind.Side => -1400,
                _ => 180,
            },
            OverlayCardKind.Notification => slot switch
            {
                SlotKind.FrontWide => 220,
                SlotKind.Side => 80,
                _ => 300,
            },
            OverlayCardKind.Transient => slot switch
            {
                SlotKind.FrontWide => 80,
                SlotKind.Side => 360,
                _ => 140,
            },
            OverlayCardKind.Alert => slot switch
            {
                SlotKind.FrontWide => 300,
                SlotKind.Side => 80,
                _ => 250,
            },
            _ => slot switch
            {
                SlotKind.FrontWide => 220,
                SlotKind.Side => 200,
                _ => 240,
            },
        };

    private static int PreferenceScore(
        OverlayCardWidthPreference preference,
        SlotKind slot) => preference switch
        {
            OverlayCardWidthPreference.Wide => slot switch
            {
                SlotKind.FrontWide => 200,
                SlotKind.Side => -2000,
                _ => 0,
            },
            OverlayCardWidthPreference.Compact => slot switch
            {
                SlotKind.FrontWide => -40,
                SlotKind.Side => 220,
                _ => 80,
            },
            _ => 0,
        };

    private static int[] CalculateRowHeights(
        LayoutAssignment assignment,
        int rowCount,
        DeckGeometry geometry)
    {
        var heights = Enumerable.Range(0, rowCount)
            .Select(row => assignment.Slots
                .Select((slot, index) => (slot, index))
                .Where(value => value.slot.Row == row &&
                    value.slot.Kind != SlotKind.Side)
                .Select(value => PreferredHeight(
                    assignment.Cards[value.index].Request.Kind,
                    geometry))
                .Max())
            .ToArray();
        var budget = geometry.MaximumDeckHeight -
            geometry.RowGap * (rowCount - 1);
        var minimum = geometry.MinimumCardHeight;

        while (heights.Sum() > budget &&
               heights.Any(height => height > minimum))
        {
            var tallest = heights.Max();
            for (var row = 0;
                 row < heights.Length && heights.Sum() > budget;
                 row++)
            {
                if (heights[row] == tallest &&
                    heights[row] > minimum)
                {
                    heights[row]--;
                }
            }
        }

        while (heights.Sum() > budget)
        {
            for (var row = 0;
                 row < heights.Length && heights.Sum() > budget;
                 row++)
            {
                if (heights[row] > 1)
                {
                    heights[row]--;
                }
            }
        }

        return heights;
    }

    private static int PreferredHeight(
        OverlayCardKind kind,
        DeckGeometry geometry)
    {
        var referenceHeight = kind switch
        {
            OverlayCardKind.Media => 190,
            OverlayCardKind.Activity => 240,
            OverlayCardKind.Progress => 240,
            OverlayCardKind.Notification => 260,
            OverlayCardKind.Transient => 190,
            OverlayCardKind.Alert => 240,
            _ => 220,
        };
        return geometry.ScaleY(referenceHeight);
    }

    private static PixelRect[] CalculateRowBounds(
        DisplayGeometry display,
        IReadOnlyList<int> rowHeights,
        DeckGeometry geometry)
    {
        var rows = new PixelRect[rowHeights.Count];
        var bottom = display.Y + display.Height - geometry.BottomMargin;
        for (var row = 0; row < rowHeights.Count; row++)
        {
            var height = rowHeights[row];
            rows[row] = new PixelRect(
                geometry.FrontX,
                bottom - height,
                geometry.FrontWidth,
                height);
            bottom = rows[row].Top - geometry.RowGap;
        }

        return rows;
    }

    private static PixelRect CalculateCardBounds(
        LayoutSlot slot,
        PixelRect row,
        DeckGeometry geometry)
    {
        var splitLeftWidth =
            (geometry.FrontWidth - geometry.ColumnGap) / 2;
        return slot.Kind switch
        {
            SlotKind.FrontWide => row,
            SlotKind.FrontLeft => new PixelRect(
                geometry.FrontX,
                row.Y,
                splitLeftWidth,
                row.Height),
            SlotKind.FrontRight => new PixelRect(
                geometry.FrontX +
                    splitLeftWidth +
                    geometry.ColumnGap,
                row.Y,
                geometry.FrontWidth -
                    splitLeftWidth -
                    geometry.ColumnGap,
                row.Height),
            SlotKind.Side => new PixelRect(
                geometry.SideX,
                row.Y,
                geometry.SideWidth,
                row.Height),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static int Capacity(RowTemplate template) => template switch
    {
        RowTemplate.FrontWide => 1,
        RowTemplate.FrontWideWithSide => 2,
        RowTemplate.FrontSplit => 2,
        RowTemplate.FrontSplitWithSide => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(template)),
    };

    private sealed record LayoutCard(
        OverlayCardLayoutRequest Request,
        int PriorityRank);

    private sealed record LayoutSlot(int Row, SlotKind Kind);

    private sealed record LayoutAssignment(
        int Score,
        LayoutSlot[] Slots,
        LayoutCard[] Cards);

    private sealed record DeckGeometry(
        int FrontX,
        int FrontWidth,
        int SideX,
        int SideWidth,
        int FoldX,
        int BottomMargin,
        int ColumnGap,
        int RowGap,
        int MaximumDeckHeight,
        int MinimumCardHeight,
        PixelRect DirectRegion,
        int DisplayHeight)
    {
        public static DeckGeometry For(DisplayGeometry display)
        {
            var leftMargin = Scale(LeftMargin, display.Width, ReferenceWidth);
            var frontWidth = Scale(
                CompositionPlanner.FrontWidth,
                display.Width,
                ReferenceWidth);
            var sideWidth = Scale(
                CompositionPlanner.SideWidth,
                display.Width,
                ReferenceWidth);
            var columnGap = Scale(
                CompositionPlanner.ColumnGap,
                display.Width,
                ReferenceWidth);
            var frontX = display.X + leftMargin;
            var sideX = frontX + frontWidth + columnGap;
            var foldX = frontX + frontWidth + columnGap / 2;
            if (sideX + sideWidth > display.X + display.Width)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(display),
                    "Display is too narrow for the overlay deck.");
            }

            var directTop = display.Y + Scale(
                DirectTopMargin,
                display.Height,
                ReferenceHeight);
            var directHeight = Scale(
                CompositionPlanner.DirectHeight,
                display.Height,
                ReferenceHeight);
            return new DeckGeometry(
                frontX,
                frontWidth,
                sideX,
                sideWidth,
                foldX,
                Scale(
                    CompositionPlanner.BottomMargin,
                    display.Height,
                    ReferenceHeight),
                columnGap,
                Scale(
                    CompositionPlanner.RowGap,
                    display.Height,
                    ReferenceHeight),
                Scale(
                    CompositionPlanner.MaximumDeckHeight,
                    display.Height,
                    ReferenceHeight),
                Scale(
                    CompositionPlanner.MinimumCardHeight,
                    display.Height,
                    ReferenceHeight),
                new PixelRect(
                    frontX,
                    directTop,
                    frontWidth,
                    directHeight),
                display.Height);
        }

        public int ScaleY(int value) => Scale(
            value,
            DisplayHeight,
            ReferenceHeight);

        private static int Scale(
            int value,
            int actual,
            int reference) => Math.Max(
                1,
                (int)Math.Round(
                    (double)value * actual / reference,
                    MidpointRounding.AwayFromZero));
    }

    private enum RowTemplate
    {
        FrontWide,
        FrontWideWithSide,
        FrontSplit,
        FrontSplitWithSide,
    }

    private enum SlotKind
    {
        FrontWide,
        FrontLeft,
        FrontRight,
        Side,
    }
}
