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

    [Fact]
    public void NewVerificationCodeUsesOneReplaceableFocusCard()
    {
        var reconciler = new PhoneNotificationSnapshotReconciler(
            TimeSpan.FromMinutes(5));
        _ = reconciler.Reconcile(
            [Item(30, "腾讯科技", "普通旧消息", OverlaySource.PhoneLink)],
            Start);

        var first = Assert.Single(reconciler.Reconcile(
            [
                Item(30, "腾讯科技", "普通旧消息", OverlaySource.PhoneLink),
                Item(
                    31,
                    "腾讯科技",
                    "您的验证码是 482731，请勿泄露",
                    OverlaySource.XiaomiHyperConnect),
            ],
            Start + TimeSpan.FromSeconds(1)));

        Assert.Equal(OverlayKind.PhoneVerificationCode, first.Kind);
        Assert.Equal("phone-verification-code", first.EventId);
        Assert.Equal("482731", first.Title);
        Assert.Null(first.Body);
        Assert.Equal("482731", first.Visual?.VerificationCode);
        Assert.Equal("腾讯科技", first.Visual?.Subtitle);
        Assert.Equal("小米妙享", first.Visual?.Meta);

        var next = Assert.Single(reconciler.Reconcile(
            [
                Item(30, "腾讯科技", "普通旧消息", OverlaySource.PhoneLink),
                Item(
                    31,
                    "腾讯科技",
                    "您的验证码是 735194，请勿泄露",
                    OverlaySource.XiaomiHyperConnect),
            ],
            Start + TimeSpan.FromSeconds(2)));

        Assert.Equal(first.EventId, next.EventId);
        Assert.Equal("735194", next.Visual?.VerificationCode);
    }

    [Fact]
    public void InitialSnapshotDoesNotReplayOldVerificationCode()
    {
        var reconciler = new PhoneNotificationSnapshotReconciler(
            TimeSpan.FromMinutes(5));

        var requests = reconciler.Reconcile(
            [Item(
                40,
                "账户安全",
                "验证码 482731",
                OverlaySource.PhoneLink)],
            Start);

        Assert.Empty(requests);
    }

    [Fact]
    public void OnlyNewestVerificationNotificationCanReplaceFocusCard()
    {
        var reconciler = new PhoneNotificationSnapshotReconciler(
            TimeSpan.FromMinutes(5));
        _ = reconciler.Reconcile(
            [Item(50, "微信", "基线消息", OverlaySource.PhoneLink)],
            Start);

        var first = Assert.Single(reconciler.Reconcile(
            [
                Item(
                    51,
                    "账户安全",
                    "验证码 111111",
                    OverlaySource.PhoneLink,
                    Start + TimeSpan.FromSeconds(1)),
                Item(
                    52,
                    "账户安全",
                    "验证码 222222",
                    OverlaySource.XiaomiHyperConnect,
                    Start + TimeSpan.FromSeconds(2)),
            ],
            Start + TimeSpan.FromSeconds(3)));
        Assert.Equal("222222", first.Visual?.VerificationCode);

        var staleChange = reconciler.Reconcile(
            [
                Item(
                    51,
                    "账户安全",
                    "验证码 333333",
                    OverlaySource.PhoneLink,
                    Start + TimeSpan.FromSeconds(1)),
                Item(
                    52,
                    "账户安全",
                    "验证码 222222",
                    OverlaySource.XiaomiHyperConnect,
                    Start + TimeSpan.FromSeconds(2)),
            ],
            Start + TimeSpan.FromSeconds(4));

        Assert.Empty(staleChange);
    }

    [Fact]
    public void VerificationCodeInTitleIsNotRepeatedInHeader()
    {
        var reconciler = new PhoneNotificationSnapshotReconciler(
            TimeSpan.FromMinutes(5));
        _ = reconciler.Reconcile(
            [Item(60, "微信", "基线消息", OverlaySource.PhoneLink)],
            Start);

        var request = Assert.Single(reconciler.Reconcile(
            [Item(
                61,
                "G-123456 is your Google verification code",
                null,
                OverlaySource.PhoneLink,
                Start + TimeSpan.FromSeconds(1))],
            Start + TimeSpan.FromSeconds(2)));

        Assert.Equal("123456", request.Visual?.VerificationCode);
        Assert.Equal("手机连接", request.Visual?.Subtitle);
        Assert.Null(request.Visual?.Meta);
        Assert.DoesNotContain(
            "123456",
            request.Visual?.Subtitle ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static PhoneNotificationSnapshotItem Item(
        uint id,
        string title,
        string? body,
        OverlaySource source,
        DateTimeOffset? creationTime = null) =>
        new(
            id,
            creationTime ?? Start,
            source == OverlaySource.PhoneLink
                ? "手机连接"
                : "小米妙享",
            title,
            body,
            source);
}
