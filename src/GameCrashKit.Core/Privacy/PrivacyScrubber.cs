using System.Text.RegularExpressions;

namespace GameCrashKit.Core.Privacy;

public sealed record ScrubContext(
    string? CurrentUserName,
    string? CurrentMachineName,
    string? UserProfilePath)
{
    public static ScrubContext Empty { get; } = new(null, null, null);
}

/// <summary>
/// Deterministic privacy scrubber with explicit regex timeouts.
/// </summary>
public static partial class PrivacyScrubber
{
    public const int MaxExcerptLines = 5;
    public const int MaxExcerptLineLength = 120;
    public const int MaxExcerptTotalChars = 600;
    private const int TimeoutMs = 200;

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]+", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex ControlCharsRegex();

    [GeneratedRegex(@"[A-Za-z]:[/\\]Users[/\\][^/\\\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex GenericUserProfilePathRegex();

    [GeneratedRegex(@"\bS-1-(?:5-21(?:-\d+){4}|\d+(?:-\d+)+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex WindowsSidRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b7656119\d{10}\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Steam64IdRegex();

    [GeneratedRegex(@"(?<key>token|apikey|api_key|secret|password|passwd|auth_token|auth|session_id|cookie)\s*[:=]\s*[^\s&;,""'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SecretKeyValueRegex();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?<![.\dv])(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}(?![.\d])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"(?<![:\da-f])(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|(?:[0-9a-f]{1,4}:){1,6}:(?:[0-9a-f]{1,4}:){0,5}[0-9a-f]{1,4}|::1)(?![:\da-f])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Ipv6Regex();

    public static string ScrubText(string input, ScrubContext context, bool scrubIpv4 = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var text = ControlCharsRegex().Replace(input, string.Empty);

        if (!string.IsNullOrWhiteSpace(context.UserProfilePath))
        {
            var winForm = context.UserProfilePath.Trim().TrimEnd('\\', '/');
            if (winForm.Length >= 4)
            {
                var unixForm = winForm.Replace('\\', '/');
                text = text.Replace(winForm, "<USER_HOME>", StringComparison.OrdinalIgnoreCase);
                if (!string.Equals(unixForm, winForm, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Replace(unixForm, "<USER_HOME>", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        text = GenericUserProfilePathRegex().Replace(text, "<USER_HOME>");
        text = WindowsSidRegex().Replace(text, "<SID>");
        text = EmailRegex().Replace(text, "<EMAIL>");
        text = Steam64IdRegex().Replace(text, "<STEAM_ID>");
        text = SecretKeyValueRegex().Replace(text, "${key}=<REDACTED>");
        text = BearerTokenRegex().Replace(text, "Bearer <REDACTED>");

        if (scrubIpv4)
        {
            text = Ipv4Regex().Replace(text, "<IP>");
            text = Ipv6Regex().Replace(text, "<IP>");
        }

        if (!string.IsNullOrWhiteSpace(context.CurrentMachineName) && context.CurrentMachineName.Trim().Length >= 2)
        {
            var pc = context.CurrentMachineName.Trim();
            text = Regex.Replace(
                text,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(pc)}(?![A-Za-z0-9_])",
                "<PC_NAME>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(TimeoutMs));
        }

        if (!string.IsNullOrWhiteSpace(context.CurrentUserName) && context.CurrentUserName.Trim().Length >= 3)
        {
            var user = context.CurrentUserName.Trim();
            text = Regex.Replace(
                text,
                $@"(?<![A-Za-z0-9_]){Regex.Escape(user)}(?![A-Za-z0-9_])",
                "<USER>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(TimeoutMs));
        }

        return text;
    }

    public static IReadOnlyList<string> ScrubExcerptLines(IReadOnlyList<string> rawLines, ScrubContext context)
    {
        ArgumentNullException.ThrowIfNull(rawLines);
        ArgumentNullException.ThrowIfNull(context);

        var result = new List<string>(MaxExcerptLines);
        var totalChars = 0;

        foreach (var raw in rawLines)
        {
            if (result.Count >= MaxExcerptLines || totalChars >= MaxExcerptTotalChars)
            {
                break;
            }

            var scrubbed = ScrubText(raw, context, scrubIpv4: true).Trim();
            if (scrubbed.Length == 0)
            {
                continue;
            }

            if (scrubbed.Length > MaxExcerptLineLength)
            {
                scrubbed = scrubbed[..(MaxExcerptLineLength - 3)] + "...";
            }

            var remaining = MaxExcerptTotalChars - totalChars;
            if (scrubbed.Length > remaining)
            {
                if (remaining <= 4)
                {
                    break;
                }

                scrubbed = scrubbed[..(remaining - 3)] + "...";
            }

            result.Add(scrubbed);
            totalChars += scrubbed.Length;
        }

        return result;
    }

    /// <summary>
    /// Sanitizes structured identifiers/version fields without mangling dotted version numbers (e.g. 32.0.16.1664).
    /// </summary>
    public static string SanitizeStructuredValue(string? value, ScrubContext context, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var scrubbed = ScrubText(value, context, scrubIpv4: false).Trim();
        if (scrubbed.Length == 0)
        {
            return fallback;
        }

        return scrubbed.Length <= maxLength
            ? scrubbed
            : scrubbed[..(maxLength - 3)] + "...";
    }
}
