using HS2.CrystalOverlay.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HS2_CrystalOverlay;

internal interface IOverlayPublisher
{
    bool Publish(OverlayRequest request);
}

internal sealed class OverlayController : IOverlayPublisher, IDisposable
{
    private const int MaximumCardWindows = 6;
    private static readonly TimeSpan NotificationPaintInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReflowDuration =
        TimeSpan.FromMilliseconds(240);

    private readonly object sync = new();
    private readonly OverlayScheduler scheduler = new();
    private readonly CrystalCardWindow[] cardWindows;
    private readonly Dictionary<string, CardBinding> bindings =
        new(StringComparer.Ordinal);
    private readonly DirectOverlayWindow directWindow;
    private readonly DisplayGeometry display;
    private readonly DispatcherQueue dispatcher;
    private readonly DispatcherTimer timer;
    private string? lastDeckSignature;
    private OverlayDeckPlan? lastDeckPlan;
    private string? lastDirectSignature;
    private int lastDriftMinute = -1;
    private int consecutiveRenderFailures;
    private DateTimeOffset lastRenderFailureLog = DateTimeOffset.MinValue;
    private bool disposed;

    internal OverlayController(
        MainWindow hostWindow,
        CrystalCardWindow cardWindow,
        DirectOverlayWindow directWindow,
        OverlayPlacement placement)
    {
        cardWindows =
        [
            cardWindow,
            new CrystalCardWindow("HS2 adaptive card 2"),
            new CrystalCardWindow("HS2 adaptive card 3"),
            new CrystalCardWindow("HS2 adaptive card 4"),
            new CrystalCardWindow("HS2 adaptive card 5"),
            new CrystalCardWindow("HS2 adaptive card 6"),
        ];
        this.directWindow = directWindow;
        display = new DisplayGeometry(
            "HS2 adaptive display",
            placement.FrontRegion.X,
            placement.FrontRegion.Y,
            placement.FrontRegion.Width + placement.SideRegion.Width,
            placement.FrontRegion.Height,
            false);
        dispatcher = hostWindow.DispatcherQueue;
        timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        timer.Tick += (_, _) => Render();
        timer.Start();
    }

    public bool Publish(OverlayRequest request)
    {
        if (disposed)
        {
            return false;
        }

        bool accepted;
        lock (sync)
        {
            accepted = scheduler.Publish(request, DateTimeOffset.UtcNow);
        }

        if (accepted)
        {
            _ = dispatcher.TryEnqueue(Render);
        }

        return accepted;
    }

    internal void ClearDismissible()
    {
        if (disposed)
        {
            return;
        }

        if (!dispatcher.HasThreadAccess)
        {
            _ = dispatcher.TryEnqueue(ClearDismissible);
            return;
        }

        lock (sync)
        {
            _ = scheduler.ClearDismissible(DateTimeOffset.UtcNow);
        }

        Render();
    }

    private void Render()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            RenderCore();
            if (consecutiveRenderFailures > 0)
            {
                RuntimeLog.Write(
                    $"Render recovered after " +
                    $"{consecutiveRenderFailures} failed frame(s).");
                consecutiveRenderFailures = 0;
            }
        }
        catch (Exception exception)
        {
            lastDeckSignature = null;
            lastDeckPlan = null;
            lastDirectSignature = null;
            lastDriftMinute = -1;
            RecordRenderFailure(exception);
        }
    }

    private void RenderCore()
    {
        var now = DateTimeOffset.UtcNow;
        OverlayFrame frame;
        lock (sync)
        {
            frame = scheduler.GetFrame(
                now,
                maxVisibleCards: MaximumCardWindows,
                maxVisibleNotifications: 3);
        }

        var cards = frame.VisibleCards
            .Take(MaximumCardWindows)
            .ToArray();
        var notificationStackOrders = cards
            .Where(item =>
                item.Policy.VisualTier ==
                OverlayVisualTier.StackedNotification)
            .OrderByDescending(item => item.PublishedAt)
            .ThenByDescending(item => item.PublishSequence)
            .Select((item, stackOrder) => new
            {
                item.Request.EventId,
                StackOrder = stackOrder,
            })
            .ToDictionary(
                item => item.EventId,
                item => item.StackOrder,
                StringComparer.Ordinal);
        var requests = cards.Select((item, index) =>
                ToLayoutRequest(
                    item,
                    index,
                    notificationStackOrders.GetValueOrDefault(
                        item.Request.EventId)))
            .ToArray();
        var deckSignature = string.Join(
            '\u001f',
            requests.Select(request =>
                $"{request.EventId}\u001e{request.Kind}\u001e" +
                $"{request.SortOrder}\u001e{request.WidthPreference}\u001e" +
                $"{request.PlacementPreference}\u001e" +
                $"{request.StackOrder}"));
        var plan = string.Equals(
                deckSignature,
                lastDeckSignature,
                StringComparison.Ordinal) &&
            lastDeckPlan is not null
            ? lastDeckPlan
            : CompositionPlanner.Plan(display, requests);
        lastDeckSignature = deckSignature;
        lastDeckPlan = plan;
        RenderCards(cards, plan.Cards, now);
        RenderDirect(frame.DirectItems, plan.DirectRegion, now);
    }

    private void RenderCards(
        IReadOnlyList<OverlayItem> items,
        IReadOnlyList<OverlayCardLayoutPlacement> placements,
        DateTimeOffset now)
    {
        var itemById = items.ToDictionary(
            item => item.Request.EventId,
            StringComparer.Ordinal);
        var visibleIds = placements
            .Select(placement => placement.EventId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var eventId in bindings.Keys
                     .Where(eventId => !visibleIds.Contains(eventId))
                     .ToArray())
        {
            bindings[eventId].Window.Hide();
            bindings.Remove(eventId);
        }

        foreach (var placement in placements)
        {
            if (!itemById.TryGetValue(placement.EventId, out var item))
            {
                continue;
            }

            var binding = GetOrCreateBinding(item, placement.Bounds, now);
            UpdateBinding(binding, item, placement.Bounds, now);
        }

        foreach (var binding in bindings.Values)
        {
            AdvanceMovement(binding, now);
        }
    }

    private CardBinding GetOrCreateBinding(
        OverlayItem item,
        PixelRect bounds,
        DateTimeOffset now)
    {
        if (bindings.TryGetValue(item.Request.EventId, out var existing))
        {
            return existing;
        }

        var used = bindings.Values
            .Select(binding => binding.Window)
            .ToHashSet();
        var window = cardWindows.First(candidate => !used.Contains(candidate));
        var created = new CardBinding(window, bounds, now);
        bindings[item.Request.EventId] = created;
        return created;
    }

    private static void UpdateBinding(
        CardBinding binding,
        OverlayItem item,
        PixelRect target,
        DateTimeOffset now)
    {
        var contentChanged = !Equals(binding.LastRequest, item.Request);
        var sizeChanged =
            binding.CurrentBounds.Width != target.Width ||
            binding.CurrentBounds.Height != target.Height;
        var animatedPaintDue =
            (item.Policy.VisualTier ==
                 OverlayVisualTier.StackedNotification ||
             item.Request.Kind is
                 OverlayKind.MediaActive or
                 OverlayKind.MediaTrackChange) &&
            now - binding.LastPaintedAt >= NotificationPaintInterval;

        if (contentChanged || sizeChanged)
        {
            if (contentChanged)
            {
                // Scheduler intentionally preserves PublishedAt when a
                // notification with the same ID updates its body. Reset the
                // visual marquee epoch independently so the new body starts
                // at its readable leading position.
                binding.NotificationScrollStartedAt = now;
            }

            binding.Window.RenderExact(
                item,
                target,
                now,
                binding.NotificationScrollStartedAt);
            binding.CurrentBounds = target;
            binding.TargetBounds = target;
            binding.MovementStartedAt = null;
            binding.LastRequest = item.Request;
            binding.LastPaintedAt = now;
            return;
        }

        if (binding.TargetBounds != target)
        {
            binding.StartBounds = binding.CurrentBounds;
            binding.TargetBounds = target;
            binding.MovementStartedAt = now;
            return;
        }

        if (animatedPaintDue && binding.MovementStartedAt is null)
        {
            binding.Window.RenderExact(
                item,
                target,
                now,
                binding.NotificationScrollStartedAt);
            binding.CurrentBounds = target;
            binding.LastPaintedAt = now;
        }
    }

    private static void AdvanceMovement(
        CardBinding binding,
        DateTimeOffset now)
    {
        if (binding.MovementStartedAt is not { } startedAt)
        {
            return;
        }

        var progress = Math.Clamp(
            (now - startedAt).TotalMilliseconds /
            ReflowDuration.TotalMilliseconds,
            0,
            1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var next = Interpolate(
            binding.StartBounds,
            binding.TargetBounds,
            eased);
        binding.Window.MoveTo(next);
        binding.CurrentBounds = next;
        if (progress >= 1)
        {
            binding.CurrentBounds = binding.TargetBounds;
            binding.MovementStartedAt = null;
        }
    }

    private void RenderDirect(
        IReadOnlyList<OverlayItem> items,
        PixelRect region,
        DateTimeOffset now)
    {
        var signature = string.Join(
            '\u001f',
            items.Select(item =>
                $"{item.Request.EventId}\u001e" +
                $"{item.Request.Title}\u001e" +
                $"{item.Request.Body}\u001e" +
                $"{item.Request.Visual?.Subtitle}\u001e" +
                $"{item.Request.Visual?.Meta}\u001e" +
                $"{item.Request.Visual?.IsCharging}\u001e" +
                $"{region}"));
        if (string.Equals(
                signature,
                lastDirectSignature,
                StringComparison.Ordinal) &&
            now.Minute == lastDriftMinute)
        {
            return;
        }

        lastDirectSignature = signature;
        lastDriftMinute = now.Minute;
        directWindow.Render(items, region, now);
    }

    private static OverlayCardLayoutRequest ToLayoutRequest(
        OverlayItem item,
        int index,
        int stackOrder)
    {
        var kind = item.Request.Kind switch
        {
            OverlayKind.PhoneNotification or
            OverlayKind.PhoneDynamic or
            OverlayKind.PhoneConnection => OverlayCardKind.Notification,
            OverlayKind.MediaActive or
            OverlayKind.MediaTrackChange => OverlayCardKind.Media,
            OverlayKind.GameActive or
            OverlayKind.GameAchievement or
            OverlayKind.GameSummary => OverlayCardKind.Activity,
            OverlayKind.ImportantTask or
            OverlayKind.ImportantTaskComplete => OverlayCardKind.Progress,
            OverlayKind.SystemOperation or
            OverlayKind.DeviceOrNetwork or
            OverlayKind.HardwareResolved => OverlayCardKind.Transient,
            OverlayKind.PhoneVerificationCode =>
                OverlayCardKind.Verification,
            OverlayKind.PhoneCall or
            OverlayKind.PhoneTransfer or
            OverlayKind.HardwareAlert => OverlayCardKind.Alert,
            _ => OverlayCardKind.Generic,
        };
        var width = kind switch
        {
            OverlayCardKind.Media or
            OverlayCardKind.Activity or
            OverlayCardKind.Progress or
            OverlayCardKind.Alert => OverlayCardWidthPreference.Wide,
            OverlayCardKind.Transient => OverlayCardWidthPreference.Compact,
            OverlayCardKind.Verification =>
                OverlayCardWidthPreference.Compact,
            _ => OverlayCardWidthPreference.Auto,
        };
        var placement = item.Policy.VisualTier ==
                        OverlayVisualTier.StackedNotification
            ? OverlayCardPlacementPreference.BottomStack
            : item.Request.Kind is
                OverlayKind.SystemOperation or
                OverlayKind.PhoneVerificationCode
                ? OverlayCardPlacementPreference.BottomLeft
                : OverlayCardPlacementPreference.Auto;
        return new OverlayCardLayoutRequest(
            item.Request.EventId,
            kind,
            index,
            width,
            placement,
            stackOrder);
    }

    private static PixelRect Interpolate(
        PixelRect from,
        PixelRect to,
        double progress) =>
        new(
            Lerp(from.X, to.X, progress),
            Lerp(from.Y, to.Y, progress),
            Lerp(from.Width, to.Width, progress),
            Lerp(from.Height, to.Height, progress));

    private static int Lerp(int from, int to, double progress) =>
        (int)Math.Round(from + (to - from) * progress);

    private void RecordRenderFailure(Exception exception)
    {
        consecutiveRenderFailures++;
        var now = DateTimeOffset.UtcNow;
        if (consecutiveRenderFailures > 1 &&
            now - lastRenderFailureLog < TimeSpan.FromSeconds(30))
        {
            return;
        }

        lastRenderFailureLog = now;
        RuntimeLog.Write(
            $"Render failed (consecutive={consecutiveRenderFailures}): " +
            exception);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Stop();
        foreach (var window in cardWindows)
        {
            window.Dispose();
        }
        directWindow.Dispose();
    }

    private sealed class CardBinding(
        CrystalCardWindow window,
        PixelRect initialBounds,
        DateTimeOffset now)
    {
        internal CrystalCardWindow Window { get; } = window;

        internal OverlayRequest? LastRequest { get; set; }

        internal PixelRect StartBounds { get; set; } = initialBounds;

        internal PixelRect CurrentBounds { get; set; } = initialBounds;

        internal PixelRect TargetBounds { get; set; } = initialBounds;

        internal DateTimeOffset? MovementStartedAt { get; set; }

        internal DateTimeOffset LastPaintedAt { get; set; } = now;

        internal DateTimeOffset NotificationScrollStartedAt { get; set; } = now;
    }
}
