using System.Text.RegularExpressions;

namespace HS2.CrystalOverlay.Core;

public enum PhoneNotificationCategory
{
    Ordinary,
    Dynamic,
    Call,
    Transfer,
}

public static partial class PhoneNotificationClassifier
{
    public static PhoneNotificationCategory Classify(
        string? title,
        string? body)
    {
        var titleText = title ?? string.Empty;
        var bodyText = body ?? string.Empty;
        var text = $"{titleText} {bodyText}";
        if (CompletedCallText().IsMatch(text) ||
            CompletedTransferText().IsMatch(text))
        {
            return PhoneNotificationCategory.Ordinary;
        }

        if (CallTitleText().IsMatch(titleText) ||
            ActiveCallBodyText().IsMatch(bodyText))
        {
            return PhoneNotificationCategory.Call;
        }

        if (TransferText().IsMatch(text))
        {
            return PhoneNotificationCategory.Transfer;
        }

        return DynamicText().IsMatch(text)
            ? PhoneNotificationCategory.Dynamic
            : PhoneNotificationCategory.Ordinary;
    }

    public static string DedupKey(
        string? title,
        string? body)
    {
        return string.Join(
            '\u001f',
            new[] { title, body }
                .Select(value => Normalize(value))
                .Where(value => value.Length > 0));
    }

    public static OverlaySource SourceForRelayApp(string? appName)
    {
        var value = appName ?? string.Empty;
        if (value.Contains(
                "Phone Link",
                StringComparison.OrdinalIgnoreCase) ||
            value.Contains(
                "手机连接",
                StringComparison.OrdinalIgnoreCase) ||
            value.Contains(
                "Link to Windows",
                StringComparison.OrdinalIgnoreCase) ||
            value.Contains(
                "Cross Device Experience Host",
                StringComparison.OrdinalIgnoreCase) ||
            value.Contains(
                "跨设备体验主机",
                StringComparison.OrdinalIgnoreCase))
        {
            return OverlaySource.PhoneLink;
        }

        if (value.Contains(
                "Xiaomi",
                StringComparison.OrdinalIgnoreCase) ||
            value.Contains(
                "小米",
                StringComparison.OrdinalIgnoreCase) ||
            value.Contains(
                "妙享",
                StringComparison.OrdinalIgnoreCase) ||
            value.Contains(
                "MiSmartShare",
                StringComparison.OrdinalIgnoreCase))
        {
            return OverlaySource.XiaomiHyperConnect;
        }

        return OverlaySource.System;
    }

    private static string Normalize(string? value) =>
        string.Join(
            ' ',
            (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(
        @"(?:^|[\s：:])来电(?:$|[\s：:])|来电\s*$|incoming\s+call|calling\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CallTitleText();

    [GeneratedRegex(
        @"正在通话|通话中|incoming\s+call|on\s+a\s+call",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveCallBodyText();

    [GeneratedRegex(
        @"帮你接|已接听|已挂断|通话结束|通话记录|未接来电|漏接|拦截|推销|骚扰|语音信箱|voicemail|missed\s+call|call\s+ended|blocked\s+call|answered\s+for\s+you",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompletedCallText();

    [GeneratedRegex(
        @"文件传输|正在传输|接收文件|发送文件|file\s+transfer|sending\s+file|receiving\s+file",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransferText();

    [GeneratedRegex(
        @"传输完成|发送完成|接收完成|已传输|transfer\s+complete|sent\s+successfully|received\s+successfully",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompletedTransferText();

    [GeneratedRegex(
        @"导航|计时器|倒计时|配送中|行程中|录音中|运动中|navigation|timer|countdown|delivery|trip\s+in\s+progress|recording",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DynamicText();
}
