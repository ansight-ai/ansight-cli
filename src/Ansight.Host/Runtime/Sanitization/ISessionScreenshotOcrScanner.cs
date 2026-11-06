using System.Diagnostics;
using System.Globalization;

namespace Ansight.Host.Runtime.Sanitization;

internal interface ISessionScreenshotOcrScanner
{
    SessionScreenshotOcrResult Scan(string imageFilePath);
}
