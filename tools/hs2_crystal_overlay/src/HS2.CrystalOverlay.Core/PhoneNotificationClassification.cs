using System.Text;
using System.Text.RegularExpressions;

namespace HS2.CrystalOverlay.Core;

public enum PhoneNotificationCategory
{
    Ordinary,
    Dynamic,
    VerificationCode,
    Call,
    Transfer,
}

public sealed record PhoneActiveNotificationIdentity(
    string EventId,
    OverlayKind Kind,
    string Title,
    string? Body);

public static partial class PhoneNotificationClassifier
{
    private const int MinimumApproximateLength = 12;
    private const double ContainmentLengthRatio = 0.72;
    private const double NGramDiceThreshold = 0.82;
    private const int NGramSize = 3;

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

        if (PhoneVerificationCodeDetector.TryExtract(
                titleText,
                bodyText,
                out _))
        {
            return PhoneNotificationCategory.VerificationCode;
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

    public static bool AreApproximatelyEquivalent(
        string? firstTitle,
        string? firstBody,
        string? secondTitle,
        string? secondBody)
    {
        var first = CombinedNormalizedText(firstTitle, firstBody);
        var second = CombinedNormalizedText(secondTitle, secondBody);
        if (first.Length == 0 || second.Length == 0)
        {
            return false;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return true;
        }

        if (!NumericTokens(first).SequenceEqual(NumericTokens(second)))
        {
            return false;
        }

        var firstRunes = first.EnumerateRunes().ToArray();
        var secondRunes = second.EnumerateRunes().ToArray();
        var shorterLength = Math.Min(firstRunes.Length, secondRunes.Length);
        var longerLength = Math.Max(firstRunes.Length, secondRunes.Length);
        if (shorterLength < MinimumApproximateLength)
        {
            return false;
        }

        var shorter = firstRunes.Length <= secondRunes.Length
            ? first
            : second;
        var longer = firstRunes.Length <= secondRunes.Length
            ? second
            : first;
        if (longer.Contains(shorter, StringComparison.Ordinal) &&
            (double)shorterLength / longerLength >=
            ContainmentLengthRatio)
        {
            return true;
        }

        return NGramDice(firstRunes, secondRunes, NGramSize) >=
               NGramDiceThreshold;
    }

    public static string ResolveActiveEventId(
        IEnumerable<PhoneActiveNotificationIdentity> existing,
        OverlayKind kind,
        string title,
        string? body,
        string fallbackEventId)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackEventId);
        return existing.FirstOrDefault(candidate =>
                   candidate.Kind == kind &&
                   AreApproximatelyEquivalent(
                       candidate.Title,
                       candidate.Body,
                       title,
                       body))
               ?.EventId ?? fallbackEventId;
    }

    public static bool ShouldPublishPersistent(
        bool known,
        bool changed,
        bool isActiveTracked) =>
        !known || changed || !isActiveTracked;

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

    private static string CombinedNormalizedText(
        string? title,
        string? body) =>
        string.Join(
            ' ',
            new[] { Normalize(title), Normalize(body) }
                .Where(value => value.Length > 0));

    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(
            NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var needsSeparator = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (needsSeparator && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(Rune.ToLowerInvariant(rune).ToString());
                needsSeparator = false;
            }
            else
            {
                needsSeparator = builder.Length > 0;
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> NumericTokens(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsDigit(rune))
            {
                current.Append(rune.ToString());
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static double NGramDice(
        IReadOnlyList<Rune> first,
        IReadOnlyList<Rune> second,
        int size)
    {
        var firstCounts = NGramCounts(first, size);
        var secondCounts = NGramCounts(second, size);
        var firstTotal = firstCounts.Values.Sum();
        var secondTotal = secondCounts.Values.Sum();
        if (firstTotal == 0 || secondTotal == 0)
        {
            return 0;
        }

        var intersection = firstCounts.Sum(pair =>
            Math.Min(
                pair.Value,
                secondCounts.GetValueOrDefault(pair.Key)));
        return 2d * intersection / (firstTotal + secondTotal);
    }

    private static Dictionary<string, int> NGramCounts(
        IReadOnlyList<Rune> runes,
        int size)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (runes.Count < size)
        {
            return result;
        }

        for (var index = 0; index <= runes.Count - size; index++)
        {
            var builder = new StringBuilder();
            for (var offset = 0; offset < size; offset++)
            {
                builder.Append(runes[index + offset].ToString());
            }

            var gram = builder.ToString();
            result[gram] = result.GetValueOrDefault(gram) + 1;
        }

        return result;
    }

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
