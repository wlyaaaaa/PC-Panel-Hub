using System.Text;
using System.Text.RegularExpressions;

namespace HS2.CrystalOverlay.Core;

public static partial class PhoneVerificationCodeDetector
{
    private const int MaximumForwardDistance = 32;
    private const int MaximumReverseDistance = 16;

    public static bool TryExtract(
        string? title,
        string? body,
        out string code)
    {
        var text = string.Join(
                ' ',
                new[] { title, body }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Normalize(NormalizationForm.FormKC);
        if (text.Length == 0)
        {
            code = string.Empty;
            return false;
        }

        var keywords = VerificationKeyword().Matches(text);
        if (keywords.Count == 0)
        {
            code = string.Empty;
            return false;
        }

        var best = CodeCandidate()
            .Matches(text)
            .Select(candidate => new
            {
                Match = candidate,
                Code = DigitsOnly(candidate.Value),
            })
            .Where(candidate =>
                candidate.Code.Length is >= 4 and <= 8 &&
                !HasExcludedLabel(text, candidate.Match.Index) &&
                !IsEmbeddedInSeparatedNumber(text, candidate.Match))
            .Select(candidate => new
            {
                candidate.Code,
                candidate.Match.Index,
                Score = Score(text, candidate.Match, keywords),
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => Math.Abs(candidate.Code.Length - 6))
            .ThenByDescending(candidate => candidate.Index)
            .FirstOrDefault();
        code = best?.Code ?? string.Empty;
        return best is not null;
    }

    private static int Score(
        string text,
        Match candidate,
        MatchCollection keywords)
    {
        var best = 0;
        foreach (Match keyword in keywords)
        {
            if (!IsSameSegment(text, candidate, keyword))
            {
                continue;
            }

            int score;
            if (keyword.Index + keyword.Length <= candidate.Index)
            {
                var distance = candidate.Index -
                               (keyword.Index + keyword.Length);
                if (distance > MaximumForwardDistance ||
                    !ForwardConnector().IsMatch(text.Substring(
                        keyword.Index + keyword.Length,
                        distance)))
                {
                    continue;
                }

                score = 300 - distance;
            }
            else if (candidate.Index + candidate.Length <= keyword.Index)
            {
                var distance = keyword.Index -
                               (candidate.Index + candidate.Length);
                var connector = text.Substring(
                    candidate.Index + candidate.Length,
                    distance);
                if (distance > MaximumReverseDistance ||
                    !ReverseConnector().IsMatch(connector) ||
                    ExcludedReverseContext().IsMatch(connector))
                {
                    continue;
                }

                score = 220 - distance;
            }
            else
            {
                continue;
            }

            var digitCount = DigitsOnly(candidate.Value).Length;
            if (digitCount == 6)
            {
                score += 8;
            }

            best = Math.Max(best, score);
        }

        return best;
    }

    private static bool IsSameSegment(
        string text,
        Match candidate,
        Match keyword)
    {
        var start = Math.Min(
            candidate.Index + candidate.Length,
            keyword.Index + keyword.Length);
        var end = Math.Max(candidate.Index, keyword.Index);
        return start >= end ||
               !SegmentBoundary().IsMatch(text[start..end]);
    }

    private static bool HasExcludedLabel(string text, int candidateIndex)
    {
        var start = Math.Max(0, candidateIndex - 18);
        return ExcludedLabel().IsMatch(text[start..candidateIndex]);
    }

    private static bool IsEmbeddedInSeparatedNumber(
        string text,
        Match candidate)
    {
        var before = candidate.Index - 1;
        while (before >= 0 && IsNumericSeparator(text[before]))
        {
            before--;
        }

        if (before >= 0 && char.IsAsciiDigit(text[before]))
        {
            return true;
        }

        var after = candidate.Index + candidate.Length;
        while (after < text.Length && IsNumericSeparator(text[after]))
        {
            after++;
        }

        var trailingDigits = 0;
        while (after + trailingDigits < text.Length &&
               char.IsAsciiDigit(text[after + trailingDigits]))
        {
            trailingDigits++;
        }

        // A remaining 3+ digit group means the regex only captured the first
        // part of a longer phone/account number. One- or two-digit values are
        // commonly durations such as "5分钟内有效" and must not hide a real OTP.
        return trailingDigits >= 3;
    }

    private static bool IsNumericSeparator(char value) =>
        value is ' ' or '\t' or '\u00a0' or '-';

    private static string DigitsOnly(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(
        @"验证码|校验码|认证码|动态(?:码|密码|口令)|一次性(?:密码|口令|验证码)|短信码|安全码|(?<![\p{L}\p{N}])(?:otp|verification\s+code|authentication\s+code|security\s+code|auth\s+code|one[\s-]*time\s+(?:code|password|passcode))(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerificationKeyword();

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:[0-9]{4}[ \t\u00a0-][0-9]{4}|[0-9]{3}[ \t\u00a0-][0-9]{3}|[0-9]{4,8})(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant)]
    private static partial Regex CodeCandidate();

    [GeneratedRegex(
        @"(?:(?:订单|运单|物流|流水|交易|取件|取餐|提货|邀请|兑换|优惠|手机号|电话|尾号|金额|价格|房间|会议|日期|时间)(?:号|码|号码)?|(?:有效期|截止日期?)(?:至|到)?)\s*[:：]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedLabel();

    [GeneratedRegex(
        @"订单|运单|物流|流水|交易|取件|取餐|提货|邀请|兑换|优惠|手机号|电话号码?|尾号|金额|价格|房间|会议|日期|时间|order\s+(?:number|id)|tracking\s+(?:number|id)|phone\s+number|pickup\s+code|invite\s+code|coupon\s+code",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedReverseContext();

    [GeneratedRegex(
        @"^[\s:：=（(【\[]*(?:(?:是|为|即为|就是|is)[\s:：=（(【\[]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForwardConnector();

    [GeneratedRegex(
        @"^[\s:：,，]*(?:(?:是|为|即为|就是)(?:您|你|您的|你的)?(?:本次)?[\p{L}0-9 ]{0,20}|is\s+(?:(?:your|the)\s+)?[A-Za-z0-9 ]{0,20})[\s:：,，]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReverseConnector();

    [GeneratedRegex(@"[。.!！?？;；\r\n]")]
    private static partial Regex SegmentBoundary();
}
