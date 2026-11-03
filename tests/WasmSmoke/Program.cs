using OneIndex.AotSmoke;
using OneIndex.Storage;

// Runs the shared scenario inside the browser runtime's WebAssembly: memory storage, then the runtime's in-memory file system.
SmokeScenario.Run(new MemoryStorage());
AnalysisScenario.Run();
AsianScenario.Run();
QueryFormsLowLevelScenario.Run();
RankingScenario.Run();
VectorScenario.RunLowLevel();
SmokeScenario.RunWriter(new MemoryStorage(), threadedMerges: false);

string directory = Path.Combine(Path.GetTempPath(), "oneindex-wasm-smoke");
SmokeScenario.Run(new FileSystemStorage(directory));

Console.WriteLine("OK");
