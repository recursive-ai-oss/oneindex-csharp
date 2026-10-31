# SearchTool: a search as an LLM tool

`SearchTool.For<T>()` describes a search over `T` as a tool a language model can call. The arguments are a [query JSON](query-json.md) document; the model's tool call goes straight to `index.Parse`.

```csharp
string tool = SearchTool.For<Invoice>();          // {"name":"search_Invoice","description":"...","input_schema":{...}}
string schema = SearchTool.SchemaFor<Invoice>();  // only the JSON Schema
// register `tool` with the model API; when the model calls it with `arguments` (JSON text):
SearchResults<Invoice> hits = index.Parse(arguments).Execute();
```

`input_schema` is the name most tool APIs use; for an API that wants `parameters`, rename the property. For a runtime schema: `SearchTool.For(schema, "Invoice")` and `SearchTool.SchemaFor(schema)`.

## What the schema says

A JSON Schema, draft 2020-12 (`$schema` is set). The root is an object with the query JSON properties (`v`, `q`, `query`, `filter`, `limit`, `offset`, `countExactly`, `highlight`), `required: ["v"]` and `additionalProperties: false`. Query nodes are in `$defs`; `$defs.node` is a `oneOf` over one definition per node kind, each with a fixed `type` (`const`), its properties, and `additionalProperties: false`.

The schema is built from the type's own fields, so a model cannot invent a field name and each value has the right JSON type:

- `field` is an `enum` of the fields that node can use: `term` over keyword and full-text fields (a string), over numeric fields (a number), date fields (a string matching an ISO date pattern) and boolean fields (a boolean) as separate definitions; `phrase`, `prefix`, `fuzzy`, `regex` and `multiFieldTerm` over full-text and keyword fields; `range` over numeric, date and keyword fields, with bounds typed to match, at least one bound and not both `gt` and `gte` (or `lt` and `lte`); `exists` over every filterable field.
- Only fields that are indexed appear (`[FullText]`, `[Keyword]`, `[Filterable]`, the identifier). Members that are only stored or only sortable do not.
- Limits are in the schema: string lengths, `limit` and `offset` 0 to 10,000, `slop` 0 to 1,000, `maxEdits` 0 to 2, phrase terms 1 to 256, boosts at least 0.
- The tool `description` lists the fields with their kinds. A definition for a node kind is left out when the type has no field it could use.
- There is no list of allowed values for keyword fields: the attributes do not declare any.

Some of what `Parse` checks cannot be written in a JSON Schema and so is not in it: that an integer is in the `long` range, that a date is a real date, that a regular expression is well formed, that `text`, `strict` and `filter` strings parse, that `limit + offset` is at most 10,000, and nesting and size limits. A document that the schema accepts and `Parse` rejects fails with a `QueryParseException` the application can return to the model as the tool result, so it can correct itself: the message names the path and, for misspelt names, the nearest valid one.

## Verification

`tests/OneIndex.Tests` generates random request documents (every node kind, 160 per run) and structurally invalid mutations of them (an unknown property, a wrong version, an unknown type, an unknown field, a negative boost, a missing `v`); `SearchIndex<T>.TryParse` accepts exactly the valid ones. When `ONEINDEX_JSONSCHEMA_PYTHON` names a Python with the `jsonschema` wheel, the same documents are given to its draft 2020-12 validator through `tools/jsonschema_check.py`, and every verdict must equal `Parse`'s.
