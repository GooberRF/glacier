using System;
using System.Text;

namespace Ged.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (Exception)
        {
            // A redirected/unsupported console keeps its encoding.
        }

        return CliApp.RunAsync(args, Console.Out, Console.Error).GetAwaiter().GetResult();
    }
}
