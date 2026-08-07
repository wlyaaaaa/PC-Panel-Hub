using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PhoneNotificationReconcilerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstSnapshotPublishesOnlyStillActiveCallOrTransfer()
    {
        var reconciler = new PhoneNotificationSnapshotReconciler(
            TimeSpan.FromMinutes(5));

        var requests = reconciler.Reconcile(
            [
                Item(1, "妈妈来电", "正在通话", OverlaySource.PhoneLink),
                Item(2, "微信", "普通旧消息", OverlaySource.PhoneLink),
            ],
            Start);

        var request = Assert.Single(requests);
        Assert.Equal(OverlayKind.PhoneCall, request.Kind);
        Assert.True(request.IsActive);
    }

    [Fact]
    public void ExpiredPersistentItemIsEndedThenRestoredFromSameSnapshot()
    {
        var reconciler = new PhoneNotificationSnapshotReconciler(
            TimeSpan.FromMinutes(5));
        var snapshot = new[]
        {
            Item(
                3,
                "文件传输",
                "正在传输 42%",
                OverlaySource.XiaomiHyperConnect),
        };
        var first = Assert.Single(reconciler.Reconcile(snapshot, Start));

        var ended = Assert.Single(reconciler.ExpireStale(
            Start + TimeSpan.FromMinutes(6)));
        Assert.False(ended.IsActive);
        Assert.Equal(first.EventId, ended.EventId);

        var restored = Assert.Single(reconciler.Reconcile(
            snapshot,
            Start + TimeSpan.FromMinutes(6)));
        Assert.True(restored.IsActive);
        Assert.Equal(first.EventId, restored.EventId);
    }

    [Fact]
    public void RelayDuplicatesShareOneCardAndSingleSourceRemovalDoesNotEndIt()
    {
        var reconciler = new PhoneNotificationSnapshotReconciler(
            TimeSpan.FromMinutes(5));
        var first = reconciler.Reconcile(
            [
                Item(
                    10,
                    "文件传输",
                    "Fight for the Future · UPDATE: KOSA theater is a gift to Big Tech",
                    OverlaySource.XiaomiHyperConnect),
                Item(
                    11,
                    "文件传输",
                    "Fight for the Future · KOSA theater is a gift to Big Tech",
                    OverlaySource.PhoneLink),
            ],
            Start);

        Assert.Equal(2, first.Count);
        Assert.Equal(first[0].EventId, first[1].EventId);

        var afterOneRelayDisappears = reconciler.Reconcile(
            [
                Item(
                    11,
                    "文件传输",
                    "Fight for the Future · KOSA theater is a gift to Big Tech",
                    OverlaySource.PhoneLink),
            ],
            Start + TimeSpan.FromSeconds(1));

        Assert.Empty(afterOneRelayDisappears);
    }

    [Fact]
    public void ChangedTimedNotificationWithSameWindowsIdIsRepublished()
    {
        var reconciler = new PhoneNotificationSnapshotReconciler(
            TimeSpan.FromMinutes(5));
        _ = reconciler.Reconcile(
            [Item(20, "下载", "23%", OverlaySource.PhoneLink)],
            Start);

        var requests = reconciler.Reconcile(
            [Item(20, "下载", "24%", OverlaySource.PhoneLink)],
            Start + TimeSpan.FromSeconds(1));

        var request = Assert.Single(requests);
        Assert.Equal(OverlayKind.PhoneNotification, request.Kind);
        Assert.Equal("24%", request.Body);
    }

    private static PhoneNotificationSnapshotItem Item(
        uint id,
        string title,
        string? body,
        OverlaySource source) =>
        new(
            id,
            Start,
            source == OverlaySource.PhoneLink
                ? "手机连接"
                : "小米妙享",
            title,
            body,
            source);
}
