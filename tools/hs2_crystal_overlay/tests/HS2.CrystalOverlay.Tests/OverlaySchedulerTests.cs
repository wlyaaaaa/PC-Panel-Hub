using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class OverlaySchedulerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameAudioHudEvent_UpdatesInPlaceAndExpiresFromTheLastChange()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(
            AudioHudProjection.Create(22, isMuted: false),
            Now);
        var original = Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleCards: 6,
            maxVisibleNotifications: 3).VisibleCards);

        scheduler.Publish(
            AudioHudProjection.Create(100, isMuted: false),
            Now.AddSeconds(2));
        var updated = Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(2),
            maxVisibleCards: 6,
            maxVisibleNotifications: 3).VisibleCards);

        Assert.Equal("100%", updated.Request.Title);
        Assert.Equal(original.PublishedAt, updated.PublishedAt);
        Assert.Equal(original.PublishSequence, updated.PublishSequence);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(7.9),
            maxVisibleCards: 6,
            maxVisibleNotifications: 3).VisibleCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(8.1),
            maxVisibleCards: 6,
            maxVisibleNotifications: 3).VisibleCards);
    }

    [Fact]
    public void ReversedDeviceState_ReplacesOldCardAndGetsFreshVisibleTime()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "network-state",
            OverlayKind.DeviceOrNetwork,
            OverlaySource.System,
            "网络已断开",
            dedupKey: "network-disconnected"), Now);
        Assert.Equal(
            "网络已断开",
            Assert.Single(scheduler.GetFrame(Now, 6, 3).VisibleCards)
                .Request.Title);

        scheduler.Publish(OverlayRequest.Timed(
            "network-state",
            OverlayKind.DeviceOrNetwork,
            OverlaySource.System,
            "网络已恢复",
            dedupKey: "network-restored"), Now.AddSeconds(5));
        var replacement = Assert.Single(
            scheduler.GetFrame(Now.AddSeconds(5), 6, 3).VisibleCards);

        Assert.Equal("网络已恢复", replacement.Request.Title);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(16.9), 6, 3).VisibleCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(17.1), 6, 3).VisibleCards);
    }

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
    public void SamePhoneNotificationId_UpdatesTextWithoutResettingVisibleTime()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "传输中",
            "已完成 20%"), Now);
        Assert.Single(scheduler.GetFrame(Now, 6, 3).VisibleCards);

        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "传输中",
            "已完成 80%"), Now.AddSeconds(20));
        var updated = Assert.Single(
            scheduler.GetFrame(Now.AddSeconds(20), 6, 3).VisibleCards);

        Assert.Equal("已完成 80%", updated.Request.Body);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(59.9), 6, 3).VisibleCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(60.1), 6, 3).VisibleCards);
    }

    [Fact]
    public void FourthPhoneNotification_PermanentlyDiscardsTheOldest()
    {
        var scheduler = new OverlayScheduler();
        for (var index = 1; index <= 4; index++)
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
            maxVisibleNotifications: 3);
        Assert.Equal(3, firstFrame.NotificationCards.Count);
        Assert.Equal("通知 4", firstFrame.NotificationCards[0].Request.Title);
        Assert.Equal("通知 3", firstFrame.NotificationCards[1].Request.Title);
        Assert.Equal("通知 2", firstFrame.NotificationCards[2].Request.Title);

        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(2),
            maxVisibleNotifications: 0).NotificationCards);
        var restored = scheduler.GetFrame(
            Now.AddSeconds(2),
            maxVisibleNotifications: 3);
        Assert.DoesNotContain(
            restored.NotificationCards,
            item => item.Request.EventId == "phone-toast-1");

        var secondFrame = scheduler.GetFrame(
            Now.AddSeconds(61.1),
            maxVisibleNotifications: 3);
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
    public void SameSourceApproximateDuplicate_IsDisplayedOnlyOnce()
    {
        var scheduler = new OverlayScheduler();
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-toast-one",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "Fight for the Future",
            "UPDATE: KOSA theater is a gift to Big Tech",
            dedupKey: PhoneNotificationClassifier.DedupKey(
                "Fight for the Future",
                "UPDATE: KOSA theater is a gift to Big Tech")), Now));

        Assert.False(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-toast-two",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "Fight for the Future",
            "KOSA theater is a gift to Big Tech",
            dedupKey: PhoneNotificationClassifier.DedupKey(
                "Fight for the Future",
                "KOSA theater is a gift to Big Tech")), Now.AddSeconds(3)));
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(3),
            maxVisibleNotifications: 3).NotificationCards);
    }

    [Fact]
    public void SameSourceApproximateDuplicate_IsSuppressedAcrossThreeMinuteWindow()
    {
        var scheduler = new OverlayScheduler();
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-kosa-early",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "Fight for the Future",
            "UPDATE: KOSA theater is a gift to Big Tech"), Now));

        Assert.False(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-kosa-delayed",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "Fight for the Future",
            "KOSA theater is a gift to Big Tech"), Now.AddSeconds(90)));
    }

    [Fact]
    public void ApproximateDuplicate_DoesNotRestartVisibleTimer()
    {
        var scheduler = new OverlayScheduler();
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-kosa-one",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "Fight for the Future",
            "UPDATE: KOSA theater is a gift to Big Tech"), Now));
        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleNotifications: 1).NotificationCards);

        Assert.False(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-kosa-two",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "Fight for the Future",
            "KOSA theater is a gift to Big Tech"), Now.AddSeconds(8)));

        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(59.9),
            maxVisibleNotifications: 1).NotificationCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(60.1),
            maxVisibleNotifications: 1).NotificationCards);
    }

    [Fact]
    public void PhonePayloadsWithDifferentNumbers_AreNotDeduplicated()
    {
        var scheduler = new OverlayScheduler();
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "otp-one",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "验证码",
            "123456"), Now));
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "otp-two",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "验证码",
            "123457"), Now.AddSeconds(1)));

        Assert.Equal(2, scheduler.GetFrame(
            Now.AddSeconds(1),
            maxVisibleNotifications: 3).NotificationCards.Count);
    }

    [Fact]
    public void SameVerificationCodeAcrossRelaysDoesNotRestartVisibleTimer()
    {
        var scheduler = new OverlayScheduler();
        Assert.True(scheduler.Publish(VerificationCode(
            "482731",
            OverlaySource.XiaomiHyperConnect), Now));
        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);

        Assert.True(scheduler.Publish(VerificationCode(
            "482731",
            OverlaySource.PhoneLink), Now.AddSeconds(5)));

        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(14.9),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(15.1),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
    }

    [Fact]
    public void NewVerificationCodeReplacesOldCodeAndReceivesFreshTimer()
    {
        var scheduler = new OverlayScheduler();
        Assert.True(scheduler.Publish(VerificationCode(
            "482731",
            OverlaySource.XiaomiHyperConnect), Now));
        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);

        Assert.True(scheduler.Publish(VerificationCode(
            "735194",
            OverlaySource.PhoneLink), Now.AddSeconds(5)));
        var immediate = Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(5),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Equal("735194", immediate.Request.Visual?.VerificationCode);

        var replacement = Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(19.9),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Equal("735194", replacement.Request.Visual?.VerificationCode);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(20.1),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
    }

    [Fact]
    public void ClearedVerificationCodeCannotReappearFromOtherRelay()
    {
        var scheduler = new OverlayScheduler();
        Assert.True(scheduler.Publish(VerificationCode(
            "482731",
            OverlaySource.XiaomiHyperConnect), Now));
        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);

        Assert.Equal(1, scheduler.ClearDismissible(Now.AddSeconds(2)));
        Assert.False(scheduler.Publish(VerificationCode(
            "482731",
            OverlaySource.PhoneLink), Now.AddSeconds(3)));
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(3),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);

        Assert.True(scheduler.Publish(VerificationCode(
            "735194",
            OverlaySource.PhoneLink), Now.AddSeconds(4)));
        var newCode = Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(4),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Equal("735194", newCode.Request.Visual?.VerificationCode);
    }

    [Fact]
    public void CrossSourceApproximateDuplicate_IsDisplayedOnlyOnce()
    {
        var scheduler = new OverlayScheduler();
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-kosa",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "Fight for the Future",
            "UPDATE: KOSA theater is a gift to Big Tech"), Now));

        Assert.False(scheduler.Publish(OverlayRequest.Timed(
            "phone-link-kosa",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "Fight for the Future",
            "KOSA theater is a gift to Big Tech"), Now.AddSeconds(90)));
    }

    [Fact]
    public void PhoneNotification_IsDiscardedAfterThreeMinutesOfWallClockAge()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "持续被挤下的通知"), Now);

        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleNotifications: 1).NotificationCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(30),
            maxVisibleNotifications: 0).NotificationCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(181),
            maxVisibleNotifications: 1).NotificationCards);
    }

    [Fact]
    public void GameSummary_UsesSixtySecondsOfAccumulatedVisibleTime()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "steam-summary",
            OverlayKind.GameSummary,
            OverlaySource.Steam,
            "本次游戏 2 小时"), Now);

        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(10),
            maxVisibleCards: 0,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(40),
            maxVisibleCards: 0,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(40),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(89.9),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(90.1),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
    }

    [Fact]
    public void NewGameSummary_WithReusedEventIdReceivesFreshVisibleTime()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "game-summary",
            OverlayKind.GameSummary,
            OverlaySource.Steam,
            "第一款游戏",
            "本次游玩 30 分钟",
            dedupKey: "summary:first"), Now);
        Assert.Single(scheduler.GetFrame(
            Now,
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(30),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);

        scheduler.Publish(OverlayRequest.Timed(
            "game-summary",
            OverlayKind.GameSummary,
            OverlaySource.Steam,
            "第二款游戏",
            "本次游玩 10 分钟",
            dedupKey: "summary:second"), Now.AddSeconds(30));
        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(30),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);

        Assert.Single(scheduler.GetFrame(
            Now.AddSeconds(89.9),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
        Assert.Empty(scheduler.GetFrame(
            Now.AddSeconds(90.1),
            maxVisibleCards: 1,
            maxVisibleNotifications: 0).VisibleCards);
    }

    [Theory]
    [InlineData(OverlayKind.ImportantTaskComplete, 15)]
    [InlineData(OverlayKind.DeviceOrNetwork, 12)]
    [InlineData(OverlayKind.HardwareResolved, 10)]
    public void DeferredStatusCard_ReceivesFullVisibleTimeAfterPressureClears(
        OverlayKind kind,
        int visibleSeconds)
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "alert-1", OverlayKind.HardwareAlert, OverlaySource.Hardware, "告警 1"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "alert-2", OverlayKind.HardwareAlert, OverlaySource.Hardware, "告警 2"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "call-1", OverlayKind.PhoneCall, OverlaySource.PhoneLink, "来电 1"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "call-2", OverlayKind.PhoneCall, OverlaySource.PhoneLink, "来电 2"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "transfer", OverlayKind.PhoneTransfer, OverlaySource.PhoneLink, "传输"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "phone", OverlayKind.PhoneNotification, OverlaySource.PhoneLink,
            "最新通知"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "deferred-status",
            kind,
            kind == OverlayKind.ImportantTaskComplete
                ? OverlaySource.Task
                : kind == OverlayKind.HardwareResolved
                    ? OverlaySource.Hardware
                    : OverlaySource.System,
            "状态提示"), Now);

        Assert.DoesNotContain(
            scheduler.GetFrame(Now, 6, 3).VisibleCards,
            item => item.Request.EventId == "deferred-status");

        scheduler.Publish(OverlayRequest.End(
            "alert-1", OverlayKind.HardwareAlert, OverlaySource.Hardware),
            Now.AddSeconds(10));
        Assert.Contains(
            scheduler.GetFrame(Now.AddSeconds(10), 6, 3).VisibleCards,
            item => item.Request.EventId == "deferred-status");
        Assert.Contains(
            scheduler.GetFrame(
                Now.AddSeconds(10 + visibleSeconds - 0.1), 6, 3).VisibleCards,
            item => item.Request.EventId == "deferred-status");
        Assert.DoesNotContain(
            scheduler.GetFrame(
                Now.AddSeconds(10 + visibleSeconds + 0.1), 6, 3).VisibleCards,
            item => item.Request.EventId == "deferred-status");
    }

    [Fact]
    public void ClearDismissible_PreservesActiveWorkAndSuppressesRelayResurrection()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "media", OverlayKind.MediaActive, OverlaySource.NetEase, "歌曲"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "game", OverlayKind.GameActive, OverlaySource.Steam, "游戏"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "task", OverlayKind.ImportantTask, OverlaySource.Task, "传输任务"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "call", OverlayKind.PhoneCall, OverlaySource.PhoneLink, "妈妈来电"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "transfer", OverlayKind.PhoneTransfer, OverlaySource.PhoneLink, "文件传输"), Now);
        scheduler.Publish(OverlayRequest.Active(
            "alert", OverlayKind.HardwareAlert, OverlaySource.Hardware, "温度告警"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "phone", OverlayKind.PhoneNotification, OverlaySource.PhoneLink,
            "微信", "午饭见"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "volume", OverlayKind.SystemOperation, OverlaySource.System, "音量 42%"), Now);
        scheduler.Publish(OverlayRequest.Timed(
            "summary", OverlayKind.GameSummary, OverlaySource.Steam, "游戏总结"), Now);

        Assert.Equal(3, scheduler.ClearDismissible(Now.AddSeconds(1)));
        var remainingKinds = scheduler.GetFrame(
                Now.AddSeconds(1),
                maxVisibleCards: 6,
                maxVisibleNotifications: 3)
            .VisibleCards
            .Select(item => item.Request.Kind)
            .ToArray();
        Assert.Contains(OverlayKind.MediaActive, remainingKinds);
        Assert.Contains(OverlayKind.GameActive, remainingKinds);
        Assert.Contains(OverlayKind.ImportantTask, remainingKinds);
        Assert.Contains(OverlayKind.PhoneCall, remainingKinds);
        Assert.Contains(OverlayKind.PhoneTransfer, remainingKinds);
        Assert.Contains(OverlayKind.HardwareAlert, remainingKinds);
        Assert.DoesNotContain(OverlayKind.PhoneNotification, remainingKinds);
        Assert.DoesNotContain(OverlayKind.SystemOperation, remainingKinds);
        Assert.DoesNotContain(OverlayKind.GameSummary, remainingKinds);

        Assert.False(scheduler.Publish(OverlayRequest.Timed(
            "phone-link-next-poll",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "微信",
            "午饭见"), Now.AddSeconds(2)));
        Assert.False(scheduler.Publish(OverlayRequest.Timed(
            "xiaomi-next-poll",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "微信",
            "午饭见"), Now.AddSeconds(2)));
        Assert.True(scheduler.Publish(OverlayRequest.Timed(
            "phone-new-content",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "微信",
            "晚饭见"), Now.AddSeconds(2)));
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

    [Fact]
    public void HardwareRecoveryCanReplaceSameEventAfterEarlierRecoveryWasDismissed()
    {
        var scheduler = new OverlayScheduler();
        var recovered = OverlayRequest.Timed(
            "hardware-alert",
            OverlayKind.HardwareResolved,
            OverlaySource.Hardware,
            "硬件状态已恢复",
            dedupKey: "resolved:pump-stopped");
        Assert.True(scheduler.Publish(recovered, Now));
        Assert.Equal(1, scheduler.ClearDismissible(Now.AddSeconds(1)));
        Assert.True(scheduler.Publish(OverlayRequest.Active(
            "hardware-alert",
            OverlayKind.HardwareAlert,
            OverlaySource.Hardware,
            "水泵转速异常",
            dedupKey: "pump-stopped"), Now.AddSeconds(2)));

        Assert.True(scheduler.Publish(recovered, Now.AddSeconds(3)));
        Assert.Equal(
            OverlayKind.HardwareResolved,
            scheduler.GetFrame(Now.AddSeconds(3))
                .PrimaryCard?.Request.Kind);
    }

    private static OverlayRequest VerificationCode(
        string code,
        OverlaySource source) =>
        OverlayRequest.Timed(
            "phone-verification-code",
            OverlayKind.PhoneVerificationCode,
            source,
            code,
            dedupKey: $"verification-code:{code}",
            visual: new OverlayVisualData(
                Eyebrow: "验证码 / CODE",
                Subtitle: "账户安全",
                VerificationCode: code));
}
