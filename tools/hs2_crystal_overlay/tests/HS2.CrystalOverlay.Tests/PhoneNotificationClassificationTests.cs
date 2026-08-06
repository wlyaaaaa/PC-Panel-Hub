using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PhoneNotificationClassificationTests
{
    [Theory]
    [InlineData("妈妈来电", null, PhoneNotificationCategory.Call)]
    [InlineData("Incoming call", "Alex", PhoneNotificationCategory.Call)]
    [InlineData("文件传输", "正在传输 42%", PhoneNotificationCategory.Transfer)]
    [InlineData("导航", "距离目的地 2 km", PhoneNotificationCategory.Dynamic)]
    [InlineData("微信", "收到一条消息", PhoneNotificationCategory.Ordinary)]
    [InlineData(
        "小爱帮你接了个[银行推销]的电话",
        "此次来电是想告知您活动信息",
        PhoneNotificationCategory.Ordinary)]
    [InlineData(
        "未接来电",
        "妈妈 18:32",
        PhoneNotificationCategory.Ordinary)]
    [InlineData(
        "文件传输完成",
        "照片已传输",
        PhoneNotificationCategory.Ordinary)]
    public void ClassifiesPhoneNotificationLifetime(
        string title,
        string? body,
        PhoneNotificationCategory expected)
    {
        Assert.Equal(
            expected,
            PhoneNotificationClassifier.Classify(title, body));
    }

    [Fact]
    public void DedupKeyUsesPayloadAndIgnoresRelaySourceCaseAndWhitespace()
    {
        Assert.Equal(
            PhoneNotificationClassifier.DedupKey(
                "New Message",
                "Hello world"),
            PhoneNotificationClassifier.DedupKey(
                "NEW   MESSAGE",
                "hello  world"));
    }

    [Fact]
    public void DedupKeyNormalizesUnicodeWidthCaseWhitespaceAndPunctuation()
    {
        Assert.Equal(
            PhoneNotificationClassifier.DedupKey(
                "Fight for the Future",
                "UPDATE: KOSA theater is a gift to Big Tech"),
            PhoneNotificationClassifier.DedupKey(
                "ＦＩＧＨＴ　ＦＯＲ　ＴＨＥ　ＦＵＴＵＲＥ！",
                "update - kosa theater is a gift to big tech."));
    }

    [Fact]
    public void ApproximateMatchAcceptsPayloadWhereMostCharactersAreShared()
    {
        Assert.True(PhoneNotificationClassifier.AreApproximatelyEquivalent(
            "Fight for the Future",
            "UPDATE: KOSA theater is a gift to Big Tech",
            "Fight for the Future",
            "KOSA theater is a gift to Big Tech"));
    }

    [Theory]
    [InlineData("验证码", "123456", "验证码", "123457")]
    [InlineData("付款提醒", "支付 ¥128.00", "付款提醒", "支付 ¥129.00")]
    public void ApproximateMatchRejectsDifferentNumbers(
        string firstTitle,
        string firstBody,
        string secondTitle,
        string secondBody)
    {
        Assert.False(PhoneNotificationClassifier.AreApproximatelyEquivalent(
            firstTitle,
            firstBody,
            secondTitle,
            secondBody));
    }

    [Theory]
    [InlineData("Phone Link", OverlaySource.PhoneLink)]
    [InlineData("手机连接", OverlaySource.PhoneLink)]
    [InlineData("Cross Device Experience Host", OverlaySource.PhoneLink)]
    [InlineData("小米妙享", OverlaySource.XiaomiHyperConnect)]
    [InlineData("MiSmartShare", OverlaySource.XiaomiHyperConnect)]
    public void RelayApplicationNamesMapToBothIndependentSources(
        string appName,
        OverlaySource expected)
    {
        Assert.Equal(
            expected,
            PhoneNotificationClassifier.SourceForRelayApp(appName));
    }
}
