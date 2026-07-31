using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal static class DemoSource
{
    internal static void Publish(IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Active(
            "demo-phone-battery",
            OverlayKind.PhoneBattery,
            OverlaySource.XiaomiHyperConnect,
            "97%",
            body: null,
            visual: new OverlayVisualData(IsCharging: true)));

        _ = publisher.Publish(OverlayRequest.Active(
            "demo-media",
            OverlayKind.MediaActive,
            OverlaySource.NetEase,
            "The Green River",
            "河流穿过光影，也穿过屏幕转角",
            visual: new OverlayVisualData(
                Eyebrow: "正在播放",
                Subtitle: "Ambient Session",
                Meta: "01:42 / 04:18  ·  网易云音乐",
                Progress: 0.395,
                AccentHex: "#8FF8FF")));

        _ = publisher.Publish(OverlayRequest.Active(
            "demo-glance",
            OverlayKind.Glance,
            OverlaySource.System,
            GlanceClock.FormatChinaTime(DateTimeOffset.UtcNow)));

        _ = publisher.Publish(OverlayRequest.Timed(
            "demo-phone-notification-1",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "包裹已经到达取件点",
            "请在今晚之前前往东门智能柜领取，取件码和柜门位置已经同步到手机。",
            visual: new OverlayVisualData(
                Eyebrow: "手机通知 / PHONE",
                Subtitle: "小米妙享",
                AccentHex: "#70F0B2")));

        _ = publisher.Publish(OverlayRequest.Timed(
            "demo-phone-notification-2",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "会议时间发生调整",
            "原定下午三点的产品讨论改到四点半，会议室不变，请确认新的时间安排。",
            visual: new OverlayVisualData(
                Eyebrow: "手机通知 / PHONE",
                Subtitle: "手机连接",
                AccentHex: "#70F0B2")));
    }
}
