# Term dictionary

Format of the **terms** part of a segment (codec `terms`, versions 1 and 2) and the term-lookup operations built on it. Version 2 is written by this build; version 1 files stay readable and a merge rewrites them as version 2. The two versions differ only in how a node of the automaton is stored (below): the file layout, the statistics table, the ordinals and every query answer are the same. Integers, varints and the file frame are as in `commit-format.md`, except that varints in this file are read leniently for length (a reader accepts a non-shortest form but rejects one past 63 bits); a writer always emits the shortest form, so the bytes are canonical.

## What it holds

For each text field of a segment that has at least one term: an acyclic automaton that maps every term to its **ordinal** (its rank in byte order), a table of per-term statistics indexed by ordinal, and field counts. The postings themselves are in the postings part (`postings.md`); a term's statistics carry the offset of its postings.

Terms are byte strings (UTF-8 for text), compared as unsigned bytes. UTF-8 byte order equals code-point order, which differs from .NET's UTF-16 ordinal order only for characters outside the BMP. A term is at most `IndexLimits.MaxTermBytes` bytes (default 32,766).

## File layout

    frame header   magic "OIXT", version 1 or 2
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

Nodes are written children first, so an arc always points to a lower address and cycles cannot exist. The root is the last node; `fstRoot` is its address.

**Node, version 1.** Arcs are read in sequence:

    varint (arcCount << 1) | isFinal        arcCount at most 256
    per arc, labels strictly increasing:
      u8 label
      varint words                          number of terms reachable through the arc
      varint distance                       node address minus child address, at least 1

**Node, version 2.** The same information in three tables of fixed-width numbers, so an arc is found by its label, and the terms before it counted, without decoding the arcs in front of it:

    u8 head                                 bit 0 isFinal; bits 1-2 cumulative-count width minus 1;
                                            bits 3-4 distance width minus 1; bits 5-7 arcCount if below 7, else 7
    [varint arcCount - 7]                   only when bits 5-7 are 7; arcCount at most 256
    u8 label[arcCount]                      strictly increasing
    cumulative[arcCount]                    little endian, width 1 to 4 bytes: words through arc i, that is the sum of the
                                            words of arcs 0 to i
    distance[arcCount]                      little endian, width 1 to 4 bytes: node address minus child address, at least 1

A writer chooses the narrowest widths that hold the node's largest values, so the bytes are still canonical. The words of arc *i* are `cumulative[i] - cumulative[i-1]`, and the terms before it `cumulative[i-1]` (0 for the first). A leaf has a head byte only. Nodes are shared exactly as in version 1 (the register compares sub-automata, not encodings). On a dictionary of 407,437 distinct lowercased word tokens of 302,567 Wikipedia abstracts the automaton is 2,764,464 bytes in version 2 against 2,535,846 in version 1 (+9.0%). The **words** counts number the terms: the ordinal of a term is the count of terms that sort before it, computed while walking the term: one for each final node passed (a node's own term sorts before its extensions), plus the `words` of every arc passed over to the left of the arc taken (Lucchesi and Kowaltowski 1993: perfect hashing from a finite automaton). This gives exact lookup in time proportional to the term length, ordered enumeration with ordinals, and the ordinal range of a whole subtree without visiting it.

### Statistics table

One entry per term in ordinal order, each three varints: `docFreq` (at least 1), `totalTermFreq - docFreq`, and the postings offset as a difference from the previous entry's offset (at least 0). The first entry of every group of 32 omits the offset: it is the group's sample. A sample is `(tableOffset, postingsOffset)` giving the table position and absolute postings offset of the group's first entry. Looking up ordinal *n* decodes at most 31 entries from the sample of `n / 32`; enumeration reads entries sequentially.

## Operations

- **Exact lookup**, **ordered iteration** and **prefix enumeration** (walk the prefix bytes, then enumerate the subtree) work on the automaton as described.
- **Intersection with a code-point automaton** (`TermAutomaton`) walks the dictionary and the automaton together, depth first in byte order, and skips a subtree (adding its `words` to the running ordinal) as soon as the automaton is dead. UTF-8 is decoded on the fly; the automaton is advanced when a code point completes, and malformed UTF-8 in a term never matches. An optional exact prefix is applied first (for example a typo-free first letter), then the automaton continues from its state. In a version 2 file, when the automaton says that only a few first bytes can keep a state alive (a Levenshtein state with no edits left reads only the letters of the word near its position), the walk looks those labels up directly in the node's label table instead of visiting every arc, and adds the skipped arcs' words from the cumulative table. The answers are identical to the sequential walk; only the arcs read differ.
- **Fuzzy** (`LevenshteinAutomaton`): terms within *k* edits (0 to 3) of a word, counting substitution, insertion and deletion of a code point, and optionally a swap of two adjacent code points, as one edit each. A state of the deterministic automaton is a set of (word position, edits, swap pending) triples of the non-deterministic automaton of Schulz and Mihov (2002, section 2). Following their section 4, the set is stored relative to its smallest position (a *shape*), and the effect of a code point depends only on the shape and on which of the next 2*k*+2 positions of the word hold that code point (the characteristic vector), plus how many of those positions lie inside the word. The transitions of each shape are computed the first time they are needed and shared by every automaton with the same edit budget and swap setting, on all threads; a step then costs a comparison of the code point with up to 2*k*+2 letters and two table reads. A state is two integers (shape, word position). With swaps the distance is the optimal string alignment distance (a swapped pair is not edited again); this is checked against dynamic programming over code points.
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
- Version 1 scans arcs linearly within a node (at most 256 per node); a node's arcs are sorted so a miss stops early. Version 2 looks an arc up in the label table (a vectorised byte search over at most 256 labels).
- Version 2 does not check at read time that a node's labels are sorted when it looks an arc up directly; a damaged label table can at worst make a lookup miss or find a wrong child, and the file checksum, the child-address rule, the count bounds and the walk limit above still hold. The sequential walk checks that labels increase.
- Reading a version 1 file keeps the version 1 node decoder; nothing converts a file in place. Segments get version 2 when they are written (flush or merge), so an index moves to version 2 as its old segments merge away.
