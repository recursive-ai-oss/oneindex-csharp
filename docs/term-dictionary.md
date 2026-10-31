# Term dictionary

Format of the **terms** part of a segment (codec `terms`, version 1) and the term-lookup operations built on it. Integers, varints and the file frame are as in `commit-format.md`, except that varints in this file are read leniently for length (a reader accepts a non-shortest form but rejects one past 63 bits); a writer always emits the shortest form, so the bytes are canonical.

## What it holds

For each text field of a segment that has at least one term: an acyclic automaton that maps every term to its **ordinal** (its rank in byte order), a table of per-term statistics indexed by ordinal, and field counts. The postings themselves are in the postings part (`postings.md`); a term's statistics carry the offset of its postings.

Terms are byte strings (UTF-8 for text), compared as unsigned bytes. UTF-8 byte order equals code-point order, which differs from .NET's UTF-16 ordinal order only for characters outside the BMP. A term is at most `IndexLimits.MaxTermBytes` bytes (default 32,766).

## File layout

    frame header   magic "OIXT", version 1
    varint maxDoc                           documents in the segment; equals the lengths file's
    repeat per field, in increasing ordinal order of the field name:
      u8 1                                  field marker
      string name
      u8 flags                              bit 0 positions stored, bit 1 offsets stored (offsets imply positions)
      varint docCount                       documents with a non-zero field length
      varint sumTotalTermFreq               occurrences of all terms
      varint sumDocFreq                     sum of document frequencies
      varint termCount                      at least 1
      varint totalTermBytes                 sum of the terms' byte lengths
      varint maxTermBytes                   length of the longest term
      varint fstLength, varint fstRoot
      fstLength bytes                       the automaton
      varint tableLength, tableLength bytes the statistics table
      varint sampleCount                    ceil(termCount / 32)
      sampleCount x (u32 tableOffset, u64 postingsOffset)
    u8 0                                    end marker
    frame footer

A field with no terms (for example after a merge dropped all its documents) is omitted.

## The automaton

The automaton is the minimal acyclic deterministic automaton of the sorted term list, built incrementally from sorted input (Daciuk, Mihov, Watson and Watson 2000, section 4): only the path of the last term stays open; when the next term diverges, the nodes below the divergence are *frozen* and looked up in a register so equal sub-automata are stored once. The result is the unique minimal automaton, so the bytes depend only on the term set.

Nodes are written children first, so an arc always points to a lower address and cycles cannot exist. A node:

    varint (arcCount << 1) | isFinal        arcCount at most 256
    per arc, labels strictly increasing:
      u8 label
      varint words                          number of terms reachable through the arc
      varint distance                       node address minus child address, at least 1

The root is the last node; `fstRoot` is its address. The **words** counts number the terms: the ordinal of a term is the count of terms that sort before it, computed while walking the term: one for each final node passed (a node's own term sorts before its extensions), plus the `words` of every arc passed over to the left of the arc taken (Lucchesi and Kowaltowski 1993: perfect hashing from a finite automaton). This gives exact lookup in time proportional to the term length, ordered enumeration with ordinals, and the ordinal range of a whole subtree without visiting it.

### Statistics table

One entry per term in ordinal order, each three varints: `docFreq` (at least 1), `totalTermFreq - docFreq`, and the postings offset as a difference from the previous entry's offset (at least 0). The first entry of every group of 32 omits the offset: it is the group's sample. A sample is `(tableOffset, postingsOffset)` giving the table position and absolute postings offset of the group's first entry. Looking up ordinal *n* decodes at most 31 entries from the sample of `n / 32`; enumeration reads entries sequentially.

## Operations

- **Exact lookup**, **ordered iteration** and **prefix enumeration** (walk the prefix bytes, then enumerate the subtree) work on the automaton as described.
- **Intersection with a code-point automaton** (`TermAutomaton`) walks the dictionary and the automaton together, depth first in byte order, and skips a subtree (adding its `words` to the running ordinal) as soon as the automaton is dead. UTF-8 is decoded on the fly; the automaton is advanced when a code point completes, and malformed UTF-8 in a term never matches. An optional exact prefix is applied first (for example a typo-free first letter), then the automaton continues from its state.
- **Fuzzy** (`LevenshteinAutomaton`): terms within *k* edits (0 to 3) of a word, counting substitution, insertion and deletion of a code point, and optionally a swap of two adjacent code points, as one edit each. The state is the set of (word position, edits) pairs of the non-deterministic automaton of Schulz and Mihov (2002, section 2), advanced one code point at a time (subset construction on the fly, not their precomputed tables). With swaps the distance is the optimal string alignment distance (a swapped pair is not edited again); this is checked against dynamic programming over code points.
- **Regular expressions** (`RegexAutomaton`): compiled to a Thompson NFA (Thompson 1968) and advanced as a set of states per code point. Always matches the whole term.

### Regular-expression syntax

| Syntax | Meaning |
|---|---|
| any other character | itself (a code point; surrogate pairs are one character) |
| `.` | any code point (including line breaks) |
| `[abc]` `[a-z]` `[^...]` | class, range, negated class; a `]` first in a class is literal; escapes work inside |
| `(...)` | group (not capturing) |
| `a\|b` | alternation; an empty branch matches the empty string |
| `*` `+` `?` | zero or more, one or more, zero or one |
| `{n}` `{n,}` `{n,m}` | repeat counts, each at most 1,000 |
| `\d` `\D` | ASCII digits `0-9`, and everything else |
| `\w` `\W` | `A-Z a-z 0-9 _`, and everything else |
| `\s` `\S` | space, tab, line feed, carriage return (`\t`-`\r`), and everything else |
| `\n \r \t \f \v` | the control characters |
| `\uXXXX`, `\U{X...}` | a code point in hex (four digits; or any number of digits in braces, at most 10FFFF) |
| `\` + punctuation | that character literally |

Not supported, and rejected with `RegexSyntaxException` (with the position): `^` and `$` (the match is always anchored), look-around, back-references, named groups, `\b`, Unicode property classes, unknown letter escapes, unbalanced brackets, empty repeats (`*a`), backward ranges, a repeat bound above 1,000, nesting deeper than 200 groups, or a pattern needing more than 10,000 automaton states. Lazy and possessive quantifiers are not special: `a*?` is "(a*) optionally".

## Reading and corruption

The terms file is read into memory once (limit `IndexLimits.MaxResidentFileBytes`, default 1 GiB), its checksum verified, and its structure validated: marker bytes, field order, counts consistent with each other, section lengths inside the file, sample count equal to `ceil(termCount/32)`. During lookups every address, arc count and number is bounds-checked: arc labels must increase, a child address must be lower than its node, `words` cannot exceed `termCount`, an ordinal must be below `termCount`, enumeration depth cannot exceed `maxTermBytes`, and the number of nodes visited in one walk cannot exceed `totalTermBytes + 2` (a trie has at most that many), which bounds work on a crafted shared-node structure. Any violation raises `CorruptIndexException` naming the file; a newer format version raises `UnsupportedFormatException`.

Lookup and enumeration do not allocate besides the enumerator object itself.

## Deviations and choices

- The automaton and statistics are resident rather than range-read per node: a term dictionary is a few bytes per term, and the static (browser) index will use its own chunked layout.
- Arcs are scanned linearly within a node (at most 256 per node); a node's arcs are sorted so a miss stops early.
