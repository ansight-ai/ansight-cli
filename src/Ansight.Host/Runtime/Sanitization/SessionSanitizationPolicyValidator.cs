namespace Ansight.Host.Runtime.Sanitization;

internal static class SessionSanitizationPolicyValidator
{
    public static void Validate(SessionSanitizationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var source = $"Sanitizer '{policy.Id}'";
        if (policy.SchemaVersion != SessionSanitizationPolicy.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"{source} schemaVersion must be {SessionSanitizationPolicy.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(policy.Id))
        {
            throw new InvalidDataException($"{source} must define a non-empty id.");
        }

        if (string.IsNullOrEmpty(policy.Replacement))
        {
            throw new InvalidDataException($"{source} must define a non-empty replacement.");
        }

        if (policy.Detectors is null
            || policy.SensitiveProperties is null
            || policy.Rules is null
            || policy.Screenshots is null
            || policy.Artifacts is null)
        {
            throw new InvalidDataException(
                $"{source} detectors, sensitiveProperties, rules, screenshots, and artifacts cannot be null.");
        }

        if (!Enum.IsDefined(policy.Screenshots.Mode)
            || !Enum.IsDefined(policy.Screenshots.Fallback)
            || !Enum.IsDefined(policy.Artifacts.Mode))
        {
            throw new InvalidDataException($"{source} contains an unsupported screenshot or artifact mode.");
        }

        if (policy.Screenshots.PaddingPixels is < 0 or > 256)
        {
            throw new InvalidDataException($"{source} screenshots.paddingPixels must be between 0 and 256.");
        }

        if (policy.Artifacts.MaximumTextBytes is < 1 or > 64 * 1024 * 1024)
        {
            throw new InvalidDataException(
                $"{source} artifacts.maximumTextBytes must be between 1 byte and 64 MiB.");
        }

        var supportedDetectors = new HashSet<string>(
            ["email", "phone", "ipAddress", "creditCard", "credential"],
            StringComparer.OrdinalIgnoreCase);
        if (policy.Detectors.Any(string.IsNullOrWhiteSpace)
            || policy.SensitiveProperties.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                $"{source} detectors and sensitiveProperties cannot contain empty values.");
        }

        var unknownDetector = policy.Detectors.FirstOrDefault(detector => !supportedDetectors.Contains(detector));
        if (unknownDetector is not null)
        {
            throw new InvalidDataException($"{source} uses unsupported detector '{unknownDetector}'.");
        }

        var duplicateRule = policy.Rules
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.Id))
            .GroupBy(static rule => rule.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateRule is not null)
        {
            throw new InvalidDataException($"{source} contains duplicate rule id '{duplicateRule.Key}'.");
        }

        foreach (var rule in policy.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Pattern))
            {
                throw new InvalidDataException($"{source} rules require non-empty id and pattern values.");
            }

            try
            {
                _ = new System.Text.RegularExpressions.Regex(
                    rule.Pattern,
                    rule.IgnoreCase
                        ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                          | System.Text.RegularExpressions.RegexOptions.CultureInvariant
                        : System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"{source} rule '{rule.Id}' has an invalid regex: {exception.Message}",
                    exception);
            }
        }
    }
}
