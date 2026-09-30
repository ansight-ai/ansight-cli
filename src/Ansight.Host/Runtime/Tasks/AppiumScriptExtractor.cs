using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>A reviewable WebdriverIO/Appium test from recorded session interactions.</summary>
public sealed record AppiumScriptExtraction(
    string SuggestedName,
    string Source,
    int GeneratedActionCount,
    IReadOnlyList<string> Diagnostics);

public static class AppiumScriptExtractor
{
    public static AppiumScriptExtraction Extract(
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string title)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var extracted = TimelineTaskExtractor.Extract(
            snapshot, startUtc, endUtc, title, includeCoordinateFallbackTaps: true);
        var diagnostics = new List<string>(extracted.Diagnostics);
        var platform = snapshot.DeviceProfile?.Device?.OsName?.Contains("android", StringComparison.OrdinalIgnoreCase) == true
            ? "Android"
            : snapshot.DeviceProfile?.Device?.OsName?.Contains("ios", StringComparison.OrdinalIgnoreCase) == true
                ? "iOS"
                : null;
        if (platform is null)
        {
            diagnostics.Add("Capture platform is unknown; set APPIUM_PLATFORM to iOS or Android before running.");
        }

        var actions = new StringBuilder();
        var count = 0;
        foreach (var action in extracted.Actions)
        {
            var rendered = RenderAction(action, diagnostics);
            if (rendered is null) continue;
            actions.Append(rendered);
            count++;
        }

        if (count == 0) diagnostics.Add("No Appium actions could be generated from the selected period.");
        var source = new StringBuilder()
            .Append("// Draft from Ansight session ").AppendLine(Comment(snapshot.SessionId))
            .Append("// Selected period: ").Append(startUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            .Append(" to ").AppendLine(endUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            .Append("// ").AppendLine(Comment(title))
            .AppendLine("import { test } from 'node:test';")
            .AppendLine("import { remote } from 'webdriverio';")
            .AppendLine()
            .Append("const appId = ").Append(Json(snapshot.AppId)).AppendLine(";")
            .Append("const platformName = process.env.APPIUM_PLATFORM || ").Append(Json(platform ?? "iOS")).AppendLine(";")
            .AppendLine("const serverUrl = new URL(process.env.APPIUM_SERVER_URL || 'http://127.0.0.1:4723/');")
            .AppendLine()
            .AppendLine("function xpathLiteral(value) {")
            .AppendLine("  const singleQuote = String.fromCharCode(39);")
            .AppendLine("  const doubleQuote = String.fromCharCode(34);")
            .AppendLine("  if (!value.includes(singleQuote)) return singleQuote + value + singleQuote;")
            .AppendLine("  if (!value.includes(doubleQuote)) return doubleQuote + value + doubleQuote;")
            .AppendLine("  return 'concat(' + value.split(singleQuote).map(part => singleQuote + part + singleQuote).join(', ' + doubleQuote + singleQuote + doubleQuote + ', ') + ')';")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("async function recordedTarget(driver, id, text) {")
            .AppendLine("  if (id) {")
            .AppendLine("    for (const selector of ['~' + id, 'id=' + id]) {")
            .AppendLine("      const element = await driver.$(selector);")
            .AppendLine("      if (await element.isExisting()) return element;")
            .AppendLine("    }")
            .AppendLine("  }")
            .AppendLine("  if (text) {")
            .AppendLine("    const value = xpathLiteral(text);")
            .AppendLine("    return driver.$('//*[@text=' + value + ' or @label=' + value + ' or @name=' + value + ']');")
            .AppendLine("  }")
            .AppendLine("  throw new Error('Recorded target has no usable locator: ' + id);")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("async function touchAt(driver, startX, startY, endX = startX, endY = startY, duration = 100) {")
            .AppendLine("  const { width, height } = await driver.getWindowRect();")
            .AppendLine("  const point = (x, y) => ({ x: Math.round(width * x), y: Math.round(height * y) });")
            .AppendLine("  await driver.action('pointer', { parameters: { pointerType: 'touch' } })")
            .AppendLine("    .move({ origin: 'viewport', duration: 0, ...point(startX, startY) })")
            .AppendLine("    .down()")
            .AppendLine("    .move({ origin: 'viewport', duration, ...point(endX, endY) })")
            .AppendLine("    .up()")
            .AppendLine("    .perform();")
            .AppendLine("}")
            .AppendLine()
            .Append("test(").Append(Json(title)).AppendLine(", async () => {")
            .AppendLine("  const android = platformName.toLowerCase() === 'android';")
            .AppendLine("  const capabilities = {")
            .AppendLine("    platformName,")
            .AppendLine("    'appium:automationName': android ? 'UiAutomator2' : 'XCUITest',")
            .AppendLine("    ...(android ? { 'appium:appPackage': appId } : { 'appium:bundleId': appId }),")
            .AppendLine("    ...(process.env.APPIUM_APP_ACTIVITY ? { 'appium:appActivity': process.env.APPIUM_APP_ACTIVITY } : {}),")
            .AppendLine("    ...(process.env.APPIUM_UDID ? { 'appium:udid': process.env.APPIUM_UDID } : {}),")
            .AppendLine("    'appium:noReset': true,")
            .AppendLine("  };")
            .AppendLine("  const driver = await remote({ protocol: serverUrl.protocol.slice(0, -1), hostname: serverUrl.hostname, port: Number(serverUrl.port || (serverUrl.protocol === 'https:' ? 443 : 80)), path: serverUrl.pathname, capabilities, logLevel: 'error' });")
            .AppendLine("  try {")
            .Append(actions)
            .Append(count == 0 ? "    throw new Error('No recorded Appium actions were generated for this range.');\n" : string.Empty)
            .AppendLine("  } finally {")
            .AppendLine("    await driver.deleteSession();")
            .AppendLine("  }")
            .AppendLine("});")
            .AppendLine()
            .AppendLine("// Review before running:")
            .AppendLine("// - Confirm the starting state and add an outcome assertion.")
            .AppendLine("// - Install WebdriverIO and start Appium with the matching driver.");
        if (diagnostics.Any(static diagnostic => diagnostic.Contains("viewport coordinate", StringComparison.Ordinal)))
        {
            source.AppendLine("// - Verify coordinate taps on the target device.");
        }

        if (extracted.Actions.Count > count)
        {
            source.AppendLine("// - Review interactions that could not be converted.");
        }

        return new AppiumScriptExtraction(extracted.SuggestedName, source.ToString(), count, diagnostics);
    }

    private static string? RenderAction(TimelineExtractedAction action, ICollection<string> diagnostics)
    {
        if (action.Kind == "swipe" && action.StartNormalizedX is { } sx && action.StartNormalizedY is { } sy
            && action.EndNormalizedX is { } ex && action.EndNormalizedY is { } ey)
        {
            return $"    await touchAt(driver, {Number(sx)}, {Number(sy)}, {Number(ex)}, {Number(ey)}, {Math.Clamp(action.DurationMilliseconds, 50, 2_000)});\n";
        }

        if (action.Kind is not ("tap" or "input"))
        {
            diagnostics.Add($"Unsupported action {action.Kind} at {action.CapturedAtUtc:O} was omitted.");
            return null;
        }

        var id = ReadString(action.Selector, "automationId");
        var label = ReadString(action.Selector, "text");
        if (id is not null || label is not null)
        {
            var target = $"recordedTarget(driver, {Json(id)}, {Json(label)})";
            return action.Kind == "tap"
                ? $"    await (await {target}).click();\n"
                : $"    await (await {target}).setValue({Json(action.InputValue ?? string.Empty)});\n";
        }

        if (action.Kind == "tap" && action.StartNormalizedX is { } x && action.StartNormalizedY is { } y)
        {
            diagnostics.Add($"Tap at {action.CapturedAtUtc:O} uses a viewport coordinate; confirm it on the target device.");
            return $"    await touchAt(driver, {Number(x)}, {Number(y)});\n";
        }

        diagnostics.Add($"{action.Description} at {action.CapturedAtUtc:O} has no Appium locator and was omitted.");
        return null;
    }

    private static string? ReadString(JsonObject? selector, string key)
        => selector?[key] is JsonValue node && node.TryGetValue<string>(out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string Number(double value) => Math.Clamp(value, 0, 1).ToString("0.######", CultureInfo.InvariantCulture);
    private static string Json(string? value) => value is null ? "null" : JsonSerializer.Serialize(value);
    private static string Comment(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
