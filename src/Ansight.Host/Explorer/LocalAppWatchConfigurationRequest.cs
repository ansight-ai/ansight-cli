using System.Net;

namespace Ansight.Host.Explorer;

internal sealed record LocalAppWatchConfigurationRequest(int? ScreenshotIntervalMilliseconds);
