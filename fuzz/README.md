# Fuzzing

`fuzz/OneIndex.Fuzz` holds the fuzz targets; SharpFuzz instruments `OneIndex.dll` and libFuzzer drives it.

| Target | Input | Surface |
|---|---|---|
| `commit` | arbitrary bytes | the commit-file reader and the schema reader; a commit or schema that parses must serialize back to exactly the same bytes |
| `segment` | arbitrary bytes | the delete-file reader and the identifier-map reader (validation and binary-search lookup); what parses must agree with itself |

Only `CorruptIndexException` (and its subclass `UnsupportedFormatException`) may come out of a reader. Any other exception, a failed invariant, a hang (`-timeout`) or a memory blow-up (`-rss_limit_mb`) is a finding.

## Running

    export ONEINDEX_FUZZ_TOOLS=/path/to/tools     # holds fuzz/libfuzzer-dotnet and sharpfuzz/sharpfuzz
    export ONEINDEX_FUZZ_WORK=/path/to/workdir    # builds, corpora, findings; must be outside the checkout
    fuzz/run.sh commit 600                        # fuzz for 10 minutes
    fuzz/run.sh replay commit some-input...       # run saved inputs once, without libFuzzer

`run.sh` builds the harness in release mode into `$ONEINDEX_FUZZ_WORK/bin`, writes seeds (a full commit, an empty commit, a schema) into `$ONEINDEX_FUZZ_WORK/corpus/commit/`, instruments `OneIndex.dll`, and runs libFuzzer. Findings land in `$ONEINDEX_FUZZ_WORK/findings/commit/`.

The tools:

* `libfuzzer-dotnet`: the C++ host from the SharpFuzz project, compiled with `clang++ -fsanitize=fuzzer`.
* `sharpfuzz`: `dotnet tool install SharpFuzz.CommandLine --tool-path <dir>/sharpfuzz`. If its runtime is older than the installed one, set `DOTNET_ROLL_FORWARD=Major`.

Reading the output: `ft` (features) is the number to watch. The host's own `cov` stays small because only the .NET side is instrumented. Run one fuzz process at a time.

## Findings

Minimize the input, save it under `testdata/fuzz/` (under 1 MB), and add a test that feeds it and asserts a typed result.
