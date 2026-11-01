namespace OneIndex.Search;

/// <summary>
/// Great-circle distances and the bounding boxes that bound them, on a spherical Earth. Distances use the haversine formula
/// (Sinnott, "Virtues of the Haversine", Sky and Telescope 68(2), 1984), which stays accurate for short distances where the spherical
/// law of cosines loses digits. The sphere's radius is the IUGG mean Earth radius, 6,371,008.8 m, so a distance is within about 0.5%
/// of the ellipsoidal one. Latitudes are degrees north in [-90, 90]; longitudes are degrees east in [-180, 180].
/// </summary>
public static class GeoMath
{
    /// <summary>The Earth's mean radius in metres (IUGG): the radius of the sphere every distance here is measured on.</summary>
    public const double EarthRadiusMeters = 6_371_008.8;

    private const double DegToRad = Math.PI / 180.0;
    private const double BoxMargin = 1e-7;

    /// <summary>The great-circle distance between two points.</summary>
    /// <param name="latitude1">Latitude of the first point, degrees.</param>
    /// <param name="longitude1">Longitude of the first point, degrees.</param>
    /// <param name="latitude2">Latitude of the second point, degrees.</param>
    /// <param name="longitude2">Longitude of the second point, degrees.</param>
    /// <returns>The distance in metres, 0 to about 20,015,087 (half the circumference).</returns>
    public static double HaversineMeters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        double p1 = latitude1 * DegToRad;
        double p2 = latitude2 * DegToRad;
        double sdp = Math.Sin((p2 - p1) * 0.5);
        double sdl = Math.Sin((longitude2 - longitude1) * DegToRad * 0.5);
        double a = (sdp * sdp) + (Math.Cos(p1) * Math.Cos(p2) * sdl * sdl);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>Whether a longitude lies in the east-going span from <paramref name="west"/> to <paramref name="east"/>; the span crosses the antimeridian when <paramref name="west"/> is greater than <paramref name="east"/>.</summary>
    /// <param name="longitude">The longitude to test.</param>
    /// <param name="west">The western edge.</param>
    /// <param name="east">The eastern edge.</param>
    /// <returns><see langword="true"/> when inside, edges included.</returns>
    public static bool LongitudeInSpan(double longitude, double west, double east)
        => west <= east ? longitude >= west && longitude <= east : longitude >= west || longitude <= east;

    /// <summary>
    /// The latitude and longitude ranges that contain every point within a radius of a centre (Matuschek, "Finding Points Within a
    /// Distance of a Latitude/Longitude Using Bounding Coordinates"): the points of the circle's latitude band, widened in longitude by
    /// asin(sin(r/R)/cos(latitude)). A circle that reaches a pole, or whose longitude half-width passes 90 degrees of arc, spans every longitude.
    /// </summary>
    /// <param name="latitude">Centre latitude.</param>
    /// <param name="longitude">Centre longitude.</param>
    /// <param name="radiusMeters">The radius, 0 or more.</param>
    /// <param name="south">The southern edge.</param>
    /// <param name="north">The northern edge.</param>
    /// <param name="west">The western edge; greater than <paramref name="east"/> when the box crosses the antimeridian.</param>
    /// <param name="east">The eastern edge.</param>
    public static void BoundingBox(double latitude, double longitude, double radiusMeters, out double south, out double north, out double west, out double east)
    {
        double angular = radiusMeters / EarthRadiusMeters;
        if (angular >= Math.PI)
        {
            (south, north, west, east) = (-90, 90, -180, 180);
            return;
        }

        // The box is only a prefilter, and floating-point rounding could put a point exactly on the circle a hair outside it, so widen it by about a centimetre.
        double dLat = (angular / DegToRad) + BoxMargin;
        south = Math.Max(-90, latitude - dLat);
        north = Math.Min(90, latitude + dLat);
        if (latitude - dLat <= -90 || latitude + dLat >= 90)
        {
            // The circle contains a pole, so every longitude is within reach.
            (west, east) = (-180, 180);
            return;
        }

        double sinAngular = Math.Sin(angular);
        double cosLat = Math.Cos(latitude * DegToRad);
        if (sinAngular >= cosLat)
        {
            (west, east) = (-180, 180);
            return;
        }

        double dLon = (Math.Asin(sinAngular / cosLat) / DegToRad) + BoxMargin;
        if (dLon >= 180)
        {
            (west, east) = (-180, 180);
            return;
        }

        west = longitude - dLon;
        east = longitude + dLon;
        if (west < -180)
        {
            west += 360;
        }

        if (east > 180)
        {
            east -= 360;
        }
    }
}
