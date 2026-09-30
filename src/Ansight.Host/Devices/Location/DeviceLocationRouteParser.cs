using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Devices.Location;

public static class DeviceLocationRouteParser
{
    private const int MaximumRoutePointCount = 100_000;
    private const long MaximumXmlCharacterCount = 25_000_000;

    public static DeviceLocationRoute Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = File.OpenRead(filePath);
        return Parse(Path.GetFileName(filePath), stream);
    }

    public static DeviceLocationRoute Parse(string sourceFileName, Stream stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);
        ArgumentNullException.ThrowIfNull(stream);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlCharacterCount,
            IgnoreComments = true
        };
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var rootName = document.Root?.Name.LocalName;
        var points = rootName switch
        {
            "gpx" => ParseGpx(document),
            "kml" => ParseKml(document),
            _ => throw new InvalidDataException("Choose a valid GPX or KML route file.")
        };

        if (points.Count < 2)
        {
            throw new InvalidDataException("The route must contain at least two valid locations.");
        }

        var distanceMeters = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            distanceMeters += DeviceLocationMath.DistanceMeters(points[index - 1], points[index]);
        }

        var hasRecordedTiming = HasStrictlyIncreasingTimestamps(points);
        TimeSpan? recordedDuration = hasRecordedTiming
            ? points[^1].Timestamp!.Value - points[0].Timestamp!.Value
            : null;
        return new DeviceLocationRoute(
            sourceFileName,
            points,
            distanceMeters,
            recordedDuration,
            hasRecordedTiming);
    }

    private static IReadOnlyList<DeviceLocationPoint> ParseGpx(XDocument document)
    {
        var pointElements = document
            .Descendants()
            .Where(element => element.Name.LocalName == "trkpt")
            .ToArray();
        if (pointElements.Length == 0)
        {
            pointElements = document
                .Descendants()
                .Where(element => element.Name.LocalName == "rtept")
                .ToArray();
        }
        if (pointElements.Length == 0)
        {
            pointElements = document
                .Descendants()
                .Where(element => element.Name.LocalName == "wpt")
                .ToArray();
        }

        EnsurePointLimit(pointElements.Length);
        return pointElements.Select(ParseGpxPoint).ToArray();
    }

    private static DeviceLocationPoint ParseGpxPoint(XElement element)
    {
        var latitude = ParseCoordinateAttribute(element, "lat", -90d, 90d);
        var longitude = ParseCoordinateAttribute(element, "lon", -180d, 180d);
        var altitude = ParseOptionalDouble(element.Elements().FirstOrDefault(child => child.Name.LocalName == "ele")?.Value);
        var timestamp = ParseOptionalTimestamp(element.Elements().FirstOrDefault(child => child.Name.LocalName == "time")?.Value);
        return new DeviceLocationPoint(latitude, longitude, altitude, timestamp);
    }

    private static IReadOnlyList<DeviceLocationPoint> ParseKml(XDocument document)
    {
        var tracks = document.Descendants().Where(element => element.Name.LocalName == "Track").ToArray();
        if (tracks.Length > 0)
        {
            var trackPoints = new List<DeviceLocationPoint>();
            foreach (var track in tracks)
            {
                var coordinateValues = track.Elements()
                    .Where(element => element.Name.LocalName == "coord")
                    .Select(element => element.Value)
                    .ToArray();
                var timestamps = track.Elements()
                    .Where(element => element.Name.LocalName == "when")
                    .Select(element => ParseOptionalTimestamp(element.Value))
                    .ToArray();
                for (var index = 0; index < coordinateValues.Length; index++)
                {
                    var timestamp = index < timestamps.Length ? timestamps[index] : null;
                    trackPoints.Add(ParseKmlCoordinate(coordinateValues[index], timestamp, splitOnWhitespace: true));
                    EnsurePointLimit(trackPoints.Count);
                }
            }
            return trackPoints;
        }

        var values = document
            .Descendants()
            .Where(element => element.Name.LocalName == "LineString")
            .SelectMany(line => line.Descendants().Where(element => element.Name.LocalName == "coordinates"))
            .SelectMany(element => element.Value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        EnsurePointLimit(values.Length);
        return values.Select(value => ParseKmlCoordinate(value, null, splitOnWhitespace: false)).ToArray();
    }

    private static DeviceLocationPoint ParseKmlCoordinate(
        string value,
        DateTimeOffset? timestamp,
        bool splitOnWhitespace)
    {
        var components = value.Split(
            splitOnWhitespace ? (char[]?)null : [','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (components.Length < 2
            || !TryParseCoordinate(components[0], -180d, 180d, out var longitude)
            || !TryParseCoordinate(components[1], -90d, 90d, out var latitude))
        {
            throw new InvalidDataException("The KML route contains an invalid coordinate.");
        }

        var altitude = components.Length > 2 ? ParseOptionalDouble(components[2]) : null;
        return new DeviceLocationPoint(latitude, longitude, altitude, timestamp);
    }

    private static double ParseCoordinateAttribute(
        XElement element,
        string attributeName,
        double minimum,
        double maximum)
    {
        var value = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == attributeName)?.Value;
        if (!TryParseCoordinate(value, minimum, maximum, out var coordinate))
        {
            throw new InvalidDataException($"The GPX route contains an invalid {attributeName} coordinate.");
        }
        return coordinate;
    }

    private static bool TryParseCoordinate(
        string? value,
        double minimum,
        double maximum,
        out double coordinate)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate)
           && double.IsFinite(coordinate)
           && coordinate >= minimum
           && coordinate <= maximum;

    private static double? ParseOptionalDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
           && double.IsFinite(result)
            ? result
            : null;

    private static DateTimeOffset? ParseOptionalTimestamp(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var result)
            ? result
            : null;

    private static bool HasStrictlyIncreasingTimestamps(IReadOnlyList<DeviceLocationPoint> points)
    {
        for (var index = 0; index < points.Count; index++)
        {
            if (points[index].Timestamp is null
                || (index > 0 && points[index].Timestamp <= points[index - 1].Timestamp))
            {
                return false;
            }
        }
        return true;
    }

    private static void EnsurePointLimit(int count)
    {
        if (count > MaximumRoutePointCount)
        {
            throw new InvalidDataException($"Routes are limited to {MaximumRoutePointCount:N0} locations.");
        }
    }
}
