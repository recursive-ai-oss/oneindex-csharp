using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace OneIndex.Harness;

/// <summary>Facts about the machine and process a measurement ran on, recorded beside every benchmark row.</summary>
public static class MachineRecord
{
    /// <summary>Captures CPU, cores, instruction sets, storage, runtime and SDK. <paramref name="workDir"/> is where the index lives.</summary>
    public static Dictionary<string, string> Capture(string workDir)
    {
        Directory.CreateDirectory(workDir);
        return new Dictionary<string, string>
        {
            ["cpu"] = CpuModel(),
            ["logicalCores"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            ["isa"] = Isa(),
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
            ["storage"] = Storage(workDir),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["sdk"] = Tool("dotnet", "--version"),
            ["totalMemoryGiB"] = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)).ToString(CultureInfo.InvariantCulture),
            ["gc"] = System.Runtime.GCSettings.IsServerGC ? "server" : "workstation",
        };
    }

    /// <summary>The 1-, 5- and 15-minute load averages as text.</summary>
    public static string LoadAverage() => File.Exists("/proc/loadavg")
        ? string.Join(' ', File.ReadAllText("/proc/loadavg").Split(' ').Take(3))
        : Tool("sysctl", "-n vm.loadavg");

    // The volume holding the index: file system type, device, size and free space, as df reports them.
    private static string Storage(string dir)
    {
        var text = Tool("df", $"-PT \"{dir}\"");
        if (!text.Contains('\n', StringComparison.Ordinal))
        {
            text = Tool("df", $"-P \"{dir}\"");
        }

        return text.Split('\n').LastOrDefault()?.Trim() ?? "unknown";
    }

    private static string CpuModel()
    {
        if (File.Exists("/proc/cpuinfo"))
        {
            var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
            if (line is not null)
            {
                return line[(line.IndexOf(':') + 1)..].Trim();
            }
        }

        return Tool("sysctl", "-n machdep.cpu.brand_string");
    }

    private static string Isa()
    {
        var flags = new List<string>();
        void Add(bool on, string name)
        {
            if (on)
            {
                flags.Add(name);
            }
        }

        Add(Sse42.IsSupported, "sse4.2");
        Add(Avx.IsSupported, "avx");
        Add(Avx2.IsSupported, "avx2");
        Add(Fma.IsSupported, "fma");
        Add(Bmi2.IsSupported, "bmi2");
        Add(Avx512F.IsSupported, "avx512f");
        Add(Avx512BW.IsSupported, "avx512bw");
        Add(AdvSimd.IsSupported, "neon");
        Add(Crc32.IsSupported, "crc32");
        Add(Dp.IsSupported, "dotprod");
        return string.Join(' ', flags);
    }

    private static string Tool(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true });
            var text = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return text;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }
}

/// <summary>Resident-memory high-water mark of this process.</summary>
public static class PeakMemory
{
    /// <summary>Resets the high-water mark where the OS allows it (Linux). Returns whether it did.</summary>
    public static bool Reset()
    {
        try
        {
            File.WriteAllText("/proc/self/clear_refs", "5");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The peak resident set in bytes since the last <see cref="Reset"/> (since process start where reset is unavailable).</summary>
    public static long Bytes()
    {
        if (File.Exists("/proc/self/status"))
        {
            var line = File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("VmHWM:", StringComparison.Ordinal));
            if (line is not null)
            {
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
            }
        }

        using var p = Process.GetCurrentProcess();
        return p.PeakWorkingSet64;
    }
}
