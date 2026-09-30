namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Text;
using System.Text.RegularExpressions;

internal static class SessionNetworkRequestSanitizer
{
    private const string RedactedValue = "<redacted>";
    private static readonly HashSet<string> sensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Proxy-Authorization",
        "Set-Cookie",
        "X-Api-Key",
        "X-Auth-Token"
    };

    private static readonly HashSet<string> sensitiveQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "accesskey", "access_key", "api_key", "apikey", "auth", "authorization",
        "client_secret", "code", "credential", "credentials", "id_token", "jwt", "key", "password",
        "passwd", "refresh_token", "sas", "sastoken", "secret", "secret_key", "security_token",
        "session_token", "sig", "signature", "token"
    };
    private static readonly HashSet<string> azureSasFingerprintNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "se", "skoid", "sp", "sr", "srt", "ss", "sv"
    };
    private static readonly HashSet<string> azureSasQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "epk", "erk", "rscc", "rscd", "rsce", "rscl", "rsct", "saoid", "scid", "se",
        "sig", "si", "sip", "ske", "skoid", "sks", "skt", "sktid", "skv", "snapshot",
        "sp", "spk", "spr", "sr", "srk", "srt", "ss", "st", "suoid", "tn", "versionid", "sv"
    };
    private static readonly HashSet<string> cloudFrontQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "signature", "key-pair-id", "policy", "expires", "hash-algorithm"
    };
    private static readonly HashSet<string> legacyGoogleQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "signature", "googleaccessid", "expires"
    };
    private static readonly UTF8Encoding strictUtf8Encoding = new(false, true);
    private static readonly Regex sensitiveAssignmentPattern = new(
        @"(?<name>access_token|accesskey|access_key|api_key|apikey|auth|authorization|client_secret|code|credential|credentials|id_token|jwt|key|password|passwd|refresh_token|sas|sastoken|secret|secret_key|security_token|session_token|sig|signature|token)(?<separator>[""']?\s*[:=]\s*[""']?)(?<value>[^&\s,;}""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex absoluteUrlPattern = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static SessionNetworkRequest? Normalize(SessionNetworkRequest? request)
    {
        if (request is null
            || !string.Equals(request.Schema, SessionNetworkRequest.SchemaName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.Id)
            || string.IsNullOrWhiteSpace(request.Method)
            || string.IsNullOrWhiteSpace(request.Url))
        {
            return null;
        }

        var startedAtUtc = request.StartedAtUtc.ToUniversalTime();
        var completedAtUtc = request.CompletedAtUtc.ToUniversalTime();
        if (completedAtUtc < startedAtUtc)
        {
            completedAtUtc = startedAtUtc;
        }

        return new SessionNetworkRequest
        {
            Id = Truncate(request.Id.Trim(), 128),
            Source = Truncate(string.IsNullOrWhiteSpace(request.Source) ? "unknown" : request.Source.Trim(), 128),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMilliseconds = double.IsFinite(request.DurationMilliseconds)
                ? Math.Max(0, request.DurationMilliseconds)
                : Math.Max(0, (completedAtUtc - startedAtUtc).TotalMilliseconds),
            Method = Truncate(request.Method.Trim().ToUpperInvariant(), 32),
            Url = request.RedactSensitiveData
                ? SanitizeUrl(request.Url)
                : Truncate(request.Url.Trim(), 16_384),
            RedactSensitiveData = request.RedactSensitiveData,
            Protocol = NormalizeOptional(request.Protocol, 64),
            RequestHeaders = SanitizeHeaders(request.RequestHeaders, request.RedactSensitiveData),
            RequestBodySizeBytes = request.RequestBodySizeBytes is >= 0 ? request.RequestBodySizeBytes : null,
            RequestBody = SanitizeBody(request.RequestBody, request.RedactSensitiveData),
            StatusCode = request.StatusCode is >= 100 and <= 999 ? request.StatusCode : null,
            ReasonPhrase = NormalizeOptional(request.ReasonPhrase, 512),
            ResponseHeaders = SanitizeHeaders(request.ResponseHeaders, request.RedactSensitiveData),
            ResponseBodySizeBytes = request.ResponseBodySizeBytes is >= 0 ? request.ResponseBodySizeBytes : null,
            ResponseBody = SanitizeBody(request.ResponseBody, request.RedactSensitiveData),
            ErrorType = NormalizeOptional(request.ErrorType, 512),
            ErrorMessage = SanitizeErrorMessage(request.ErrorMessage, request.RedactSensitiveData)
        };
    }

    private static IReadOnlyList<SessionNetworkHeader> SanitizeHeaders(
        IEnumerable<SessionNetworkHeader>? headers,
        bool redactSensitiveData)
        => headers?
            .Where(header => header is not null && !string.IsNullOrWhiteSpace(header.Name))
            .Take(128)
            .Select(header =>
            {
                var name = Truncate(header.Name.Trim(), 256);
                return new SessionNetworkHeader
                {
                    Name = name,
                    Value = redactSensitiveData && IsSensitiveHeader(name)
                        ? RedactedValue
                        : Truncate(header.Value?.Trim() ?? string.Empty, 4096)
                };
            })
            .ToArray() ?? Array.Empty<SessionNetworkHeader>();

    private static bool IsSensitiveHeader(string name)
    {
        if (sensitiveHeaders.Contains(name))
        {
            return true;
        }

        var compact = name.Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.Contains("token", StringComparison.OrdinalIgnoreCase)
               || compact.Contains("secret", StringComparison.OrdinalIgnoreCase)
               || compact.Contains("apikey", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeUrl(string value)
    {
        var normalized = Truncate(value.Trim(), 16_384);
        if (!Uri.TryCreate(normalized, UriKind.RelativeOrAbsolute, out var uri) || !uri.IsAbsoluteUri)
        {
            return SanitizeRelativeUrl(normalized);
        }

        try
        {
            var builder = new UriBuilder(uri)
            {
                UserName = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : RedactedValue,
                Password = string.Empty,
                Query = SanitizeQuery(uri.Query)
            };
            return Truncate(builder.Uri.AbsoluteUri, 16_384);
        }
        catch
        {
            return normalized;
        }
    }

    private static string SanitizeRelativeUrl(string value)
    {
        var queryIndex = value.IndexOf('?');
        if (queryIndex < 0)
        {
            return value;
        }

        var fragmentIndex = value.IndexOf('#', queryIndex);
        var query = fragmentIndex < 0 ? value[(queryIndex + 1)..] : value[(queryIndex + 1)..fragmentIndex];
        var fragment = fragmentIndex < 0 ? string.Empty : value[fragmentIndex..];
        return Truncate($"{value[..queryIndex]}?{SanitizeQuery(query)}{fragment}", 16_384);
    }

    private static string SanitizeQuery(string query)
    {
        var pairs = query.TrimStart('?').Split('&');
        var names = pairs.Select(GetDecodedQueryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasAzureSas = names.Contains("sig") && names.Overlaps(azureSasFingerprintNames);
        var hasAwsSignature = names.Contains("x-amz-signature");
        var hasGoogleSignature = names.Contains("x-goog-signature");
        var hasCloudFrontSignature = names.Contains("signature")
                                     && (names.Contains("key-pair-id")
                                         || names.Contains("policy")
                                         || names.Contains("expires"));
        var hasLegacyGoogleSignature = names.Contains("signature") && names.Contains("googleaccessid");
        var hasAlibabaSignature = names.Contains("signature") && names.Contains("ossaccesskeyid")
                                  || names.Contains("x-oss-signature");
        return string.Join("&", pairs.Select(pair =>
        {
            var equalsIndex = pair.IndexOf('=');
            var encodedName = equalsIndex < 0 ? pair : pair[..equalsIndex];
            var name = GetDecodedQueryName(pair);
            var providerSensitive = hasAzureSas && azureSasQueryNames.Contains(name)
                                    || hasAwsSignature && name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase)
                                    || hasGoogleSignature && name.StartsWith("x-goog-", StringComparison.OrdinalIgnoreCase)
                                    || hasCloudFrontSignature && cloudFrontQueryNames.Contains(name)
                                    || hasLegacyGoogleSignature && legacyGoogleQueryNames.Contains(name)
                                    || hasAlibabaSignature && (name.StartsWith("x-oss-", StringComparison.OrdinalIgnoreCase)
                                                               || name.Equals("signature", StringComparison.OrdinalIgnoreCase)
                                                               || name.Equals("ossaccesskeyid", StringComparison.OrdinalIgnoreCase)
                                                               || name.Equals("security-token", StringComparison.OrdinalIgnoreCase));
            return providerSensitive || sensitiveQueryNames.Contains(name)
                ? $"{encodedName}={Uri.EscapeDataString(RedactedValue)}"
                : pair;
        }));
    }

    private static string GetDecodedQueryName(string pair)
    {
        var equalsIndex = pair.IndexOf('=');
        var encodedName = equalsIndex < 0 ? pair : pair[..equalsIndex];
        try
        {
            return Uri.UnescapeDataString(encodedName.Replace("+", " "));
        }
        catch
        {
            return encodedName;
        }
    }

    private static SessionNetworkBody? SanitizeBody(
        SessionNetworkBody? body,
        bool redactSensitiveData)
    {
        if (body is null)
        {
            return null;
        }

        var encoding = body.Encoding.Trim().ToLowerInvariant();
        byte[] decoded;
        try
        {
            decoded = encoding switch
            {
                "utf8" => Encoding.UTF8.GetBytes(
                    redactSensitiveData ? SanitizeSensitiveText(body.Data) : body.Data),
                "base64" => Convert.FromBase64String(body.Data),
                _ => Array.Empty<byte>()
            };
        }
        catch
        {
            return null;
        }
        if (encoding is not ("utf8" or "base64"))
        {
            return null;
        }

        var originalLength = decoded.Length;
        if (encoding == "utf8")
        {
            decoded = EnsureCompleteUtf8(decoded);
        }

        var totalBytes = body.TotalBytes is >= 0 ? body.TotalBytes : null;
        return new SessionNetworkBody
        {
            ContentType = NormalizeOptional(body.ContentType, 512),
            Encoding = encoding,
            Data = encoding == "base64" ? Convert.ToBase64String(decoded) : Encoding.UTF8.GetString(decoded),
            CapturedBytes = decoded.Length,
            TotalBytes = totalBytes,
            Truncated = body.Truncated
                        || originalLength > decoded.Length
                        || totalBytes is not null && totalBytes.Value > decoded.Length
        };
    }

    private static string SanitizeSensitiveText(string value)
    {
        var assignments = sensitiveAssignmentPattern.Replace(
            value,
            match => $"{match.Groups["name"].Value}{match.Groups["separator"].Value}{RedactedValue}");
        return absoluteUrlPattern.Replace(assignments, match => SanitizeUrl(match.Value));
    }

    private static byte[] EnsureCompleteUtf8(byte[] bytes)
    {
        var length = bytes.Length;
        while (length > 0)
        {
            try
            {
                _ = strictUtf8Encoding.GetString(bytes, 0, length);
                return length == bytes.Length ? bytes : bytes[..length];
            }
            catch (DecoderFallbackException)
            {
                length--;
            }
        }
        return Array.Empty<byte>();
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
        => string.IsNullOrWhiteSpace(value) ? null : Truncate(value.Trim(), maximumLength);

    private static string? SanitizeErrorMessage(string? value, bool redactSensitiveData)
    {
        var normalized = NormalizeOptional(value, 4096);
        if (normalized is null)
        {
            return null;
        }
        if (!redactSensitiveData)
        {
            return normalized;
        }

        var assignmentsRedacted = sensitiveAssignmentPattern.Replace(
            normalized,
            match => $"{match.Groups["name"].Value}{match.Groups["separator"].Value}{RedactedValue}");
        var urlsRedacted = absoluteUrlPattern.Replace(
            assignmentsRedacted,
            match => SanitizeUrl(match.Value));
        return Truncate(urlsRedacted, 4096);
    }

    private static string Truncate(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength] + "…";
}
