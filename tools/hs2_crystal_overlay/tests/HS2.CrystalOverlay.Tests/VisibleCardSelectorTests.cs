using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class VisibleCardSelectorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GlobalSelection_IsCappedAtSixAndKeepsHardCardsAndLatestPhone()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "media", OverlayKind.MediaActive, OverlaySource.NetEase, "歌曲"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "game", OverlayKind.GameActive, OverlaySource.Steam, "游戏"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "incoming-call", OverlayKind.PhoneCall, OverlaySource.PhoneLink, "手机来电"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "active-transfer", OverlayKind.PhoneTransfer, OverlaySource.PhoneLink, "手机传输"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "network-state", OverlayKind.DeviceOrNetwork, OverlaySource.System, "网络已断开"), Now);
        for (var index = 1; index <= 3; index++)
        {
            scheduler.Publish(OverlayRequest.Timed(
                $"phone-{index}",
                OverlayKind.PhoneNotification,
                OverlaySource.XiaomiHyperConnect,
                $"手机通知 {index}"), Now.AddMilliseconds(index));
        }

        var frame = scheduler.GetFrame(
            Now.AddSeconds(1),
            maxVisibleCards: 20,
            maxVisibleNotifications: 20);

        Assert.Equal(6, frame.VisibleCards.Count);
        Assert.Contains(frame.VisibleCards, item => item.Request.EventId == "media");
        Assert.Contains(frame.VisibleCards, item => item.Request.EventId == "game");
        Assert.Contains(frame.VisibleCards, item => item.Request.EventId == "incoming-call");
        Assert.Contains(frame.VisibleCards, item => item.Request.EventId == "phone-3");
        Assert.DoesNotContain(frame.VisibleCards, item => item.Request.EventId == "phone-1");
        Assert.Equal(
            frame.VisibleCards
                .OrderByDescending(item => item.Policy.Priority)
                .ThenByDescending(item => item.PublishedAt)
                .Select(item => item.Request.EventId),
            frame.VisibleCards.Select(item => item.Request.EventId));
        Assert.Equal(
            frame.VisibleCards.Where(item =>
                item.Policy.VisualTier != OverlayVisualTier.StackedNotification),
            frame.Cards);
        Assert.Equal(
            frame.VisibleCards.Where(item =>
                item.Policy.VisualTier == OverlayVisualTier.StackedNotification),
            frame.NotificationCards);
    }

    [Fact]
    public void LatestPhoneNotification_SurvivesEvenWhenSixProtectedCardsCompete()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "media", OverlayKind.MediaActive, OverlaySource.NetEase, "歌曲"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "game", OverlayKind.GameActive, OverlaySource.Steam, "游戏"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "summary", OverlayKind.GameSummary, OverlaySource.Steam, "游戏总结"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "incoming-call", OverlayKind.PhoneCall, OverlaySource.PhoneLink, "手机来电"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "call", OverlayKind.PhoneCall, OverlaySource.PhoneLink, "来电"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "transfer", OverlayKind.PhoneTransfer, OverlaySource.PhoneLink, "文件传输"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "latest-phone",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "刚到的手机通知"), Now.AddSeconds(1));

        var visible = scheduler.GetFrame(
                Now.AddSeconds(1),
                maxVisibleCards: 6,
                maxVisibleNotifications: 3)
            .VisibleCards;

        Assert.Equal(6, visible.Count);
        Assert.Contains(
            visible,
            item => item.Request.EventId == "latest-phone");
        Assert.DoesNotContain(
            visible,
            item => item.Request.EventId == "summary");
    }

    [Fact]
    public void IndependentOperationalCards_CompeteAsSeparateEventIds()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "active-transfer", OverlayKind.PhoneTransfer, OverlaySource.PhoneLink, "手机传输"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "phone-transfer", OverlayKind.PhoneTransfer, OverlaySource.PhoneLink, "手机文件传输"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "network-state", OverlayKind.DeviceOrNetwork, OverlaySource.System, "网络已断开"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "audio-device", OverlayKind.DeviceOrNetwork, OverlaySource.System, "音频输出已切换"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "network", OverlayKind.DeviceOrNetwork, OverlaySource.System, "网络已恢复"), Now);

        var ids = scheduler.GetFrame(
                Now,
                maxVisibleCards: 6,
                maxVisibleNotifications: 3)
            .VisibleCards
            .Select(item => item.Request.EventId)
            .ToArray();

        Assert.Equal(5, ids.Length);
        Assert.Contains("active-transfer", ids);
        Assert.Contains("phone-transfer", ids);
        Assert.Contains("network-state", ids);
        Assert.Contains("audio-device", ids);
        Assert.Contains("network", ids);
    }

    [Fact]
    public void VerificationCodeSurvivesMaximumPressureWithoutUsingPhoneQuota()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "media", OverlayKind.MediaActive, OverlaySource.NetEase, "歌曲"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "game", OverlayKind.GameActive, OverlaySource.Steam, "游戏"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "incoming-call", OverlayKind.PhoneCall, OverlaySource.PhoneLink, "手机来电"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "active-transfer", OverlayKind.PhoneTransfer, OverlaySource.PhoneLink, "手机传输"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "verification",
            OverlayKind.PhoneVerificationCode,
            OverlaySource.PhoneLink,
            "482731",
            dedupKey: "verification-code:482731",
            visual: new OverlayVisualData(VerificationCode: "482731")), Now);
        for (var index = 1; index <= 3; index++)
        {
            scheduler.Publish(OverlayRequest.Timed(
                $"phone-{index}",
                OverlayKind.PhoneNotification,
                OverlaySource.XiaomiHyperConnect,
                $"手机通知 {index}"), Now.AddMilliseconds(index));
        }

        var frame = scheduler.GetFrame(
            Now.AddSeconds(1),
            maxVisibleCards: 6,
            maxVisibleNotifications: 3);

        Assert.Equal(6, frame.VisibleCards.Count);
        Assert.Contains(frame.VisibleCards, item =>
            item.Request.Kind == OverlayKind.PhoneVerificationCode);
        Assert.Single(frame.VisibleCards, item =>
            item.Request.Kind == OverlayKind.PhoneVerificationCode);
        Assert.Contains(frame.NotificationCards, item =>
            item.Request.EventId == "phone-3");
        Assert.DoesNotContain(frame.NotificationCards, item =>
            item.Request.Kind == OverlayKind.PhoneVerificationCode);
    }
}
