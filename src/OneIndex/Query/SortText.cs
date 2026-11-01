using System.Globalization;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Queries;

/// <summary>
/// The text form of a sort key in query JSON, after Meilisearch's documented sort parameter: <c>Field:asc</c>, <c>Field:desc</c>,
/// <c>_geoPoint(lat, lng):asc</c>. Extensions: <c>_score</c> sorts by relevance (descending unless told otherwise), a geo point may name
/// its field first (<c>_geoPoint(Location, lat, lng)</c>), and a trailing <c>:first</c> or <c>:last</c> says where documents without a value go.
/// </summary>
internal static class SortText
{
    public static string Write(SortCriterion key)
    {
        var sb = new StringBuilder();
        switch (key)
        {
            case ScoreSort:
                sb.Append("_score");
                break;
            case FieldSort f:
                if (f.Field.Contains(':', StringComparison.Ordinal) || f.Field.StartsWith('_') || f.Field.Contains('(', StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Field '{f.Field}' cannot be written in a sort string: its name has a colon, a parenthesis or a leading underscore.");
                }

                sb.Append(f.Field);
                break;
            case GeoDistanceSort g:
                sb.Append("_geoPoint(").Append(g.Field).Append(", ").Append(g.Latitude.ToString("R", CultureInfo.InvariantCulture)).Append(", ").Append(g.Longitude.ToString("R", CultureInfo.InvariantCulture)).Append(')');
                break;
            default:
                throw new ArgumentException($"A {key.GetType().Name} cannot be written as query JSON.");
        }

        sb.Append(key.Direction == SortDirection.Ascending ? ":asc" : ":desc");
        if (key.Missing == MissingValuePlacement.First)
        {
            sb.Append(":first");
        }

        return sb.ToString();
    }

    public static SortCriterion Read(string text, Schema schema, string path)
    {
        string rest = text.Trim();
        SortDirection? direction = null;
        MissingValuePlacement? missing = null;
        while (true)
        {
            int colon = rest.LastIndexOf(':');
            if (colon < 0)
            {
                break;
            }

            string tail = rest[(colon + 1)..].Trim().ToLowerInvariant();
            if (tail is "asc" or "desc")
            {
                if (direction is not null)
                {
                    throw Error(path, "The sort key gives its direction twice.");
                }

                direction = tail == "asc" ? SortDirection.Ascending : SortDirection.Descending;
            }
            else if (tail is "first" or "last")
            {
                if (missing is not null)
                {
                    throw Error(path, "The sort key says where missing values go twice.");
                }

                missing = tail == "first" ? MissingValuePlacement.First : MissingValuePlacement.Last;
            }
            else
            {
                break;
            }

            rest = rest[..colon];
        }

        rest = rest.Trim();
        if (rest.Length == 0)
        {
            throw Error(path, "The sort key is empty. Write a field name, optionally with :asc or :desc.");
        }

        if (rest == "_score")
        {
            return new ScoreSort { Direction = direction ?? SortDirection.Descending, Missing = missing ?? MissingValuePlacement.Last };
        }

        if (rest.StartsWith("_geoPoint(", StringComparison.Ordinal) && rest.EndsWith(')'))
        {
            string[] parts = rest[10..^1].Split(',');
            string? field = null;
            if (parts.Length == 3)
            {
                field = parts[0].Trim();
                parts = parts[1..];
            }

            if (parts.Length != 2 || !TryNumber(parts[0], -90, 90, out double lat) || !TryNumber(parts[1], -180, 180, out double lng))
            {
                throw Error(path, "_geoPoint takes a latitude (-90 to 90) and a longitude (-180 to 180), optionally after the geo field name: _geoPoint(48.85, 2.29).");
            }

            string name = field is null ? OnlyGeoField(schema, path) : GeoFieldOrThrow(schema, field, path);
            return new GeoDistanceSort(name, lat, lng) { Direction = direction ?? SortDirection.Ascending, Missing = missing ?? MissingValuePlacement.Last };
        }

        if (!schema.TryGetField(rest, out FieldDefinition? def))
        {
            string? near = QuerySyntax.Nearest(rest, schema.Fields.Where(static f => f.Column && f.Type != FieldType.GeoPoint).Select(static f => f.Name));
            throw Error(path, $"The schema has no field \"{rest}\".{(near is null ? string.Empty : $" Did you mean \"{near}\"?")}", near);
        }

        if (!def.Column || def.Type is FieldType.Text or FieldType.Bytes or FieldType.Vector or FieldType.Json)
        {
            throw Error(path, $"Field \"{rest}\" cannot be sorted by: {(def.Type == FieldType.GeoPoint ? "use _geoPoint(lat, lng)" : def.Column ? "it is " + def.Type : "it keeps no column; mark it [Sortable]")}.");
        }

        if (def.Type == FieldType.GeoPoint)
        {
            throw Error(path, $"Field \"{rest}\" is a geo point; sort by _geoPoint(lat, lng).");
        }

        return new FieldSort(def.Name) { Direction = direction ?? SortDirection.Ascending, Missing = missing ?? MissingValuePlacement.Last };
    }

    public static string GeoFieldOrThrow(Schema schema, string name, string path)
    {
        if (!schema.TryGetField(name, out FieldDefinition? def))
        {
            string? near = QuerySyntax.Nearest(name, schema.Fields.Where(static f => f.Type == FieldType.GeoPoint).Select(static f => f.Name));
            throw Error(path, $"The schema has no field \"{name}\".{(near is null ? string.Empty : $" Did you mean \"{near}\"?")}", near);
        }

        if (def.Type != FieldType.GeoPoint)
        {
            throw Error(path, $"Field \"{name}\" is {def.Type}, not a geo point.");
        }

        return def.Indexed && def.Column ? def.Name : throw Error(path, $"Geo field \"{name}\" cannot be searched: mark it [Geo].");
    }

    private static string OnlyGeoField(Schema schema, string path)
    {
        string[] fields = [.. schema.Fields.Where(static f => f.Type == FieldType.GeoPoint && f.Indexed && f.Column).Select(static f => f.Name)];
        return fields.Length == 1
            ? fields[0]
            : throw Error(path, fields.Length == 0 ? "The schema has no searchable geo field." : "The schema has several geo fields; name one: _geoPoint(" + fields[0] + ", lat, lng).");
    }

    private static bool TryNumber(string s, double min, double max, out double value)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= min && value <= max;

    private static QueryParseException Error(string path, string detail, string? suggestion = null) => QueryParseException.AtPath(path, detail, null, suggestion);
}
