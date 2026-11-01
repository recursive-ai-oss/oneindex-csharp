# Geo search

This note is normative for geo points: the index, the filters and sorting by distance. The terms follow Meilisearch's documented geosearch (`_geoRadius`, `_geoBoundingBox`, `_geoPoint`); the documents hold one point (or several) per geo field instead of a `_geo` object.

```csharp
public class Restaurant {
    [SearchId] public string Id { get; set; }
    [Geo] public GeoPoint? Location { get; set; }
}

index.Search().Where(x => Geo.WithinRadius(x.Location!.Value, 45.4727, 9.1840, 2000))
     .OrderByDistance(x => x.Location, 45.4727, 9.1840).Take(10);
```

## Model

- **Coordinates** are degrees: latitude -90 to 90 (north positive), longitude -180 to 180 (east positive). `GeoPoint` rejects anything else and NaN.
- **Distance** is the great-circle distance on a sphere of radius **6,371,008.8 m** (the IUGG mean Earth radius), by the haversine formula (Sinnott, "Virtues of the Haversine", *Sky and Telescope* 68(2), 1984): `d = 2R asin(min(1, sqrt(sin²(Δφ/2) + cos φ1 cos φ2 sin²(Δλ/2))))`. Against the WGS84 ellipsoid it is off by up to about 0.5%.
- **Radius filter** `_geoRadius(lat, lng, meters)`: points at a distance **less than or equal to** the radius. Radius 0 matches a point exactly at the centre.
- **Bounding box filter** `_geoBoundingBox([topRightLat, topRightLng], [bottomLeftLat, bottomLeftLng])`: latitude from the south edge to the north edge, edges included; longitude from the west edge **east** to the east edge. When the west edge is greater than the east edge the box crosses the antimeridian (`[10, -170]` top right and `[-10, 170]` bottom left is the strip from 170° east across 180° to 170° west). A box from -180 to 180 spans every longitude. (Meilisearch's documentation does not say how it treats this case; the rule here is ours.) A box is not a circle: its edges are parallels and meridians, not great circles.
- A document with several points matches when **any** point matches; a document without a point matches neither filter and sorts after the others by distance.
- **Distance sort** orders by the distance to the document's nearest point (farthest when descending) and reports it as `SearchHit.GeoDistance` in metres; see [sorting.md](sorting.md).
- A geo field needs `Indexed` and `Column` for filters (the `[Geo]` attribute sets both); a `GeoPoint` property with only `[Sortable]` keeps the column and sorts by distance but cannot be filtered. A geo field cannot be a facet or be collapsed by `Distinct`.

## Index

Geo points live in the point index of the segment ([points.md](points.md)), as a second kind of tree in the same `points` file (format version 2).

- **Key.** Latitude and longitude are each quantized to 31 bits (steps of 180/(2³¹-1) and 360/(2³¹-1) degrees, about 8 mm and 17 mm) and their bits interleaved, latitude in the even bits (a Z-order or Morton key, Morton 1966). Points near each other in the plane are mostly near in the key.
- **Leaves.** The pairs (key, document) are sorted and cut into leaves of at most 512, exactly like numeric points. Each leaf record also stores its **bounding cell box**, the top 16 bits of the smallest and largest latitude and longitude quantum of its points, four little-endian `u16` in the record's last eight bytes. A cell is about 0.0027° of latitude (300 m), so the box is a conservative cover.
- **Directory.** Each field entry carries a kind byte after its name: 0 for numeric, 1 for geo.
- **Query.** The query turns its shape into rectangles of quanta, widened by one quantum on every side (two rectangles when it crosses the antimeridian): for a radius the rectangle that bounds the circle (the circle's latitude band, widened in longitude by asin(sin(r/R)/cos φ), Matuschek, "Finding Points Within a Distance of a Latitude/Longitude Using Bounding Coordinates"; spanning every longitude when the circle reaches a pole), widened by 10⁻⁷ degrees so rounding cannot drop a point that lies on the circle. The resident leaf records are scanned linearly: a leaf whose cell box misses every rectangle is skipped, a leaf wholly inside one contributes all its documents, the others have each point's key checked. Only the leaves that survive are read from storage.
- **Exactness.** The index only proposes candidates. The query confirms each candidate against the exact coordinates in the column (haversine for a radius, comparisons for a box), so the result does not depend on the quantization. A segment with no geo values for the field matches nothing.
- A linear scan of the leaf records costs one 32-byte record per 512 points (a million points: about 2,000 records); a split tree over the leaves (Procopiuc, Agarwal, Arge and Vitter, "Bkd-tree", SSTD 2003) would replace the scan with a descent and is not needed at this size. Merge copies the leaves of the sources through the document map and rewrites the tree, as for numeric points.

## Query surfaces

| Surface | Form |
|---|---|
| Query objects | `GeoRadiusQuery(field, lat, lng, meters)`, `GeoBoundingBoxQuery(field, north, south, west, east)`; sort keys `GeoDistanceSort(field, lat, lng)` |
| LINQ | `Geo.WithinRadius(x.Location, lat, lng, meters)` and `Geo.WithinBoundingBox(x.Location, north, south, west, east)` inside `Where` (they combine with `&&`, `\|\|`, `!`); `OrderByDistance` / `ThenByDistance` |
| Filter string | `_geoRadius(lat, lng, meters)`, `_geoBoundingBox([lat, lng], [lat, lng])`; the geo field may be named first (`_geoRadius(Location, lat, lng, meters)`) and must be when the schema has several |
| Query JSON | nodes `{"type":"geoRadius","field":..,"lat":..,"lng":..,"meters":..}` and `{"type":"geoBoundingBox","field":..,"north":..,"south":..,"west":..,"east":..}`; sort strings `_geoPoint(lat, lng):asc` |

The tool schema (`SearchTool`) lists the geo nodes when the type has a `[Geo]` property, and `sort` and `searchAfter` always.

## Documented deviations from Meilisearch

- Geo fields have names (`Location`) instead of the single `_geo` object; the filter functions can omit the name when the schema has one geo field.
- The antimeridian rule for boxes (above), and several points per document.
- Distances use the haversine sphere with the IUGG mean radius. The radius Meilisearch uses is not stated in its documentation, so a point within a few metres of a circle's edge can be on the other side of it there. A black-box comparison was not made.

## Limits

Coordinates are validated at construction (`GeoPoint`), at query construction and at parse. A radius is finite and not negative; any radius larger than half the circumference matches every point.
