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
    public void DedupKeyIgnoresCaseAndWhitespace()
    {
        Assert.Equal(
            PhoneNotificationClassifier.DedupKey(
                "Phone Link",
                "New Message",
                "Hello world"),
            PhoneNotificationClassifier.DedupKey(
                " phone link ",
                "NEW   MESSAGE",
                "hello  world"));
    }
}
