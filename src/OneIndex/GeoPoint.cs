using System.Diagnostics.CodeAnalysis;

namespace OneIndex;

/// <summary>A place on Earth: latitude and longitude in degrees. Mark a property of this type <see cref="GeoAttribute"/> to search and sort by it.</summary>
public readonly record struct GeoPoint
{
    /// <summary>Creates a point.</summary>
    /// <param name="latitude">Degrees north, -90 to 90.</param>
    /// <param name="longitude">Degrees east, -180 to 180.</param>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside its range or not a number.</exception>
    public GeoPoint(double latitude, double longitude)
    {
        if (!(latitude >= -90 && latitude <= 90))
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be between -90 and 90 degrees.");
        }

        if (!(longitude >= -180 && longitude <= 180))
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be between -180 and 180 degrees.");
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    /// <summary>Degrees north.</summary>
    public double Latitude { get; }

    /// <summary>Degrees east.</summary>
    public double Longitude { get; }

    /// <summary>The great-circle distance to another point in metres (see <see cref="Search.GeoMath"/>).</summary>
    /// <param name="other">The other point.</param>
    /// <returns>The distance.</returns>
    public double DistanceMetersTo(GeoPoint other) => Search.GeoMath.HaversineMeters(Latitude, Longitude, other.Latitude, other.Longitude);

    /// <inheritdoc/>
    [ExcludeFromCodeCoverage]
    public override string ToString() => FormattableString.Invariant($"{Latitude},{Longitude}");
}

/// <summary>
/// Geo conditions for <c>Where</c> predicates. The methods are never run: the predicate is translated into a query on the property they
/// read, so they combine with <c>&amp;&amp;</c>, <c>||</c> and <c>!</c> like any other condition. The property must be marked <see cref="GeoAttribute"/>.
/// </summary>
public static class Geo
{
    /// <summary>Whether a point lies within a distance of a centre.</summary>
    /// <param name="point">A <see cref="GeoAttribute"/> property of the document.</param>
    /// <param name="latitude">Centre latitude in degrees.</param>
    /// <param name="longitude">Centre longitude in degrees.</param>
    /// <param name="radiusMeters">The radius in metres; a point exactly that far is inside.</param>
    /// <returns>Never returns: this is translated, not called.</returns>
    /// <exception cref="NotSupportedException">Always, when called outside a predicate passed to <c>Where</c>.</exception>
    public static bool WithinRadius(GeoPoint point, double latitude, double longitude, double radiusMeters)
        => throw new NotSupportedException("Geo.WithinRadius only works inside a predicate passed to Where; it is translated, not run.");

    /// <summary>Whether a point lies in a latitude and longitude rectangle, edges included. A west edge greater than the east edge means the rectangle crosses the antimeridian.</summary>
    /// <param name="point">A <see cref="GeoAttribute"/> property of the document.</param>
    /// <param name="north">Northern edge in degrees.</param>
    /// <param name="south">Southern edge in degrees.</param>
    /// <param name="west">Western edge in degrees.</param>
    /// <param name="east">Eastern edge in degrees.</param>
    /// <returns>Never returns: this is translated, not called.</returns>
    /// <exception cref="NotSupportedException">Always, when called outside a predicate passed to <c>Where</c>.</exception>
    public static bool WithinBoundingBox(GeoPoint point, double north, double south, double west, double east)
        => throw new NotSupportedException("Geo.WithinBoundingBox only works inside a predicate passed to Where; it is translated, not run.");
}
