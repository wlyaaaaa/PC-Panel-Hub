using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class OverlaySchedulerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DirectPhoneBattery_RemainsVisibleAlongsidePhoneNotification()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "phone-battery",
            OverlayKind.PhoneBattery,
            OverlaySource.XiaomiHyperConnect,
            "手机 87% · 充电中"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "微信",
            "一条新消息",
            dedupKey: "wechat|一条新消息"), Now);

        var frame = scheduler.GetFrame(Now, maxVisibleNotifications: 2);

        Assert.Single(frame.DirectItems);
        Assert.Equal(OverlayKind.PhoneBattery, frame.DirectItems[0].Request.Kind);
        Assert.Null(frame.PrimaryCard);
        Assert.Single(frame.NotificationCards);
        Assert.Equal(
            OverlayKind.PhoneNotification,
            frame.NotificationCards[0].Request.Kind);
    }

    [Fact]
    public void OrdinaryPhoneNotification_ExpiresAfterSixtyVisibleSeconds()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "短信",
            "验证码 1234"), Now);

        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleNotifications: 1).NotificationCards);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(59.9),
            maxVisibleNotifications: 1).NotificationCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(60.1),
            maxVisibleNotifications: 1).NotificationCards);
    }

    [Fact]
    public void MultiplePhoneNotifications_AlwaysKeepOnlyTheLatestTwo()
    {
        var scheduler = new OverlayScheduler();
        for (var index = 1; index <= 3; index++)
        {
            scheduler.Publish(OverlayRequest.Timed(
                $"phone-toast-{index}",
                OverlayKind.PhoneNotification,
                OverlaySource.XiaomiHyperConnect,
                $"通知 {index}",
                $"正文 {index}"), Now);
        }

        var firstFrame = scheduler.GetFrame(
            Now.AddSeconds(1),
            maxVisibleNotifications: 2);
        Assert.Equal(2, firstFrame.NotificationCards.Count);
        Assert.Equal("通知 3", firstFrame.NotificationCards[0].Request.Title);
        Assert.Equal("通知 2", firstFrame.NotificationCards[1].Request.Title);

        var secondFrame = scheduler.GetFrame(
            Now.AddSeconds(61.1),
            maxVisibleNotifications: 2);
        Assert.Empty(secondFrame.NotificationCards);
    }

    [Fact]
    public void NotificationVisibleTime_PausesWhileNoSlotIsAvailable()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "短信",
            "请稍后查看"), Now);

        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleNotifications: 1).NotificationCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(2),
            maxVisibleNotifications: 0).NotificationCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(20),
            maxVisibleNotifications: 0).NotificationCards);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(20),
            maxVisibleNotifications: 1).NotificationCards);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(77.9),
            maxVisibleNotifications: 1).NotificationCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(78.1),
            maxVisibleNotifications: 1).NotificationCards);
    }

    [Fact]
    public void NotificationQueuedForMoreThanThreeMinutes_IsDiscardedAsStale()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "一条旧通知"), Now);

        Assert.Empty(scheduler.GetFrame(
            Now,
            maxVisibleNotifications: 0).NotificationCards);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(61),
            maxVisibleNotifications: 1).NotificationCards);

        var staleScheduler = new OverlayScheduler();
        staleScheduler.Publish(OverlayRequest.Timed(
            "stale-phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "另一条旧通知"), Now);
        Assert.Empty(staleScheduler.GetFrame(
            Now.AddSeconds(179),
            maxVisibleNotifications: 0).NotificationCards);
        Assert.Empty(staleScheduler.GetFrame(
            Now.AddSeconds(181),
            maxVisibleNotifications: 1).NotificationCards);
    }

    [Fact]
    public void PhoneNotifications_DoNotReplacePersistentMediaCard()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "media",
            OverlayKind.MediaActive,
            OverlaySource.NetEase,
            "Night Current"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast-1",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "通知 1"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast-2",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "通知 2"), Now.AddMilliseconds(1));

        var frame = scheduler.GetFrame(
            Now.AddSeconds(1),
            maxVisibleNotifications: 2);

        Assert.Equal(OverlayKind.MediaActive, frame.PrimaryCard?.Request.Kind);
        Assert.Equal(2, frame.NotificationCards.Count);
    }

    [Fact]
    public void TemporaryEvent_SuspendsMediaThenMediaReturns()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "media",
            OverlayKind.MediaActive,
            OverlaySource.NetEase,
            "Night Current"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "volume",
            OverlayKind.SystemOperation,
            OverlaySource.System,
            "音量 42%"), Now.AddSeconds(1));

        Assert.Equal(
            OverlayKind.SystemOperation,
            scheduler.GetFrame(Now.AddSeconds(2)).PrimaryCard?.Request.Kind);
        Assert.Equal(
            OverlayKind.MediaActive,
            scheduler.GetFrame(Now.AddSeconds(8)).PrimaryCard?.Request.Kind);
    }

    [Fact]
    public void DynamicPhoneState_CannotRemainForever()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "ride",
            OverlayKind.PhoneDynamic,
            OverlaySource.XiaomiHyperConnect,
            "司机即将到达",
            "距离 300 米"), Now);

        var activeFrame = scheduler.GetFrame(
            Now,
            maxVisibleNotifications: 2);
        Assert.Null(activeFrame.PrimaryCard);
        Assert.Equal(
            OverlayKind.PhoneDynamic,
            activeFrame.NotificationCards.Single().Request.Kind);

        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(60.1),
            maxVisibleNotifications: 2).NotificationCards);
    }

    [Fact]
    public void RepeatedPublish_CannotRestartAVisibleNotificationTimer()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "配送状态",
            "骑手已取货"), Now);
        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleNotifications: 1).NotificationCards);

        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "配送状态",
            "骑手距离 300 米"), Now.AddSeconds(4));

        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(59.9),
            maxVisibleNotifications: 1).NotificationCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(60.1),
            maxVisibleNotifications: 1).NotificationCards);
    }

    [Fact]
    public void XiaomiAndPhoneLinkDuplicate_IsDisplayedOnlyOnce()
    {
        var scheduler = new OverlayScheduler();
        var accepted = scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "微信",
            "午饭见",
            dedupKey: PhoneNotificationClassifier.DedupKey(
                "微信",
                "午饭见")), Now);
        var duplicate = scheduler.Publish(OverlayRequest.Timed(
            "phone-link-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "微信",
            "午饭见",
            dedupKey: PhoneNotificationClassifier.DedupKey(
                "微信",
                "午饭见")), Now.AddSeconds(3));

        Assert.True(accepted);
        Assert.False(duplicate);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(3),
            maxVisibleNotifications: 2).NotificationCards);
    }

    [Fact]
    public void CrossSourceDuplicate_IsStillSuppressedAfterRelayDelay()
    {
        var scheduler = new OverlayScheduler();
        var dedupKey = PhoneNotificationClassifier.DedupKey(
            "微信",
            "同一条跨设备消息");
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-delayed-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "微信",
            "同一条跨设备消息",
            dedupKey: dedupKey), Now));

        Assert.False(scheduler.Publish(OverlayRequest.Timed(
            "phone-link-delayed-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "微信",
            "同一条跨设备消息",
            dedupKey: dedupKey), Now.AddSeconds(90)));
    }

    [Fact]
    public void SameSourceIdenticalPayloads_RemainSeparateNotifications()
    {
        var scheduler = new OverlayScheduler();
        var dedupKey = PhoneNotificationClassifier.DedupKey(
            "微信",
            "收到一条消息");
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-toast-one",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "微信",
            "收到一条消息",
            dedupKey: dedupKey), Now));

        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-toast-two",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "微信",
            "收到一条消息",
            dedupKey: dedupKey), Now.AddSeconds(3)));
        Assert.Equal(2, scheduler.GetFrame(
            Now.AddSeconds(3),
            maxVisibleNotifications: 2).NotificationCards.Count);
    }

    [Fact]
    public void NonPhoneDuplicateFromSameSource_RetainsGenericDeduplication()
    {
        var scheduler = new OverlayScheduler();
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "network-event-one",
            OverlayKind.DeviceOrNetwork,
            OverlaySource.System,
            "网络已恢复",
            dedupKey: "network-restored"), Now));

        Assert.False(scheduler.Publish(OverlayRequest.Timed(
            "network-event-two",
            OverlayKind.DeviceOrNetwork,
            OverlaySource.System,
            "网络已恢复",
            dedupKey: "network-restored"), Now.AddSeconds(3)));
    }

    [Fact]
    public void HardwareAlert_OutranksPhoneCall()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "call",
            OverlayKind.PhoneCall,
            OverlaySource.XiaomiHyperConnect,
            "来电：张三"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "pump",
            OverlayKind.HardwareAlert,
            OverlaySource.Hardware,
            "水泵转速异常"), Now);

        Assert.Equal(
            OverlayKind.HardwareAlert,
            scheduler.GetFrame(Now).PrimaryCard?.Request.Kind);
    }
}
