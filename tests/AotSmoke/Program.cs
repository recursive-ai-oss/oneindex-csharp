using OneIndex.AotSmoke;
using OneIndex.Storage;

SmokeScenario.Run(new MemoryStorage());
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
