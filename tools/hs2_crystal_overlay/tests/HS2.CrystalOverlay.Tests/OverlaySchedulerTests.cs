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

        var frame = scheduler.GetFrame(Now);

        Assert.Single(frame.DirectItems);
        Assert.Equal(OverlayKind.PhoneBattery, frame.DirectItems[0].Request.Kind);
        Assert.Equal(OverlayKind.PhoneNotification, frame.PrimaryCard?.Request.Kind);
    }

    [Fact]
    public void OrdinaryPhoneNotification_ExpiresAndDoesNotRemainForever()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Timed(
            "phone-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "短信",
            "验证码 1234"), Now);

        Assert.NotNull(scheduler.GetFrame(Now.AddSeconds(17)).PrimaryCard);
        Assert.Null(scheduler.GetFrame(Now.AddSeconds(19)).PrimaryCard);
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
    public void DynamicPhoneState_DisappearsWhenSourceEnds()
    {
        var scheduler = new OverlayScheduler();
        scheduler.Publish(OverlayRequest.Active(
            "ride",
            OverlayKind.PhoneDynamic,
            OverlaySource.XiaomiHyperConnect,
            "司机即将到达",
            "距离 300 米"), Now);

        Assert.Equal(
            OverlayKind.PhoneDynamic,
            scheduler.GetFrame(Now.AddHours(1)).PrimaryCard?.Request.Kind);

        scheduler.Publish(OverlayRequest.End(
            "ride",
            OverlayKind.PhoneDynamic,
            OverlaySource.XiaomiHyperConnect), Now.AddHours(1));

        Assert.Null(scheduler.GetFrame(Now.AddHours(1)).PrimaryCard);
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
            dedupKey: "wechat|午饭见"), Now);
        var duplicate = scheduler.Publish(OverlayRequest.Timed(
            "phone-link-toast",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "微信",
            "午饭见",
            dedupKey: "wechat|午饭见"), Now.AddSeconds(3));

        Assert.True(accepted);
        Assert.False(duplicate);
        Assert.Single(scheduler.GetFrame(Now.AddSeconds(3)).Cards);
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
