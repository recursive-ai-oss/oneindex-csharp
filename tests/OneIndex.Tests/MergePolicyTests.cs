using OneIndex.Index;

namespace OneIndex.Tests;

public sealed class MergePolicyTests
{
    // Flushes of a fixed size arrive one at a time and every planned merge runs at once, as a settled index would.
    private static (List<int> Sizes, double Rewritten) Settle(TieredMergePolicy policy, int flushes, int flushDocs)
    {
        var sizes = new List<int>();
        long rewritten = 0;
        long total = 0;
        int next = 0;
        var names = new List<string>();
        for (int f = 0; f < flushes; f++)
        {
            sizes.Add(flushDocs);
            names.Add($"s{next++:D6}");
            total += flushDocs;
            while (true)
            {
                var candidates = new List<MergeCandidate>();
                for (int i = 0; i < sizes.Count; i++)
                {
                    candidates.Add(new MergeCandidate(names[i], sizes[i], 0));
                }

                IReadOnlyList<IReadOnlyList<string>> plans = policy.Select(candidates);
                if (plans.Count == 0)
                {
                    break;
                }

                foreach (IReadOnlyList<string> plan in plans)
                {
                    int merged = 0;
                    foreach (string name in plan)
                    {
                        int at = names.IndexOf(name);
                        merged += sizes[at];
                        sizes.RemoveAt(at);
                        names.RemoveAt(at);
                    }

                    rewritten += merged;
                    sizes.Add(merged);
                    names.Add($"s{next++:D6}");
                }
            }
        }

        return (sizes, (total + rewritten) / (double)total);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(40)]
    [InlineData(100)]
    public void SizeRatioKeepsFewSegmentsWithLogarithmicRewrites(int flushes)
    {
        (List<int> sizes, double written) = Settle(new TieredMergePolicy(), flushes, 20_000);
        Assert.True(sizes.Count <= 5, $"{flushes} flushes left {sizes.Count} segments");
        Assert.True(written <= 1 + Math.Log2(flushes) * 1.5, $"{flushes} flushes rewrote {written:F1}x");
        Assert.Equal(flushes * 20_000L, sizes.Sum(static s => (long)s));
    }

    [Fact]
    public void ZeroRatioIsThePlainTieredPolicy()
    {
        (List<int> sizes, _) = Settle(new TieredMergePolicy { SizeRatio = 0 }, 15, 20_000);
        Assert.Equal(6, sizes.Count); // ten flushes merged, five still waiting for a tenth in their tier
    }

    [Fact]
    public void PlansAreDisjointAndRespectTheSizeCap()
    {
        var policy = new TieredMergePolicy { MaxMergedSegmentDocs = 50_000 };
        var candidates = new List<MergeCandidate>();
        for (int i = 0; i < 12; i++)
        {
            candidates.Add(new MergeCandidate($"s{i:D2}", 20_000, 0));
        }

        var seen = new HashSet<string>();
        foreach (IReadOnlyList<string> plan in policy.Select(candidates))
        {
            Assert.True(plan.Count * 20_000L <= 50_000);
            foreach (string name in plan)
            {
                Assert.True(seen.Add(name), name + " is in two plans");
            }
        }
    }

    [Fact]
    public void PlansDoNotDependOnTheOrderOfCandidates()
    {
        var candidates = new List<MergeCandidate> { new("a", 3_000, 0), new("b", 20_000, 0), new("c", 2_000, 0), new("d", 2_500, 0), new("e", 90_000, 0) };
        var policy = new TieredMergePolicy();
        string Show(IReadOnlyList<MergeCandidate> list) => string.Join(";", policy.Select(list).Select(static p => string.Join(",", p)));
        string expected = Show(candidates);
        candidates.Reverse();
        Assert.Equal(expected, Show(candidates));
        Assert.NotEqual("", expected);
    }
}
