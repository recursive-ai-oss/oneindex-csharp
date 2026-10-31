namespace OneIndex.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version);
            return 0;
        }

        Console.Error.WriteLine("oneindex: no commands are available yet (pre-alpha).");
        return 2;
    }
}
