namespace Ansight.Host.Devices.Location;

public static class DeviceLocationMath
{
    private const double EarthRadiusMeters = 6_371_000d;

    public static double DistanceMeters(DeviceLocationPoint start, DeviceLocationPoint end)
    {
        var startLatitude = DegreesToRadians(start.Latitude);
        var endLatitude = DegreesToRadians(end.Latitude);
        var latitudeDelta = endLatitude - startLatitude;
        var longitudeDelta = DegreesToRadians(end.Longitude - start.Longitude);
        var haversine = Math.Pow(Math.Sin(latitudeDelta / 2d), 2d)
                        + Math.Cos(startLatitude)
                        * Math.Cos(endLatitude)
                        * Math.Pow(Math.Sin(longitudeDelta / 2d), 2d);
        return EarthRadiusMeters * 2d * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1d - haversine));
    }

    public static DeviceLocationPoint Interpolate(
        DeviceLocationPoint start,
        DeviceLocationPoint end,
        double progress)
    {
        progress = Math.Clamp(progress, 0d, 1d);
        return new DeviceLocationPoint(
            start.Latitude + ((end.Latitude - start.Latitude) * progress),
            start.Longitude + ((end.Longitude - start.Longitude) * progress),
            InterpolateNullable(start.AltitudeMeters, end.AltitudeMeters, progress));
    }

    private static double? InterpolateNullable(double? start, double? end, double progress)
    {
        if (start is null && end is null)
        {
            return null;
        }

        var resolvedStart = start ?? end!.Value;
        var resolvedEnd = end ?? resolvedStart;
        return resolvedStart + ((resolvedEnd - resolvedStart) * progress);
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180d;
}
