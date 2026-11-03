using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using OneIndex.Browser;

/// <summary>An <see cref="IOpfsHost"/> (file contents cross the interop boundary as base64 text, which the generated marshalling supports for tasks) over the browser's origin-private file system, through the functions of <c>opfs.mjs</c>.</summary>
[SupportedOSPlatform("browser")]
internal sealed partial class JsOpfsHost : IOpfsHost
{
    public static async Task InitAsync() => await JSHost.ImportAsync("opfs", "/app/opfs.mjs").ConfigureAwait(false);

    [JSImport("list", "opfs")]
    private static partial Task<string> JsList();

    [JSImport("read", "opfs")]
    private static partial Task<string> JsRead(string name);

    [JSImport("write", "opfs")]
    private static partial Task JsWrite(string name, string base64);

    [JSImport("remove", "opfs")]
    private static partial Task JsRemove(string name);

    public async ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        string all = await JsList().ConfigureAwait(false);
        return all.Length == 0 ? [] : all.Split('\n');
    }

    public async ValueTask<byte[]> ReadAsync(string name, CancellationToken cancellationToken = default) => Convert.FromBase64String(await JsRead(name).ConfigureAwait(false));

    public async ValueTask WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) => await JsWrite(name, Convert.ToBase64String(bytes.Span)).ConfigureAwait(false);

    public async ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) => await JsRemove(name).ConfigureAwait(false);
}
