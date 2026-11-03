namespace OneIndex.Durability;

/// <summary>
/// The directory a kill campaign works in. The campaigns compare the index against a model that starts empty, so a directory that
/// already holds an index would show up as lost or invented documents. A non-empty directory is therefore refused unless the caller
/// asked for it to be cleared.
/// </summary>
public static class CampaignDirectory
{
    /// <summary>Makes sure <paramref name="directory"/> exists and is empty.</summary>
    /// <param name="directory">The campaign directory.</param>
    /// <param name="clear">Delete what the directory holds instead of refusing.</param>
    /// <exception cref="CampaignDirectoryException">The directory holds files and <paramref name="clear"/> is false.</exception>
    public static void Prepare(string directory, bool clear)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            if (!clear)
            {
                throw new CampaignDirectoryException($"The campaign directory '{directory}' is not empty. Use a fresh directory, or pass --clear true to delete its contents.");
            }

            Directory.Delete(directory, true);
        }

        Directory.CreateDirectory(directory);
    }
}

/// <summary>A campaign was pointed at a directory that already holds files.</summary>
/// <param name="message">What to do about it.</param>
public sealed class CampaignDirectoryException(string message) : InvalidOperationException(message);
