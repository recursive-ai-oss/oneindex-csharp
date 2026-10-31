# Did you mean

`OneIndex.Suggest.DidYouMean` proposes corrected queries from the terms of a field's dictionary in a snapshot. It is separate from
typo-tolerant *matching* (which finds documents for the typo itself); this is the "Did you mean *x*?" line.

## Algorithm

1. **Tokenize** the query with the field's analyzer. Each word keeps its offsets in the query text. Numbers are never corrected.
2. **Known words.** The document frequency of a word is summed over the snapshot's segments (`TryGetTerm`; deleted documents still
   count, as everywhere in term statistics). In the default `Missing` mode only words with frequency 0 are corrected. In `Popular`
   mode a known word is also corrected when a close term has at least `PopularFactor` (10) times its frequency.
3. **Edit budget** by the word's length in code points: shorter than `OneEditMinLength` (4): none, the word is left alone; from 4:
   one edit; from `TwoEditsMinLength` (6): two. Edits are insertion, deletion, substitution and the swap of two adjacent code
   points, each 1 (optimal string alignment). The first `PrefixLength` (1) code points must be right; people rarely mistype the first letter and fixing it keeps the walk small, and `PrefixLength = 0`
   lifts it (measured below: it costs about 0.3 to 0.6 points).
4. **Candidates.** For each segment, `ITextFieldReader.Intersect(LevenshteinAutomaton(word, budget), prefix)` walks the term
   dictionary together with the automaton (Schulz and Mihov 2002), visiting only the neighbourhood of the word. At most
   `MaxTermsVisited` (20,000) terms are looked at per word and segment, in byte order, so a cut-off is deterministic. Candidates
   are merged across segments by term, their frequencies summed, and their distance recomputed with `EditDistance` (the same
   distance the automaton uses, checked against a dynamic-programming matrix on 3,000 random pairs). The best
   `CandidatesPerWord` (4) are kept: fewest edits, then most documents, then UTF-8 byte order.
5. **Combine.** A multi-word query is a left-to-right beam search (`BeamWidth` 8) over each word's options (its candidates; or
   itself when it is known or has no candidates). A partial suggestion scores

   `sum( FrequencyWeight * ln(1 + df) - EditPenalty * edits )  +  CooccurrenceWeight * sum( ln(1 + both) )`

   where `both` is the number of documents that contain a word and the word before it (two adjacent words), counted by
   leapfrogging their postings lists, at most `MaxCooccurrenceScan` (50,000) steps per pair and segment. Defaults: weights 1, 1,
   `EditPenalty` 4 (one more edit costs as much as the candidate being ~55 times rarer) [agent defaults].
6. **Output.** Combinations that change nothing are dropped; the rest are sorted by score, then by text, and cut to
   `MaxSuggestions` (3). The suggested text is the query with each corrected word's span replaced by the dictionary term, so the
   punctuation and untouched words are exactly as typed. The replacement is the indexed (case-folded) form.

Everything is deterministic: ties break by term bytes, the beam sorts on (score, choice indexes), and no step depends on hash order.

**Cost bounds** per word: one automaton walk per segment (at most `MaxTermsVisited` terms) plus one `TryGetTerm` per segment; per
pair of words at most `BeamWidth * CandidatesPerWord` co-occurrence counts, cached within the call.

**Stemming.** Dictionary terms of a stemmed field are stems, so suggestions from such a field are stems. Use an analyzer that
does not stem for the suggestion field (or a `-both` analyzer's unstemmed terms are indexed beside the stems, but the dictionary
cannot tell them apart). The function never throws on query text, including control characters and lone surrogates, and honours
the cancellation token between segments and every 256 terms.

`SuggestAsync` is the asynchronous twin; readers are synchronous over cached ranges, so it completes synchronously (it works in the
browser, where nothing depends on threads).

## Measured accuracy

Method: the English word list of the Snowball vocabulary data (28,266 lowercase words of 3+ letters; fetch-only), 20,000
documents of 8 to 15 words drawn with Zipf frequencies (seed 5), 1,000 queries per row. A query is a run of 1 (or 2 to 3) words from
a document with one word replaced by a typo of exactly 1 or 2 edits (random substitution, insertion, deletion or swap; the word has 4 or more letters for one edit,
6 or more for two; with prefix 1 the first letter is untouched), where the typo is not itself a word of the corpus. *top-1* is the fraction in which the first suggestion equals the original text; *top-3* any of the three. "Engine = brute
force" compares the single-word candidate list with a brute-force scan of the whole vocabulary under the same rule and ordering.

| Words | Edits | Prefix | top-1 | top-3 | Engine = brute force |
|---|---|---|---|---|---|
| 1 | 1 | 1 | 95.2% | 98.1% | 1000 / 1000 |
| 1 | 1 | 0 | 94.9% | 97.9% | 1000 / 1000 |
| 1 | 2 | 1 | 85.3% | 90.5% | 1000 / 1000 |
| 1 | 2 | 0 | 84.8% | 90.1% | 1000 / 1000 |
| 2 to 3 | 1 | 1 | 97.2% | 98.5% | |
| 2 to 3 | 1 | 0 | 96.5% | 98.1% | |
| 2 to 3 | 2 | 1 | 88.3% | 90.8% | |
| 2 to 3 | 2 | 0 | 88.3% | 91.1% | |

The two-edit threshold was chosen from a run on words of 8 or more letters (typos that delete letters can shorten them below the
threshold): two-edit single-word top-1 was 85.5% with the threshold at 8, 92.9% at 7 and 94.1% at 6, with the one-edit rows
unchanged. The rows above also include 6- and 7-letter words, the harder set (Meilisearch's own typo threshold is 9 letters; ours
is for suggesting a replacement for a word the index does not contain at all, so a lower one is affordable).

Per call, one typo, the same corpus (Release, x64, 32-thread host at load ~18, indicative only): one-word query 207 microseconds
and 4.2 KB allocated; three-word query 227 microseconds and 31.7 KB allocated. The edit distance kernel allocates nothing
(`TestHelpers.AssertNoAllocation`, strings of up to 64 code points on a fixed stack buffer). The call as a whole allocates
(strings for candidates, result lists), because the automaton and the term enumerators are created per call.

## Verification

`tests/OneIndex.Tests/DidYouMeanTests.cs`: edit distance against a full matrix (3,000 random pairs including supplementary
characters), no allocation, unit cases (frequency, co-occurrence tie-break, popular mode, several segments, cancellation, punctuation
preserved, nothing suggested for known or short words or numbers), seeded synthetic-vocabulary accuracy with an exact brute-force
comparison of candidate lists, and the real-vocabulary report above (skipped without `ONEINDEX_SNOWBALL_DATA`).
