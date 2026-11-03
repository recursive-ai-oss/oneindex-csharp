namespace OneIndex.VectorData.Tests.Conformance;

/// <summary>
/// The shared conformance tests for features the adapter documents as unsupported (docs/vectordata.md). Each is skipped when it starts, with a
/// reason naming the feature, so a plain test run is green and the skips stay visible in the report.
/// </summary>
internal static class Unsupported
{
    private const string Dynamic = "dynamic (dictionary) collections are not supported";

    // (test class, method name or null for every method, reason)
    private static readonly (string Class, string? Method, string Reason)[] s_cases =
    [
        (nameof(Distance), "ManhattanDistance", "Manhattan distance is not supported"),
        (nameof(Distance), "HammingDistance", "Hamming distance is not supported"),
        (nameof(KeyTypes), "Guid", "the Guid key test also needs " + Dynamic),
        (nameof(DependencyInjection), "EmbeddingGeneratorIsResolved", "embedding generators are not resolved from the service container"),
        (nameof(DataTypes), null, "each data-type test also creates a " + Dynamic + "; the typed path is covered by ContractTests"),
    ];

    // For field initializers, which run before the base constructor: the skip is raised before any fixture work starts.
    public static bool Run()
    {
        Check();
        return true;
    }

    /// <summary>Skips the running test when it is one of the documented unsupported cases. Call it from the test class constructor.</summary>
    public static void Check()
    {
        string? cls = TestContext.Current.TestClass?.TestClassSimpleName;
        string? method = TestContext.Current.TestMethod?.MethodName;
        foreach ((string c, string? m, string reason) in s_cases)
        {
            if (string.Equals(c, cls, StringComparison.Ordinal) && (m is null || string.Equals(m, method, StringComparison.Ordinal)))
            {
                Assert.Skip("Unsupported: " + reason + " (docs/vectordata.md).");
            }
        }
    }
}
