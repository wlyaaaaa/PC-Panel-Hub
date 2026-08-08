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

    [Fact]
    public void ActiveRelayDuplicateReusesExistingCardIdentity()
    {
        PhoneActiveNotificationIdentity[] existing =
        [
            new(
                "active-phone:first",
                OverlayKind.PhoneTransfer,
                "Fight for the Future",
                "UPDATE: KOSA theater is a gift to Big Tech"),
        ];

        var eventId = PhoneNotificationClassifier.ResolveActiveEventId(
            existing,
            OverlayKind.PhoneTransfer,
            "Fight for the Future",
            "KOSA theater is a gift to Big Tech",
            "active-phone:fallback");

        Assert.Equal("active-phone:first", eventId);
    }

    [Fact]
    public void PersistentNotificationIsRepublishedAfterSafetyLeaseExpiry()
    {
        Assert.True(PhoneNotificationClassifier.ShouldPublishPersistent(
            known: true,
            changed: false,
            isActiveTracked: false));
        Assert.False(PhoneNotificationClassifier.ShouldPublishPersistent(
            known: true,
            changed: false,
            isActiveTracked: true));
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

    [Theory]
    [InlineData("腾讯科技", "您的验证码是 482731，请勿泄露", "482731")]
    [InlineData("安全验证", "验证码：1357", "1357")]
    [InlineData("账户安全", "动态口令 24680", "24680")]
    [InlineData("Sign-in", "Verification code: 7351942", "7351942")]
    [InlineData("Sign-in", "Your one-time code is 86420917", "86420917")]
    [InlineData("账户安全", "验证码：１２３４５６", "123456")]
    [InlineData("Sign-in", "531 902 is your verification code", "531902")]
    [InlineData("Sign-in", "Verification code: 531-902", "531902")]
    [InlineData("账户安全", "482731 是您的腾讯验证码", "482731")]
    [InlineData("Google", "G-123456 is your Google verification code", "123456")]
    [InlineData("账户安全", "验证码：123456 5分钟内有效", "123456")]
    [InlineData("账户安全", "旧验证码 111111，新验证码 222222", "222222")]
    [InlineData("订单 112233", "本次登录验证码为 445566", "445566")]
    public void ExtractsOnlyContextualFourToEightDigitVerificationCodes(
        string title,
        string body,
        string expected)
    {
        Assert.True(PhoneVerificationCodeDetector.TryExtract(
            title,
            body,
            out var code));
        Assert.Equal(expected, code);
        Assert.Equal(
            PhoneNotificationCategory.VerificationCode,
            PhoneNotificationClassifier.Classify(title, body));
    }

    [Theory]
    [InlineData("物流", "取件码 6821")]
    [InlineData("订单", "订单号 482731")]
    [InlineData("支付", "支付金额 128.00 元")]
    [InlineData("会议", "会议时间 14:25")]
    [InlineData("联系号码", "手机号 13800138000")]
    [InlineData("邀请好友", "邀请码 735194")]
    [InlineData("安全提醒", "验证码已发送，请注意查收")]
    [InlineData("账户提醒", "订单号 482731，验证码已发送")]
    [InlineData("账户提醒", "验证码有效期至 2026-08-07")]
    [InlineData("账户提醒", "482731，验证码已发送")]
    [InlineData("Hotpot delivery", "Order 482731 is ready")]
    [InlineData("账户提醒", "验证码已发送至 138 0013 8000")]
    [InlineData("账户提醒", "验证码已发送至 138****8000")]
    [InlineData("账户提醒", "验证码将在 2026-08-07 发送")]
    [InlineData("账户提醒", "验证码已发送，订单将在 1234 秒后取消")]
    [InlineData("账户提醒", "482731 是您的订单号，验证码已发送")]
    [InlineData("账户提醒", "13800138 是您的手机号，验证码已发送")]
    [InlineData("Account", "482731 is your order number, verification code sent")]
    [InlineData("Account", "13800138 is your phone number, verification code sent")]
    [InlineData("账户提醒", "验证码：1380 0138 000")]
    [InlineData("账户提醒", "验证码：1380\t0138\t000")]
    [InlineData("账户提醒", "验证码：1380 0138 000")]
    [InlineData("账户提醒", "验证码：123456AB")]
    [InlineData("账户提醒", "验证码：AB123456")]
    public void DoesNotPromoteUnrelatedNumbers(
        string title,
        string body)
    {
        Assert.False(PhoneVerificationCodeDetector.TryExtract(
            title,
            body,
            out _));
        Assert.NotEqual(
            PhoneNotificationCategory.VerificationCode,
            PhoneNotificationClassifier.Classify(title, body));
    }
}
