namespace OneIndex.Storage;

/// <summary>
/// Marks read-only storage where every byte read costs a network fetch. Segment readers then open the parts a query may never use
/// (column, point and vector parts) on first use, so a text query does not pay for files it does not read. A failure while opening such
/// a part is raised by the query that needs it and is not remembered: the next use tries again.
/// </summary>
internal interface IDeferredOpenStorage
{
}
