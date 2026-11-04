# The `oneindex` tool

`oneindex` is a .NET tool (package `RecursiveAI.OneIndex.Cli`) that turns a folder into a searchable passage index, searches it with citations, and builds a static index for browsers. It uses the folder package ([files.md](files.md)) and the engine; it adds no behaviour of its own.

```
dotnet tool install --global RecursiveAI.OneIndex.Cli --prerelease
oneindex index ~/papers --index ~/papers-index
oneindex query ~/papers-index "attention is all you need" --limit 5
oneindex static build ~/papers-index ./site/search
```

The tool package is 151 MB (about 463 MB installed) because it carries the default English models (embedder, reranker and sparse encoder, 134 MB in `RecursiveAI.OneIndex.Models`). `oneindex --version` prints the package version.

## Commands

| Command | What it does |
|---|---|
| `oneindex index <folder> [--index <dir>] [--watch] [--embed] [--ocr]` | Reads every file under the folder (text, Markdown, HTML, PDF, archives) and keeps the passage index in `<dir>` (default `<folder>/.oneindex`). Prints progress. It commits as it goes, so stopping it (Ctrl+C or a kill) and running it again resumes. `--watch` keeps the index current until Ctrl+C. `--embed` also computes passage embeddings with the packaged embedder (slower; needed for `--hybrid`). `--ocr` reads scans: images and PDFs whose pages are pictures. |
| `oneindex query <index> "<text>" [--limit N] [--filter "<filter>"] [--hybrid] [--json] [--files]` | Prints the best passages with their citation (file, page, character range, heading). `--filter` is the filter-string syntax ([filter-strings.md](filter-strings.md)) over the passage fields, named as in `FilePassage` (`Format = pdf AND Page > 2`). `--files` shows one hit per file. `--json` writes one JSON object: `totalHits`, `totalHitsExact` and the hits with their citations, boxes included. It opens the index read-only, so it works while `index --watch` writes. |
| `oneindex static build <index> <out> [--chunk-kb N] [--scattered-kb N]` | Writes a static index of the newest commit into `<out>` (default chunks of 16 KiB for vector files and 4 KiB for term, posting and stored files) for plain file servers and the browser ([static-index.md](static-index.md)). Refuses an `<out>` that already holds one. |
| `oneindex --version`, `--help` | |

## Exit codes and errors

0 done; 1 the command failed (a missing folder, a damaged index, a bad filter); 2 the command line is wrong; 130 cancelled. A user mistake prints one line to standard error, never a stack trace. A damaged index names the file.

## Notes

- The tool references only OneIndex packages: the library, `.Files`, `.Files.Ocr`, `.Models` and `.Analysis.Asian`. Install it with `dotnet tool install`; the test `IndexQueryAndStaticBuildFromThePackedTool` packs the tool and its library packages into a throwaway feed, installs it with `--tool-path`, and runs `index`, `query` (text, `--json`, `--filter`, `--files`), a second `index` (resume, nothing changed) and `static build`, then opens the static index it built.
- Everything stays local. The index holds the extracted text of the files, so it is as sensitive as they are.
- OCR is slow next to reading a text layer (seconds per scanned page); `--ocr` raises the per-file time limit.
