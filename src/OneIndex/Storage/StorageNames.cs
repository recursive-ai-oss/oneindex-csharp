namespace OneIndex.Storage;

/// <summary>
/// Storage is a flat set of files. A name is 1 to <see cref="IndexLimits.MaxFileNameLength"/> characters from
/// <c>A-Z a-z 0-9 . _ -</c> and does not start with a dot, so a name can never leave the directory, collide with a hidden
/// file or need escaping in a URL.
/// </summary>
public static class StorageNames
{
    /// <summary>Whether <paramref name="name"/> is a valid storage file name.</summary>
    /// <param name="name">The candidate name.</param>
    /// <param name="maxLength">The longest accepted name; default 255.</param>
    /// <returns><see langword="true"/> when the name is valid.</returns>
    public static bool IsValid(string? name, int maxLength = 255)
    {
        if (string.IsNullOrEmpty(name) || name.Length > maxLength || name[0] == '.')
        {
            return false;
        }

        foreach (char c in name)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="name"/> is valid.</summary>
    /// <param name="name">The name to check.</param>
    /// <param name="parameterName">The parameter being checked, for the exception.</param>
    public static void Validate(string? name, string parameterName = "name")
    {
        if (!IsValid(name))
        {
            throw new ArgumentException($"'{name}' is not a valid storage file name (1-255 characters of A-Z a-z 0-9 . _ -, not starting with a dot).", parameterName);
        }
    }
}
