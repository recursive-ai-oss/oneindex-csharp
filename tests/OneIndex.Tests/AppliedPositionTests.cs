namespace OneIndex.Tests;

public class AppliedPositionTests
{
    [Fact]
    public void CommitStoresTheMarkWithTheDocumentsAndANoOpCommitStillPublishesIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oneindex-pos-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (SearchIndex<Article> index = SearchIndex.Open<Article>(dir))
            {
                Assert.True(index.AppliedPosition.IsEmpty);
                index.Upsert(new Article { Id = "a", Title = "alpha" });
                index.Upsert(new Article { Id = "b", Title = "beta" });
                index.Commit([1, 2, 3]);
                Assert.Equal([1, 2, 3], index.AppliedPosition.ToArray());
                Assert.Equal(["a", "b"], index.CommittedIds().Order(StringComparer.Ordinal));

                index.Delete("a");
                Assert.Equal([1, 2, 3], index.AppliedPosition.ToArray()); // uncommitted changes do not move it
                index.Commit([4]);
                index.Commit([5]); // nothing else changed
                Assert.Equal(["b"], index.CommittedIds());
            }

            using SearchIndex<Article> again = SearchIndex.Open<Article>(dir);
            Assert.Equal([5], again.AppliedPosition.ToArray());
            Assert.Equal(["b"], again.CommittedIds());
            Assert.Throws<ArgumentException>(() => again.Commit(new byte[2000]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
