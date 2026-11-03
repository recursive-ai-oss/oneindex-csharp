using OneIndex.Durability;

namespace OneIndex.Tests;

public class CampaignDirectoryTests
{
    [Fact]
    public void AMissingOrEmptyDirectoryIsAccepted()
    {
        string root = TestHelpers.NewTempDirectory();
        try
        {
            string fresh = Path.Combine(root, "fresh");
            CampaignDirectory.Prepare(fresh, clear: false);
            Assert.True(Directory.Exists(fresh));
            CampaignDirectory.Prepare(fresh, clear: false);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ANonEmptyDirectoryIsRefusedUnlessClearingWasAskedFor()
    {
        string root = TestHelpers.NewTempDirectory();
        try
        {
            string dir = Path.Combine(root, "lane0");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "commit-0000000000000001.oic"), "x");
            CampaignDirectoryException e = Assert.Throws<CampaignDirectoryException>(() => CampaignDirectory.Prepare(dir, clear: false));
            Assert.Contains(dir, e.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dir, "commit-0000000000000001.oic")), "a refused directory is left untouched");

            CampaignDirectory.Prepare(dir, clear: true);
            Assert.True(Directory.Exists(dir));
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TheKillCampaignRefusesAReusedLaneDirectoryBeforeStartingAChild()
    {
        string root = TestHelpers.NewTempDirectory();
        try
        {
            string lane = Path.Combine(root, "lane0");
            Directory.CreateDirectory(lane);
            File.WriteAllText(Path.Combine(lane, "leftover"), "x");
            Assert.Throws<CampaignDirectoryException>(() => KillHarness.RunLane(lane, Path.Combine(root, "failures"), 1, 1, 10, ["not-started"]));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
