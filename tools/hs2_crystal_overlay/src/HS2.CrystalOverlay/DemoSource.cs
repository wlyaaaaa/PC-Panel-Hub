using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal static class DemoSource
{
    internal static void Publish(
        IOverlayPublisher publisher,
        string? scenario = null)
    {
        PublishAmbient(publisher);

        switch (scenario?.Trim().ToLowerInvariant())
        {
            case "media":
                PublishMedia(publisher);
                break;
            case "notification":
                PublishMedia(publisher);
                PublishLongNotification(publisher);
                break;
            case "verification":
                PublishVerificationCode(publisher);
                break;
            case "stack":
                PublishMedia(publisher);
                PublishNotificationBurst(publisher);
                break;
            case "max-six":
                PublishMaximumDeck(publisher);
                break;
            case "sparse":
                PublishMedia(publisher);
                PublishLongNotification(publisher);
                break;
            case "overflow":
                PublishMaximumDeck(publisher);
                PublishOverflowNotification(publisher);
                break;
            case "reflow":
                PublishMaximumDeck(publisher);
                _ = RunReflowAsync(publisher);
                break;
            case "call":
                PublishCall(publisher);
                break;
            case "game":
                PublishGame(publisher);
                break;
            case "task":
                PublishTask(publisher);
                break;
            case "system":
                PublishSystemOperation(publisher);
                break;
            case "system-mute":
                PublishMutedSystemOperation(publisher);
                break;
            case "audio-phone":
                PublishNotificationBurst(publisher);
                PublishSystemOperation(publisher);
                break;
            case "audio-media":
                PublishMedia(publisher);
                PublishSystemOperation(publisher);
                break;
            case "phone-order":
                PublishNotificationBurst(publisher);
                break;
            case "alert":
                PublishHardwareAlert(publisher);
                break;
            default:
                PublishMedia(publisher);
                PublishNotificationBurst(publisher);
                break;
        }
    }

    private static void PublishAmbient(IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Active(
            "demo-phone-battery",
            OverlayKind.PhoneBattery,
            OverlaySource.XiaomiHyperConnect,
            "97%",
            body: null,
            visual: new OverlayVisualData(IsCharging: true)));
    }

    private static void PublishMedia(IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Active(
            "demo-media",
            OverlayKind.MediaActive,
            OverlaySource.NetEase,
            "The Green River",
            body: null,
            visual: new OverlayVisualData(
                Subtitle: "Ambient Session",
                TranslatedTitle: "绿色河流",
                ArtworkPath: Path.Combine(
                    AppContext.BaseDirectory,
                    "Assets",
                    "Square150x150Logo.scale-200.png"),
                AccentHex: "#8FF8FF")));
    }

    private static void PublishNotificationBurst(
        IOverlayPublisher publisher)
    {
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

        _ = publisher.Publish(OverlayRequest.Timed(
            "demo-phone-notification-3",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "外卖已放入智能柜",
            "取餐码 6821；这是当前最新三条手机通知中的第三条。",
            visual: new OverlayVisualData(
                Eyebrow: "手机通知 / PHONE",
                Subtitle: "小米妙享",
                AccentHex: "#70F0B2")));
    }

    private static void PublishMaximumDeck(IOverlayPublisher publisher)
    {
        PublishMedia(publisher);
        PublishNotificationBurst(publisher);
        PublishGame(publisher);
        PublishSystemOperation(publisher);
    }

    private static void PublishOverflowNotification(
        IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Timed(
            "demo-phone-notification-4",
            OverlayKind.PhoneNotification,
            OverlaySource.PhoneLink,
            "第四条通知到达，最旧一条应退出",
            "只保留最新三条；布局应自动补位，不能出现空框、遮挡或文字突然缩小。",
            visual: new OverlayVisualData(
                Eyebrow: "手机通知 / PHONE",
                Subtitle: "手机连接",
                AccentHex: "#70F0B2")));
    }

    private static async Task RunReflowAsync(IOverlayPublisher publisher)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        _ = publisher.Publish(OverlayRequest.End(
            AudioHudProjection.EventId,
            OverlayKind.SystemOperation,
            OverlaySource.System));
        await Task.Delay(TimeSpan.FromSeconds(3));
        _ = publisher.Publish(OverlayRequest.End(
            "demo-phone-notification-3",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect));
        await Task.Delay(TimeSpan.FromSeconds(3));
        _ = publisher.Publish(OverlayRequest.End(
            "demo-game",
            OverlayKind.GameActive,
            OverlaySource.Steam));
    }

    private static void PublishLongNotification(
        IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Timed(
            "demo-phone-notification-long",
            OverlayKind.PhoneNotification,
            OverlaySource.XiaomiHyperConnect,
            "行程发生变化，请确认新的安排",
            "司机将在十分钟后到达约定地点，车辆信息、上车位置和备用联系电话已同步到手机；如果道路临时管制，系统会继续更新新的集合位置。",
            visual: new OverlayVisualData(
                Eyebrow: "手机通知 / PHONE",
                Subtitle: "小米妙享",
                AccentHex: "#70F0B2")));
    }

    private static void PublishVerificationCode(
        IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Timed(
            "phone-verification-code",
            OverlayKind.PhoneVerificationCode,
            OverlaySource.XiaomiHyperConnect,
            "482731",
            dedupKey: "verification-code:482731",
            visual: new OverlayVisualData(
                Eyebrow: "验证码 / CODE",
                Subtitle: "账户安全",
                Meta: "小米妙享",
                AccentHex: "#70F0B2",
                VerificationCode: "482731")));
    }

    private static void PublishCall(IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Active(
            "demo-phone-call",
            OverlayKind.PhoneCall,
            OverlaySource.XiaomiHyperConnect,
            "手机来电",
            "妈妈  ·  小米妙享",
            visual: new OverlayVisualData(
                Eyebrow: "正在呼入 / INCOMING CALL",
                Meta: "请在手机上接听或拒绝",
                AccentHex: "#FF9EAE")));
    }

    private static void PublishGame(IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Active(
            "demo-game",
            OverlayKind.GameActive,
            OverlaySource.Steam,
            "Cyberpunk 2077",
            "当前游玩 01:26:43",
            visual: new OverlayVisualData(
                Eyebrow: "游戏进行中 / PLAYING",
                Meta: "Steam  ·  本次游玩 1 小时 26 分",
                AccentHex: "#8FF8FF")));
    }

    private static void PublishTask(IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Active(
            "demo-task",
            OverlayKind.ImportantTask,
            OverlaySource.Task,
            "正在复制照片归档",
            "G: → H: 冷备  ·  预计剩余 12 分钟",
            visual: new OverlayVisualData(
                Eyebrow: "重要任务 / TASK",
                Meta: "64%  ·  128 GB / 200 GB",
                Progress: 0.64,
                AccentHex: "#70F0B2")));
    }

    private static void PublishSystemOperation(
        IOverlayPublisher publisher)
    {
        _ = publisher.Publish(
            AudioHudProjection.Create(100, isMuted: false));
    }

    private static void PublishMutedSystemOperation(
        IOverlayPublisher publisher)
    {
        _ = publisher.Publish(
            AudioHudProjection.Create(100, isMuted: true));
    }

    private static void PublishHardwareAlert(
        IOverlayPublisher publisher)
    {
        _ = publisher.Publish(OverlayRequest.Active(
            "demo-hardware-alert",
            OverlayKind.HardwareAlert,
            OverlaySource.Hardware,
            "CPU 温度过高",
            "当前 96°C，已持续 30 秒。建议立即检查水泵转速与冷排风扇。",
            visual: new OverlayVisualData(
                Eyebrow: "硬件告警 / HARDWARE ALERT",
                Meta: "建议动作  ·  暂停高负载任务并检查散热",
                AccentHex: "#FF7589")));
    }
}
