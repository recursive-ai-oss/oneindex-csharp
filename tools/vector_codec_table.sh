#!/usr/bin/env bash
# Recall, QPS and memory of the compressed vector copies on the segment engine: float, 8-bit, 4-bit and sign-bit at the oversamples
# the codec table reports. One configuration after another so each runs alone (QPS is the median of three passes).
# With k = 10 the search width is max(ef, 10 * oversample): ef = 10 shows each oversample on its own, ef >= 100 is the same for all.
#
#   tools/vector_codec_table.sh <embeddings dir> 384|768
set -u
export PATH="$HOME/dotnet10:$HOME/oneindex/dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/dotnet10}" DOTNET_CLI_TELEMETRY_OPTOUT=1
sets=$1; dims=$2
out=${ONEINDEX_BENCH_OUT:-$HOME/oneindex/bench-build}
dotnet build -c Release -m:2 -v quiet -clp:NoSummary -o "$out/vb" bench/OneIndex.VectorBench/OneIndex.VectorBench.csproj >/dev/null || exit 1
if [ "$dims" = 384 ]; then dir=$sets/small-384; count=150000; else dir=$sets/base-768; count=75000; fi
echo "# machine: $(uname -sm), $(nproc) cores; load before: $(uptime | sed 's/.*load average[s]*: *//')"
for spec in "float:" "int8-o2:--quant 8 --oversample 2" "int4-o3:--quant 4 --oversample 3" \
  "bit-o1:--quant 1 --oversample 1" "bit-o2:--quant 1 --oversample 2" "bit-o3:--quant 1 --oversample 3" \
  "bit-o5:--quant 1 --oversample 5" "bit-o10:--quant 1 --oversample 10"; do
  label=${spec%%:*}; extra=${spec#*:}
  echo "== $dims $label"
  # shellcheck disable=SC2086
  dotnet "$out/vb/OneIndex.VectorBench.dll" --engine oneindex-segment --grid 10,20,30,50,100,220,450,900,1300 --dir "$dir" --count "$count" --passes 3 $extra | grep -E '^#|^@|^oneindex'
  echo "# load after: $(uptime | sed 's/.*load average[s]*: *//')"
done
