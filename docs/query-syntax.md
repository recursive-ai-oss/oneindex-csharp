# Strict query syntax

The strict syntax is an opt-in query language for people and programs that want exactly what they wrote. It is a separate grammar from the forgiving user text of `index.Search("...")` (see [user-text.md](user-text.md)), which accepts anything and never throws. The strict syntax throws `QueryParseException` with the position of the problem, and it checks field names against the schema. It is not compatible with any other engine's query syntax.

```csharp
index.SearchStrict("Title:(overdue OR late)^2 AND NOT Tags:draft AND Total:[100 TO *}");
Query q = StrictQuery.Parse(text, index.Schema);       // query objects, to compose yourself
StrictNode tree = StrictQuery.ParseSyntax(text);        // syntax only, no schema
string text = StrictQuery.Print(tree);                  // reads back to an equal tree
```

## Grammar

```
query    := or
or       := and ( "OR" and )*
and      := unary ( [ "AND" ] unary )*          adjacency is AND
unary    := ( "NOT" | "-" ) unary | primary
primary  := group | atom
group    := [ field ":" ] "(" or ")" suffix
atom     := [ field ":" ] value suffix
value    := word | word "*" | word "~" [ digits ] | phrase | regex | range | comparison | "*"
phrase   := '"' chars '"' [ "~" digits ]
regex    := "/" chars "/"
range    := ( "[" | "{" ) bound "TO" bound ( "]" | "}" )
comparison := ( ">" | ">=" | "<" | "<=" ) bound
bound    := word-without-spaces | '"' chars '"' | "*"       "*" is an open end
suffix   := [ "^" number ]
```

- **Precedence**, tightest first: `NOT` / `-`, `AND` (or adjacency), `OR`. `a b OR c` is `(a AND b) OR c`. This differs from user text, where `OR` joins neighbours.
- **Operators are upper case and bare.** `and`, `or`, `not` are words. To search for the word `AND`, write `\AND` or `"AND"`.
- **A word** runs up to white space or one of `( ) " ^ ~ : [ ] { }`. A backslash makes the next character literal (`\(`, `\:`, `\*`, `\\`, `\-`, and a backslash before a space). A `*` may only be the last character of a word (a prefix search); anywhere else it is an error, and `\*` is a literal star.
- **`-`** at the start of an operand means `NOT`; inside a word it is an ordinary character. A negative number as a term needs a backslash (`\-5`). In a range bound or after `field:>` it needs none.
- **Phrase:** inside quotes `\"` and `\\` are the only escapes. `~N` is the slop: how many position moves are allowed (0 to 1000; default 0).
- **Fuzzy:** `word~` allows 2 edits; `word~N` allows N (0, 1 or 2). A swap of two neighbouring characters is one edit.
- **Regex:** a regular expression between slashes that must match a whole term. `\/` is a slash; every other backslash pair is passed to the expression untouched. The syntax of the expression is in [term-dictionary.md](term-dictionary.md). It is checked when the query is compiled, and the error points into the pattern.
- **Range:** `[` and `]` include the bound, `{` and `}` exclude it, `*` is no bound. `field:>5`, `>=`, `<`, `<=` are one-sided ranges. `field:*` means "has a value" and is the same as `field:[* TO *]`. A range always needs a field.
- **Match all:** a lone `*`.
- **Boost:** `^2`, `^0.5`: a non-negative number multiplying the score of the atom or group it follows. Boosts multiply: `(a^2 b)^3` gives `a` a boost of 6.
- **Fields:** `field:` applies to the one value after it. Before a group it applies to every unqualified atom inside it: `Title:(a OR Body:b)` searches `a` in Title and `b` in Body. Field names are case-sensitive and may use backslash escapes.
- **Limits:** 8,192 characters, nesting depth 64, 1,024 nodes. Longer or deeper input is a `QueryParseException`.

## Compilation against the schema

Each atom becomes query objects, using the field's type:

| Atom | Full-text field | Keyword field | Number, date, bool field |
|---|---|---|---|
| word | analyzed with the field's analyzer: one term is a `TermQuery`, several (`acme.corp`) a `PhraseQuery`; nothing left (a stop word) drops the atom | `TermQuery` on the exact text | one-value `RangeQuery` (the value is read like a filter-string value) |
| phrase | analyzed; slop is the phrase slop plus positions the analyzer skipped | the whole text is one exact value; slop must be 0 | not allowed |
| `word*` | the analyzed word as a `PrefixQuery` | `PrefixQuery` on the exact text | not allowed |
| `word~N` | the analyzed word as a `FuzzyQuery` | `FuzzyQuery` on the exact text | not allowed |
| regex | `RegexQuery` | `RegexQuery` | not allowed |
| range, comparison | not allowed | `RangeQuery` on text | `RangeQuery` (booleans not allowed) |

- A prefix or fuzzy word that analyzes to several terms is an error. Stemming analyzers can change the end of a prefix (`runn*` is stemmed like any word), so prefix search on a stemmed field is best done on an unstemmed field.
- **Unqualified atoms** search every full-text field the way user text does: fields that share an analyzer are scored together as one BM25F term; prefix, fuzzy and regex atoms are tried on each field and any may match. With no full-text field an unqualified atom matches nothing.
- `AND` is a boolean query of `Must` clauses, `NOT x` inside an `AND` a `MustNot` clause (an `AND` of only `NOT`s starts from every document), `OR` a boolean query of `Should` clauses with `MinimumShouldMatch` 1. A lone `NOT x` is every document except `x`.
- An operand that analysis removes entirely is dropped from its parent; if nothing is left the query matches no document.
- Scoring is BM25F as in [scoring.md](scoring.md); range atoms and comparisons score as constants.

## Errors

`QueryParseException` carries `Offset` (UTF-16 index), `Length`, `Line` and `Column` (one-based; `\n`, `\r\n` and `\r` end lines), `Expected` (what the parser would have accepted) and `Suggestion`. The message reads `Expected ')' to close the '(' at offset 0, but found the end of the query. (at offset 4, line 1, column 5)`.

- Syntax errors point at the first character that cannot continue the query, or at the end of the input.
- Unknown fields point at the field name and name the nearest field of the schema (`Did you mean 'Color'?`) and the searchable fields.
- Type errors (`Count:abc`) point at the start of the atom.
- Regex errors point into the pattern.

## Printing

`StrictQuery.Print(tree)` writes a tree as text that `ParseSyntax` reads back to an equal tree (positions are not compared). Composite children are always parenthesized and operators are explicit, so the nesting survives; escapes are added where a character would otherwise be read as syntax.
