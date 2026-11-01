using OneIndex.AotSmoke;
using OneIndex.Storage;

SmokeScenario.Run(new MemoryStorage());
AnalysisScenario.Run();
HappyPathScenario.Run();
DynamicAndEvolutionScenario.Run();
SortingGeoScenario.Run();
FunctionScoreScenario.Run();
QueryFormsScenario.Run();
QueryFormsLowLevelScenario.Run();
ModelsScenario.Run();
HybridScenario.Run();
RerankScenario.Run();
SparseScenario.Run();
VectorScenario.RunLowLevel();
VectorScenario.RunHappyPath();
SmokeScenario.RunWriter(new MemoryStorage(), threadedMerges: true);
SmokeScenario.RunWriter(new MemoryStorage(), threadedMerges: false);

string directory = Path.Combine(Path.GetTempPath(), "oneindex-aot-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    SmokeScenario.Run(new FileSystemStorage(directory));
}
finally
{
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }
}

Console.WriteLine("OK");
