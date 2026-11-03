using System.Reflection;

namespace OneIndex.Durability;

/// <summary>
/// The command that starts another copy of this program, for the campaigns' child processes. Run through <c>dotnet</c> (the host is the
/// <c>dotnet</c> executable) the copy is <c>dotnet &lt;entry assembly&gt;</c>; run as an application host the copy is the host itself.
/// Taking the first command-line argument as the assembly, as the campaigns once did, adds a stray argument to the application host's
/// command and every child then fails with its usage text.
/// </summary>
public static class SelfCommand
{
    /// <summary>The program and leading arguments that start a copy of the running program.</summary>
    /// <returns>The command, ready for the campaigns' <c>childCommand</c> parameter.</returns>
    /// <exception cref="InvalidOperationException">The process path or entry assembly is not known.</exception>
    public static string[] Get()
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running program is not known.");
        return Resolve(host, Assembly.GetEntryAssembly()?.Location);
    }

    /// <summary>Builds the command from the process path and the entry assembly's location.</summary>
    /// <param name="host">The running executable.</param>
    /// <param name="entryAssembly">The entry assembly's file, or null or empty when unknown.</param>
    /// <returns>The command.</returns>
    public static string[] Resolve(string host, string? entryAssembly)
    {
        if (!string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return [host];
        }

        if (string.IsNullOrEmpty(entryAssembly))
        {
            throw new InvalidOperationException("The program runs under the dotnet host but its entry assembly is not known.");
        }

        return [host, entryAssembly];
    }
}
