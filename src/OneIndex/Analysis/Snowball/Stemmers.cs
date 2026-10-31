namespace OneIndex.Analysis.Snowball;

/// <summary>The Snowball stemmers this build includes, by language name. The list is a fixed table in code, not discovered by reflection.</summary>
public static class Stemmers
{
    /// <summary>The language names (English, lower case) that <see cref="TryCreate"/> accepts, in alphabetical order.</summary>
    public static IReadOnlyList<string> Languages { get; } = GeneratedStemmers.Names;

    /// <summary>Creates a stemmer for a language.</summary>
    /// <param name="language">The language name, for example <c>english</c>.</param>
    /// <param name="stemmer">A new stemmer with its own working buffer when found.</param>
    /// <returns><see langword="true"/> when the language is included.</returns>
    public static bool TryCreate(string language, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Stemmer? stemmer)
    {
        stemmer = GeneratedStemmers.Create(language);
        return stemmer is not null;
    }
}
