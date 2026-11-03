using OneIndex.Samples.Ef;
using OneIndex.Samples.HappyPath;

string root = Path.Combine(Path.GetTempPath(), "oneindex-samples-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Outcome o = Sample.Run(Path.Combine(root, "search"));
    Console.WriteLine($"search: {string.Join(",", o.Ids)}  facets: {string.Join(",", o.Facets)}  tool: {o.ToolName}");
    string[] ef = await EfSample.RunAsync(Path.Combine(root, "app.db"), Path.Combine(root, "ef-search"));
    Console.WriteLine($"ef: {string.Join(",", ef)}");
}
finally
{
    Directory.Delete(root, true);
}
