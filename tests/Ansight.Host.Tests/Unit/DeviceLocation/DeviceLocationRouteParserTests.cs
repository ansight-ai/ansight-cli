using System.Text;
using System.Xml;

namespace Ansight.Host.Tests.Unit.DeviceLocation;

public sealed class DeviceLocationRouteParserTests
{
    [Fact]
    public void Parse_GpxTrack_PreservesRecordedTimingAndCalculatesDistance()
    {
        const string gpx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <trk><trkseg>
                <trkpt lat="-33.8688" lon="151.2093">
                  <ele>10.5</ele><time>2026-08-10T01:00:00Z</time>
                </trkpt>
                <trkpt lat="-33.8698" lon="151.2103">
                  <ele>12</ele><time>2026-08-10T01:00:10Z</time>
                </trkpt>
              </trkseg></trk>
            </gpx>
            """;

        var route = Parse("walk.gpx", gpx);

        Assert.Equal("walk.gpx", route.SourceFileName);
        Assert.Equal(2, route.Points.Count);
        Assert.Equal(-33.8688d, route.Points[0].Latitude, 4);
        Assert.Equal(151.2093d, route.Points[0].Longitude, 4);
        Assert.Equal(10.5d, route.Points[0].AltitudeMeters);
        Assert.True(route.HasRecordedTiming);
        Assert.Equal(TimeSpan.FromSeconds(10), route.RecordedDuration);
        Assert.InRange(route.DistanceMeters, 140d, 150d);
    }

    [Fact]
    public void Parse_KmlLineString_UsesFixedSpeedFallback()
    {
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Document><Placemark><LineString>
                <coordinates>151.2093,-33.8688,10 151.2103,-33.8698,12</coordinates>
              </LineString></Placemark></Document>
            </kml>
            """;

        var route = Parse("route.kml", kml);

        Assert.Equal(2, route.Points.Count);
        Assert.False(route.HasRecordedTiming);
        Assert.Null(route.RecordedDuration);
        Assert.Equal(-33.8698d, route.Points[1].Latitude, 4);
        Assert.Equal(151.2103d, route.Points[1].Longitude, 4);
    }

    [Fact]
    public void Parse_KmlGxTrack_AlignsTimestampsWithCoordinates()
    {
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2" xmlns:gx="http://www.google.com/kml/ext/2.2">
              <Placemark><gx:Track>
                <when>2026-08-10T01:00:00Z</when>
                <when>2026-08-10T01:00:05Z</when>
                <gx:coord>151.2093 -33.8688 10</gx:coord>
                <gx:coord>151.2103 -33.8698 12</gx:coord>
              </gx:Track></Placemark>
            </kml>
            """;

        var route = Parse("track.kml", kml);

        Assert.True(route.HasRecordedTiming);
        Assert.Equal(TimeSpan.FromSeconds(5), route.RecordedDuration);
        Assert.Equal(12d, route.Points[1].AltitudeMeters);
    }

    [Fact]
    public void Parse_RejectsXmlDocumentTypes()
    {
        const string gpx = """
            <!DOCTYPE gpx [<!ENTITY example "151.2093">]>
            <gpx><trk><trkseg>
              <trkpt lat="-33.8688" lon="&example;" />
              <trkpt lat="-33.8698" lon="151.2103" />
            </trkseg></trk></gpx>
            """;

        Assert.Throws<XmlException>(() => Parse("unsafe.gpx", gpx));
    }

    private static DeviceLocationRoute Parse(string fileName, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return DeviceLocationRouteParser.Parse(fileName, stream);
    }
}
