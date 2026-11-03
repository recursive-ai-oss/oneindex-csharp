# Folder indexing

`RecursiveAI.OneIndex.Files` turns a folder into searchable, cited passages. `RecursiveAI.OneIndex.Files.Ocr` adds scans and photos of text. A passage index is an ordinary OneIndex index of one document type, `FilePassage`, so hybrid search, the embedder, reranking, highlighting and the LINQ builder work on it unchanged.

**Everything stays local.** Nothing in either package uses the network. The index holds the extracted text of your files, which is as sensitive as the files themselves: protect the index directory like the folder it was built from, and delete it when you stop needing it.

```csharp
using OneIndex;
using OneIndex.Files;

using SearchIndex<FilePassage> index = FolderIndexer.OpenIndex("data/passages");   // add an IEmbedder here for hybrid search

FolderIndexResult run = FolderIndexer.Run(index, "docs",
    progress: new Progress<FolderProgress>(p => Console.WriteLine($"{p.FilesIndexed}/{p.FilesToIndex} files")));
Console.WriteLine($"{run.Added} added, {run.Updated} updated, {run.Renamed} renamed, {run.Removed} removed, {run.Unchanged} unchanged");

foreach (SearchHit<FilePassage> hit in index.Passages("overdue \"acme corp\"").Distinct(p => p.FileId).Take(5))
{
    Citation c = hit.Cite();   // file, archive entry, page, character range, boxes
    Console.WriteLine($"{c.File}{(c.Entry is null ? "" : " > " + c.Entry)} page {c.Page} [{c.Start}..{c.End}]: {c.Text}");
}
```

This sample is compiled and run by `tests/OneIndex.Files.Tests` (`DocsSampleRuns`). Add the OCR extractor to read scans:

```csharp
var options = new FolderIndexOptions { Extractors = [new TextExtractor(), new HtmlExtractor(), new PdfExtractor(), new OcrImageExtractor()] };
```

## What is read

| Files | Read by | Units | Geometry |
|---|---|---|---|
| `.txt`, logs, CSV, source code and other text (a file without a known extension is read as text if it looks like text) | `TextExtractor` | the whole file | no |
| `.md`, `.markdown` | `TextExtractor` | the whole file; headings, lists, tables and fenced code are blocks | no |
| `.html`, `.htm`, `.xhtml` | `HtmlExtractor` (onehtml) | the page's visible text | no |
| `.pdf` | `PdfExtractor` (OnePDF) | one unit per page with text | boxes in points |
| `.png`, `.jpg`, `.tif`, `.webp`, `.bmp`, `.gif` | `OcrImageExtractor` (oneocr, package `.Files.Ocr`) | one unit per frame | boxes in pixels |
| `.zip`, `.tar`, `.tgz`, `.7z`, `.gz`, `.bz2`, `.xz`, `.zst`, `.cpio`, `.iso`, `.cab`, `.rar` | the indexer, entry by entry (onearchive) | each entry is a file of its own | as its format |
| anything else | not read | a record with format `binary` | no |

Archives nest: `/backup.zip!/reports/q3.zip!/summary.pdf` is a PDF inside a zip inside a zip, down to `MaxArchiveDepth` (default 4). Entry sizes, the entry count and the total size are limited (`FolderIndexOptions`); an entry over its limit, a path that climbs out with `..` and a damaged archive never stop the run. A scanned PDF without a text layer yields no passages; its page images are not read (see "Not done").

Ignore rules follow `.gitignore`: `.gitignore` and `.oneindexignore` files in any folder, `FolderIndexOptions.IgnorePatterns`, and by default version-control folders, `node_modules`, `__pycache__`, editor and system litter (`IgnoreRules.Defaults`). Links are not followed.

## Identifiers

A file's identifier is its path below the root with `/` separators and a leading `/` (`/docs/a.md`); an archive entry is `/archive.zip!/inner/path.txt`. A passage is `FileId#ordinal`; a file's record is `file:FileId`. The three never collide, so ids are deterministic and an interrupted and a clean run give the same ids. Search results come from passages only (`index.Passages(text)`); `index.Errors()` lists the error documents.

`Distinct(p => p.FileId)` groups hits per file. `Where(p => p.Format == "pdf" && p.Page == 3)` and the rest of the LINQ surface work on `FilePassage`'s members.

## Passages

The chunker (`Chunker.Cut`) follows the structure of each unit:

- A heading starts a passage; a heading followed at once by a deeper one stays with it. The headings above a passage, outermost first, are in `Heading` (searched at weight 2).
- Blocks (paragraphs, list items, tables, code) fill a passage to the **target** of 256 estimated tokens. A passage under 48 tokens takes the next block even past the target, up to the **maximum** of 384.
- A block over the maximum is cut at UAX #29 sentence boundaries (at lines for tables and code); a sentence over the maximum is cut at word ends, then anywhere.
- A passage never spans units (pages), and is always an exact substring of its unit's text, so `unit.Text[Start..End]` is the passage.
- Tokens are estimated, not counted: four characters of Latin text are a token, two of other alphabetic scripts, one CJK character or kana. `ChunkOptions` sets the three sizes.

The unit text depends on the format. For text and Markdown it is the decoded file (UTF-8 with or without a byte-order mark, UTF-16 with a mark, otherwise Latin-1). For HTML it is the extractor's rendering of the page; scripts, styles, templates and SVG are dropped, whitespace is collapsed, blocks are separated by a blank line, table rows by a line feed and cells by a tab. For PDF and scans it is the blocks of the page in reading order, a block's lines joined by a line feed and blocks by a blank line; headers and footers that repeat across a PDF's pages are left out.

## Citations

`hit.Cite()` returns a `Citation`: `File` (path below the root), `Entry` (path inside the archive, or null), `Page` (from 1; 0 without pages), `Start` and `End` (UTF-16 code units in the page's extracted text), `Boxes`, `BoxUnits`, `PageWidth`, `PageHeight`, `Heading`, `Text`.

- **The range is exact.** `pageText[Start..End] == Text`. `citation.ReadSourceText(root, options)` extracts the page again from the file and returns that substring: use it to show the source, or to verify a citation.
- **Boxes** (PDF and scans): one box per line of text the passage touches, the union of its words' boxes on that line, with the origin at the top left and y growing downwards, in points (PDF) or pixels (scans), inside the page of `PageWidth` by `PageHeight`. They exist for every passage of a PDF or scan.
- **HTML** ranges count characters of the extracted text, not of the markup.
- A file that changed since it was indexed can no longer be re-read to the same text; `ReadSourceText` then returns different text or throws `InvalidDataException`. Run the indexer again.

## Keeping the index current

`FolderIndexer.Run` is one pass and can be repeated at will:

1. Walk the folder in path order and compare each file with its record: **size and modification time**. Equal means unchanged and the file is not opened.
2. A file whose size or time differ is hashed (SHA-256); the same bytes only update the record (a touch). Other bytes are read again, and the file's old passages that the new version does not replace are deleted.
3. A new file whose size and hash equal a vanished file's is a **rename**: its passages are copied under the new identifiers without extraction (and without embedding again).
4. Files that vanished are removed with all their passages and, for an archive, its entries.
5. A file indexed with other extractors or other chunk sizes (the record's `Fingerprint`) is read again.

Everything for one file is written between two commits, and the indexer commits every `CommitEveryFiles` (200) files or `CommitEveryBytes` (256 MiB) read. After a crash or a kill the index holds whole files only; running again picks up where the last commit left off and ends with the same passage identifiers and text as an uninterrupted run (the kill campaign below tests exactly that). Cancelling a run commits what is complete first. After any other exception from `Run`, dispose the index: the uncommitted tail is discarded.

**Extraction failures** become documents of kind `error`: the record carries the exception's type and message in `Error`, searchable with `index.Errors()`. They are not retried while the file stays as it was. A per-file time limit (`FileTimeout`, default 5 minutes) covers reading, extraction and chunking, archives included; `MaxFileBytes` (1 GiB) turns an oversized file into an error document.

**Threads.** The default (`MaxDegreeOfParallelism = 1`) does everything on the calling thread. A larger value extracts that many files at once on pool threads and still writes them in path order, so the index is the same for every setting. `RunAsync` is `Run` on a pool thread.

**Watching.** `FolderWatcher` indexes once, then reruns the indexer a debounce (default 2 s) after file-system events and every 10 minutes regardless (watchers miss events: queue overflow, network shares, platform limits). It runs on one low-priority background thread and is the index's only writer while alive. A rerun is a directory walk plus the files that changed. Tested on Linux (inotify) and macOS (FSEvents); on Windows it uses the platform watcher and the same rerun.

## Embeddings

`FilePassage.Embedding` is a 384-dimension vector filled from `Text` when the index has an embedder (`FolderIndexer.OpenIndex(path, embedder)`; the packaged default embedder produces 384). Hybrid search then works as described in [hybrid.md](hybrid.md). Without an embedder the index is text-only. Embedding dominates the cost of indexing a folder (see [models.md](models.md) for throughput); the measurements below are text-only.

## Checks

All numbers are from the generated corpus in `tools/OneIndex.Files.Tool` (seed 1: `gen --files 6000 --mib 1100 --scans 12`): 6,191 files, 1.1 GiB, in 10 folders — 1,680 text files, 1,368 short notes, 1,200 Markdown, 900 HTML, 480 PDF (3 to 28 pages each, generated with OnePDF, with a repeating header and footer), 300 zip archives (every third with an archive inside), 12 scanned pages (rendered text in PNG, read with oneocr), 62 corrupt files (random bytes named `.pdf`, `.zip`, `.png`, deeply nested HTML, truncated PDF and zip), 10 encoding and ignore-rule cases and 179 filler binaries of 3 to 5 MB (696 MiB). Entries of the archives count as files in the index: 12,843 file records and 581,632 passages in all.

- **Kill campaign** (`oneindex-files-tool campaign`, the corpus above on x64 Linux, Release, one extraction thread, commit every 10 files): a clean run, then 60 runs of the indexing child process each killed with SIGKILL after a seeded random 1.0 to 3.5 s, the index reopened after every kill, then a final run to completion. Result: 60 of 60 kills delivered, 60 of 60 reopens succeeded, the file-record count never fell between kills (7,065 to 7,765 over the last 30; no lost commit), 0 differences between the resumed and the clean index (identifier, kind, format, page, range, record counts, hashes of text, boxes and headings: 12,843 + 61 + 581,632 lines), 0 duplicate file records, 0 files whose passage count differed from their record, 0 orphan passages, 61 error documents for 60 corrupt-on-purpose files plus the truncated zip. An earlier campaign with four extraction threads and seed 1 delivered 24 kills before the folder was done and gave the same result.
- **Citations**: 1,500 passages sampled with seed 3 across formats (HTML 362, images 12, Markdown 408, PDF 311, text 407; archive entries among them): the page re-extracted from the file gave exactly the passage text for 1,500 of 1,500, and every PDF (311) and scan (12) passage had boxes inside the page.
- **Change detection**: `FolderIndexerTests.ARescanAppliesExactlyTheChanges`: modify, rename, delete, add and touch files; a rescan reports exactly 1 updated, 1 renamed, 1 removed, 1 added and 1 unchanged (touch), reads no file of an unchanged folder, and the resulting index equals one built from scratch.
- **Throughput** (indicative, not a benchmark; princessDonut, 32 cores shared with other jobs, load average 1 to 19 during the runs; first index of each folder into a fresh index, text only, OCR for the scans):

| Folder | Files | MiB | Passages | 8 threads: seconds, files/s, MiB/s | 1 thread: seconds, files/s, MiB/s |
|---|---:|---:|---:|---|---|
| text | 1,680 | 163 | 209,453 | 27.3 s, 62, 6.0 | 28.1 s, 60, 5.8 |
| md | 1,200 | 93 | 137,822 | 14.3 s, 84, 6.5 | 14.4 s, 83, 6.5 |
| web (HTML) | 900 | 78 | 107,153 | 11.9 s, 76, 6.6 | 10.8 s, 83, 7.2 |
| archives | 300 | 36 | 100,273 | 12.3 s, 24, 2.9 | 12.4 s, 24, 2.9 |
| pdf | 480 | 29 | 21,041 | 3.4 s, 143, 8.7 | 4.9 s, 98, 6.0 |
| scans (OCR) | 12 | 1 | 12 | 0.7 s, 18, 1.4 | 1.3 s, 9, 0.7 |
| notes | 1,368 | 4 | 5,783 | 0.9 s, 1,562, 4.8 | 0.7 s, 1,961, 6.1 |
| blobs (binary, hashed only) | 179 | 695 | 0 | 0.2 s | 0.4 s |

  Peak working set 1.3 GiB (8 threads) and 1.1 GiB (1 thread) for the whole process. Writing passages into the index is the limit: more extraction threads help only PDF and OCR. The text index builds about 7,500 passages per second here.

## Not done

- Scanned PDFs (pages that are only images) are not read; an image extractor for them needs the page rendered or its images extracted, which neither OnePDF nor this package does yet.
- Office documents, mail and media wait for their libraries.
- A renamed archive is read again (its entries' identifiers change with its name).
- A file and an archive entry can only share an identifier if a folder is named `name!` next to an archive `name`.
