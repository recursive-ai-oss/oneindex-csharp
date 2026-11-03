# Reads like

Each layer of OneIndex is shaped after a .NET API that readers already know. This page puts them side by side. Every OneIndex snippet here is compiled and run by the sample tests (`samples/OneIndex.Samples`: `HappyPath.cs`, `EfCore.cs`, `ReadsLike.cs`, `Migration.cs`).

## The happy path reads like EF Core

An EF query and a OneIndex query have the same grammar: a source, `Where` with a lambda, an ordering, a page, then run it.

```csharp
// Entity Framework Core
var list = await db.Invoices
    .Where(x => x.CustomerId == 7 && x.Amount > 100)
    .OrderBy(x => x.Amount)
    .Take(10)
    .ToListAsync();

// OneIndex
var hits = await index.Search("overdue acme")
    .Where(x => x.Tags.Contains("invoice") && x.Amount > 100)
    .OrderBy(x => x.Amount)
    .Take(10)
    .ToListAsync();
```

The lambdas are expression trees that OneIndex walks and never compiles; one it cannot translate fails naming the unsupported sub-expression. Entry points read like `DbContext`: `SearchIndex.Open<T>(path)` and `SearchIndex.InMemory<T>()`; `Upsert` and `Delete` buffer, `Commit()` is `SaveChanges()` (atomic and durable). Settings come from attributes on the class, as EF's data annotations do. With the Entity Framework package the two compose (the index finds, the database filters and loads):

```csharp
var list = await db.Invoices.Search("overdue acme")
                   .Where(x => x.CustomerId == 7 && x.Amount > 100)
                   .Take(10)
                   .ToListAsync();
```

## Readers read like `Utf8JsonReader`

The low-level readers are forward-only and span-based, and do the work the caller asks for and no more. A term dictionary and its postings are walked the way a JSON token stream is:

```csharp
// System.Text.Json
var reader = new Utf8JsonReader(json);
while (reader.Read())
{
    if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("price"u8))
    {
        reader.Read();
        total += reader.GetDouble();
    }
}

// OneIndex: the terms of a field with a prefix, and for each the documents that contain it
ITermEnumerator terms = field.GetTerms("head"u8);
while (terms.MoveNext())
{
    PostingsIterator postings = field.GetPostings(terms.Info, PostingsFeatures.Frequencies);
    while (postings.NextDoc() != DocIdSetIterator.NoMoreDocs)
    {
        occurrences += postings.Frequency;           // postings.DocId is the document
    }
}
```

`terms.Term` is a `ReadOnlySpan<byte>` (the term's UTF-8) valid until the next `MoveNext`, as `reader.ValueSpan` is until the next `Read`. Lookups come in the `Try*` shape the BCL uses: `field.TryGetTerm(term, out TermInfo info)`, `segment.TryFindId(id, out int docId)`, `snapshot.TryFindById(...)`, and `index.TryParse(json, out search, out error)` beside `Parse`. Where an exception is the right answer for damaged input it is typed (`CorruptIndexException`, naming the file).

The segment readers are interfaces (`ITextFieldReader`, `IColumnReader`, ...), not `ref struct`s, because each codec supplies its own and a segment's reader must outlive a single stack frame; the *iterators* and the *spans* they return are the parts that behave like `Utf8JsonReader`.

## Vector kernels

`System.Numerics.Tensors.TensorPrimitives` is the BCL's public home for vectorized math. OneIndex's distance kernels (`Kernels/VectorKernels.cs`, `QuantizedKernels.cs`) are **internal**: they are fixed-width `Vector128/256/512` loops with a scalar path, shaped for the engine's exact needs (normalized dot, int8/int4/bit codes), and have no public surface to compare. The public vector surface is the query: `KnnQuery`, `SearchVector(x => x.Embedding, vector)`, `Hybrid(...)`. Use `TensorPrimitives` in your own code when you need a distance outside an index.
