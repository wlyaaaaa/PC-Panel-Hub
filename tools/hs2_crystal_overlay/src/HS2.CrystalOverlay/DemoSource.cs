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
    }
}
