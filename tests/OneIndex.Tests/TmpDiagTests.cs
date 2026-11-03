using Microsoft.Win32.SafeHandles;

namespace OneIndex.Tests;

public class TmpDiagTests
{
    [Fact]
    public void Diag()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        string f = Path.Combine(Path.GetTempPath(), "diag-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(f, new byte[4096]);
        var sb = new System.Text.StringBuilder();
        foreach (FileOptions o in new[] { FileOptions.None, FileOptions.Asynchronous })
        {
            using SafeFileHandle h = File.OpenHandle(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, o);
            var buf = new byte[8];
            for (int i = 0; i < 100; i++) { RandomAccess.Read(h, buf, 0); }
            long b = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { RandomAccess.Read(h, buf, 0); }
            sb.Append(o).Append(" bytes per 1000 reads: ").Append(GC.GetAllocatedBytesForCurrentThread() - b).Append("; ");
        }
        File.Delete(f);
        throw new InvalidOperationException("DIAG " + sb);
    }
}
